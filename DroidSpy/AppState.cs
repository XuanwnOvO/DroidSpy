using System.Collections.Generic;
using DroidSpy.Services;

namespace DroidSpy;

/// <summary>
/// 跨 Activity 共享的反编译会话。
/// 反编译器实例较重，全应用复用同一份，避免每次打开页面都重新解析。
/// </summary>
public static class AppState
{
    public static DecompilerService? Decompiler { get; private set; }
    public static AssemblySummary? Summary { get; private set; }
    public static List<TypeEntry>? Types { get; private set; }

    /// <summary>
    /// 本地 MCP 服务。放在这里而不是 Activity 里，是因为它必须比界面活得久：
    /// 用户切到代码页继续看源码时，电脑端的 AI 还在连着。
    /// </summary>
    public static McpServer? Mcp { get; private set; }

    public static bool IsLoaded => Decompiler?.IsLoaded == true;

    /// <summary>
    /// 界面上此刻正在看的那个目标。
    /// MCP 的 current_type 靠它回答「用户现在看的是什么」——电脑问的时候，
    /// 用户很可能已经切到别的页面了，所以它不能只存在 Activity 的字段里。
    /// </summary>
    public sealed record CurrentView(string TypeFullName, string Title, int MetadataToken, bool ShowIl);

    private static object? _currentOwner;

    public static CurrentView? Current { get; private set; }

    /// <summary>登记当前浏览目标。<paramref name="owner"/> 用来判断记录是不是自己的。</summary>
    public static void SetCurrent(object owner, CurrentView view)
    {
        _currentOwner = owner;
        Current = view;
    }

    /// <summary>
    /// 页面关闭时撤销登记。只有记录还属于自己才清 ——
    /// 否则会把紧接着打开的另一个代码页登记的目标一起抹掉。
    /// </summary>
    public static void ClearCurrent(object owner)
    {
        if (!ReferenceEquals(_currentOwner, owner)) return;
        _currentOwner = null;
        Current = null;
    }

    /// <summary>MCP 服务当前是否在监听。</summary>
    public static bool IsMcpRunning => Mcp?.IsRunning == true;

    /// <summary>启停 MCP 服务。启动失败会原样抛出，由界面提示原因。</summary>
    public static string StartMcp(int port)
    {
        if (Mcp == null || Mcp.Port != port)
        {
            Mcp?.Dispose();
            Mcp = new McpServer(port);
        }

        // Start 内部会先 Stop 再重新绑定，所以换端口直接重启即可
        if (!Mcp.IsRunning) Mcp.Start();

        return Mcp.LanUrl ?? Mcp.LocalUrl;
    }

    public static void StopMcp() => Mcp?.Stop();

    /// <summary>载入一个新的程序集（会替换掉旧的会话）。</summary>
    public static void Load(string path)
    {
        Disposer();
        var svc = new DecompilerService();
        svc.Load(path);
        Decompiler = svc;
        Summary = svc.GetSummary();
        Types = svc.GetTypes();
    }

    /// <summary>
    /// 松开当前程序集占用的文件句柄，但保留已经解析出来的概要 / 类型列表。
    ///
    /// 用在「要往旧文件上重新写内容」之前：引擎打开程序集用的是 FileShare.Read，
    /// 这个共享模式在 Android 上由 flock 实现，旧会话只要还活着，同一个路径上的
    /// 写操作就会撞成 IO_SharingViolation_File。而重新打开同一个 DLL、
    /// 或重新解包同一个 UnityFS 包时，落地路径必然和上次一模一样。
    ///
    /// 之所以不直接用 DisposeSession：万一后面解包或解析失败了，
    /// 界面上的旧列表还能照常显示，不会只剩一个空壳。
    /// </summary>
    public static void ReleaseAssembly() => Disposer();

    public static void DisposeSession()
    {
        Disposer();
        Summary = null;
        Types = null;
    }

    private static void Disposer()
    {
        Decompiler?.Dispose();
        Decompiler = null;
    }
}
