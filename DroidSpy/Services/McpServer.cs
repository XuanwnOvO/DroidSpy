using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace DroidSpy.Services;

/// <summary>
/// 跑在手机上的本地 MCP 服务。
///
/// 实现的是 MCP 的 Streamable HTTP 传输：客户端往 http://手机IP:端口/mcp
/// POST 一个 JSON-RPC 2.0 请求，服务端直接用 application/json 回一个响应。
/// 协议允许在 application/json 和 text/event-stream 之间二选一，
/// 这里选前者——不需要维护 SSE 长连接，实现简单且所有客户端都认。
///
/// 手机上没有特权端口的问题：HttpListener 在 .NET for Android 上是纯托管实现，
/// 只要监听 1024 以上的端口即可，不需要 root。
/// </summary>
public sealed class McpServer : IDisposable
{
    private readonly int _port;
    private readonly object _gate = new();

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public McpServer(int port) => _port = port;

    public int Port => _port;

    public bool IsRunning
    {
        get { lock (_gate) return _listener?.IsListening == true; }
    }

    /// <summary>手机自己访问用的地址。</summary>
    public string LocalUrl => $"http://127.0.0.1:{_port}/mcp";

    /// <summary>局域网地址；拿不到（没连 Wi-Fi / 没分到 IP）时返回 null。</summary>
    public string? LanUrl
    {
        get
        {
            var ip = GetLanAddress();
            return ip == null ? null : $"http://{ip}:{_port}/mcp";
        }
    }

    // ------------------------------------------------------------ 生命周期

    /// <summary>启动监听。已在运行时会先停掉再启，方便直接换端口。</summary>
    public void Start()
    {
        Stop();

        // 绑定到所有网卡：手机自己用 127.0.0.1，电脑用局域网 IP 连同一个服务。
        // 前缀必须带尾斜杠，HttpListener 不接受不带路径的写法。
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{_port}/");

        listener.Start();

        var cts = new CancellationTokenSource();
        lock (_gate)
        {
            _listener = listener;
            _cts = cts;
        }

        _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
    }

    public void Stop()
    {
        HttpListener? listener;
        CancellationTokenSource? cts;

        lock (_gate)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
        }

        if (cts == null && listener == null) return;

