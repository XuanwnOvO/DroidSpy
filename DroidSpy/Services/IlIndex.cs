using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Threading;
using ICSharpCode.Decompiler.Metadata;

namespace DroidSpy.Services;

/// <summary>交叉引用里一条引用的性质。</summary>
public enum ReferenceKind
{
    /// <summary>调用 / 跳转到一个方法。</summary>
    Call,

    /// <summary>new 一个对象（也就是调它的构造函数）。</summary>
    Create,

    /// <summary>读取一个字段。</summary>
    Read,

    /// <summary>写入一个字段。</summary>
    Write,

    /// <summary>把某个类型当类型用（box / cast / typeof / newarr 等），不针对具体成员。</summary>
    TypeUse,
}

/// <summary>一条交叉引用命中：谁（调用者）引用了目标。</summary>
public sealed record ReferenceHit(
    string Target,
    ReferenceKind Kind,
    string CallerType,
    string CallerMethod,
    int CallerToken);

/// <summary>一条字符串常量命中。</summary>
public sealed record IlStringHit(
    string Value,
    int Count,
    string CallerType,
    string CallerMethod,
    int CallerToken);

/// <summary>
/// 程序集 IL 索引：把每个方法体里的 call / ldfld / ldstr 之类的操作数收集起来，
/// 供「谁调用了这个方法」「哪些字符串出现过」这类查询使用。
///
/// 索引本身是只读的纯数据，建好之后可以随便跨线程查。
/// 构建过程自己开一份 PEReader 读文件，和主反编译会话完全解耦 ——
/// 这样即使用户在构建期间又打开了新程序集、把旧会话 Dispose 掉，
/// 也不会把正在跑的索引读崩（PEReader 一旦释放，它底下的内存指针就失效了）。
/// </summary>
public sealed class IlIndex
{
    /// <summary>方法数上限，防止极端程序集把内存吃光。</summary>
    private const int MaxMethods = 300_000;

    /// <summary>引用条数上限。</summary>
    private const int MaxReferences = 2_000_000;

    private readonly struct Entry
    {
        public readonly int TargetId;
        public readonly int CallerId;
        public readonly ReferenceKind Kind;

        public Entry(int targetId, int callerId, ReferenceKind kind)
        {
            TargetId = targetId;
            CallerId = callerId;
            Kind = kind;
        }
    }

    // 目标池：「类型::成员」或纯类型名，靠字典去重，同一个目标只留一份字符串
    private readonly List<string> _targets = new();
    private readonly Dictionary<string, int> _targetIds = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<int>> _byTarget = new();

    // 调用者池
    private readonly List<string> _callerTypes = new();
    private readonly List<string> _callerMethods = new();
    private readonly List<int> _callerTokens = new();
    private readonly Dictionary<string, int> _callerIds = new(StringComparer.Ordinal);

    // 某个 token 对应哪个 callerId。方法和 caller 是一对多（重载同名会共用一个 id），
    // 所以这里只留最后一个，够 FindUsages 定位用。
    private readonly Dictionary<int, int> _callerByToken = new();

    private readonly List<Entry> _entries = new();

    // 注意：这里不要为「某个方法引用了哪些目标」再建一份 Dictionary<callerId, List<int>>。
    // 那等于给每条引用都挂一个 List，几百万条引用时对象头 + 扩容空档会把内存吃光，
    // Android 上直接 OOM 闪退。FindUsages 走 _entries 线性扫描，几十万条也就毫秒级。

    // 字符串常量池：每个不同的值只存一份，另记出现次数和第一次出现的位置
    private readonly List<string> _strings = new();
    private readonly Dictionary<string, int> _stringIds = new(StringComparer.Ordinal);
    private readonly List<int> _stringCount = new();
    private readonly List<int> _stringCaller = new();

    /// <summary>实际扫描到的方法数。</summary>
    public int MethodCount { get; private set; }

