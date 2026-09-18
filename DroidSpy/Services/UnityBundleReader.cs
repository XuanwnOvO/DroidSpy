using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using K4os.Compression.LZ4;
using SharpCompress.Compressors.LZMA;

namespace DroidSpy.Services;

/// <summary>
/// Unity 资源包（AssetBundle）解包器。
///
/// Unity 游戏发布时，Assembly-CSharp.dll 往往被 LZ4 / LZMA 压缩后塞进资源包，
/// 直接交给反编译引擎只会报元数据异常。这里把它解开，
/// 在解压后的数据流里定位内嵌的托管 PE，并原样提取成 .dll。
///
/// 支持的容器：
///   UnityFS                  —— Unity 5.3 以后的标准格式
///   UnityWeb / UnityRaw      —— 更老的格式，version 6 时内部结构与 UnityFS 相同
///   BuildPlayer-*            —— 打包玩家资源时用的变体，内部结构与 UnityFS 相同
///
/// 支持的压缩：不压缩 / LZ4 / LZ4HC / LZMA。
///
/// 全部流式处理：包体和数据流都不整体读进内存，避免大游戏包 OOM。
/// </summary>
public static class UnityBundleReader
{
    /// <summary>认得出的容器签名。</summary>
    private static readonly string[] KnownSignatures = { "UnityFS", "UnityWeb", "UnityRaw" };

    /// <summary>BuildPlayer 容器签名前缀，后面跟平台名。</summary>
    private const string BuildPlayerPrefix = "BuildPlayer-";

    /// <summary>判定一个 PE 需要读到多少字节（PE 头 + 可选头 + 数据目录）。</summary>
    private const int ProbeHeadBytes = 4096;

    /// <summary>滑动窗口保留的尾部长度，用于跨块拼接 PE 头。</summary>
    private const int CarryBytes = 64 * 1024;

    /// <summary>解压后数据流的体积上限，超过就认为不是我们要处理的包。</summary>
    private const long MaxDataStreamSize = 1L << 30; // 1 GB

