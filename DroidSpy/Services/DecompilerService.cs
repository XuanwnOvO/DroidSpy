using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace DroidSpy.Services;

/// <summary>类型节点，用于列表展示。</summary>
public sealed record TypeEntry(
    string FullName,
    string Namespace,
    string Name,
    string Kind,
    int MetadataToken,
    bool IsPublic,
    bool IsCompilerGenerated);

/// <summary>成员节点（方法 / 字段 / 属性 / 事件）。</summary>
public sealed record MemberEntry(
    string Name,
    string Kind,
    string Signature,
    int MetadataToken);

public sealed record AssemblySummary(
    string FileName,
    string FilePath,
    long FileSize,
    string RuntimeVersion,
    string Architecture,
    string TargetFramework,
    bool IsSigned,
    int TypeCount,
    int MethodCount,
    int FieldCount,
    int ReferenceCount,
    IReadOnlyList<string> ReferencedAssemblies);

/// <summary>源码搜索结果里的一行。</summary>
public sealed record SourceHit(string TypeFullName, int Line, string Text);

/// <summary>源码搜索的整体结果。Truncated 表示因为条数 / 时间上限没搜完。</summary>
public sealed record SourceSearchResult(
    List<SourceHit> Hits,
    int ScannedTypes,
    int TotalTypes,
    bool Truncated);

/// <summary>一个类型的继承关系。</summary>
public sealed record TypeHierarchy(
    string TypeFullName,
    string Kind,
    List<string> DirectBases,
    List<string> Interfaces,
    List<string> BaseChain,
    List<string> DerivedTypes);

/// <summary>被引用程序集的一条记录。</summary>
public sealed record AssemblyRefEntry(
    string Name,
    string Version,
    string Culture,
    string PublicKeyToken,
    bool IsRetargetable);

/// <summary>
/// 反编译服务：封装 dnSpy / ILSpy 同款引擎 ICSharpCode.Decompiler。
/// 全部运算在设备本地完成，不依赖任何外部工具。
/// </summary>
public sealed class DecompilerService : IDisposable
{
    private readonly object _gate = new();

    private PEFile? _module;
    private CSharpDecompiler? _decompiler;
    private UniversalAssemblyResolver? _resolver;
    private DecompilerSettings? _settings;

    /// <summary>类型全名 → 类型定义。嵌套类型的查找必须走这张表。</summary>
    private Dictionary<string, ITypeDefinition>? _typeIndex;

    // 交叉引用索引：构建要遍历整份 IL，比较耗时，所以放到后台慢慢跑，
    // 期间 find_references / search_strings 会告诉调用方「还在建，稍等」。
    private IlIndex? _index;
    private bool _indexRunning;
    private string? _indexError;
    private int _indexScanned;
    private CancellationTokenSource? _indexCts;

    public string? FilePath { get; private set; }
    public string? FileName { get; private set; }
    public bool IsLoaded => _decompiler != null;

    /// <summary>
    /// 加载一个 .NET 程序集。会连同所在目录一起加入引用搜索路径，
    /// 这样 Unity 的 Assembly-CSharp.dll 才能正确解析 UnityEngine.* 引用。
    /// </summary>
    public void Load(string path)
    {
        lock (_gate)
        {
            Dispose();

            if (!File.Exists(path))
                throw new FileNotFoundException("文件不存在", path);

            var dir = Path.GetDirectoryName(path);
            var resolver = new UniversalAssemblyResolver(path, false, null);
            if (!string.IsNullOrEmpty(dir))
            {
                resolver.AddSearchDirectory(dir);
                // Unity 常见的托管目录布局
                var managed = Path.Combine(dir, "Managed");
                if (Directory.Exists(managed))
                    resolver.AddSearchDirectory(managed);
            }

            var module = new PEFile(path);
            EnsureManaged(module);

            var settings = new DecompilerSettings(LanguageVersion.Latest)
            {
                ThrowOnAssemblyResolveErrors = false,
                ShowXmlDocumentation = false,
                // 反编译产出尽量接近可编译的常规 C#
                UseDebugSymbols = false,
            };

            _resolver = resolver;
            _module = module;
            _settings = settings;
            _decompiler = new CSharpDecompiler(module, resolver, settings);
            _typeIndex = null;
            FilePath = path;
            FileName = Path.GetFileName(path);
        }

        // 索引不在这里同步等：打开一个几 MB 的 DLL 时它要跑好几百毫秒，
        // 界面会明显卡住。先开后台任务，等真有人来查的时候多半已经好了。
        EnsureIndex();
    }

