using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Text.Style;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.Core.View;
using AndroidX.DrawerLayout.Widget;
using AndroidX.RecyclerView.Widget;
using DroidSpy.Adapters;
using DroidSpy.Services;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace DroidSpy;

/// <summary>
/// 代码页：C# 源码查看（高亮 + 行号）。
/// 左上角三条杠展开类列表，可直接切换到别的类型；
/// 代码区支持左右滑动与自动换行切换，菜单里可以整包导出 C# 源码。
/// </summary>
[Activity(
    Label = "@string/app_name",
    Theme = "@style/Theme.DroidSpy",
    ParentActivity = typeof(BrowserActivity))]
public class CodeActivity : AppCompatActivity
{
    private const string ExtraType = "type_full_name";
    private const string ExtraTitle = "title";
    private const string ExtraToken = "metadata_token";
    private const int RequestExport = 2001;
    private const int RequestExportTree = 2002;

    private string _typeFullName = string.Empty;
    private string _title = string.Empty;
    private int _token;

    private TextView _codeText = null!, _lineNumbers = null!;
    private View _progress = null!;
    private MaterialToolbar _toolbar = null!;
    private DrawerLayout _drawer = null!;
    private ScrollView _lineScroll = null!, _codeScroll = null!;
    private HorizontalScrollView _hscroll = null!;
    private MaterialButton _btnWrap = null!;

    private RowAdapter _classAdapter = null!;
    private TextView _txtClassCount = null!;

    private string _plainText = string.Empty;
    private int _fontSizeSp = 12;
    private bool _showIl;
    private bool _wordWrap;

    // ApplyWordWrap 会改 TextView 的最大宽度，也就是改布局；改布局又会触发
    // LayoutChange。用这个标志挡住重入，避免来回弹。
    private bool _applyingWrap;

    // 上一次真正生效的换行宽度。宽度没变就不再动 UI —— LayoutChange 回调很频繁，
    // 每次都重设一遍会让界面一直在重新布局，表现就是整体卡顿。
    private int _wrapAppliedWidth;
    private string _filter = string.Empty;

    private CancellationTokenSource? _exportCts;

    // 导出进度。后台线程写、主线程定时器读，都是 int/string 这类引用赋值，
    // 读到稍旧的值最多让进度条跳一下，不值得为它加锁
    private volatile int _exportDone;
    private volatile int _exportTotal;
    private volatile string _exportPath = string.Empty;

    public static Intent IntentForType(Context ctx, string fullName, string title) =>
        new Intent(ctx, typeof(CodeActivity))
            .PutExtra(ExtraType, fullName)
            .PutExtra(ExtraTitle, title);