    /// <summary>收集到的引用条数。</summary>
    public int ReferenceCount => _entries.Count;

    /// <summary>字符串常量的去重数量。</summary>
    public int StringCount => _strings.Count;

    /// <summary>hit 到上限提前收手时为 true，结果只是程序集的一部分。</summary>
    public bool Truncated { get; private set; }

    public TimeSpan BuildTime { get; private set; }

    // ------------------------------------------------------------ 构建

    public static IlIndex Build(string path, CancellationToken ct, Action<int>? progress)
    {
        var index = new IlIndex();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        int scanned = 0;

        foreach (var th in md.TypeDefinitions)
        {
            ct.ThrowIfCancellationRequested();
            if (scanned >= MaxMethods || index._entries.Count >= MaxReferences)
            {
                index.Truncated = true;
                break;
            }

            var td = md.GetTypeDefinition(th);
            var typeName = FormatTypeName(md, th);

            foreach (var mh in td.GetMethods())
            {
                if (scanned >= MaxMethods)
                {
                    index.Truncated = true;
                    break;
                }

                scanned++;

                // 每 256 个方法才查一次取消 / 报一次进度，避免这些开销盖过解析本身
                if ((scanned & 0xFF) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Invoke(scanned);
                }

                index.ScanMethod(pe, md, mh, typeName);
            }
        }

        index.MethodCount = scanned;
        index.BuildTime = watch.Elapsed;
        progress?.Invoke(scanned);
        ct.ThrowIfCancellationRequested();
        return index;
    }

    private void ScanMethod(PEReader pe, MetadataReader md, MethodDefinitionHandle mh, string callerType)
    {
        MethodDefinition mdef;
        try
        {
            mdef = md.GetMethodDefinition(mh);
        }
        catch
        {
            return;
        }

        // 抽象 / 接口 / 外部实现没有方法体，也就没有引用可收
        if (mdef.RelativeVirtualAddress == 0) return;

        MethodBodyBlock body;
        try
        {
            body = pe.GetMethodBody(mdef.RelativeVirtualAddress);
        }
        catch
        {
            return;
        }

        var callerId = AddCaller(callerType, md.GetString(mdef.Name), MetadataTokens.GetToken(mh));

        // 个别方法 IL 解不动就整条跳过，不能让它影响整个索引
        try
        {
            var reader = body.GetILReader();
            Decode(md, ref reader, callerId);
        }
        catch
        {
            // 忽略
        }
    }

    private void Decode(MetadataReader md, ref BlobReader reader, int callerId)
    {
        // 单方法的指令数封顶：正常情况下远达不到，只是防 IL 损坏时死循环
        int guard = 0;

        while (reader.RemainingBytes > 0 && guard++ < 200_000)
        {
            ILOpCode op;
            var first = reader.ReadByte();
            if (first == 0xFE)
                op = (ILOpCode)(0xFE00 | reader.ReadByte());
            else
                op = (ILOpCode)first;

            OperandType type;
            try
            {
                type = op.GetOperandType();
            }
            catch
            {
                return;
            }

            switch (type)
            {
                case OperandType.None:
                    break;

                case OperandType.ShortI:
                case OperandType.ShortVariable:
                case OperandType.ShortBrTarget:
                    reader.ReadByte();
                    break;

                case OperandType.Variable:
                    reader.ReadUInt16();
                    break;

                case OperandType.I:
                case OperandType.BrTarget:
                case OperandType.Sig:
                case OperandType.ShortR:
                    reader.ReadInt32();
                    break;

                case OperandType.I8:
                case OperandType.R:
                    reader.ReadInt64();
                    break;

                case OperandType.Switch:
                    {
                        int n = reader.ReadInt32();
                        if (n < 0) return;
                        long skip = (long)n * 4;
                        if (skip > reader.RemainingBytes) return;
                        reader.Offset += (int)skip;
                        break;
                    }

                case OperandType.Method:
                case OperandType.Field:
                case OperandType.Type:
                case OperandType.Tok:
                case OperandType.String:
                    HandleToken(md, reader.ReadInt32(), type, op, callerId);
                    break;

                default:
                    // 出现没见过的操作数类型，后面的字节边界已经不可信了，就此打住
                    return;
            }
        }
    }