    private static void EnsureManaged(PEFile module)
    {
        if (!module.Metadata.IsAssembly && module.Metadata.TypeDefinitions.Count == 0)
            throw new InvalidDataException("该文件不含托管元数据");
    }

    // ---------------------------------------------------------------- 概要

    public AssemblySummary GetSummary()
    {
        lock (_gate)
        {
            var module = Require(_module);
            var md = module.Metadata;
            var ts = Require(_decompiler).TypeSystem;

            var types = ts.MainModule.TypeDefinitions.ToList();
            var methodCount = types.Sum(t => t.Methods.Count());
            var fieldCount = types.Sum(t => t.Fields.Count());

            var refs = md.AssemblyReferences
                .Select(h => md.GetString(md.GetAssemblyReference(h).Name))
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var asmDef = md.GetAssemblyDefinition();
            var version = md.MetadataVersion;

            var fi = new FileInfo(FilePath!);

            return new AssemblySummary(
                FileName: FileName!,
                FilePath: FilePath!,
                FileSize: fi.Length,
                RuntimeVersion: version,
                Architecture: module.Metadata.IsAssembly ? "AnyCPU / IL" : "IL",
                TargetFramework: module.DetectTargetFrameworkId() ?? "未知",
                IsSigned: (asmDef.Flags & System.Reflection.AssemblyFlags.PublicKey) != 0,
                TypeCount: types.Count,
                MethodCount: methodCount,
                FieldCount: fieldCount,
                ReferenceCount: refs.Count,
                ReferencedAssemblies: refs);
        }
    }

    // ---------------------------------------------------------------- 类型