    public static Intent IntentForMember(Context ctx, string typeFullName, string title, int token) =>
        new Intent(ctx, typeof(CodeActivity))
            .PutExtra(ExtraType, typeFullName)
            .PutExtra(ExtraTitle, title)
            .PutExtra(ExtraToken, token);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_code);

        _typeFullName = Intent?.GetStringExtra(ExtraType) ?? string.Empty;
        _title = Intent?.GetStringExtra(ExtraTitle) ?? _typeFullName;
        _token = Intent?.GetIntExtra(ExtraToken, 0) ?? 0;

        // 只按成员进来时没有类型全名，用标题兜底显示
        if (_typeFullName.Length == 0) _typeFullName = _title;

        var prefs = GetSharedPreferences("droidspy_prefs", FileCreationMode.Private)!;
        _fontSizeSp = prefs.GetInt("font_size", 12);
        _wordWrap = prefs.GetBoolean("word_wrap", false);

        _drawer = FindViewById<DrawerLayout>(Resource.Id.drawer)!;
        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.Title = _title;
        _toolbar.Subtitle = _typeFullName;
        _toolbar.SetNavigationOnClickListener(new ClickListener(ToggleDrawer));
        _toolbar.InflateMenu(Resource.Menu.menu_code);
        _toolbar.MenuItemClick += (_, e) => OnMenuItem(e.Item?.ItemId ?? 0);

        _codeText = FindViewById<TextView>(Resource.Id.codeText)!;
        _lineNumbers = FindViewById<TextView>(Resource.Id.lineNumbers)!;
        _progress = FindViewById(Resource.Id.progress)!;
        _lineScroll = FindViewById<ScrollView>(Resource.Id.lineScroll)!;
        _codeScroll = FindViewById<ScrollView>(Resource.Id.codeScroll)!;
        _hscroll = FindViewById<HorizontalScrollView>(Resource.Id.hscroll)!;

        // 行号列自己不能滚动，只跟着代码列走，否则两列会错位
        _lineScroll.SetOnTouchListener(new IgnoreTouchListener());

        // 纵向：代码列 → 行号列
        _codeScroll.ScrollChange += (_, _) => _lineScroll.ScrollTo(0, _codeScroll.ScrollY);

        _btnWrap = FindViewById<MaterialButton>(Resource.Id.btnWrap)!;
        _btnWrap.Click += (_, _) => SetWordWrap(!_wordWrap);

        FindViewById<MaterialButton>(Resource.Id.btnFontDown)!.Click += (_, _) => ChangeFont(-1);
        FindViewById<MaterialButton>(Resource.Id.btnFontUp)!.Click += (_, _) => ChangeFont(+1);
        FindViewById<MaterialButton>(Resource.Id.btnCopy)!.Click += (_, _) => CopyAll();

        SetupClassList();

        ApplyFontSize();
        ApplyWordWrap();

        // 换行靠的是「按当前宽度限制 TextView 的最大宽度」，而宽度只有布局测量完
        // 才有值。OnCreate 那一趟基本都拿不到，所以这里在布局落定后补一次；
        // 横竖屏切换导致宽度变化时，也是靠这条兜住。
        _hscroll.LayoutChange += (_, _) =>
        {
            if (_wordWrap && !_applyingWrap) ApplyWordWrap();
        };

        Decompile();
    }

    // ------------------------------------------------------------ 类列表抽屉

    private void SetupClassList()
    {
        _txtClassCount = FindViewById<TextView>(Resource.Id.txtClassCount)!;

        _classAdapter = new RowAdapter();
        _classAdapter.ItemClick += (_, item) =>
        {
            if (item.Payload is not TypeEntry t) return;
            _drawer.CloseDrawers();
            ShowType(t);
        };

        var list = FindViewById<RecyclerView>(Resource.Id.classList)!;
        list.SetLayoutManager(new LinearLayoutManager(this));
        list.SetAdapter(_classAdapter);

        var editFilter = FindViewById<TextInputEditText>(Resource.Id.editFilter)!;
        editFilter.TextChanged += (_, e) =>
        {
            _filter = e?.Text?.ToString()?.Trim() ?? string.Empty;
            FillClassList();
        };

        FillClassList();
    }

    private void FillClassList()
    {
        var all = AppState.Types ?? new List<TypeEntry>();

        // 名字里带 < 的是编译器生成的，列表里刷屏又没什么用，默认不展示
        var rows = new List<RowItem>();
        int shown = 0;

        foreach (var t in all)
        {
            if (t.Name.StartsWith('<')) continue;
            if (_filter.Length > 0 &&
                !t.FullName.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;

            shown++;
            rows.Add(new RowItem(
                Badge: Badge(t.Kind),
                Title: t.Name,
                Subtitle: string.IsNullOrEmpty(t.Namespace) ? t.Kind : $"{t.Namespace} · {t.Kind}",
                Payload: t));

            // 万级类型的列表全量塞进 RecyclerView 会卡，够滚就行
            if (rows.Count >= 1500) break;
        }

        _classAdapter.Submit(rows);
        _txtClassCount.Text = shown > rows.Count
            ? $"{rows.Count} / {shown}"
            : GetString(Resource.String.type_count, rows.Count);
    }

    private static string Badge(string kind) => kind switch
    {
        "class" => "CLS",
        "static class" => "STC",
        "struct" => "STR",
        "interface" => "INT",
        "enum" => "ENM",
        "delegate" => "DEL",
        "record" => "RCD",
        "record struct" => "RCS",
        _ => kind.Length >= 3 ? kind[..3].ToUpperInvariant() : kind.ToUpperInvariant(),
    };

    private void ToggleDrawer()
    {
        if (_drawer.IsDrawerOpen((int)GravityFlags.Start))
            _drawer.CloseDrawers();
        else
            _drawer.OpenDrawer((int)GravityFlags.Start);
    }

    /// <summary>切换到另一个类型，复用当前页面，不再开新的 Activity。</summary>
    private void ShowType(TypeEntry type)
    {
        _typeFullName = type.FullName;
        _title = type.Name;
        _token = 0;
        _showIl = false;

        _toolbar.Title = _title;
        _toolbar.Menu.FindItem(Resource.Id.action_il)!.SetTitle(Resource.String.show_il);

        Decompile();
    }

    // ------------------------------------------------------------ 反编译

    private void Decompile()
    {
        var targetType = _typeFullName;
        var targetToken = _token;
        var showIl = _showIl;
        var fontSize = _fontSizeSp;

        // 登记给 MCP：电脑那边随时可能问"用户现在看的是什么"
        AppState.SetCurrent(this, new AppState.CurrentView(targetType, _title, targetToken, showIl));

        _progress.Visibility = ViewStates.Visible;
        _codeText.Text = string.Empty;
        _lineNumbers.Text = string.Empty;

        Task.Run(() =>
        {
            try
            {
                var svc = AppState.Decompiler
                          ?? throw new InvalidOperationException("尚未加载程序集");

                string text = showIl
                    ? svc.DecompileIl(targetToken)
                    : targetToken != 0
                        ? svc.DecompileMember(targetToken)
                        : svc.DecompileType(targetType);

                // 高亮、行号、着色全部在后台算完，主线程只负责贴上去。
                var payload = BuildPayload(text, showIl, fontSize);

                RunOnUiThread(() =>
                {
                    // 反编译期间可能已经切到别的类型了，过期结果直接丢掉
                    if (targetType != _typeFullName || targetToken != _token || showIl != _showIl)
                        return;
                    Apply(payload);
                });
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                RunOnUiThread(() =>
                {
                    _progress.Visibility = ViewStates.Gone;
                    RenderPlain($"// {GetString(Resource.String.decompile_failed, message)}");
                });
            }
        });
    }

    /// <summary>
    /// 一次渲染所需的全部内容。构造过程比较重（词法扫描 + 建 span），
    /// 所以整包丢到后台线程做，主线程拿到就直接能用。
    /// </summary>
    private sealed record RenderPayload(
        string Plain, string Numbers, int LineCount,
        SpannableString? Highlighted, bool Truncated);

    /// <summary>
    /// 源码超过这个长度就放弃语法高亮。
    /// 高亮要为每个 token 建一个 span 对象，几万行的文件会造出几十万个 span，
    /// TextView 渲染时逐个二分查找，卡到没法用 —— 大文件纯文本反而是最优解。
    /// </summary>
    private const int MaxHighlightChars = 120_000;

    /// <summary>行号超过这个数量就不再显示（否则行号列自身也会拖慢布局）。</summary>
    private const int MaxNumberedLines = 20_000;

    private RenderPayload BuildPayload(string text, bool showIl, int fontSizeSp)
    {
        int lineCount = CSharpHighlighter.CountLines(text);

        var numbers = new StringBuilder();
        if (lineCount <= MaxNumberedLines)
        {
            for (int i = 1; i <= lineCount; i++)
                numbers.Append(i).Append('\n');
        }

        bool skipHighlight = showIl || text.Length > MaxHighlightChars;
        SpannableString? highlighted = null;

        if (!skipHighlight)
        {
            var spannable = new SpannableString(text);
            foreach (var token in CSharpHighlighter.Tokenize(text))
            {
                int end = token.Start + token.Length;
                if (token.Start < 0 || end > text.Length) continue;
                spannable.SetSpan(
                    new ForegroundColorSpan(ColorFor(token.Kind)),
                    token.Start, end,
                    SpanTypes.ExclusiveExclusive);
            }
            highlighted = spannable;
        }

        return new RenderPayload(text, numbers.ToString(), lineCount,
            highlighted, skipHighlight);
    }

    private void Apply(RenderPayload payload)
    {
        _plainText = payload.Plain;
        _progress.Visibility = ViewStates.Gone;

        _toolbar.Subtitle = _token != 0
            ? _typeFullName
            : $"{_typeFullName} · {payload.LineCount} 行" +
              (payload.Truncated ? "（已关闭高亮）" : string.Empty);

        if (payload.Highlighted != null)
            _codeText.SetText(payload.Highlighted, TextView.BufferType.Spannable);
        else
            RenderPlain(payload.Plain, redrawNumbers: false);

        _lineNumbers.Text = payload.Numbers;
        _lineNumbers.SetTextSize(Android.Util.ComplexUnitType.Sp, Math.Max(8, _fontSizeSp - 1));

        _codeScroll.ScrollTo(0, 0);
        _hscroll.ScrollTo(0, 0);
        _lineScroll.ScrollTo(0, 0);
    }

    /// <summary>出错提示、IL 这类短文本走这里，不做高亮。</summary>
    private void RenderPlain(string text, bool redrawNumbers = true)
    {
        _plainText = text;
        _progress.Visibility = ViewStates.Gone;
        _codeText.SetText(text, TextView.BufferType.Normal);

        if (redrawNumbers)
        {
            int lineCount = CSharpHighlighter.CountLines(text);
            var numbers = new StringBuilder();
            for (int i = 1; i <= lineCount; i++)
                numbers.Append(i).Append('\n');
            _lineNumbers.Text = numbers.ToString();
            _lineNumbers.SetTextSize(Android.Util.ComplexUnitType.Sp, Math.Max(8, _fontSizeSp - 1));
        }
    }

    private Color ColorFor(TokenKind kind)
    {
        int res = kind switch
        {
            TokenKind.Keyword => Resource.Color.code_keyword,
            TokenKind.Type => Resource.Color.code_type,
            TokenKind.String => Resource.Color.code_string,
            TokenKind.Comment => Resource.Color.code_comment,
            TokenKind.Number => Resource.Color.code_number,
            TokenKind.Punctuation => Resource.Color.code_punct,
            _ => Resource.Color.code_text,
        };
        return new Color(AndroidX.Core.Content.ContextCompat.GetColor(this, res));
    }

    // ------------------------------------------------------------ 显示设置

    private void ChangeFont(int delta)
    {
        int next = Math.Clamp(_fontSizeSp + delta, 8, 28);
        if (next == _fontSizeSp) return;
        _fontSizeSp = next;
        ApplyFontSize();
        GetSharedPreferences("droidspy_prefs", FileCreationMode.Private)!
            .Edit()!.PutInt("font_size", _fontSizeSp)!.Apply();
    }

    private void ApplyFontSize()
    {
        _codeText.SetTextSize(Android.Util.ComplexUnitType.Sp, _fontSizeSp);
        _lineNumbers.SetTextSize(Android.Util.ComplexUnitType.Sp, Math.Max(8, _fontSizeSp - 1));
    }

    private void SetWordWrap(bool on)
    {
        _wordWrap = on;
        ApplyWordWrap();
        GetSharedPreferences("droidspy_prefs", FileCreationMode.Private)!
            .Edit()!.PutBoolean("word_wrap", on)!.Apply();
    }

    /// <summary>
    /// 换行开：限制 TextView 的最大宽度，长行在宽度处折行；
    /// 换行关：解除宽度限制，靠外层 HorizontalScrollView 左右滑动。
    ///
    /// 这里必须用 MaxWidth 而不是把 LayoutParams.Width 设成 MatchParent：
    /// TextView 的父级是 HorizontalScrollView，它测量子 View 时用的是
    /// UNSPECIFIED，任何基于父容器宽度的宽度值都会被直接忽略，折行也就无从谈起。
    /// 只有 MaxWidth 是 TextView 自己在 onMeasure 里强制的，才真正管用。
    /// </summary>
    private void ApplyWordWrap()
    {
        _applyingWrap = true;
        try
        {
            ApplyWordWrapCore();
        }
        finally
        {
            _applyingWrap = false;
        }
    }

    private void ApplyWordWrapCore()
    {
        if (_wordWrap)
        {
            var avail = AvailableCodeWidth();

            // OnCreate 里调这一趟时布局还没测量，拿不到宽度。
            // 这时候千万别退化成 int.MaxValue —— 那等于没限宽、不折行，
            // 表现就是「上次明明开着换行，重开却没换行」。
            // 什么都不做，等布局落定后再补。
            if (avail <= 0) return;

            // 宽度没变就到此为止。LayoutChange 会频繁回调，而 SetMaxWidth /
            // ScrollTo / 改菜单标题每一样都会再掀一次布局，不在这儿刹住就是
            // 每帧来回抖，整个界面都会卡住。
            if (avail == _wrapAppliedWidth) return;
            _wrapAppliedWidth = avail;

            _codeText.SetMaxWidth(avail);
            _codeText.SetHorizontallyScrolling(false);
            _hscroll.HorizontalScrollBarEnabled = false;
            _hscroll.ScrollTo(0, 0);
        }
        else
        {
            _wrapAppliedWidth = 0;
            _codeText.SetMaxWidth(int.MaxValue);
            _codeText.SetHorizontallyScrolling(true);
            _hscroll.HorizontalScrollBarEnabled = true;
        }

        _btnWrap.Text = GetString(_wordWrap
            ? Resource.String.word_wrap_on
            : Resource.String.word_wrap_off);
        _toolbar.Menu.FindItem(Resource.Id.action_wrap)!.SetTitle(_wordWrap
            ? Resource.String.word_wrap_on
            : Resource.String.word_wrap_off);
    }

    /// <summary>代码列当前可用的宽度（不含行号列）。布局还没完成时返回 0。</summary>
    private int AvailableCodeWidth()
    {
        int w = _hscroll.Width;
        if (w > 0) return w - _codeText.PaddingLeft - _codeText.PaddingRight;
        return 0;
    }

    private void CopyAll()
    {
        if (string.IsNullOrEmpty(_plainText)) return;
        var clipboard = (Android.Content.ClipboardManager?)GetSystemService(ClipboardService);
        if (clipboard != null)
            clipboard.PrimaryClip = ClipData.NewPlainText(_title, _plainText);
        Toast.MakeText(this, Resource.String.copy_all, ToastLength.Short)?.Show();
    }

    // ------------------------------------------------------------ 导出

    /// <summary>导出当前这一份源码（单个 .cs 文件）。</summary>
    private void ExportCs()
    {
        if (string.IsNullOrEmpty(_plainText)) return;

        var safe = _title;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars())
            safe = safe.Replace(c, '_');

        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(CSharpMime);
        intent.PutExtra(Intent.ExtraTitle, $"{safe}.cs");
        StartActivityForResult(intent, RequestExport);
    }

    /// <summary>请求一个目录，之后把整个程序集的 C# 按类型写进去。</summary>
    private void ExportAllSources()
    {
        if (!AppState.IsLoaded)
        {
            Toast.MakeText(this, Resource.String.export_none, ToastLength.Short)?.Show();
            return;
        }

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.PutExtra("android:grantWrite", true);
        StartActivityForResult(intent, RequestExportTree);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (resultCode != Result.Ok || data?.Data == null) return;

        if (requestCode == RequestExport) WriteSingleFile(data.Data);
        else if (requestCode == RequestExportTree) StartTreeExport(data.Data);
    }

    /// <summary>在 SAF 目录里按显示名找一个已存在的子项，找不到返回 null。</summary>
    private Android.Net.Uri? FindTreeChild(Android.Net.Uri parent, string displayName)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(
            parent, DocumentsContract.GetDocumentId(parent));
        if (children == null) return null;

        string[] columns =
        {
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
        };

        using var cursor = ContentResolver!.Query(children, columns, null, null, null);
        if (cursor == null) return null;

        while (cursor.MoveToNext())
        {
            if (!string.Equals(cursor.GetString(1), displayName, StringComparison.Ordinal))
                continue;

            return DocumentsContract.BuildDocumentUriUsingTree(parent, cursor.GetString(0));
        }

        return null;
    }

    /// <summary>把相对路径里的目录逐级在 SAF 树上建出来（父目录先建）。</summary>
    private Android.Net.Uri EnsureTreeDir(Android.Net.Uri rootDoc,
        Dictionary<string, Android.Net.Uri> cache, string relDir)
    {
        if (relDir.Length == 0) return rootDoc;
        if (cache.TryGetValue(relDir, out var cached)) return cached;

        var parentRel = System.IO.Path.GetDirectoryName(relDir) ?? string.Empty;
        var parent = EnsureTreeDir(rootDoc, cache, parentRel);

        var name = System.IO.Path.GetFileName(relDir);
        var existing = FindTreeChild(parent, name);
        var child = existing ?? DocumentsContract.CreateDocument(
            ContentResolver!, parent, DocumentsContract.Document.MimeTypeDir, name)
            ?? throw new System.IO.IOException($"无法创建目录 {relDir}");

        cache[relDir] = child;
        return child;
    }

    /// <summary>
    /// 创建 .cs 文件时用的 MIME 类型。
    ///
    /// 不能用 "text/plain"：Android 内置的 Downloads / DocumentsUI 这类
    /// DocumentsProvider 会拿 MIME 去查它自己的扩展名表，text/plain 对应的
    /// 是 .txt，于是最终落盘的名字就变成 AAAFBKBHAKM.cs.txt —— 显示名我们
    /// 给的是完整的 "xxx.cs"，扩展名却被它按 MIME 又追加了一遍。
    ///
    /// 换成 .cs 这个确切类型，它查不到对应扩展名，就老老实实用我们给的名字。
    /// 万一某个 Provider 更挑剔、认不出这个类型，CreateDocument 会抛
    /// IllegalArgumentException，届时退回 text/plain 至少还能写成功
    /// （代价就是多一个 .txt 后缀，总比整个导出失败强）。
    /// </summary>
    private const string CSharpMime = "text/x-csharp";

    private Android.Net.Uri CreateCSharpDocument(Android.Net.Uri parent, string fileName)
    {
        try
        {
            return DocumentsContract.CreateDocument(
                ContentResolver!, parent, CSharpMime, fileName)!;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException
                                      or Java.Lang.UnsupportedOperationException)
        {
            return DocumentsContract.CreateDocument(
                ContentResolver!, parent, "text/plain", fileName)
                   ?? throw new System.IO.IOException($"无法创建 {fileName}");
        }
    }

    /// <summary>把一个 .cs 写进 SAF 目录，返回是否成功。</summary>
    private bool WriteToTree(Android.Net.Uri rootDoc,
        Dictionary<string, Android.Net.Uri> cache, string relPath, string code)
    {
        var relDir = System.IO.Path.GetDirectoryName(relPath) ?? string.Empty;
        var fileName = System.IO.Path.GetFileName(relPath);

        var parent = EnsureTreeDir(rootDoc, cache, relDir);

        // 同名的旧文件先删掉：直接 CreateDocument 会留下 xxx (1).cs 这种重复
        var stale = FindTreeChild(parent, fileName);
        if (stale != null) DocumentsContract.DeleteDocument(ContentResolver!, stale);

        var doc = CreateCSharpDocument(parent, fileName);

        var bytes = Encoding.UTF8.GetBytes(code);
        using var output = ContentResolver!.OpenOutputStream(doc)
                           ?? throw new System.IO.IOException($"无法写入 {relPath}");
        output.Write(bytes, 0, bytes.Length);
        output.Flush();
        return true;
    }

    private void WriteSingleFile(Android.Net.Uri uri)
    {
        try
        {
            using var stream = ContentResolver!.OpenOutputStream(uri)
                               ?? throw new System.IO.IOException("无法写入目标文件");
            var bytes = Encoding.UTF8.GetBytes(_plainText);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
            Toast.MakeText(this, GetString(Resource.String.exported_to,
                uri.LastPathSegment ?? "?"), ToastLength.Long)?.Show();
        }
        catch (Exception ex)
        {
            Toast.MakeText(this, ex.Message, ToastLength.Long)?.Show();
        }
    }

    /// <summary>
    /// 整包导出：边反编译边写，不再经过私有缓存中转。
    /// 目录结构和 dnSpy 的「导出到项目」一样，命名空间按层级展开成子目录。
    /// </summary>
    private void StartTreeExport(Android.Net.Uri treeUri)
    {
        _toolbar.Menu.FindItem(Resource.Id.action_export_all)!.SetEnabled(false);

        var dialog = new MaterialAlertDialogBuilder(this)!
            .SetTitle(Resource.String.export_all_cs)!
            .SetMessage(GetString(Resource.String.exporting, 0, 0))!
            .SetNegativeButton(Resource.String.cancel, (_, _) => _exportCts?.Cancel())!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();

        _exportCts = new CancellationTokenSource();
        var token = _exportCts.Token;

        // 对话框上的文字由它按固定节奏刷，后台只管更新那几个进度字段
        StartExportTicker(dialog, token);

        Task.Run(() =>
        {
            try
            {
                var svc = AppState.Decompiler
                          ?? throw new InvalidOperationException("尚未加载程序集");

                var rootId = DocumentsContract.GetTreeDocumentId(treeUri);
                var rootDoc = DocumentsContract.BuildDocumentUriUsingTree(treeUri, rootId)
                              ?? throw new System.IO.IOException("无法访问所选目录");
                var dirs = new Dictionary<string, Android.Net.Uri>(StringComparer.Ordinal)
                {
                    [string.Empty] = rootDoc,
                };

                // 写盘和反编译都在这个后台线程上跑，所以不用锁；界面只读这几个字段
                _exportDone = 0;
                _exportTotal = 0;
                _exportPath = string.Empty;

                int written = 0;
                svc.ExportAllSources((rel, code) =>
                {
                    WriteToTree(rootDoc, dirs, rel, code);
                    _exportDone = ++written;
                    return true;
                },
                (current, total, rel) =>
                {
                    _exportTotal = total;
                    _exportPath = rel;
                }, token);

                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishExport();
                    if (written > 0)
                    {
                        Toast.MakeText(this, GetString(Resource.String.export_done, written),
                            ToastLength.Long)?.Show();
                    }
                });
            }
            catch (System.OperationCanceledException)
            {
                // 取消发生在循环中间，此刻已经落盘的文件是完整可用的，如实报个数
                var written = _exportDone;
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishExport();
                    Toast.MakeText(this, GetString(Resource.String.export_done_stopped, written),
                        ToastLength.Long)?.Show();
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishExport();
                    new MaterialAlertDialogBuilder(this)!
                        .SetTitle(Resource.String.export_all_cs)!
                        .SetMessage(ex.Message)!
                        .SetPositiveButton(Resource.String.ok, (IDialogInterfaceOnClickListener?)null)!
                        .Show();
                });
            }
        }, token);
    }

    private void FinishExport()
    {
        _exportCts?.Dispose();
        _exportCts = null;
        _toolbar.Menu.FindItem(Resource.Id.action_export_all)!.SetEnabled(true);
    }

    /// <summary>
    /// 导出进度。反编译本身是同步的，每写完一个类型才有机会回到消息循环，
    /// 如果每个文件都往主线程抛一次更新，几千个类型就会把主线程淹掉。
    /// 所以这里只把状态记在字段里，由定时器按固定节奏刷新对话框。
    /// </summary>
    private Java.Lang.Runnable? _exportTicker;

    private void StartExportTicker(AndroidX.AppCompat.App.AlertDialog dialog, CancellationToken token)
    {
        var handler = new Handler(Looper.MainLooper!);

        void Stop()
        {
            if (_exportTicker != null)
            {
                handler.RemoveCallbacks(_exportTicker);
                _exportTicker = null;
            }
        }

        // token 注册在后台线程上，所以要回主线程才能碰控件
        token.Register(() => RunOnUiThread(Stop));

        _exportTicker = new Java.Lang.Runnable(() =>
        {
            if (token.IsCancellationRequested || !dialog.IsShowing)
            {
                Stop();
                return;
            }

            dialog.SetMessage(GetString(Resource.String.export_writing,
                _exportDone, _exportTotal, _exportPath));
            handler.PostDelayed(_exportTicker!, 120);
        });

        handler.Post(_exportTicker);
    }

    // ------------------------------------------------------------ 菜单

    private void OnMenuItem(int id)
    {
        if (id == Resource.Id.action_il)
        {
            _showIl = !_showIl;
            _toolbar.Menu.FindItem(Resource.Id.action_il)!.SetTitle(
                _showIl ? Resource.String.copy : Resource.String.show_il);
            Decompile();
        }
        else if (id == Resource.Id.action_members)
        {
            ShowMembers();
        }
        else if (id == Resource.Id.action_wrap)
        {
            SetWordWrap(!_wordWrap);
        }
        else if (id == Resource.Id.action_share)
        {
            if (string.IsNullOrEmpty(_plainText)) return;
            var share = new Intent(Intent.ActionSend);
            share.SetType("text/plain");
            share.PutExtra(Intent.ExtraText, _plainText);
            share.PutExtra(Intent.ExtraSubject, _title);
            StartActivity(Intent.CreateChooser(share, GetString(Resource.String.share)));
        }
        else if (id == Resource.Id.action_export_all)
        {
            new MaterialAlertDialogBuilder(this)!
                .SetTitle(Resource.String.export_all_cs)!
                .SetMessage(Resource.String.export_all_hint)!
                .SetNegativeButton(Resource.String.cancel, (IDialogInterfaceOnClickListener?)null)!
                .SetPositiveButton(Resource.String.ok, (_, _) => ExportAllSources())!
                .Show();
        }
    }

    private void ShowMembers()
    {
        if (_token != 0) return;
        var members = AppState.Decompiler?.GetMembers(_typeFullName) ?? new();
        if (members.Count == 0) return;

        var items = new string[members.Count];
        for (int i = 0; i < members.Count; i++)
            items[i] = $"[{members[i].Kind}] {members[i].Name}";

        new MaterialAlertDialogBuilder(this)!
            .SetTitle(Resource.String.members_header)!
            .SetItems(items, (_, e) =>
            {
                var picked = members[e.Which];
                StartActivity(IntentForMember(this, _typeFullName, picked.Name, picked.MetadataToken));
            })!
            .Show();
    }

    // ------------------------------------------------------------ 杂项

    public override void OnBackPressed()
    {
        if (_drawer.IsDrawerOpen((int)GravityFlags.Start))
        {
            _drawer.CloseDrawers();
            return;
        }

        base.OnBackPressed();
    }

    protected override void OnDestroy()
    {
        _exportCts?.Cancel();

        // 页面没了就别再对外声称"用户正在看 XX"
        AppState.ClearCurrent(this);

        base.OnDestroy();
    }

    private sealed class ClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }

    /// <summary>行号列只是跟随代码列滚动的装饰，自己不该响应触摸。</summary>
    private sealed class IgnoreTouchListener : Java.Lang.Object, View.IOnTouchListener
    {
        public bool OnTouch(View? v, MotionEvent? e) => true;
    }
}