    private void HandleToken(MetadataReader md, int token, OperandType type, ILOpCode op, int callerId)
    {
        if (token == 0) return;

        if (type == OperandType.String)
        {
            try
            {
                var text = md.GetUserString(MetadataTokens.UserStringHandle(token & 0x00FFFFFF));
                if (!string.IsNullOrEmpty(text)) AddString(text, callerId);
            }
            catch
            {
                // 忽略坏 token
            }
            return;
        }

        EntityHandle handle;
        try
        {
            handle = MetadataTokens.EntityHandle(token);
        }
        catch
        {
            return;
        }

        if (handle.IsNil) return;

        if (type == OperandType.Field)
        {
            var (owner, name) = ResolveMember(md, handle);

            // stfld / stsfld 是写，其余（ldfld / ldflda / ldsfld / ldsflda）都按读记
            var kind = op == ILOpCode.Stfld || op == ILOpCode.Stsfld
                ? ReferenceKind.Write
                : ReferenceKind.Read;

            AddReference(owner, name, kind, callerId);
            return;
        }

        if (type == OperandType.Type || type == OperandType.Tok)
        {
            // box / castclass / ldtoken …：这里拿到的可能是类型，也可能是方法或字段句柄，
            // ResolveMember 能解出成员名就带上，解不出就当类型用
            var (owner, name) = ResolveMember(md, handle);
            if (name != null)
                AddReference(owner, name, ReferenceKind.TypeUse, callerId);
            else
                AddReference(ResolveTypeName(md, handle, 0), null, ReferenceKind.TypeUse, callerId);
            return;
        }

        var (callerOwner, member) = ResolveMember(md, handle);
        if (member == null) return;

        AddReference(callerOwner, member,
            op == ILOpCode.Newobj ? ReferenceKind.Create : ReferenceKind.Call, callerId);
    }

    // ------------------------------------------------------------ 池

    private int AddCaller(string typeName, string methodName, int token)
    {
        var key = typeName + "::" + methodName;
        if (_callerIds.TryGetValue(key, out var id))
        {
            _callerByToken[token] = id;
            return id;
        }

        id = _callerTypes.Count;
        _callerTypes.Add(typeName);
        _callerMethods.Add(methodName);
        _callerTokens.Add(token);
        _callerIds[key] = id;
        _callerByToken[token] = id;
        return id;
    }

    private void AddReference(string? owner, string? member, ReferenceKind kind, int callerId)
    {
        if (_entries.Count >= MaxReferences)
        {
            Truncated = true;
            return;
        }

        string target;
        if (member == null)
            target = owner ?? string.Empty;
        else if (owner == null)
            target = member;
        else
            target = owner + "::" + member;

        if (target.Length == 0) return;

        if (!_targetIds.TryGetValue(target, out var targetId))
        {
            targetId = _targets.Count;
            _targets.Add(target);
            _targetIds[target] = targetId;
        }

        var entryId = _entries.Count;
        _entries.Add(new Entry(targetId, callerId, kind));

        if (!_byTarget.TryGetValue(targetId, out var list))
        {
            list = new List<int>(2);
            _byTarget[targetId] = list;
        }
        list.Add(entryId);
    }

    private void AddString(string value, int callerId)
    {
        if (_stringIds.TryGetValue(value, out var id))
        {
            _stringCount[id]++;
            return;
        }

        id = _strings.Count;
        _strings.Add(value);
        _stringIds[value] = id;
        _stringCount.Add(1);
        _stringCaller.Add(callerId);
    }

    // ------------------------------------------------------------ 查询