    /// <summary>取出程序集中全部类型（含嵌套类型）。</summary>
    public List<TypeEntry> GetTypes()
    {
        lock (_gate)
        {
            var result = new List<TypeEntry>();

            // TypeDefinitions 本身已经把嵌套类型包含在内了，再按 NestedTypes
            // 递归展开一遍会让每个嵌套类型重复出现两次。
            foreach (var t in Require(_decompiler).TypeSystem.MainModule.TypeDefinitions)
            {
                result.Add(new TypeEntry(
                    FullName: t.FullName,
                    Namespace: t.Namespace,
                    Name: t.Name,
                    Kind: KindText(t),
                    MetadataToken: MetadataTokens.GetToken(t.MetadataToken),
                    IsPublic: t.Accessibility == Accessibility.Public,
                    IsCompilerGenerated: IsCompilerGenerated(t.Name)));
            }

            return result
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string KindText(ITypeDefinition t) => t.Kind switch
    {
        TypeKind.Class => t.IsRecord ? "record" : t.IsAbstract && t.IsSealed ? "static class" : "class",
        TypeKind.Struct => t.IsRecord ? "record struct" : "struct",
        TypeKind.Interface => "interface",
        TypeKind.Enum => "enum",
        TypeKind.Delegate => "delegate",
        _ => t.Kind.ToString().ToLowerInvariant(),
    };

    // ---------------------------------------------------------------- 成员

    public List<MemberEntry> GetMembers(string typeFullName)
    {
        lock (_gate)
        {
            var def = FindType(typeFullName);
            if (def == null) return new List<MemberEntry>();

            var list = new List<MemberEntry>();
            foreach (var m in def.Members)
            {
                if (IsCompilerGenerated(m.Name)) continue;
                list.Add(new MemberEntry(
                    Name: m.Name,
                    Kind: MemberKindText(m),
                    Signature: m.FullName ?? m.Name,
                    MetadataToken: MetadataTokens.GetToken(m.MetadataToken)));
            }
            return list;
        }
    }

    /// <summary>编译器生成的成员名形如 &lt;Foo&gt;b__0 / &lt;Bar&gt;k__BackingField。</summary>
    private static bool IsCompilerGenerated(string name) =>
        name.Length > 0 && name[0] == '<';

    private static string MemberKindText(IMember m) => m switch
    {
        IMethod { IsConstructor: true } => "ctor",
        IMethod { IsDestructor: true } => "dtor",
        IMethod mi => mi.IsStatic ? "static method" : "method",
        IField fi => fi.IsConst ? "const" : fi.IsStatic ? "static field" : "field",
        IProperty => "property",
        IEvent => "event",
        _ => "member",
    };

    // ---------------------------------------------------------------- 反编译

    /// <summary>把一个类型整体反编译为 C# 源码。</summary>
    public string DecompileType(string typeFullName)
    {
        lock (_gate)
        {
            var def = FindType(typeFullName)
                ?? throw new InvalidOperationException($"在程序集里找不到类型 {typeFullName}");
            return Require(_decompiler).DecompileTypeAsString(def.FullTypeName);
        }
    }

    /// <summary>只反编译一个成员（方法 / 属性 / 字段等）。</summary>
    public string DecompileMember(int metadataToken)
    {
        lock (_gate)
        {
            var decompiler = Require(_decompiler);
            var handle = MetadataTokens.EntityHandle(metadataToken);
            return decompiler.Decompile(handle).ToString();
        }
    }

    /// <summary>方法的 IL 反汇编。</summary>
    public string DecompileIl(int metadataToken)
    {
        lock (_gate)
        {
            var module = Require(_module);
            var handle = MetadataTokens.EntityHandle(metadataToken);
            if (handle.Kind != HandleKind.MethodDefinition)
                return "// 该成员没有方法体";

            var methodHandle = (MethodDefinitionHandle)handle;
            var methodDef = module.Metadata.GetMethodDefinition(methodHandle);
            if (methodDef.RelativeVirtualAddress == 0)
                return "// 该成员没有方法体（抽象 / 接口 / 外部实现）";

            var output = new PlainTextOutput();
            var disassembler = new ReflectionDisassembler(output, CancellationToken.None)
            {
                ShowMetadataTokens = true,
                DetectControlStructure = true,
            };
            disassembler.DisassembleMethod(module, methodHandle);
            return output.ToString();
        }
    }

    // ---------------------------------------------------------------- 搜索

    /// <summary>按关键字搜索类型名 / 命名空间 / 成员名。</summary>
    public List<TypeEntry> SearchTypes(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TypeEntry>();
        var q = query.Trim();
        return GetTypes()
            .Where(t => t.FullName.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>按关键字搜索成员，返回 (所属类型, 成员)。</summary>
    public List<(string TypeFullName, MemberEntry Member)> SearchMembers(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<(string, MemberEntry)>();

        var q = query.Trim();
        var hits = new List<(string, MemberEntry)>();

        lock (_gate)
        {
            var ts = Require(_decompiler).TypeSystem;
            foreach (var t in ts.MainModule.TypeDefinitions)
            {
                foreach (var m in t.Members)
                {
                    if (IsCompilerGenerated(m.Name)) continue;
                    if (m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        hits.Add((t.FullName, new MemberEntry(
                            m.Name, MemberKindText(m), m.FullName ?? m.Name,
                            MetadataTokens.GetToken(m.MetadataToken))));
                    }
                }
            }
        }

        return hits.OrderBy(h => h.Item2.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>
    /// 把整个程序集的 C# 源码导出到指定目录：一个顶层类型一个 .cs 文件
    /// （嵌套类型跟着外层一起输出），命名空间展开成子目录，
    /// 目录结构与 dnSpy 的"导出到项目"一致。
    /// </summary>
    /// <param name="progress">回调参数为 (已完成, 总数, 当前类型名)。</param>
    /// <returns>实际写出的文件数。</returns>
    public int ExportAllSources(string outputDir, Action<int, int, string>? progress, CancellationToken ct)
    {
        lock (_gate)
        {
            var decompiler = Require(_decompiler);
            var targets = BuildExportTargets(decompiler);

            Directory.CreateDirectory(outputDir);

            int done = 0;
            foreach (var target in targets)
            {
                // 个别类型反编译失败不该中断整个导出，ExportTypeToFile 内部会兜住
                ExportTypeToFile(decompiler, target, outputDir, ct);

                done++;
                progress?.Invoke(done, targets.Count, target.Type.FullName);
            }

            return done;
        }
    }

    /// <summary>
    /// 编译器生成的名字里有 &lt;&gt; 之类的字符（如 &lt;&gt;f__AnonymousType0）。
    /// Android 本身允许，但导出到电脑上就会炸，所以按最严的 Windows 规则过滤。
    /// </summary>
    private static readonly char[] InvalidNameChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    private static string SafeFileName(string name)
    {
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidNameChars, chars[i]) >= 0) chars[i] = '_';
        }

        var safe = new string(chars).Trim().TrimEnd('.');
        return safe.Length == 0 ? "type" : safe;
    }

    /// <summary>
    /// 一边反编译一边把每个类型交给 <paramref name="write"/> 写出去。
    /// 和整包导出不同，这里不经过任何暂存目录 —— 谁调用谁负责把内容落到最终位置，
    /// 所以用户选的是手机上的目录时，就不用先写私有缓存再整份拷一遍。
    /// </summary>
    /// <param name="write">
    /// 参数为 (相对路径, 反编译好的源码)。相对路径里已经按命名空间展开好子目录，
    /// 直接拿它去建目录、建文件即可。
    /// </param>
    /// <returns>成功交出的类型数。</returns>
    public int ExportAllSources(
        Func<string, string, bool> write,
        Action<int, int, string>? progress,
        CancellationToken ct)
    {
        lock (_gate)
        {
            var decompiler = Require(_decompiler);
            var targets = BuildExportTargets(decompiler);

            int done = 0;
            foreach (var target in targets)
            {
                ct.ThrowIfCancellationRequested();

                string code;
                try
                {
                    code = decompiler.DecompileTypeAsString(target.Type.FullTypeName);
                }
                catch (Exception ex)
                {
                    // 个别类型反编译失败不该中断整个导出，留一行说明继续走
                    code = $"// 反编译失败：{ex.Message}\r\n";
                }

                var rel = Path.Combine(target.RelativeDir, target.FileName);

                // 写失败（目录被删、空间不够……）不该让剩下的全丢，交给上层决定怎么提示
                progress?.Invoke(done, targets.Count, rel);
                if (write(rel, code)) done++;
            }

            return done;
        }
    }

    /// <summary>一个待导出的顶层类型，以及它该落到哪个相对路径上。</summary>
    private sealed record ExportTarget(ITypeDefinition Type, string RelativeDir, string FileName);

    /// <summary>
    /// 整个程序集里所有需要单独出文件的顶层类型。
    /// 嵌套类型不单独算一个（它们跟着外层一起反编译出来），
    /// 所以这里按 DeclaringType 过滤一遍，数量才对得上导出的文件数。
    /// </summary>
    private List<ExportTarget> BuildExportTargets(CSharpDecompiler decompiler)
    {
        var types = decompiler.TypeSystem.MainModule.TypeDefinitions
            .Where(t => t.DeclaringType == null && t.Name != "<Module>")
            .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 同名类型分别落在哪一层目录要先定下来，否则逐个现算会互相覆盖
        var used = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var targets = new List<ExportTarget>(types.Count);

        foreach (var type in types)
        {
            var relDir = type.Namespace.Replace('.', Path.DirectorySeparatorChar);

            if (!used.TryGetValue(relDir, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                used[relDir] = names;
            }

            var candidate = SafeFileName(type.Name);
            var name = candidate;
            int n = 2;
            while (!names.Add(name))
                name = $"{candidate}_{n++}";

            targets.Add(new ExportTarget(type, relDir, name + ".cs"));
        }

        return targets;
    }

    /// <summary>
    /// 反编译一个顶层类型并写进 outputDir，返回写出的完整路径。
    /// 单类型反编译失败不算致命，会写一个只含错误说明的 .cs 继续往下走。
    /// </summary>
    private string ExportTypeToFile(CSharpDecompiler decompiler, ExportTarget target,
        string outputDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var dir = outputDir;
        if (target.RelativeDir.Length > 0)
        {
            dir = Path.Combine(outputDir, target.RelativeDir);
            Directory.CreateDirectory(dir);
        }

        var file = Path.Combine(dir, target.FileName);

        string code;
        try
        {
            code = decompiler.DecompileTypeAsString(target.Type.FullTypeName);
        }
        catch (Exception ex)
        {
            code = $"// 反编译失败：{ex.Message}\r\n";
        }

        File.WriteAllText(file, code, new UTF8Encoding(false));
        return file;
    }

    // ---------------------------------------------------------------- 交叉引用索引

    /// <summary>索引在后台构建，这里只负责起个头；重复调用是安全的。</summary>
    public void EnsureIndex()
    {
        string? path;

        lock (_gate)
        {
            if (_index != null || _indexRunning) return;
            if (_indexError != null) return;   // 已经失败过就不反复重试
            path = FilePath;
            if (path == null) return;

            _indexRunning = true;
            _indexScanned = 0;
        }

        // 这里必须用独立的一份 reader 读文件，不能复用 _module：
        // 用户随时可能打开另一个程序集把当前会话 Dispose 掉，
        // 而 PEReader 一释放，它底下的内存指针就作废了，正在跑的遍历会直接崩。
        var cts = new CancellationTokenSource();
        lock (_gate) _indexCts = cts;

        var file = path!;
        var token = cts.Token;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var built = IlIndex.Build(file, token, n => Volatile.Write(ref _indexScanned, n));
                lock (_gate) _index = built;
            }
            catch (OperationCanceledException)
            {
                // 会话被换掉，正常收尾，不算失败
            }
            catch (Exception ex)
            {
                lock (_gate) _indexError = ex.Message;
            }
            finally
            {
                lock (_gate)
                {
                    _indexRunning = false;
                    _indexCts = null;
                }
                cts.Dispose();
            }
        });
    }

    /// <summary>索引状态的一句话描述，直接给 AI 看。</summary>
    public string GetIndexStatus()
    {
        lock (_gate)
        {
            if (_index != null)
                return $"就绪（{_index.MethodCount} 个方法，{_index.ReferenceCount} 条引用，" +
                       $"{_index.StringCount} 个字符串常量，耗时 {_index.BuildTime.TotalSeconds:F1} 秒）"
                       + (_index.Truncated ? "；程序集过大，索引只覆盖了一部分" : string.Empty);

            if (_indexError != null) return $"构建失败：{_indexError}";
            if (_indexRunning) return $"正在后台构建（已扫描 {Volatile.Read(ref _indexScanned)} 个方法）";
            return "尚未开始构建";
        }
    }

    private IlIndex RequireIndex()
    {
        IlIndex? index;
        lock (_gate) index = _index;

        if (index != null) return index;

        EnsureIndex();
        throw new InvalidOperationException(
            "交叉引用索引还在后台构建中（" + GetIndexStatus() +
            "）。请等几秒再调用一次，不要改用别的办法。");
    }

    /// <summary>查谁引用了某个方法 / 字段 / 类型。</summary>
    public List<ReferenceHit> FindReferences(string query, int limit, out bool truncated) =>
        RequireIndex().FindReferences(query, limit, out truncated);

    /// <summary>搜字符串常量。</summary>
    public List<IlStringHit> SearchStrings(string query, bool caseSensitive, int limit, out bool truncated) =>
        RequireIndex().SearchStrings(query, caseSensitive, limit, out truncated);

    /// <summary>反过来：这个方法里引用了哪些东西。</summary>
    public List<ReferenceHit> GetUsages(int methodToken, int limit, out bool truncated) =>
        RequireIndex().FindUsages(methodToken, limit, out truncated);

    /// <summary>类型全名是否真的存在于当前程序集。</summary>
    public bool HasType(string typeFullName)
    {
        lock (_gate)
        {
            var definition = _decompiler == null ? null : FindType(typeFullName);
            return definition != null;
        }
    }

    /// <summary>
    /// 给交叉引用结果补上「在调用者代码里的第几行」。
    ///
    /// 索引里只有 IL 层面的关系，没有行号 —— 行号是反编译产物才有的东西，
    /// 而反编译一个方法要几十毫秒，所以这里必须封顶：
    /// 命中太多或时间用完就只留 token，让调用方自己决定要不要再读一次。
    /// </summary>
    public void AnnotateLocations(
        List<ReferenceHit> hits, int maxAnnotated, TimeSpan budget,
        Dictionary<int, int> lineNumbers)
    {
        if (hits.Count == 0) return;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;

        // 同一个调用者方法可能命中多次，同一个 token 只反编译一遍
        var cache = new Dictionary<int, string?>();

        lock (_gate)
        {
            var decompiler = _decompiler;
            if (decompiler == null) return;

            foreach (var hit in hits)
            {
                if (done >= maxAnnotated) break;
                if (watched(watch, budget)) break;
                if (lineNumbers.ContainsKey(hit.CallerToken)) { done++; continue; }

                if (!cache.TryGetValue(hit.CallerToken, out var code))
                {
                    try
                    {
                        code = decompiler.Decompile(MetadataTokens.EntityHandle(hit.CallerToken)).ToString();
                    }
                    catch
                    {
                        code = null;
                    }
                    cache[hit.CallerToken] = code;
                }

                done++;

                if (code == null) continue;

                var line = FindLine(code, hit.Target);
                if (line > 0) lineNumbers[hit.CallerToken] = line;
            }
        }

        static bool watched(System.Diagnostics.Stopwatch w, TimeSpan budget) => w.Elapsed > budget;
    }

    /// <summary>
    /// 在反编译出来的源码里找目标成员名第一次出现的行号。
    /// 只按名字匹配，不做语义分析 —— 用来给 AI 一个「大概在哪」的锚点就够了。
    /// </summary>
    private static int FindLine(string code, string target)
    {
        // target 是「类型::成员」或纯类型名，只需要最后那截成员名
        var name = target;
        int sep = target.LastIndexOf("::", StringComparison.Ordinal);
        if (sep >= 0 && sep + 2 < target.Length) name = target.Substring(sep + 2);

        int line = 1;
        int start = 0;

        while (start < code.Length)
        {
            int end = code.IndexOf('\n', start);
            if (end < 0) end = code.Length;

            if (code.IndexOf(name, start, end - start, StringComparison.Ordinal) >= 0)
                return line;

            line++;
            if (end == code.Length) break;
            start = end + 1;
        }

        return 0;
    }

    // ---------------------------------------------------------------- 源码搜索

    /// <summary>在反编译出来的 C# 源码里搜文本（不是成员名，是内容）。</summary>
    /// <param name="timeBudget">时间预算，超了就带着已找到的部分收工。</param>
    public SourceSearchResult SearchSource(
        string query, int maxHits, TimeSpan timeBudget, CancellationToken ct)
    {
        var hits = new List<SourceHit>();
        int scanned = 0;
        bool truncated = false;

        var watch = System.Diagnostics.Stopwatch.StartNew();

        lock (_gate)
        {
            var decompiler = Require(_decompiler);

            // 编译器生成的类型（<>c、<>f__AnonymousType0 之类）整片跳过：
            // 数量多、名字不可读，命中它们对理解代码没有帮助
            var types = decompiler.TypeSystem.MainModule.TypeDefinitions
                .Where(t => t.Name.Length > 0 && t.Name[0] != '<')
                .OrderBy(t => t.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var type in types)
            {
                if (ct.IsCancellationRequested) { truncated = true; break; }
                if (watch.Elapsed > timeBudget) { truncated = true; break; }
                if (hits.Count >= maxHits) { truncated = true; break; }

                scanned++;

                string code;
                try
                {
                    code = decompiler.DecompileTypeAsString(type.FullTypeName);
                }
                catch
                {
                    // 个别类型反编译不出来很正常，跳过继续
                    continue;
                }

                int lineNo = 0;
                int start = 0;
                while (start <= code.Length && hits.Count < maxHits)
                {
                    int end = code.IndexOf('\n', start);
                    if (end < 0) end = code.Length;

                    lineNo++;
                    var line = code.Substring(start, end - start);

                    if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hits.Add(new SourceHit(type.FullName, lineNo, line.Trim()));
                    }

                    if (end == code.Length) break;
                    start = end + 1;
                }
            }

            return new SourceSearchResult(
                Hits: hits,
                ScannedTypes: scanned,
                TotalTypes: types.Count,
                Truncated: truncated);
        }
    }