    /// <summary>文件头是不是一个认得出的 Unity 资源包。</summary>
    public static bool IsBundle(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            return ReadSignature(fs) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读文件头签名；不是资源包时返回 null。</summary>
    private static string? ReadSignature(Stream s)
    {
        string sig = ReadCString(s);
        return LineUp(sig);
    }

    /// <summary>规范化签名：认得出就返回原样，否则返回 null。</summary>
    private static string? LineUp(string sig)
    {
        if (Array.IndexOf(KnownSignatures, sig) >= 0) return sig;
        if (sig.StartsWith(BuildPlayerPrefix, StringComparison.Ordinal)) return sig;
        return null;
    }

    /// <summary>
    /// 从资源包中提取第一个内嵌的托管程序集。
    /// 失败时抛 <see cref="InvalidDataException"/>，消息可直接展示给用户。
    /// </summary>
    /// <returns>一段描述提取结果的信息。</returns>
    public static string ExtractAssembly(string bundlePath, string outputPath)
    {
        // 输出和输入是同一个文件时，写入会把源文件截断，读下去必然失败
        if (string.Equals(Path.GetFullPath(bundlePath), Path.GetFullPath(outputPath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("解包输出路径不能和源文件相同。");

        using var fs = File.OpenRead(bundlePath);

        // ---------- 1. 解析容器头 ----------
        string signature = ReadCString(fs);
        string? kind = LineUp(signature);
        if (kind == null)
            throw new InvalidDataException("这不是 Unity 资源包。");

        uint version = ReadU32Be(fs);

        // UnityWeb / UnityRaw 从第 6 版起，内部结构与 UnityFS 完全一致；
        // BuildPlayer-* 不论版本都跟 UnityFS 走同一套布局
        bool useFsLayout = signature == "UnityFS"
            || signature.StartsWith(BuildPlayerPrefix, StringComparison.Ordinal)
            || version >= 6;

        string unityVersion = "";
        long headerEnd;
        uint compressedBlocksInfoSize;
        uint uncompressedBlocksInfoSize;
        uint flags;

        if (useFsLayout)
        {
            unityVersion = ReadCString(fs);
            string unityRevision = ReadCString(fs);
            long totalSize = ReadI64Be(fs);
            compressedBlocksInfoSize = ReadU32Be(fs);
            uncompressedBlocksInfoSize = ReadU32Be(fs);
            flags = ReadU32Be(fs);
            headerEnd = fs.Position;
        }
        else
        {
            // 老格式：头里带哈希、crc 和分级下载信息，之后才是完整的压缩数据流
            (compressedBlocksInfoSize, uncompressedBlocksInfoSize, flags, headerEnd) =
                ReadLegacyHeader(fs, version);
        }

        if (uncompressedBlocksInfoSize > 64u * 1024 * 1024)
            throw new InvalidDataException("资源包的索引信息异常庞大，无法解析。");

        int compressionType = (int)(flags & 0x3F);
        bool blocksInfoAtEnd = (flags & 0x80) != 0;

        // ---------- 2. 取出并解压索引信息 ----------
        // 头部之后可能有对齐填充（flags & 0x200 时要求 16 字节对齐），
        // 具体跳过多少字节各版本不一致，直接在小范围内试到能解开为止。
        byte[]? blocksInfo = null;
        long blocksInfoPos = -1;
        var compBuf = new byte[compressedBlocksInfoSize];
        long scanLimit = Math.Min(headerEnd + 512, fs.Length - compressedBlocksInfoSize);

        for (long cand = headerEnd; cand <= scanLimit; cand++)
        {
            fs.Position = cand;
            ReadFull(fs, compBuf, 0, compBuf.Length);
            blocksInfo = TryDecompress(compBuf, compBuf.Length, (int)uncompressedBlocksInfoSize, compressionType);
            if (blocksInfo != null)
            {
                blocksInfoPos = cand;
                break;
            }
        }

        if (blocksInfo == null)
            throw new InvalidDataException("资源包的索引信息解压失败，可能已加密或损坏。");

        // ---------- 3. 解析索引信息 ----------
        int q = 16; // 跳过 16 字节哈希
        int blockCount = ReadI32Be(blocksInfo, ref q);
        if (blockCount <= 0 || blockCount > 1 << 20)
            throw new InvalidDataException("资源包的块数量异常。");

        var blocks = new (uint Uncompressed, uint Compressed, int Type)[blockCount];
        long dataStreamSize = 0;
        for (int i = 0; i < blockCount; i++)
        {
            uint unc = ReadU32Be(blocksInfo, ref q);
            uint comp = ReadU32Be(blocksInfo, ref q);
            int type = ReadU16Be(blocksInfo, ref q) & 0x3F;
            blocks[i] = (unc, comp, type);
            dataStreamSize += unc;
        }

        if (dataStreamSize > MaxDataStreamSize)
            throw new InvalidDataException(
                $"资源包解压后超过 {MaxDataStreamSize / (1024 * 1024)} MB，太大了。");

        // 解析完索引后如果要求 16 字节对齐，数据块起点要相应后移
        int dataAlignment = (flags & 0x200) != 0 ? 16 : 1;

        var nodePaths = ReadNodePaths(blocksInfo, ref q, useFsLayout);

        // ---------- 4. 逐块解压，边解压边找内嵌程序集 ----------
        long dataStart = blocksInfoAtEnd ? headerEnd : blocksInfoPos + compressedBlocksInfoSize;
        dataStart = AlignUp(dataStart, dataAlignment);
        long cursor = dataStart;

        long foundAt = -1;      // PE 在数据流中的绝对偏移
        int foundLength = -1;   // PE 的真实长度（由节表推得）
        long nextCollectAt = -1;

        var carry = Array.Empty<byte>();
        long streamPos = 0;     // 当前块在数据流中的绝对偏移

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = File.Create(outputPath);

        foreach (var (unc, comp, type) in blocks)
        {
            byte[]? chunk = DecompressBlock(fs, ref cursor, comp, unc, type);
            if (chunk == null)
                throw new InvalidDataException("资源包数据块解压失败，可能已加密或损坏。");

            // 拼接上一块尾部，保证跨块边界的 PE 头能被完整看到
            var combined = new byte[carry.Length + chunk.Length];
            Buffer.BlockCopy(carry, 0, combined, 0, carry.Length);
            Buffer.BlockCopy(chunk, 0, combined, carry.Length, chunk.Length);
            long combinedBase = streamPos - carry.Length;

            // 还没找到目标时，扫描窗口里有没有托管 PE
            if (foundAt < 0)
            {
                int scanTo = combined.Length - ProbeHeadBytes;
                for (int i = 0; i <= scanTo; i++)
                {
                    if (combined[i] != (byte)'M' || combined[i + 1] != (byte)'Z') continue;
                    if (!IsManagedPe(combined, i)) continue;

                    int len = ComputePeLength(combined, i);
                    if (len <= 0) continue;

                    foundAt = combinedBase + i;
                    foundLength = len;
                    nextCollectAt = foundAt;
                    break;
                }
            }

            // 已锁定目标：把落在本窗口内的字节写出去
            if (foundAt >= 0 && nextCollectAt < foundAt + foundLength)
            {
                long combinedEnd = combinedBase + combined.Length;
                if (nextCollectAt >= combinedBase && nextCollectAt < combinedEnd)
                {
                    int local = (int)(nextCollectAt - combinedBase);
                    long remain = foundAt + foundLength - nextCollectAt;
                    int take = (int)Math.Min(combined.Length - local, remain);
                    output.Write(combined, local, take);
                    nextCollectAt += take;
                }
            }

            streamPos += chunk.Length;

            // 裁剪滑动窗口
            int keep = Math.Min(combined.Length, CarryBytes);
            carry = new byte[keep];
            Buffer.BlockCopy(combined, combined.Length - keep, carry, 0, keep);

            if (foundAt >= 0 && nextCollectAt >= foundAt + foundLength) break;
        }

        if (foundAt < 0)
            throw new InvalidDataException("资源包里没有找到 .NET 程序集，可能这是一个纯资源包。");

        long written = new FileInfo(outputPath).Length;
        string node = nodePaths.Count > 0 ? nodePaths[0] : "(无节点)";
        string unity = string.IsNullOrEmpty(unityVersion) ? "" : $" · Unity {unityVersion}";
        return $"{signature} v{version}{unity}\n" +
               $"节点 {node}\n" +
               $"程序集 {AssemblyStore.FormatSize(written)} @ 数据流偏移 {foundAt}";
    }

    /// <summary>解析索引里的节点表。老格式的字段顺序和宽度都不一样。</summary>
    private static List<string> ReadNodePaths(byte[] blocksInfo, ref int q, bool fsLayout)
    {
        int nodeCount = ReadI32Be(blocksInfo, ref q);
        if (nodeCount < 0) nodeCount = 0;

        var paths = new List<string>(nodeCount);
        for (int i = 0; i < nodeCount; i++)
        {
            if (fsLayout)
            {
                q += 8 + 8 + 4;                 // offset(i64) / size(i64) / flags(u32)
                paths.Add(ReadCString(blocksInfo, ref q));
                while ((q & 3) != 0) q++;       // 路径字符串按 4 字节对齐
            }
            else
            {
                paths.Add(ReadCString(blocksInfo, ref q));
                q += 4 + 4;                     // offset(u32) / size(u32)
            }
        }

        return paths;
    }

    /// <summary>
    /// 读老格式（UnityWeb / UnityRaw）的头部。索引信息是整包压缩的，
    /// 所以这里直接把"压缩块大小"当成从 headerEnd 到文件尾的长度。
    /// </summary>
    private static (uint Compressed, uint Uncompressed, uint Flags, long HeaderEnd)
        ReadLegacyHeader(FileStream fs, uint version)
    {
        if (version >= 4)
        {
            // hash(16) + crc(4)
            var skip = new byte[20];
            ReadFull(fs, skip, 0, skip.Length);
        }

        _ = ReadU32Be(fs);   // minimumStreamedBytes
        uint size = ReadU32Be(fs);
        _ = ReadU32Be(fs);   // numberOfLevelsToDownloadBeforeStreaming

        int levelCount = ReadI32Be(fs);
        if (levelCount <= 0 || levelCount > 1 << 16)
            throw new InvalidDataException("资源包的层级数量异常。");

        uint lastUncompressed = 0;
        for (int i = 0; i < levelCount; i++)
        {
            _ = ReadU32Be(fs);              // 每级的 compressedSize
            lastUncompressed = ReadU32Be(fs);
        }

        if (version >= 2) _ = ReadU32Be(fs);   // completeFileSize
        if (version >= 3) _ = ReadU32Be(fs);   // fileInfoHeaderSize

        // 老格式没有块表，索引信息整段压缩在 size 之后
        long headerEnd = size;
        if (headerEnd <= 0 || headerEnd > fs.Length)
            headerEnd = fs.Position;

        fs.Position = headerEnd;
        uint compressed = (uint)Math.Max(0, fs.Length - headerEnd);

        // 老格式固定用 LZMA，flags 里的压缩类型位直接置成 LZMA
        return (compressed, lastUncompressed, 1u, headerEnd);
    }

    private static long AlignUp(long value, int alignment)
    {
        if (alignment <= 1) return value;
        long mask = alignment - 1;
        return (value + mask) & ~mask;
    }

    // ------------------------------------------------------------ 块解压

    /// <summary>解压一个数据块。块起点可能带对齐填充，逐个候选位置试。</summary>
    private static byte[]? DecompressBlock(FileStream fs, ref long cursor, uint comp, uint unc, int type)
    {
        var src = new byte[comp];

        // 候选起点：紧邻、4/8/16 字节对齐、再往后挪一点
        Span<int> candidates = stackalloc[]
        {
            0, (int)((cursor + 3) & ~3) - (int)cursor, (int)((cursor + 7) & ~7) - (int)cursor,
            (int)((cursor + 15) & ~15) - (int)cursor, 4, 8,
        };

        var tried = new List<long>();
        foreach (int delta in candidates)
        {
            long cand = cursor + delta;
            if (cand < cursor || tried.Contains(cand)) continue;
            if (cand + comp > fs.Length) continue;
            tried.Add(cand);

            fs.Position = cand;
            ReadFull(fs, src, 0, src.Length);

            var dst = TryDecompress(src, src.Length, (int)unc, type);
            if (dst != null)
            {
                cursor = cand + comp;
                return dst;
            }
        }

        return null;
    }

    /// <summary>
    /// 按压缩类型解码；失败返回 null。
    /// 0=不压缩 1=LZMA 2=LZ4 3=LZ4HC。
    /// </summary>
    private static byte[]? TryDecompress(byte[] src, int srcLen, int dstLen, int type)
    {
        try
        {
            if (type == 0)
            {
                if (srcLen != dstLen) return null;
                var copy = new byte[dstLen];
                Buffer.BlockCopy(src, 0, copy, 0, dstLen);
                return copy;
            }

            if (type is 2 or 3)
            {
                var dst = new byte[dstLen];
                int n = LZ4Codec.Decode(src, 0, srcLen, dst, 0, dstLen);
                return n == dstLen ? dst : null;
            }

            if (type == 1)
            {
                if (srcLen <= 5 || dstLen <= 0) return null;

                // Unity 的 LZMA 帧 = 5 字节属性 + 裸 LZMA 流
                var props = new byte[5];
                Buffer.BlockCopy(src, 0, props, 0, 5);

                using var input = new MemoryStream(src, 5, srcLen - 5);
                using var lz = new LzmaStream(props, input, srcLen - 5, dstLen);

                var dst = new byte[dstLen];
                int n = 0, r;
                while (n < dst.Length && (r = lz.Read(dst, n, dst.Length - n)) > 0) n += r;
                return n == dstLen ? dst : null;
            }
        }
        catch
        {
            // 候选位置不对时解压器会抛异常，当作"这里不是块起点"即可
        }

        return null;
    }

    // ------------------------------------------------------------ PE 识别

    /// <summary>是不是一个含 CLR 元数据的托管 PE。</summary>
    private static bool IsManagedPe(byte[] b, int off)
    {
        if (off + ProbeHeadBytes > b.Length) return false;

        int peSig = off + BitConverter.ToInt32(b, off + 0x3C);
        if (peSig <= 0 || peSig + 24 > b.Length) return false;
        if (b[peSig] != (byte)'P' || b[peSig + 1] != (byte)'E' || b[peSig + 2] != 0 || b[peSig + 3] != 0)
            return false;

        int opt = peSig + 24;
        int dataDir = BitConverter.ToUInt16(b, opt) switch
        {
            0x10B => opt + 96,   // PE32
            0x20B => opt + 112,  // PE32+
            _ => -1,
        };
        if (dataDir < 0 || dataDir + 14 * 8 + 4 > b.Length) return false;

        // 数据目录第 15 项是 CLI Header，非空说明是托管程序集
        return BitConverter.ToInt32(b, dataDir + 14 * 8) != 0;
    }

    /// <summary>由节表算出 PE 的完整长度（文件里没有额外长度字段，只能这么求）。</summary>
    private static int ComputePeLength(byte[] b, int off)
    {
        int peSig = off + BitConverter.ToInt32(b, off + 0x3C);
        int numSections = BitConverter.ToUInt16(b, peSig + 6);
        int optSize = BitConverter.ToUInt16(b, peSig + 20);
        int sectTab = peSig + 24 + optSize;

        long end = 0;
        for (int s = 0; s < numSections; s++)
        {
            int sec = sectTab + s * 40;
            if (sec + 40 > b.Length) return -1;

            uint rawSize = BitConverter.ToUInt32(b, sec + 16);
            uint rawPtr = BitConverter.ToUInt32(b, sec + 20);
            long tail = (long)rawPtr + rawSize;
            if (tail > end) end = tail;
        }

        return end is <= 0 or > int.MaxValue ? -1 : (int)end;
    }

    // ------------------------------------------------------------ 读取辅助

    private static void ReadFull(Stream s, byte[] buf, int offset, int count)
    {
        int done = 0;
        while (done < count)
        {
            int n = s.Read(buf, offset + done, count - done);
            if (n <= 0) throw new EndOfStreamException("文件在读取过程中意外结束。");
            done += n;
        }
    }

    /// <summary>读零结尾的字符串，顺带消费掉结尾的 0。</summary>
    private static string ReadCString(Stream s)
    {
        var sb = new StringBuilder(32);
        int b;
        while ((b = s.ReadByte()) > 0) sb.Append((char)b);
        if (b < 0) throw new EndOfStreamException("文件在读取过程中意外结束。");
        return sb.ToString();
    }

    private static string ReadCString(byte[] b, ref int p)
    {
        int start = p;
        while (p < b.Length && b[p] != 0) p++;
        var s = Encoding.UTF8.GetString(b, start, p - start);
        if (p < b.Length) p++;   // 消费结尾的 0
        return s;
    }

    private static uint ReadU32Be(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadUInt32BigEndian(b);
    }

    private static long ReadI64Be(Stream s)
    {
        Span<byte> b = stackalloc byte[8];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt64BigEndian(b);
    }

    private static int ReadI32Be(Stream s)
    {
        Span<byte> b = stackalloc byte[4];
        s.ReadExactly(b);
        return BinaryPrimitives.ReadInt32BigEndian(b);
    }

    private static int ReadI32Be(byte[] b, ref int p)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(p));
        p += 4;
        return v;
    }

    private static uint ReadU32Be(byte[] b, ref int p)
    {
        uint v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(p));
        p += 4;
        return v;
    }

    private static int ReadU16Be(byte[] b, ref int p)
    {
        int v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(p));
        p += 2;
        return v;
    }
}