    /// <summary>
    /// 查谁引用了名字里含 <paramref name="query"/> 的目标。
    /// query 可以直接写成员名（<c>Update</c>），也可以写「类型::成员」精确到某一个。
    /// </summary>
    public List<ReferenceHit> FindReferences(string query, int limit, out bool truncated)
    {
        var hits = new List<ReferenceHit>();
        var seen = new HashSet<(int Caller, int Target, ReferenceKind Kind)>();
        truncated = false;

        foreach (var pair in _targetIds)
        {
            if (pair.Key.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!_byTarget.TryGetValue(pair.Value, out var list)) continue;

            foreach (var entryId in list)
            {
                var entry = _entries[entryId];
                if (!seen.Add((entry.CallerId, pair.Value, entry.Kind))) continue;

                if (hits.Count >= limit)
                {
                    truncated = true;
                    goto Done;
                }

                hits.Add(new ReferenceHit(
                    Target: pair.Key,
                    Kind: entry.Kind,
                    CallerType: _callerTypes[entry.CallerId],
                    CallerMethod: _callerMethods[entry.CallerId],
                    CallerToken: _callerTokens[entry.CallerId]));
            }
        }

    Done:
        hits.Sort((a, b) =>
        {
            var c = string.Compare(a.CallerType, b.CallerType, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.Compare(a.CallerMethod, b.CallerMethod, StringComparison.OrdinalIgnoreCase);
        });
        return hits;
    }

    /// <summary>
    /// 查某个方法（由 token 标识）引用了哪些目标。
    /// 线性扫一遍全部引用记录 —— 不额外建反查索引，因为那份索引的内存开销
    /// 和引用条数同阶，在安卓上很容易把进程撑爆。
    /// </summary>
    public List<ReferenceHit> FindUsages(int callerToken, int limit, out bool truncated)
    {
        var hits = new List<ReferenceHit>();
        truncated = false;

        if (!_callerByToken.TryGetValue(callerToken, out var callerId))
            return hits;

        var seen = new HashSet<(int Target, ReferenceKind Kind)>();

        for (int entryId = 0; entryId < _entries.Count; entryId++)
        {
            var entry = _entries[entryId];
            if (entry.CallerId != callerId) continue;
            if (!seen.Add((entry.TargetId, entry.Kind))) continue;

            if (hits.Count >= limit)
            {
                truncated = true;
                break;
            }

            hits.Add(new ReferenceHit(
                Target: _targets[entry.TargetId],
                Kind: entry.Kind,
                CallerType: _callerTypes[callerId],
                CallerMethod: _callerMethods[callerId],
                CallerToken: _callerTokens[callerId]));
        }

        hits.Sort((a, b) =>
        {
            var c = string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });
        return hits;
    }

    /// <summary>搜字符串常量。按出现次数从多到少排。</summary>
    public List<IlStringHit> SearchStrings(string query, bool caseSensitive, int limit, out bool truncated)
    {
        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        // 先攒一批候选再排序，否则「出现次数最多的排前面」就无从谈起了。
        // 候选本身也封顶，免得 query 只有一个字母时把内存撑爆。
        var buffer = new List<IlStringHit>();
        int cap = Math.Max(limit * 20, 200);
        truncated = false;

        for (int i = 0; i < _strings.Count; i++)
        {
            if (_strings[i].IndexOf(query, comparison) < 0) continue;

            if (buffer.Count >= cap)
            {
                truncated = true;
                break;
            }

            var caller = _stringCaller[i];
            buffer.Add(new IlStringHit(
                Value: _strings[i],
                Count: _stringCount[i],
                CallerType: _callerTypes[caller],
                CallerMethod: _callerMethods[caller],
                CallerToken: _callerTokens[caller]));
        }

        buffer.Sort((a, b) => b.Count.CompareTo(a.Count));

        if (buffer.Count > limit)
        {
            truncated = true;
            buffer.RemoveRange(limit, buffer.Count - limit);
        }

        return buffer;
    }

    // ------------------------------------------------------------ 名称解析

    /// <summary>
    /// 把方法 / 字段句柄解成 (所属类型, 成员名)。
    /// 解不出来时对应位置是 null —— 比如 MemberReference 的父级是个泛型实例。
    /// </summary>
    private static (string? Owner, string? Member) ResolveMember(MetadataReader md, EntityHandle handle)
    {
        for (int guard = 0; guard < 8; guard++)
        {
            switch (handle.Kind)
            {
                case HandleKind.MethodSpecification:
                    // 泛型方法实例，真正的定义在 Method 属性里
                    handle = md.GetMethodSpecification((MethodSpecificationHandle)handle).Method;
                    continue;

                case HandleKind.MethodDefinition:
                    {
                        var m = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                        return (FormatTypeName(md, m.GetDeclaringType()), md.GetString(m.Name));
                    }

                case HandleKind.FieldDefinition:
                    {
                        var f = md.GetFieldDefinition((FieldDefinitionHandle)handle);
                        return (FormatTypeName(md, f.GetDeclaringType()), md.GetString(f.Name));
                    }

                case HandleKind.MemberReference:
                    {
                        var mr = md.GetMemberReference((MemberReferenceHandle)handle);
                        return (ResolveTypeName(md, mr.Parent, 0), md.GetString(mr.Name));
                    }

                default:
                    return (null, null);
            }
        }

        return (null, null);
    }

    private static string? ResolveTypeName(MetadataReader md, EntityHandle handle, int depth)
    {
        // depth 必须一路透传下去。TypeSpecification 的签名里可以再嵌类型，
        // 如果在这里把 depth 归零，ResolveTypeName → DecodeTypeSpec →
        // DecodeTypeSignature → ResolveTypeName 就成了不耗栈预算的死循环。
        if (depth > MaxTypeDepth) return null;

        try
        {
            return handle.Kind switch
            {
                HandleKind.TypeDefinition => FormatTypeName(md, (TypeDefinitionHandle)handle),
                HandleKind.TypeReference => FormatTypeName(md, (TypeReferenceHandle)handle),
                HandleKind.TypeSpecification => DecodeTypeSpec(md, (TypeSpecificationHandle)handle, depth),
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 嵌套类型的层数上限。真实代码里嵌套超过十几层已经极罕见，
    /// 之所以要封顶是因为元数据里 declaring type 可以成环 ——
    /// 一旦成环，递归就停不下来，而栈溢出在 .NET 里是不可捕获的，进程会直接闪退。
    /// </summary>
    private const int MaxTypeDepth = 32;

    private static string FormatTypeName(MetadataReader md, TypeDefinitionHandle handle) =>
        FormatTypeName(md, md.GetTypeDefinition(handle), 0);

    private static string FormatTypeName(MetadataReader md, TypeDefinition td, int depth)
    {
        var name = StripArity(md.GetString(td.Name));

        // 到顶了就只报当前这一层的名字，宁可名字不全也不能把栈耗尽
        if (depth >= MaxTypeDepth) return name;

        string prefix;
        var declaring = td.GetDeclaringType();
        if (!declaring.IsNil)
        {
            prefix = FormatTypeName(md, md.GetTypeDefinition(declaring), depth + 1) + ".";
        }
        else
        {
            var ns = td.Namespace.IsNil ? null : md.GetString(td.Namespace);
            prefix = string.IsNullOrEmpty(ns) ? string.Empty : ns + ".";
        }

        var full = prefix + name;

        // 带上泛型参数名，才能和 list_types 给出的 FullName（List<T> 这种）对得上
        var parameters = td.GetGenericParameters();
        if (parameters.Count > 0)
        {
            var parts = new List<string>(parameters.Count);
            foreach (var ph in parameters)
                parts.Add(md.GetString(md.GetGenericParameter(ph).Name));

            full += "<" + string.Join(", ", parts) + ">";
        }

        return full;
    }

    private static string FormatTypeName(MetadataReader md, TypeReferenceHandle handle) =>
        FormatTypeName(md, handle, 0);

    private static string FormatTypeName(MetadataReader md, TypeReferenceHandle handle, int depth)
    {
        var tr = md.GetTypeReference(handle);
        var name = StripArity(md.GetString(tr.Name));

        // 同 TypeDefinition：ResolutionScope 链也可能成环
        if (depth >= MaxTypeDepth) return name;

        var scope = tr.ResolutionScope;
        if (!scope.IsNil && scope.Kind == HandleKind.TypeReference)
            return FormatTypeName(md, (TypeReferenceHandle)scope, depth + 1) + "." + name;

        var ns = tr.Namespace.IsNil ? null : md.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    /// <summary>
    /// 解开 TypeSpecification 的签名，尽量还原成 <c>List&lt;int&gt;</c> 这种可读名字。
    /// 认不出来的形态（多维数组、指针……）返回 null，对应的引用就只记一个类型占位。
    /// </summary>
    private static string? DecodeTypeSpec(MetadataReader md, TypeSpecificationHandle handle, int depth)
    {
        try
        {
            var reader = md.GetBlobReader(md.GetTypeSpecification(handle).Signature);
            return DecodeTypeSignature(md, ref reader, depth + 1);
        }
        catch
        {
            return null;
        }
    }

    private static string? DecodeTypeSignature(MetadataReader md, ref BlobReader reader, int depth)
    {
        if (depth > MaxTypeDepth || reader.RemainingBytes < 1) return null;

        var element = reader.ReadByte();
        switch (element)
        {
            case 0x11: // ELEMENT_TYPE_VALUETYPE
            case 0x12: // ELEMENT_TYPE_CLASS
                return ResolveTypeName(md, reader.ReadTypeHandle(), depth + 1);

            case 0x15: // ELEMENT_TYPE_GENERICINST
                {
                    var generic = ResolveTypeName(md, reader.ReadTypeHandle(), depth + 1);
                    if (generic == null) return null;

                    int argc = reader.ReadCompressedInteger();

                    // 参数个数是个压缩整数，坏数据能给出天文数字，先卡住再分配
                    if (argc < 0 || argc > 64) return null;

                    var args = new List<string>(argc);
                    for (int i = 0; i < argc; i++)
                    {
                        var arg = DecodeTypeSignature(md, ref reader, depth + 1);
                        if (arg == null) return null;
                        args.Add(arg);
                    }

                    return generic + "<" + string.Join(", ", args) + ">";
                }

            case 0x1D: // ELEMENT_TYPE_SZARRAY
                {
                    var item = DecodeTypeSignature(md, ref reader, depth + 1);
                    return item == null ? null : item + "[]";
                }

            case 0x13: // ELEMENT_TYPE_VAR
            case 0x1E: // ELEMENT_TYPE_MVAR
                return (element == 0x13 ? "!" : "!!") + reader.ReadCompressedInteger();

            default:
                return PrimitiveName(element);
        }
    }

    private static string? PrimitiveName(byte element) => element switch
    {
        0x01 => "void",
        0x02 => "bool",
        0x03 => "char",
        0x04 => "sbyte",
        0x05 => "byte",
        0x06 => "short",
        0x07 => "ushort",
        0x08 => "int",
        0x09 => "uint",
        0x0A => "long",
        0x0B => "ulong",
        0x0C => "float",
        0x0D => "double",
        0x0E => "string",
        0x1C => "object",
        0x18 => "IntPtr",
        0x19 => "UIntPtr",
        _ => null,
    };

    private static string StripArity(string name)
    {
        int tick = name.IndexOf('`');
        return tick < 0 ? name : name.Substring(0, tick);
    }
}