        try { cts?.Cancel(); } catch { /* 已经释放 */ }
        try { listener?.Stop(); } catch { /* 没在跑 */ }
        try { listener?.Close(); } catch { /* 没在跑 */ }
        try { cts?.Dispose(); } catch { /* 已经释放 */ }
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                // Stop() 会让阻塞中的 GetContext 抛异常，这是正常的收尾路径
                break;
            }

            // 每个请求单独跑，慢请求（反编译大类型）不该堵住后面的连接
            _ = Task.Run(() => HandleAsync(ctx), CancellationToken.None);
        }
    }

    // ------------------------------------------------------------ 请求处理

    private static async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var req = ctx.Request;
            var path = req.Url?.AbsolutePath ?? "/";

            // 只对外提供一个端点，其余路径给个明确的 404，免得客户端连错了还以为通了
            if (!path.TrimEnd('/').EndsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            {
                await WriteTextAsync(ctx, 404, "text/plain; charset=utf-8",
                    "DroidSpy MCP: 请把请求发到 /mcp").ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "GET")
            {
                // 有些客户端会先 GET 探活。Streamable HTTP 要求服务端在不支持
                // 服务端推送流时回 405，客户端据此回落到纯 POST 模式。
                ctx.Response.AddHeader("Allow", "POST, DELETE");
                await WriteTextAsync(ctx, 405, "text/plain; charset=utf-8",
                    "DroidSpy MCP: 请使用 POST 发送 JSON-RPC").ConfigureAwait(false);
                return;
            }

            if (req.HttpMethod == "DELETE")
            {
                // 会话结束通知，本地服务没有会话状态，回 204 即可
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            if (req.HttpMethod != "POST")
            {
                await WriteTextAsync(ctx, 405, "text/plain; charset=utf-8", "只支持 POST").ConfigureAwait(false);
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync().ConfigureAwait(false);

            var response = McpProtocol.Handle(body);

            // 通知类消息（notifications/*）没有 id，也就没有响应体
            if (response == null)
            {
                ctx.Response.StatusCode = 202;
                ctx.Response.Close();
                return;
            }

            await WriteTextAsync(ctx, 200, "application/json; charset=utf-8", response).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await WriteTextAsync(ctx, 500, "text/plain; charset=utf-8", "internal error")
                    .ConfigureAwait(false);
            }
            catch { /* 连接已经断了 */ }
        }
    }

    private static async Task WriteTextAsync(HttpListenerContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = contentType;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    /// <summary>取本机在局域网里的 IPv4 地址（Wi-Fi 地址），拿不到返回 null。</summary>
    public static string? GetLanAddress()
    {
        try
        {
            // 连一个外网地址让系统挑出默认出口网卡：不实际发包，
            // 只是借路由表拿到本机在这一侧的地址，比遍历网卡靠谱得多。
            using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            probe.Connect("8.8.8.8", 65530);
            return (probe.LocalEndPoint as IPEndPoint)?.Address.ToString();
        }
        catch
        {
            // 没网络时退回到枚举网卡，挑一个非回环的私有地址
            try
            {
                return Dns.GetHostAddresses(Dns.GetHostName())
                    .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork
                                         && !IPAddress.IsLoopback(a))
                    ?.ToString();
            }
            catch
            {
                return null;
            }
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// JSON-RPC 2.0 之上的 MCP 协议编排：把请求分发到各个工具，
/// 再按 MCP 的 tools/call 结果格式（content 数组）包装返回值。
/// </summary>
internal static class McpProtocol
{
    private const string ProtocolVersion = "2025-06-18";

    public static string? Handle(string body)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            return Error(null, -32700, $"JSON 解析失败：{ex.Message}");
        }

        if (root is not JsonObject req)
            return Error(null, -32600, "请求必须是 JSON 对象");

        var id = req["id"];
        var method = req["method"]?.GetValue<string>();

        if (string.IsNullOrEmpty(method))
            return Error(id, -32600, "缺少 method");

        // 通知没有 id，处理完不回包
        var isNotification = id == null;

        try
        {
            var result = method switch
            {
                "initialize" => Initialize(),
                "ping" => new JsonObject(),
                "tools/list" => new JsonObject { ["tools"] = ToolDefinitions() },
                "tools/call" => CallTool(req["params"] as JsonObject),
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                _ => null,
            };

            if (isNotification) return null;

            if (result == null)
                return Error(id, -32601, $"不支持的方法：{method}");

            return new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id!.DeepClone(),
                ["result"] = result,
            }.ToJsonString();
        }
        catch (Exception ex)
        {
            if (isNotification) return null;
            // 工具内部的异常按协议回进 content，让 AI 能看到完整原因并自行纠偏
            return Error(id, -32603, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JsonObject Initialize() => new()
    {
        ["protocolVersion"] = ProtocolVersion,
        ["capabilities"] = new JsonObject
        {
            ["tools"] = new JsonObject { ["listChanged"] = false },
        },
        ["serverInfo"] = new JsonObject
        {
            ["name"] = "droidspy",
            ["title"] = "DroidSpy",
            ["version"] = "1.0",
        },
        ["instructions"] =
            "DroidSpy 是运行在安卓手机上的 .NET 反编译器。这里的每个工具都直接读取" +
            "手机上已经打开的那个程序集。建议先用 assembly_info 了解程序集，" +
            "再用 list_types / search_members 定位，然后 decompile_type 读整个类的源码、" +
            "type_members 看类里有哪些成员。用户提到「当前这个类」或「我正在看的」时，" +
            "直接用 current_type 拿界面上的内容，不要猜类型名。" +
            "要找「谁调用了它」用 find_references，「这个方法用了谁」用 find_usages（用 token 传参），" +
            "「哪行代码写了什么」用 search_source，" +
            "「谁继承 / 实现了它」用 type_hierarchy，找字符串常量用 search_strings。" +
            "这几个工具在第一次调用时需要在后台构建交叉引用索引，" +
            "如果返回「索引还在构建中」，等几秒重试同一个调用即可，不要换别的方式绕过。" +
            "如果分析出了「某个混淆名字其实是干什么的」这类结论，" +
            "先 save_note 记下来，再用 set_rename 给它登记一个可读别名 —— " +
            "登记后所有工具输出都会自动用别名显示，读起来会顺畅很多。",
    };

    // ------------------------------------------------------------ 工具定义

    private static JsonArray ToolDefinitions()
    {
        static JsonObject Str(string name, string desc, bool required = false) => new()
        {
            ["type"] = "string",
            ["description"] = desc,
        };

        JsonObject Tool(string name, string desc, JsonObject props, params string[] required)
        {
            var req = new JsonArray();
            foreach (var r in required) req.Add(r);

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = props,
            };
            if (req.Count > 0) schema["required"] = req;

            return new JsonObject
            {
                ["name"] = name,
                ["description"] = desc,
                ["inputSchema"] = schema,
            };
        }

        return new JsonArray
        {
            Tool("assembly_info",
                "读取当前已加载程序集的概要信息：文件名、大小、目标框架、" +
                "类型/方法/字段数量、引用的程序集列表。",
                new JsonObject()),

            Tool("list_types",
                "列出程序集里的类型。可按关键字过滤（匹配类型全名，不区分大小写）。" +
                "混淆过的程序集里通常有大量编译器生成的怪名字，" +
                "用 skip_compiler_generated 和 public_only 能把它们滤掉。",
                new JsonObject
                {
                    ["query"] = Str("query", "过滤关键字，留空则列出全部"),
                    ["kind"] = Str("kind", "只要某一种：class / struct / interface / enum / delegate"),
                    ["namespace"] = Str("namespace", "只列某个命名空间下的类型（前缀匹配）"),
                    ["public_only"] = Str("public_only", "true 表示只看公开类型，默认 false"),
                    ["skip_compiler_generated"] = Str("skip_compiler_generated",
                        "true 表示跳过编译器生成的类型（名字以 < 开头），默认 false"),
                    ["limit"] = Str("limit", "最多返回多少条，默认 200"),
                }),

            Tool("search_members",
                "按关键字搜索成员（方法 / 字段 / 属性 / 事件），不区分大小写。",
                new JsonObject
                {
                    ["query"] = Str("query", "成员名关键字", required: true),
                    ["limit"] = Str("limit", "最多返回多少条，默认 100"),
                },
                "query"),

            Tool("decompile_type",
                "把一个类型完整反编译成 C# 源码。type_name 必须是 list_types 给出的全名。" +
                "大类型会分页返回：默认一次给 1000 行，头部会写明总行数和续读用的 offset。",
                new JsonObject
                {
                    ["type_name"] = Str("type_name", "类型的全名，如 UnityEngine.UI.Button", required: true),
                    ["offset"] = Str("offset", "从第几行开始返回（从 0 算），用于续读，默认 0"),
                    ["limit"] = Str("limit", "本次最多返回多少行，默认 1000；填 0 表示不限"),
                },
                "type_name"),

            Tool("type_members",
                "列出一个类型里有哪些成员（方法 / 字段 / 属性 / 事件 / 构造函数），" +
                "每条都带 metadata_token，可以接着用 decompile_member 或 get_il 细看。" +
                "想要整个类的源码全文，用 decompile_type。",
                new JsonObject
                {
                    ["type_name"] = Str("type_name", "类型的全名，如 UnityEngine.UI.Button", required: true),
                },
                "type_name"),

            Tool("current_type",
                "读取手机上 DroidSpy 界面此刻正在显示的内容，也就是用户正看着的那个类型或成员的完整源码。" +
                "用户说「这个类」「当前这个」「我正在看的」时用它，不要去猜类型名。" +
                "它返回的是最新反编译的结果，和界面上看到的一致。大内容会分页，规则同 decompile_type。",
                new JsonObject
                {
                    ["offset"] = Str("offset", "从第几行开始返回（从 0 算），用于续读，默认 0"),
                    ["limit"] = Str("limit", "本次最多返回多少行，默认 1000；填 0 表示不限"),
                }),

            Tool("decompile_member",
                "反编译单个成员（方法 / 属性等）为 C#。需要 list_types 或 " +
                "search_members 返回的 metadata_token。大方法会分页，规则同 decompile_type。",
                new JsonObject
                {
                    ["metadata_token"] = Str("metadata_token", "成员的元数据 token（十进制整数）", required: true),
                    ["offset"] = Str("offset", "从第几行开始返回（从 0 算），用于续读，默认 0"),
                    ["limit"] = Str("limit", "本次最多返回多少行，默认 1000；填 0 表示不限"),
                },
                "metadata_token"),

            Tool("get_il",
                "读取方法的 IL 反汇编结果。需要方法的元数据 token。大方法会分页，" +
                "规则同 decompile_type。",
                new JsonObject
                {
                    ["metadata_token"] = Str("metadata_token", "方法的元数据 token（十进制整数）", required: true),
                    ["offset"] = Str("offset", "从第几行开始返回（从 0 算），用于续读，默认 0"),
                    ["limit"] = Str("limit", "本次最多返回多少行，默认 1000；填 0 表示不限"),
                },
                "metadata_token"),

            Tool("search_source",
                "在反编译出来的 C# 源码里搜文本内容 —— 搜的是代码本身，不是类型名或成员名。" +
                "想找「哪里用了 PlayerPrefs」「哪行判断了 level > 5」这类问题就用它。" +
                "会跳过编译器自动生成的类型。返回「类型名:行号: 该行内容」。",
                new JsonObject
                {
                    ["query"] = Str("query", "要搜的代码文本，不区分大小写", required: true),
                    ["max_hits"] = Str("max_hits", "最多返回多少条命中，默认 50"),
                },
                "query"),

            Tool("find_references",
                "查交叉引用：谁调用了这个方法 / 谁读写了这个字段 / 谁用了这个类型。" +
                "query 可以只写成员名（如 Update，会匹配所有同名成员），" +
                "也可以写「类型::成员」精确到某一个。返回调用它的方法名、token，" +
                "能定位时还会给出在调用者代码里的行号。" +
                "想知道反过来的关系（这个方法用了谁），用 find_usages。",
                new JsonObject
                {
                    ["query"] = Str("query", "成员名或「类型::成员」，不区分大小写", required: true),
                    ["limit"] = Str("limit", "最多返回多少条，默认 100"),
                    ["with_lines"] = Str("with_lines",
                        "true 表示额外反编译调用者来标注行号，默认 true；" +
                        "命中很多时关掉可以更快拿到结果"),
                },
                "query"),

            Tool("find_usages",
                "反过来的交叉引用：这个方法的代码里用了哪些方法 / 字段 / 类型。" +
                "适合顺着一条调用链往下追（A 调用了 B，B 又用了什么）。" +
                "需要方法的 metadata_token，可以先用 type_members 拿到。",
                new JsonObject
                {
                    ["metadata_token"] = Str("metadata_token", "方法的元数据 token（十进制整数）", required: true),
                    ["limit"] = Str("limit", "最多返回多少条，默认 100"),
                },
                "metadata_token"),

            Tool("search_strings",
                "在程序集的字符串常量里搜（ILDASM 里的 ldstr），常用于找 URL、密钥、" +
                "报错文案、AssetBundle 名字这类线索。按出现次数从多到少返回，" +
                "并给出第一次使用它的方法。",
                new JsonObject
                {
                    ["query"] = Str("query", "要搜的字符串片段", required: true),
                    ["case_sensitive"] = Str("case_sensitive", "true 表示区分大小写，默认 false"),
                    ["limit"] = Str("limit", "最多返回多少条，默认 50"),
                },
                "query"),

            Tool("type_hierarchy",
                "查一个类型的继承关系：它继承了什么、实现了哪些接口（往上），" +
                "以及程序集里谁继承或实现了它（往下）。想知道「谁是 MonoBehaviour 的子类」" +
                "「这个接口有哪些实现」就用它。",
                new JsonObject
                {
                    ["type_name"] = Str("type_name", "类型的全名", required: true),
                },
                "type_name"),

            Tool("assembly_references",
                "列出这个程序集依赖的所有外部程序集，带版本号、区域和公钥 token。" +
                "比 assembly_info 里那份只有名字的清单更适合判断依赖版本。",
                new JsonObject()),

            Tool("save_note",
                "把一条分析结论记下来（比如「AAAFBKBHAKM 是网络管理器」），" +
                "下次连上来还能读到。key 相同的会覆盖，所以用同一把 key 更新同一件事。",
                new JsonObject
                {
                    ["key"] = Str("key", "笔记的标识，如「类型:AAAFBKBHAKM」", required: true),
                    ["text"] = Str("text", "笔记内容", required: true),
                },
                "key", "text"),

            Tool("list_notes",
                "读出之前记下的所有分析结论。",
                new JsonObject()),

            Tool("delete_note",
                "删掉一条笔记。",
                new JsonObject
                {
                    ["key"] = Str("key", "要删掉的笔记标识", required: true),
                },
                "key"),

            Tool("set_rename",
                "给混淆的名字起一个可读的别名。设完之后，所有工具的输出里都会自动用别名显示" +
                "（比如把 AAAFBKBHAKM 显示成 NetworkManager），token 和程序集本身不变。" +
                "之后把别名传给别的工具时会自动换回原名，所以调用链是通的。" +
                "这是纯显示层替换，不会修改 DLL。",
                new JsonObject
                {
                    ["original"] = Str("original", "程序集里的原始名字（类型全名或成员全名）", required: true),
                    ["alias"] = Str("alias", "要显示成什么", required: true),
                },
                "original", "alias"),

            Tool("list_renames",
                "列出所有已登记的别名。",
                new JsonObject()),

            Tool("clear_rename",
                "删掉一条别名，之后恢复显示原始名字。",
                new JsonObject
                {
                    ["original"] = Str("original", "要删除的原始名字", required: true),
                },
                "original"),
        };
    }

    // ------------------------------------------------------------ 工具实现

    private static JsonObject CallTool(JsonObject? p)
    {
        var name = p?["name"]?.GetValue<string>();
        var args = p?["arguments"] as JsonObject ?? new JsonObject();

        try
        {
            var text = name switch
            {
                "assembly_info" => AssemblyInfo(),
                "list_types" => ListTypes(args),
                "search_members" => SearchMembers(args),
                "decompile_type" => DecompileType(args),
                "type_members" => TypeMembers(args),
                "current_type" => CurrentType(args),
                "decompile_member" => DecompileMember(args),
                "get_il" => GetIl(args),
                "search_source" => SearchSource(args),
                "find_references" => FindReferences(args),
                "find_usages" => FindUsages(args),
                "search_strings" => SearchStrings(args),
                "type_hierarchy" => TypeHierarchy(args),
                "assembly_references" => AssemblyReferences(),
                "save_note" => SaveNote(args),
                "list_notes" => ListNotes(),
                "delete_note" => DeleteNote(args),
                "set_rename" => SetRename(args),
                "list_renames" => ListRenames(),
                "clear_rename" => ClearRename(args),
                _ => throw new UnknownToolException(name),
            };

            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    // 出站时统一过一遍别名表：设过 set_rename 的混淆名字，
                    // 在这里全部换成可读名字。放在这一个出口做，
                    // 就不用每个工具各自记得替换一次。
                    new JsonObject { ["type"] = "text", ["text"] = RenameStore.Apply(text) },
                },
                ["isError"] = false,
            };
        }
        catch (UnknownToolException)
        {
            // 工具名都不认识，属于协议层面的错误，应该走 JSON-RPC error
            throw;
        }
        catch (Exception ex)
        {
            // 工具跑失败（没打开程序集、类型名写错、token 不存在……）按 MCP 规范
            // 回成 isError 的正常结果，而不是 JSON-RPC error：
            // 前者会作为工具输出交给模型，模型能看到原因并自己纠正参数重试；
            // 后者会让客户端直接判定这次调用失败，很多时候就此中断整个会话了。
            return new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"调用失败：{ex.Message}",
                    },
                },
                ["isError"] = true,
            };
        }
    }

    private sealed class UnknownToolException : Exception
    {
        public UnknownToolException(string? name) : base($"未知的工具：{name}") { }
    }

    private static DecompilerService RequireAssembly()
    {
        var svc = AppState.Decompiler;
        if (svc?.IsLoaded != true)
            throw new InvalidOperationException(
                "DroidSpy 里还没有打开程序集。请先在手机上打开一个 .NET DLL，再重试。");
        return svc;
    }

    private static string AssemblyInfo()
    {
        var svc = RequireAssembly();
        var s = AppState.Summary ?? svc.GetSummary();

        var sb = new StringBuilder();
        sb.AppendLine($"文件名：{s.FileName}");
        sb.AppendLine($"路径：{s.FilePath}");
        sb.AppendLine($"大小：{s.FileSize / 1024.0 / 1024.0:F2} MB");
        sb.AppendLine($"运行时版本：{s.RuntimeVersion}");
        sb.AppendLine($"目标框架：{s.TargetFramework}");
        sb.AppendLine($"签名：{(s.IsSigned ? "有强名称签名" : "无签名")}");
        sb.AppendLine($"类型数：{s.TypeCount}");
        sb.AppendLine($"方法数：{s.MethodCount}");
        sb.AppendLine($"字段数：{s.FieldCount}");
        sb.AppendLine($"引用的程序集（{s.ReferenceCount}）：");

        foreach (var r in s.ReferencedAssemblies)
            sb.AppendLine($"  - {r}");

        return sb.ToString();
    }

    private static int ReadLimit(JsonObject args, int fallback)
    {
        var raw = args["limit"]?.ToString();
        if (int.TryParse(raw, out var n) && n > 0) return n;
        return fallback;
    }

    private static string ListTypes(JsonObject args)
    {
        var svc = RequireAssembly();
        var query = args["query"]?.ToString();
        var limit = ReadLimit(args, 200);
        var kind = args["kind"]?.ToString();
        var ns = args["namespace"]?.ToString();
        var publicOnly = ReadBool(args, "public_only");
        var skipCompilerGenerated = ReadBool(args, "skip_compiler_generated");

        var all = string.IsNullOrWhiteSpace(query)
            ? svc.GetTypes()
            : svc.SearchTypes(query);

        if (!string.IsNullOrWhiteSpace(kind))
            all = all.Where(t => string.Equals(t.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrWhiteSpace(ns))
        {
            var prefix = ns.Trim();
            all = all.Where(t => string.Equals(t.Namespace, prefix, StringComparison.Ordinal)
                                 || (t.Namespace?.StartsWith(prefix + ".", StringComparison.Ordinal) ?? false)).ToList();
        }

        if (publicOnly)
            all = all.Where(t => t.IsPublic).ToList();

        if (skipCompilerGenerated)
            all = all.Where(t => !t.IsCompilerGenerated).ToList();

        if (all.Count == 0)
            return string.IsNullOrWhiteSpace(query)
                ? "没有符合过滤条件的类型。"
                : $"没有匹配「{query}」的类型。";

        var sb = new StringBuilder();
        sb.AppendLine($"共 {all.Count} 个类型" +
                      (all.Count > limit ? $"，下面只列出前 {limit} 个" : string.Empty) + "：");
        sb.AppendLine();

        foreach (var t in all.Take(limit))
            sb.AppendLine($"{t.FullName}  [{t.Kind}]");

        return sb.ToString();
    }

    private static string SearchMembers(JsonObject args)
    {
        var svc = RequireAssembly();
        var query = args["query"]?.ToString() ?? string.Empty;
        var limit = ReadLimit(args, 100);

        var hits = svc.SearchMembers(query);
        if (hits.Count == 0) return $"没有匹配「{query}」的成员。";

        var sb = new StringBuilder();
        sb.AppendLine($"共 {hits.Count} 个成员" +
                      (hits.Count > limit ? $"，下面只列出前 {limit} 个" : string.Empty) + "：");
        sb.AppendLine();

        foreach (var (typeFullName, m) in hits.Take(limit))
            sb.AppendLine($"{typeFullName}::{m.Name}  [{m.Kind}] token={m.MetadataToken}");

        return sb.ToString();
    }

    private static string ReadTypeName(JsonObject args)
    {
        var typeName = args["type_name"]?.ToString();
        if (string.IsNullOrWhiteSpace(typeName))
            throw new InvalidOperationException("缺少参数 type_name");

        typeName = typeName.Trim();

        // 调用方可能是拿着别名（NetworkManager）来问的，这里换回原名。
        // 真名优先：程序集里真有同名类型时不会被别名劫持。
        var svc = AppState.Decompiler;
        if (svc != null && !svc.HasType(typeName))
            typeName = RenameStore.Resolve(typeName, svc.HasType);

        return typeName;
    }

    private static string DecompileType(JsonObject args)
    {
        var svc = RequireAssembly();
        var typeName = ReadTypeName(args);
        var code = svc.DecompileType(typeName);
        return Paged($"类型 {typeName} 的反编译结果", code, args);
    }

    private static string TypeMembers(JsonObject args)
    {
        var svc = RequireAssembly();
        var typeName = ReadTypeName(args);

        var members = svc.GetMembers(typeName);
        if (members.Count == 0)
        {
            return $"{typeName} 里没有可列出的成员。\n" +
                   "要么这个类型确实没有成员（枚举、空接口、标记类型），" +
                   "要么名字不对 —— 用 list_types 或 search_members 核对一下全名。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{typeName} 共 {members.Count} 个成员：");
        sb.AppendLine();

        foreach (var m in members)
            sb.AppendLine($"{m.Kind,-8} {m.Signature}  token={m.MetadataToken}");

        sb.AppendLine();
        sb.AppendLine("用 decompile_member 读某个成员的源码，用 get_il 看它的 IL。");
        return sb.ToString();
    }

    /// <summary>
    /// 界面当前正在看的内容。这里重新反编译一遍而不是去读界面上的文本：
    /// 界面那边可能还在渲染中，或者是上一轮切换留下的旧内容，
    /// 重新算一遍才能保证交给 AI 的和用户看到的是同一份。
    /// </summary>
    private static string CurrentType(JsonObject args)
    {
        var svc = RequireAssembly();

        var view = AppState.Current
                   ?? throw new InvalidOperationException(
                       "手机上现在没有打开任何类。请让用户在 DroidSpy 里点开一个类，" +
                       "或者改用 list_types / decompile_type 按名字读取。");

        string source;
        string what;

        if (view.ShowIl)
        {
            source = svc.DecompileIl(view.MetadataToken);
            what = $"IL 反汇编 · {view.Title}";
        }
        else if (view.MetadataToken != 0)
        {
            source = svc.DecompileMember(view.MetadataToken);
            what = $"成员 {view.Title}";
        }
        else
        {
            source = svc.DecompileType(view.TypeFullName);
            what = view.TypeFullName;
        }

        var header = new StringBuilder();
        header.AppendLine($"当前界面正在显示：{what}");
        header.AppendLine($"所属类型：{view.TypeFullName}");

        return Paged(header.ToString().TrimEnd(), source, args);
    }

    private static int CountLines(string text)
    {
        // 空内容也算一行，和界面上的行号列保持一致
        int n = 1;
        foreach (var c in text)
            if (c == '\n') n++;
        return n;
    }

    // ------------------------------------------------------------ 分页

    /// <summary>单次返回的默认行数上限。</summary>
    private const int DefaultPageLines = 1000;

    private static int ReadInt(JsonObject args, string key, int fallback)
    {
        var raw = args[key]?.ToString();
        return int.TryParse(raw, out var n) ? n : fallback;
    }

    /// <summary>
    /// 按 offset / limit 切一段出来。
    ///
    /// 一个几万行的大类整个塞回给客户端，轻则把模型的上下文挤爆，
    /// 重则让客户端直接报错断掉这次会话 —— 所以内容类工具一律分页，
    /// 并在头部写清楚总行数和下一次该用哪个 offset，让模型自己续读。
    /// limit 传 0 表示不限，需要整份内容时可以用。
    /// </summary>
    private static string Paged(string header, string body, JsonObject args)
    {
        var offset = ReadInt(args, "offset", 0);
        var limit = ReadInt(args, "limit", DefaultPageLines);

        if (offset < 0) offset = 0;
        if (limit <= 0) limit = int.MaxValue;

        var lines = body.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var total = lines.Length;

        var sb = new StringBuilder();
        sb.AppendLine(header);

        if (offset >= total)
        {
            sb.AppendLine($"共 {total} 行，offset={offset} 已经超出末尾。");
            return sb.ToString();
        }

        var take = Math.Min(limit, total - offset);
        var tail = offset + take < total
            ? $"（还有 {total - offset - take} 行，用 offset={offset + take} 继续读）"
            : "（已到末尾）";

        sb.AppendLine($"共 {total} 行，本次返回第 {offset + 1}-{offset + take} 行{tail}");
        sb.AppendLine();

        for (int i = 0; i < take; i++)
            sb.AppendLine(lines[offset + i]);

        return sb.ToString();
    }

    private static int ReadToken(JsonObject args)
    {
        var raw = args["metadata_token"]?.ToString();
        if (!int.TryParse(raw, out var token))
            throw new InvalidOperationException($"metadata_token 不是合法整数：{raw}");
        return token;
    }

    /// <summary>读一个布尔开关，缺省或读不出来时用 fallback。</summary>
    private static bool ReadBool(JsonObject args, string key, bool fallback = false)
    {
        var raw = args[key]?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return string.Equals(raw.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }

    private static string DecompileMember(JsonObject args)
    {
        var svc = RequireAssembly();
        var token = ReadToken(args);
        var code = svc.DecompileMember(token);
        return Paged($"token {token} 的反编译结果", code, args);
    }

    private static string GetIl(JsonObject args)
    {
        var svc = RequireAssembly();
        var token = ReadToken(args);
        var code = svc.DecompileIl(token);
        return Paged($"token {token} 的 IL", code, args);
    }

    // ------------------------------------------------------------ 新工具

    private static string SearchSource(JsonObject args)
    {
        var svc = RequireAssembly();
        var query = ReadRequired(args, "query");
        var maxHits = ReadInt(args, "max_hits", 50);
        if (maxHits <= 0) maxHits = 50;

        // 时间预算兜底：整包源码可能有几十万行，不设上限的话
        // 客户端早就超时了，而工具还在这里跑
        var result = svc.SearchSource(query, maxHits, TimeSpan.FromSeconds(20), CancellationToken.None);

        if (result.Hits.Count == 0)
        {
            return result.Truncated
                ? $"搜完 {result.ScannedTypes}/{result.TotalTypes} 个类型（时间用完提前收手），没有找到「{query}」。"
                : $"在 {result.TotalTypes} 个类型的源码里没有找到「{query}」。";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"在 {result.ScannedTypes}/{result.TotalTypes} 个类型里找到 {result.Hits.Count} 处「{query}」：");
        if (result.Truncated)
            sb.AppendLine("（未搜完：命中了 max_hits 上限或时间用完，符合条件的可能还有更多）");
        sb.AppendLine();

        foreach (var hit in result.Hits)
            sb.AppendLine($"{hit.TypeFullName}:{hit.Line}: {hit.Text}");

        return sb.ToString();
    }

    private static string FindReferences(JsonObject args)
    {
        var svc = RequireAssembly();
        var query = ReadRequired(args, "query");
        var limit = ReadLimit(args, 100);
        var withLines = ReadBool(args, "with_lines", true);

        var hits = svc.FindReferences(query, limit, out var truncated);

        if (hits.Count == 0)
            return $"没有任何地方引用「{query}」。可能是名字不对，" +
                   "也可能是它只在程序集外部被使用（比如被 Unity 引擎回调）。";

        var lineNumbers = new Dictionary<int, int>();
        if (withLines)
            svc.AnnotateLocations(hits, 30, TimeSpan.FromSeconds(8), lineNumbers);

        var sb = new StringBuilder();
        sb.AppendLine($"共找到 {hits.Count} 处对「{query}」的引用" +
                      (truncated ? "（已达到返回上限，实际更多）" : string.Empty) + "：");
        sb.AppendLine();

        foreach (var hit in hits)
        {
            var at = lineNumbers.TryGetValue(hit.CallerToken, out var line) ? $"  行 {line}" : string.Empty;
            sb.AppendLine($"{KindText(hit.Kind)}  {hit.Target}\n    ← {hit.CallerType}::{hit.CallerMethod}  token={hit.CallerToken}{at}");
        }

        sb.AppendLine();
        sb.AppendLine("用 decompile_member（传 token）可以看调用处的完整代码。");
        sb.AppendLine("想知道反过来的关系（这个方法用了谁），用 find_usages。");
        return sb.ToString();
    }

    private static string FindUsages(JsonObject args)
    {
        var svc = RequireAssembly();
        var token = ReadToken(args);
        var limit = ReadLimit(args, 100);

        var hits = svc.GetUsages(token, limit, out var truncated);

        if (hits.Count == 0)
            return $"token={token} 的这个方法没有引用任何东西" +
                   "（可能是空方法，或者索引里没覆盖到）。";

        var sb = new StringBuilder();
        sb.AppendLine($"这个方法引用了 {hits.Count} 个目标" +
                      (truncated ? "（已达到返回上限，实际更多）" : string.Empty) + "：");
        sb.AppendLine();

        foreach (var hit in hits)
            sb.AppendLine($"{KindText(hit.Kind)}  {hit.Target}");

        sb.AppendLine();
        sb.AppendLine("其中某个目标如果是方法，可以用 find_references 查它还被谁用过。");
        return sb.ToString();
    }

    private static string KindText(ReferenceKind kind) => kind switch
    {
        ReferenceKind.Call => "[调用]  ",
        ReferenceKind.Create => "[实例化]",
        ReferenceKind.Read => "[读字段]",
        ReferenceKind.Write => "[写字段]",
        ReferenceKind.TypeUse => "[用类型]",
        _ => "[引用]  ",
    };

    private static string SearchStrings(JsonObject args)
    {
        var svc = RequireAssembly();
        var query = ReadRequired(args, "query");
        var caseSensitive = string.Equals(args["case_sensitive"]?.ToString(), "true",
            StringComparison.OrdinalIgnoreCase);
        var limit = ReadLimit(args, 50);

        var hits = svc.SearchStrings(query, caseSensitive, limit, out var truncated);

        if (hits.Count == 0)
            return $"字符串常量里没有包含「{query}」的条目。";

        var sb = new StringBuilder();
        sb.AppendLine($"找到 {hits.Count} 个匹配「{query}」的字符串常量" +
                      (truncated ? "（已达上限，可能还有更多）" : string.Empty) + "：");
        sb.AppendLine();

        foreach (var hit in hits)
        {
            var text = hit.Value.Length > 200 ? hit.Value.Substring(0, 200) + "…" : hit.Value;
            sb.AppendLine($"「{text}」  出现 {hit.Count} 次");
            sb.AppendLine($"    ← {hit.CallerType}::{hit.CallerMethod}  token={hit.CallerToken}");
        }

        return sb.ToString();
    }

    private static string TypeHierarchy(JsonObject args)
    {
        var svc = RequireAssembly();
        var typeName = ReadTypeName(args);
        var hierarchy = svc.GetHierarchy(typeName);

        var sb = new StringBuilder();
        sb.AppendLine($"{hierarchy.TypeFullName}  [{hierarchy.Kind}]");
        sb.AppendLine();

        sb.AppendLine("直接基类：" + (hierarchy.DirectBases.Count == 0
            ? "（无，接口或 object）"
            : string.Join(", ", hierarchy.DirectBases)));

        sb.AppendLine("实现的接口：" + (hierarchy.Interfaces.Count == 0
            ? "（无）"
            : string.Join(", ", hierarchy.Interfaces)));

        sb.AppendLine();
        if (hierarchy.BaseChain.Count == 0)
        {
            sb.AppendLine("基类链：（没有可解析的基类）");
        }
        else
        {
            sb.AppendLine("基类链（由近及远）：");
            foreach (var b in hierarchy.BaseChain) sb.AppendLine($"  ↑ {b}");
        }

        sb.AppendLine();
        if (hierarchy.DerivedTypes.Count == 0)
        {
            sb.AppendLine("没有类型继承或实现它。");
        }
        else
        {
            sb.AppendLine($"程序集里有 {hierarchy.DerivedTypes.Count} 个类型直接继承 / 实现了它：");
            foreach (var d in hierarchy.DerivedTypes) sb.AppendLine($"  ↓ {d}");
        }

        return sb.ToString();
    }

    private static string AssemblyReferences()
    {
        var svc = RequireAssembly();
        var refs = svc.GetAssemblyReferences();

        var sb = new StringBuilder();
        sb.AppendLine($"共依赖 {refs.Count} 个外部程序集：");
        sb.AppendLine();

        foreach (var r in refs)
        {
            sb.AppendLine($"{r.Name}");
            sb.AppendLine($"    版本 {r.Version}  culture={r.Culture}  publicKeyToken={r.PublicKeyToken}" +
                          (r.IsRetargetable ? "  [retargetable]" : string.Empty));
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------ 笔记

    private static string SaveNote(JsonObject args)
    {
        var key = ReadRequired(args, "key");
        var text = ReadRequired(args, "text");

        var replaced = NoteStore.Save(key, text);
        return replaced
            ? $"已更新笔记「{key}」。"
            : $"已记录笔记「{key}」。";
    }

    private static string ListNotes()
    {
        var notes = NoteStore.List();
        if (notes.Count == 0)
            return "还没有任何笔记。分析出结论时可以用 save_note 记一条，下次还能读到。";

        var sb = new StringBuilder();
        sb.AppendLine($"共有 {notes.Count} 条笔记：");
        sb.AppendLine();

        foreach (var note in notes)
        {
            sb.AppendLine($"[{note.Key}]  （{note.UpdatedAt}）");
            sb.AppendLine(note.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string DeleteNote(JsonObject args)
    {
        var key = ReadRequired(args, "key");
        return NoteStore.Delete(key)
            ? $"已删除笔记「{key}」。"
            : $"没有找到笔记「{key}」。";
    }

    // ------------------------------------------------------------ 别名

    private static string SetRename(JsonObject args)
    {
        var original = ReadRequired(args, "original").Trim();
        var alias = ReadRequired(args, "alias").Trim();

        if (string.Equals(original, alias, StringComparison.Ordinal))
            return "原始名字和别名一样，没有意义，没有登记。";

        RenameStore.Set(original, alias);
        return $"已登记：{original} → {alias}。之后所有工具输出里都会用「{alias}」显示。";
    }

    private static string ListRenames()
    {
        var entries = RenameStore.List();
        if (entries.Count == 0)
            return "还没有登记任何别名。遇到混淆的名字可以用 set_rename 起个可读的名字。";

        var sb = new StringBuilder();
        sb.AppendLine($"共有 {entries.Count} 条别名：");
        sb.AppendLine();

        foreach (var entry in entries)
            sb.AppendLine($"{entry.Original}  →  {entry.Alias}");

        return sb.ToString();
    }

    private static string ClearRename(JsonObject args)
    {
        var original = ReadRequired(args, "original").Trim();
        return RenameStore.Remove(original)
            ? $"已删除别名「{original}」，之后恢复显示原始名字。"
            : $"没有找到「{original}」的别名。";
    }

    private static string ReadRequired(JsonObject args, string key)
    {
        var value = args[key]?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"缺少参数 {key}");
        return value;
    }

    // ------------------------------------------------------------ 响应封装

    private static string Error(JsonNode? id, int code, string message) => new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        },
    }.ToJsonString();
}
