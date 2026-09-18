using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Android.Content;
using Android.Net;

namespace DroidSpy.Services;

/// <summary>
/// 程序集仓库：把用户通过文件选择器选中的文件复制到应用私有目录，
/// 得到稳定的真实路径（反编译引擎需要文件路径），并维护最近打开列表。
/// </summary>
public static class AssemblyStore
{
    private const string PrefsName = "droidspy_prefs";
    private const string RecentKey = "recent_files";
    private const int MaxRecent = 20;

    public static string AssembliesDir(Context ctx)
    {
        var dir = new Java.IO.File(ctx.FilesDir, "assemblies");
        if (!dir.Exists()) dir.Mkdirs();
        return dir.AbsolutePath!;
    }

    /// <summary>把 content:// 选中的文件导入私有目录，返回真实路径。</summary>
    public static string Import(Context ctx, Android.Net.Uri uri)
    {
        var name = QueryDisplayName(ctx, uri) ?? $"assembly_{DateTime.Now:yyyyMMddHHmmss}.dll";
        name = SanitizeFileName(name);

        var destDir = AssembliesDir(ctx);
        var destPath = Path.Combine(destDir, name);

        using (var input = ctx.ContentResolver!.OpenInputStream(uri)
                           ?? throw new IOException("无法读取所选文件"))
        using (var output = File.Create(destPath))
        {
            input.CopyTo(output);
        }

        AddRecent(ctx, destPath);
        return destPath;
    }

