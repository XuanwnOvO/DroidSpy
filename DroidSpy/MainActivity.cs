using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using DroidSpy.Adapters;
using DroidSpy.Services;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomNavigation;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ImageView;

namespace DroidSpy;

/// <summary>主界面：首页（打开文件 + 最近记录）/ MCP / 关于，底部导航切换。</summary>
[Activity(
    Label = "@string/app_name",
    MainLauncher = true,
    Exported = true,
    Theme = "@style/Theme.DroidSpy")]
public class MainActivity : AppCompatActivity
{
    private const int RequestOpen = 1001;

    private const string AuthorQq = "790399726";
    private const string LoverQq = "3193583971";

    /// <summary>交流群。点一下会试着唤起 QQ 加群，不行就复制群号。</summary>
    private const string GroupQq = "1040835997";

    private const string RepoUrl = "https://github.com/XuanwnOvO/DroidSpy";

    /// <summary>用到的第三方库。名称、版本、用途、仓库地址，关于页里逐个列出来。</summary>
    private static readonly (string Name, string Version, string Role, string Url)[] Libraries =
    {
        ("ICSharpCode.Decompiler", "9.1.0.7988", "反编译引擎，dnSpy / ILSpy 同款",
            "https://github.com/icsharpcode/ILSpy"),
        ("K4os.Compression.LZ4", "1.3.8", "解包资源包用的 LZ4 解压",
            "https://github.com/MiloszKrajewski/K4os.Compression.LZ4"),
        ("SharpCompress", "0.39.0", "解包资源包用的 LZMA 解压",
            "https://github.com/adamhathcock/sharpcompress"),
        ("Xamarin.Google.Android.Material", "1.14.0.6", "Material 3 界面组件",
            "https://github.com/xamarin/GooglePlayServicesComponents"),
    };

    /// <summary>MCP 默认端口。用户改了端口会记在这，下次进来还是他填的值。</summary>
    private const int DefaultMcpPort = 1233;

    private const string PrefMcpPort = "mcp_port";

    private RowAdapter _recentAdapter = null!;
    private TextView _txtNoRecent = null!;
    private MaterialToolbar _toolbar = null!;
    private View _pageHome = null!, _pageMcp = null!, _pageAbout = null!;

    private ShapeableImageView _imgAuthor = null!, _imgLover = null!;
    private TextView _txtAvatarStatus = null!;
    private MaterialButton _btnRefresh = null!;