    // ---------------------------------------------------------------- 继承关系

    /// <summary>查一个类型的上下游：它继承 / 实现了谁，谁又继承 / 实现了它。</summary>
    public TypeHierarchy GetHierarchy(string typeFullName)
    {
        lock (_gate)
        {
            var def = FindType(typeFullName)
                ?? throw new InvalidOperationException($"在程序集里找不到类型 {typeFullName}");

            var bases = new List<string>();
            var interfaces = new List<string>();

            foreach (var bt in def.DirectBaseTypes)
            {
                var name = bt.FullName;
                if (bt.Kind == TypeKind.Interface) interfaces.Add(name);
                else if (name != "System.Object") bases.Add(name);
                else bases.Add(name);
            }

            // 顺着基类一路往上，直到 object 或解析不出来为止
            var chain = new List<string>();
            var seenBase = new HashSet<string>(StringComparer.Ordinal) { def.FullName };
            var cursor = def.DirectBaseTypes.FirstOrDefault(t => t.Kind != TypeKind.Interface);

            for (int depth = 0; cursor != null && depth < 32; depth++)
            {
                var definition = cursor.GetDefinition();
                if (definition == null) { chain.Add(cursor.FullName + "（在别的程序集里）"); break; }
                if (!seenBase.Add(definition.FullName)) break;

                chain.Add(definition.FullName);
                if (definition.FullName == "System.Object") break;

                cursor = definition.DirectBaseTypes.FirstOrDefault(t => t.Kind != TypeKind.Interface);
            }

            // 下游：所有直接把自己挂在它下面的类型
            var derived = new List<string>();
            foreach (var t in Require(_decompiler).TypeSystem.MainModule.TypeDefinitions)
            {
                if (t.FullName == def.FullName) continue;

                foreach (var bt in t.DirectBaseTypes)
                {
                    var target = bt.GetDefinition() ?? bt;
                    if (target.FullName == def.FullName)
                    {
                        derived.Add(t.FullName);
                        break;
                    }
                }
            }

            derived.Sort(StringComparer.OrdinalIgnoreCase);
            interfaces.Sort(StringComparer.OrdinalIgnoreCase);

            return new TypeHierarchy(
                TypeFullName: def.FullName,
                Kind: KindText(def),
                DirectBases: bases,
                Interfaces: interfaces,
                BaseChain: chain,
                DerivedTypes: derived);
        }
    }