    private static string? QueryDisplayName(Context ctx, Android.Net.Uri uri)
    {
        try
        {
            using var cursor = ctx.ContentResolver!.Query(uri, null, null, null, null);
            if (cursor != null && cursor.MoveToFirst())
            {
                int idx = cursor.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                if (idx >= 0)
                    return cursor.GetString(idx);
            }
        }
        catch
        {
            // 查询失败时回退到用 URI 末段命名
        }

        var last = uri.LastPathSegment;
        return string.IsNullOrWhiteSpace(last) ? null : Path.GetFileName(last);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ------------------------------------------------------------ 最近打开

    public static void AddRecent(Context ctx, string path)
    {
        var list = GetRecent(ctx).Where(p => !string.Equals(p, path, StringComparison.Ordinal)).ToList();
        list.Insert(0, path);
        if (list.Count > MaxRecent) list = list.Take(MaxRecent).ToList();
        Save(ctx, list);
    }

    public static List<string> GetRecent(Context ctx) =>
        GetPrefs(ctx).GetString(RecentKey, null) is { Length: > 0 } raw
            ? raw.Split('\u0001', StringSplitOptions.RemoveEmptyEntries).ToList()
            : new List<string>();

    public static void ClearRecent(Context ctx) =>
        GetPrefs(ctx).Edit()!.Remove(RecentKey)!.Apply();

    /// <summary>删掉一条最近记录，连同导入时缓存的程序集文件。</summary>
    public static void RemoveRecent(Context ctx, string path)
    {
        var list = GetRecent(ctx);
        if (!list.Remove(path)) return;
        Save(ctx, list);
        DeleteCache(ctx, path);
    }

    /// <summary>清空最近记录，并删掉它们对应的缓存文件（只清 files/assemblies 下的）。</summary>
    public static void ClearRecentAndCache(Context ctx)
    {
        foreach (var path in GetRecent(ctx)) DeleteCache(ctx, path);
        ClearRecent(ctx);
    }

    /// <summary>
    /// 删除导入的缓存副本。只认自己目录里的文件，避免记录被改写后误删用户别处的文件。
    /// </summary>
    private static void DeleteCache(Context ctx, string path)
    {
        try
        {
            if (!string.Equals(Path.GetDirectoryName(path), AssembliesDir(ctx),
                    StringComparison.Ordinal)) return;
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 文件被占用或已删，记录清掉就够了
        }
    }

    private static void Save(Context ctx, List<string> list) =>
        GetPrefs(ctx).Edit()!.PutString(RecentKey, string.Join('\u0001', list))!.Apply();

    private static ISharedPreferences GetPrefs(Context ctx) =>
        ctx.GetSharedPreferences(PrefsName, FileCreationMode.Private)!;

    // ------------------------------------------------------------ 显示辅助

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} MB",
    };

    /// <summary>生成诊断信息：文件名、大小、开头若干字节。用于解释"为什么打不开"。</summary>
    public static string Describe(string path, int headBytes = 16)
    {
        string size;
        try { size = FormatSize(new FileInfo(path).Length); }
        catch { size = "未知"; }

        string head;
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[headBytes];
            int n = fs.Read(buf, 0, headBytes);
            head = n == 0
                ? "(空文件)"
                : string.Join(' ', buf.Take(n).Select(b => b.ToString("X2")));
        }
        catch (Exception ex)
        {
            head = $"({ex.GetType().Name}: {ex.Message})";
        }

        return $"{Path.GetFileName(path)}  ·  {size}\n{head}";
    }

    // ------------------------------------------------------------ 文件类型识别

    /// <summary>用户选中的文件到底是什么。</summary>
    public enum FileKind
    {
        /// <summary>含 CLI 头的 .NET 托管程序集，可以反编译。</summary>
        ManagedAssembly,

        /// <summary>原生 PE（.exe / .so 之类），不含托管元数据。</summary>
        NativePe,

        /// <summary>IL2CPP 游戏的 global-metadata.dat。</summary>
        Il2CppMetadata,

        /// <summary>Unity 资源包，里面可能压着 Assembly-CSharp.dll。</summary>
        UnityBundle,

        /// <summary>认不出来。</summary>
        Unknown,
    }

    /// <summary>
    /// 只看文件头判断类型。用于在真正解析前给出准确提示，
    /// 避免把 "选错文件" 报成引擎内部的元数据异常。
    /// </summary>
    public static FileKind SniffKind(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var head = new byte[24];
            if (fs.Read(head, 0, head.Length) < 4) return FileKind.Unknown;

            // Unity 资源包，解开之后里面通常就是 Assembly-CSharp.dll
            if (Matches(head, "UnityFS", 0) || Matches(head, "UnityWeb", 0)
                || Matches(head, "UnityRaw", 0) || Matches(head, "BuildPlayer-", 0))
                return FileKind.UnityBundle;

            // IL2CPP 的 global-metadata.dat 魔数 0xFAB11BAF（小端存放）
            if (head[0] == 0xAF && head[1] == 0x1B && head[2] == 0xB1 && head[3] == 0xFA)
                return FileKind.Il2CppMetadata;

            if (head[0] != (byte)'M' || head[1] != (byte)'Z')
                return FileKind.Unknown;

            return HasCliHeader(fs) ? FileKind.ManagedAssembly : FileKind.NativePe;
        }
        catch
        {
            return FileKind.Unknown;
        }
    }

    /// <summary>
    /// 比较文件头里从 <paramref name="offset"/> 开始的签名。
    /// <paramref name="signature"/> 以 '-' 结尾时按前缀匹配（BuildPlayer-* 后面跟平台名）。
    /// </summary>
    private static bool Matches(byte[] head, string signature, int offset)
    {
        bool prefix = signature.EndsWith("-", StringComparison.Ordinal);
        if (offset + signature.Length + 1 > head.Length) return false;

        for (int i = 0; i < signature.Length; i++)
            if (head[offset + i] != (byte)signature[i]) return false;

        // 前缀签名后面必须还有平台名，不能是 '-\0' 直接结束
        return prefix || head[offset + signature.Length] == 0;
    }

    /// <summary>PE 可选头的数据目录第 15 项（CLI Header）非空即为托管程序集。</summary>
    private static bool HasCliHeader(FileStream fs)
    {
        var buf = new byte[4];

        fs.Position = 0x3C;
        if (fs.Read(buf, 0, 4) < 4) return false;
        int peOffset = BitConverter.ToInt32(buf, 0);
        if (peOffset <= 0) return false;

        // PE 签名 "PE\0\0" + COFF 头 20 字节，之后是可选头
        int optHeader = peOffset + 4 + 20;
        fs.Position = optHeader;
        if (fs.Read(buf, 0, 2) < 2) return false;

        int magic = BitConverter.ToUInt16(buf, 0);
        int dataDirOffset = magic switch
        {
            0x10B => optHeader + 96,   // PE32
            0x20B => optHeader + 112,  // PE32+
            _ => -1,
        };
        if (dataDirOffset < 0) return false;

        // DataDirectory[14] 是 CLI Header，结构为 (RVA:4, Size:4)
        fs.Position = dataDirOffset + 14 * 8;
        if (fs.Read(buf, 0, 4) < 4) return false;
        return BitConverter.ToInt32(buf, 0) != 0;
    }
}