    private EditText _editMcpPort = null!;
    private MaterialButton _btnMcpToggle = null!, _btnMcpCopy = null!;
    private TextView _txtMcpState = null!, _txtMcpHint = null!;
    private TextView _txtMcpClient = null!, _txtMcpClientTitle = null!;
    private TextView _txtMcpLocal = null!, _txtMcpLocalTitle = null!, _txtMcpLocalHint = null!;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private bool _avatarsLoaded;
    private bool _avatarLoading;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_main);

        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;

        _pageHome = FindViewById(Resource.Id.pageHome)!;
        _pageMcp = FindViewById(Resource.Id.pageMcp)!;
        _pageAbout = FindViewById(Resource.Id.pageAbout)!;

        var btnOpen = FindViewById<MaterialButton>(Resource.Id.btnOpen)!;
        btnOpen.Click += (_, _) => PickFile();

        _txtNoRecent = FindViewById<TextView>(Resource.Id.txtNoRecent)!;

        _recentAdapter = new RowAdapter();
        _recentAdapter.ItemClick += (_, item) =>
        {
            if (item.Payload is string path) OpenAssembly(path);
        };

        var list = FindViewById<RecyclerView>(Resource.Id.recentList)!;
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(_recentAdapter);

        SetupAbout();
        SetupMcp();

        var nav = FindViewById<BottomNavigationView>(Resource.Id.bottomNav)!;
        nav.ItemSelected += (_, e) => ShowPage(e.Item.ItemId);
        ShowPage(Resource.Id.nav_home);
    }

    protected override void OnResume()
    {
        base.OnResume();
        RefreshRecent();
    }

    // ------------------------------------------------------------ 分页

    private void ShowPage(int itemId)
    {
        _pageHome.Visibility = itemId == Resource.Id.nav_home ? ViewStates.Visible : ViewStates.Gone;
        _pageMcp.Visibility = itemId == Resource.Id.nav_mcp ? ViewStates.Visible : ViewStates.Gone;
        _pageAbout.Visibility = itemId == Resource.Id.nav_about ? ViewStates.Visible : ViewStates.Gone;

        _toolbar.Title = itemId switch
        {
            Resource.Id.nav_mcp => GetString(Resource.String.mcp_title),
            Resource.Id.nav_about => GetString(Resource.String.about_title),
            _ => GetString(Resource.String.app_name),
        };

        // 进了关于页才去拉头像，没必要开机就联网
        if (itemId == Resource.Id.nav_about && !_avatarsLoaded && !_avatarLoading)
            LoadAvatars(manual: false);

        // 服务可能在别的页面被启停过，也可能程序集换了一个，进来就对齐一次状态
        if (itemId == Resource.Id.nav_mcp)
            RefreshMcpState();
    }

    // ------------------------------------------------------------ MCP 页

    private void SetupMcp()
    {
        _editMcpPort = FindViewById<EditText>(Resource.Id.editMcpPort)!;
        _btnMcpToggle = FindViewById<MaterialButton>(Resource.Id.btnMcpToggle)!;
        _btnMcpCopy = FindViewById<MaterialButton>(Resource.Id.btnMcpCopy)!;
        _txtMcpState = FindViewById<TextView>(Resource.Id.txtMcpState)!;
        _txtMcpHint = FindViewById<TextView>(Resource.Id.txtMcpHint)!;
        _txtMcpClient = FindViewById<TextView>(Resource.Id.txtMcpClient)!;
        _txtMcpClientTitle = FindViewById<TextView>(Resource.Id.txtMcpClientTitle)!;
        _txtMcpLocal = FindViewById<TextView>(Resource.Id.txtMcpLocal)!;
        _txtMcpLocalTitle = FindViewById<TextView>(Resource.Id.txtMcpLocalTitle)!;
        _txtMcpLocalHint = FindViewById<TextView>(Resource.Id.txtMcpLocalHint)!;

        var prefs = GetSharedPreferences("droidspy", FileCreationMode.Private)!;
        _editMcpPort.Text = prefs.GetInt(PrefMcpPort, DefaultMcpPort).ToString();

        _btnMcpToggle.Click += (_, _) => ToggleMcp();
        _btnMcpCopy.Click += (_, _) => CopyMcpAddress();
    }

    private void ToggleMcp()
    {
        if (AppState.IsMcpRunning)
        {
            AppState.StopMcp();
            RefreshMcpState();
            return;
        }

        if (!int.TryParse(_editMcpPort.Text?.Trim(), out var port) || port < 1024 || port > 65535)
        {
            Toast.MakeText(this, GetString(Resource.String.mcp_port_invalid),
                ToastLength.Long)?.Show();
            return;
        }

        _txtMcpState.SetText(Resource.String.mcp_state_starting);

        try
        {
            AppState.StartMcp(port);
            GetSharedPreferences("droidspy", FileCreationMode.Private)!
                .Edit()!.PutInt(PrefMcpPort, port)!.Apply();
        }
        catch (Exception ex)
        {
            // 端口被占用是最常见的失败原因，把原始报错带给用户才好排查
            _txtMcpState.Text = GetString(Resource.String.mcp_state_failed, ex.Message);
            return;
        }

        RefreshMcpState();
    }

    private void RefreshMcpState()
    {
        bool running = AppState.IsMcpRunning;

        _txtMcpState.SetText(running
            ? Resource.String.mcp_state_running
            : Resource.String.mcp_state_stopped);
        _btnMcpToggle.SetText(running
            ? Resource.String.mcp_btn_stop
            : Resource.String.mcp_btn_start);
        _btnMcpToggle.SetIconResource(running
            ? Resource.Drawable.ic_stop
            : Resource.Drawable.ic_play);
        _editMcpPort.Enabled = !running;
        _btnMcpCopy.Visibility = running ? ViewStates.Visible : ViewStates.Gone;

        if (!running)
        {
            _txtMcpClientTitle.Visibility = ViewStates.Gone;
            _txtMcpClient.Visibility = ViewStates.Gone;
            _txtMcpLocalTitle.Visibility = ViewStates.Gone;
            _txtMcpLocalHint.Visibility = ViewStates.Gone;
            _txtMcpLocal.Visibility = ViewStates.Gone;
            _txtMcpHint.SetText(AppState.IsLoaded
                ? Resource.String.mcp_hint_stopped
                : Resource.String.mcp_need_assembly);
            return;
        }

        var lan = AppState.Mcp?.LanUrl;
        var address = lan ?? AppState.Mcp!.LocalUrl;
        var local = AppState.Mcp!.LocalUrl;

        _txtMcpHint.Text = lan != null
            ? GetString(Resource.String.mcp_addr_hint)
            : GetString(Resource.String.mcp_lan_unavailable);

        _txtMcpClientTitle.Visibility = ViewStates.Visible;
        _txtMcpClient.Visibility = ViewStates.Visible;
        // 直接给出可粘贴的 JSON，省得用户自己去查客户端怎么配
        _txtMcpClient.Text = BuildClientConfig(address);

        _txtMcpLocalTitle.Visibility = ViewStates.Visible;
        _txtMcpLocalHint.Visibility = ViewStates.Visible;
        _txtMcpLocal.Visibility = ViewStates.Visible;
        _txtMcpLocal.Text = BuildClientConfig(local);
    }

    private static string BuildClientConfig(string url) =>
        "{\n" +
        "  \"mcpServers\": {\n" +
        "    \"droidspy\": {\n" +
        $"      \"url\": \"{url}\"\n" +
        "    }\n" +
        "  }\n" +
        "}";

    private void CopyMcpAddress()
    {
        // 按钮挨着局域网那块，复制的是电脑要用的地址
        var address = AppState.Mcp?.LanUrl ?? AppState.Mcp?.LocalUrl;
        if (address == null) return;

        var clipboard = (Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard != null)
            clipboard.PrimaryClip = Android.Content.ClipData.NewPlainText("DroidSpy MCP", address);
        Toast.MakeText(this, GetString(Resource.String.mcp_copied), ToastLength.Short)?.Show();
    }

    // ------------------------------------------------------------ 关于页

    private void SetupAbout()
    {
        _imgAuthor = FindViewById<ShapeableImageView>(Resource.Id.imgAuthor)!;
        _imgLover = FindViewById<ShapeableImageView>(Resource.Id.imgLover)!;
        _txtAvatarStatus = FindViewById<TextView>(Resource.Id.txtAvatarStatus)!;
        _btnRefresh = FindViewById<MaterialButton>(Resource.Id.btnRefreshAvatar)!;

        FindViewById<TextView>(Resource.Id.txtAuthorQq)!.Text =
            GetString(Resource.String.about_qq, AuthorQq);
        FindViewById<TextView>(Resource.Id.txtLoverQq)!.Text =
            GetString(Resource.String.about_qq, LoverQq);

        FindViewById<TextView>(Resource.Id.txtQqGroup)!.Text =
            GetString(Resource.String.about_qq_group_id, GroupQq);
        FindViewById<TextView>(Resource.Id.txtGithub)!.Text = RepoUrl;
        FindViewById<LinearLayout>(Resource.Id.rowQqGroup)!.Click += (_, _) => JoinGroup();
        FindViewById<LinearLayout>(Resource.Id.rowGithub)!.Click += (_, _) => OpenUrl(RepoUrl);

        BuildLibraryRows();

        // 版本号直接读清单，免得改了 csproj 忘了同步界面
        var version = PackageManager?.GetPackageInfo(PackageName!, 0)?.VersionName ?? "1.0";
        FindViewById<TextView>(Resource.Id.txtVersion)!.Text =
            GetString(Resource.String.about_version, version);

        _btnRefresh.Click += (_, _) => LoadAvatars(manual: true);
    }

    /// <summary>
    /// 引用库列表是代码生成的，不写死在布局里：改依赖时只要动上面那个数组，
    /// 布局和这里都不用跟着改。
    /// </summary>
    private void BuildLibraryRows()
    {
        var container = FindViewById<LinearLayout>(Resource.Id.libContainer)!;
        var inflater = LayoutInflater!;

        for (int i = 0; i < Libraries.Length; i++)
        {
            if (i > 0)
            {
                var divider = new View(this);
                divider.LayoutParameters = new LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MatchParent, Dp(1));
                divider.SetBackgroundColor(new Android.Graphics.Color(
                    ResolveColor(Resource.Attribute.colorOutlineVariant)));
                container.AddView(divider);
            }

            var lib = Libraries[i];
            var row = inflater.Inflate(Resource.Layout.item_library, container, false)!;

            row.FindViewById<TextView>(Resource.Id.txtLibName)!.Text = lib.Name;
            row.FindViewById<TextView>(Resource.Id.txtLibRole)!.Text =
                GetString(Resource.String.about_lib_version, lib.Version, lib.Role);

            var url = lib.Url;
            row.Click += (_, _) => OpenUrl(url);
            container.AddView(row);
        }
    }

    /// <summary>
    /// 拉起 QQ 加群。QQ 的加群 scheme 各版本不一，用官方提供的这个卡片 scheme，
    /// 认不出来的设备会抛 ActivityNotFoundException，那就退回复制群号，
    /// 让用户自己去 QQ 里搜 —— 总比点了没反应好。
    /// </summary>
    private void JoinGroup()
    {
        var uri = Android.Net.Uri.Parse(
            $"mqqapi://card/show_pslcard?src_type=internal&version=1&uin={GroupQq}&card_type=group&source=qrcode");

        try
        {
            StartActivity(new Intent(Intent.ActionView, uri));
        }
        catch
        {
            var clipboard = (Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
            if (clipboard != null)
                clipboard.PrimaryClip = Android.Content.ClipData.NewPlainText("QQ 群", GroupQq);
            Toast.MakeText(this, Resource.String.about_qq_copied, ToastLength.Long)?.Show();
        }
    }

    private void OpenUrl(string url)
    {
        try
        {
            StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url)));
        }
        catch (Exception ex)
        {
            Toast.MakeText(
                this,
                GetString(Resource.String.about_open_failed, ex.Message),
                ToastLength.Long)?.Show();
        }
    }

    private int Dp(int value) =>
        (int)Math.Round(value * Resources!.DisplayMetrics!.Density);

    private int ResolveColor(int attribute)
    {
        var typed = new Android.Util.TypedValue();
        Theme!.ResolveAttribute(attribute, typed, true);
        return typed.Data;
    }

    /// <summary>头像走 QQ 的公开接口，每次进页面重新下载，保证是最新的。</summary>
    private void LoadAvatars(bool manual)
    {
        _avatarLoading = true;
        _btnRefresh.Enabled = false;
        _txtAvatarStatus.SetText(Resource.String.about_avatar_loading);

        Task.Run(async () =>
        {
            Bitmap? author = null, lover = null;
            string? error = null;

            try
            {
                var tasks = new[]
                {
                    FetchAvatarAsync(AuthorQq),
                    FetchAvatarAsync(LoverQq),
                };
                var results = await Task.WhenAll(tasks);
                author = results[0];
                lover = results[1];
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            RunOnUiThread(() =>
            {
                _avatarLoading = false;
                _btnRefresh.Enabled = true;

                if (author != null)
                {
                    _imgAuthor.SetImageBitmap(author);
                    _avatarsLoaded = true;
                }
                if (lover != null) _imgLover.SetImageBitmap(lover);

                if (error != null && author == null)
                {
                    _txtAvatarStatus.Text =
                        $"{GetString(Resource.String.about_avatar_failed)}（{error}）";
                }
                else if (manual)
                {
                    _txtAvatarStatus.SetText(Resource.String.about_avatar_done);
                }
                else
                {
                    _txtAvatarStatus.Text = string.Empty;
                }
            });
        });
    }

    private static async Task<Bitmap?> FetchAvatarAsync(string qq)
    {
        // 带个时间戳绕开接口侧的缓存，否则"实时刷新"刷不动
        var url = $"https://q1.qlogo.cn/g?b=qq&nk={qq}&s=640&t={DateTimeOffset.Now.ToUnixTimeSeconds()}";
        var bytes = await Http.GetByteArrayAsync(url);
        return BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
    }

    // ------------------------------------------------------------ 打开文件

    private void PickFile()
    {
        // 不限定 MIME 类型：Android 对 .dll 没有统一的 MIME 归类，
        // 一旦加了类型过滤或 CategoryOpenable，文件选择器就会把 .dll 直接隐藏掉。
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.SetType("*/*");

        try
        {
            StartActivityForResult(intent, RequestOpen);
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != RequestOpen || resultCode != Result.Ok || data?.Data == null) return;

        var uri = data.Data;
        var dialog = new MaterialAlertDialogBuilder(this)!
            .SetMessage(Resource.String.loading)!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();

        Task.Run(() =>
        {
            try
            {
                // 导入会往 assemblies/<同名文件> 上写。上一次打开的那个 DLL
                // 如果就叫这个名字，旧会话正占着它，必须先松手。
                AppState.ReleaseAssembly();

                var path = AssemblyStore.Import(this, uri);
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    OpenAssembly(path);
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    Toast.MakeText(this, GetString(Resource.String.load_failed, ex.Message),
                        ToastLength.Long)?.Show();
                });
            }
        });
    }

    /// <summary>在后台解析程序集，成功后进入浏览器页面。</summary>
    private void OpenAssembly(string path)
    {
        var kind = AssemblyStore.SniffKind(path);

        // 这两种可以确定不是托管程序集，直接给结论，不必再让引擎白跑一趟
        if (kind is AssemblyStore.FileKind.Il2CppMetadata
                 or AssemblyStore.FileKind.NativePe)
        {
            ShowCannotOpen(path, kind);
            return;
        }

        var dialog = new MaterialAlertDialogBuilder(this)!
            .SetMessage(kind == AssemblyStore.FileKind.UnityBundle
                ? GetString(Resource.String.unpacking)
                : GetString(Resource.String.loading))!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();

        Task.Run(() =>
        {
            try
            {
                // 解包会覆盖 unpacked/<同名>.dll。第二次打开同一个包时，
                // 上一次解出来的那个文件还握在旧会话手里，这里必须先放掉，
                // 否则 File.Create 会直接报 IO_SharingViolation_File。
                AppState.ReleaseAssembly();

                // UnityFS 包里的 dll 是压缩存放的，先解出来再交给引擎
                var target = kind == AssemblyStore.FileKind.UnityBundle
                    ? UnpackBundle(path)
                    : path;

                // ManagedAssembly 和 Unknown 都交给引擎判定：
                // 文件头嗅探只是启发式，不该因为认不出来就否定一个本来能反编译的文件。
                AppState.Load(target);
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    StartActivity(new Intent(this, typeof(BrowserActivity)));
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    ShowOpenFailed(path, ex);
                });
            }
        });
    }

    /// <summary>
    /// 从 Unity 资源包里提取托管程序集，返回要加载的那个 dll 路径。
    ///
    /// 一个包里通常不止一个程序集（Assembly-CSharp.dll 之外还有各种第三方库），
    /// 全部解出来放在以包名命名的子目录里，返回其中最该看的那个。
    /// </summary>
    private string UnpackBundle(string bundlePath)
    {
        // 解包结果单独放一个目录：Unity 的 AssetBundle 常被命名成 xxx.dll，
        // 若就地生成同名文件，File.Create 会把正在读取的源文件截断。
        var name = System.IO.Path.GetFileNameWithoutExtension(bundlePath);
        var dir = System.IO.Path.Combine(AssemblyStore.AssembliesDir(this), "unpacked", name);

        var result = UnityBundleReader.ExtractAll(bundlePath, dir);
        return result.PrimaryPath;
    }

    /// <summary>文件头就能看出不是 .NET 程序集时的说明。</summary>
    private void ShowCannotOpen(string path, AssemblyStore.FileKind kind)
    {
        var body = kind switch
        {
            AssemblyStore.FileKind.Il2CppMetadata => GetString(Resource.String.wrong_file_il2cpp),
            AssemblyStore.FileKind.NativePe => GetString(Resource.String.wrong_file_native),
            _ => GetString(Resource.String.wrong_file_unknown),
        };

        new MaterialAlertDialogBuilder(this)!
            .SetTitle(Resource.String.wrong_file_title)!
            .SetMessage($"{body}\n\n{AssemblyStore.Describe(path)}")!
            .SetPositiveButton(Resource.String.ok, (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    /// <summary>引擎解析失败时的完整报错。用对话框而不是 Toast，避免长消息看不全。</summary>
    private void ShowOpenFailed(string path, Exception ex)
    {
        new MaterialAlertDialogBuilder(this)!
            .SetTitle(Resource.String.open_failed_title)!
            .SetMessage($"{ex.GetType().Name}: {ex.Message}\n\n{AssemblyStore.Describe(path)}")!
            .SetPositiveButton(Resource.String.ok, (IDialogInterfaceOnClickListener?)null)!
            .Show();
    }

    private void RefreshRecent()
    {
        var files = AssemblyStore.GetRecent(this);
        var rows = new List<RowItem>();

        foreach (var path in files)
        {
            var exists = File.Exists(path);
            rows.Add(new RowItem(
                Badge: "DLL",
                Title: System.IO.Path.GetFileName(path),
                Subtitle: exists
                    ? $"{AssemblyStore.FormatSize(new FileInfo(path).Length)} · {path}"
                    : GetString(Resource.String.file_missing),
                Payload: path));
        }

        _recentAdapter.Submit(rows);
        _txtNoRecent.Visibility = rows.Count == 0 ? ViewStates.Visible : ViewStates.Gone;
    }
}