    // ---------------------------------------------------------------- 依赖详情

    /// <summary>
    /// 引用的程序集清单，带版本号和公钥 token。
    /// 比 summary 里那张只有名字的列表更能说明「这个 DLL 依赖哪个版本的库」。
    /// </summary>
    public List<AssemblyRefEntry> GetAssemblyReferences()
    {
        lock (_gate)
        {
            var md = Require(_module).Metadata;
            var list = new List<AssemblyRefEntry>();

            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                list.Add(new AssemblyRefEntry(
                    Name: md.GetString(reference.Name),
                    Version: reference.Version.ToString(),
                    Culture: reference.Culture.IsNil ? "neutral" : md.GetString(reference.Culture),
                    PublicKeyToken: TokenText(md, reference.PublicKeyOrToken),
                    IsRetargetable: (reference.Flags & System.Reflection.AssemblyFlags.Retargetable) != 0));
            }

            return list
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    private static string TokenText(System.Reflection.Metadata.MetadataReader md,
        System.Reflection.Metadata.BlobHandle blob)
    {
        if (blob.IsNil) return "无";

        var bytes = md.GetBlobBytes(blob);
        if (bytes.Length == 0) return "无";

        // 公钥 token 在元数据里是完整公钥（或已经是 8 字节 token），
        // 显示时按惯例取最后 8 字节并反转，和 sn.exe / ildasm 看到的写法一致
        var start = Math.Max(0, bytes.Length - 8);
        var sb = new StringBuilder(16);
        for (int i = start; i < bytes.Length; i++)
            sb.Append(bytes[bytes.Length - 1 - (i - start)].ToString("x2"));

        return sb.ToString();
    }

    // ---------------------------------------------------------------- 内部

    /// <summary>
    /// 按全名查找类型定义。必须走这张表，不能直接 new FullTypeName(名字)：
    /// 嵌套类型在 FullTypeName 里是分层保存的，而 new FullTypeName("A.B") 会把它
    /// 解析成"命名空间 A + 类型 B"，于是嵌套类型永远找不到。
    /// </summary>
    private ITypeDefinition? FindType(string typeFullName)
    {
        var index = _typeIndex;
        if (index == null)
        {
            index = new Dictionary<string, ITypeDefinition>(StringComparer.Ordinal);
            foreach (var t in Require(_decompiler).TypeSystem.MainModule.TypeDefinitions)
                index[t.FullName] = t;
            _typeIndex = index;
        }

        return index.TryGetValue(typeFullName, out var def) ? def : null;
    }

    private static T Require<T>(T? value) where T : class =>
        value ?? throw new InvalidOperationException("尚未加载任何程序集");

    public void Dispose()
    {
        lock (_gate)
        {
            _decompiler = null;
            _settings = null;
            _typeIndex = null;

            _index = null;
            _indexError = null;
            _indexScanned = 0;

            // 后台索引任务还开着的话让它尽快收手，免得白跑一趟
            try { _indexCts?.Cancel(); } catch { /* 已经释放 */ }
            _indexCts = null;

            // 解析器自己缓存着它打开过的程序集（PEFile 内部持有 PEReader/FileStream），
            // 只把字段置空的话这些句柄会一直挂到 GC 回收为止 ——
            // 而句柄不松手，往同一个文件上再写就会撞成 IO_SharingViolation_File。
            (_resolver as IDisposable)?.Dispose();
            _resolver = null;

            _module?.Dispose();
            _module = null;

            // 引擎内部还留着大量类型的惰性引用，等 GC 太慢也太不稳，
            // 主动收一次让内存和文件句柄都尽快回落
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }
}
