using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Provider;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using DroidSpy.Adapters;
using DroidSpy.Services;
using Google.Android.Material.AppBar;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;

namespace DroidSpy;

/// <summary>
/// 批量反编译：选一个文件夹，把里面所有 .NET 程序集逐个反编译成 C# 源码。
///
/// 和 dnSpy 的「导出到项目」对齐，两点保持一致：
///   1. 每个程序集在目标目录下单独占一个子目录，同名命名空间 / 类型不会互相覆盖；
///   2. 子目录里面按命名空间层级展开，一个类型一个 .cs 文件。
///
/// 和单个程序集导出不同，这里不能走 AppState —— 它全局只保留一份反编译会话，
/// 用完一个 DLL 就得把上一个 Dispose 掉。所以每个 DLL 现建一个 DecompilerService。
/// </summary>
[Activity(Label = "@string/batch_title", Theme = "@style/Theme.DroidSpy")]
public class BatchActivity : AppCompatActivity
{
    /// <summary>用户提供的文件夹。</summary>
    public const string ExtraSourceTree = "source_tree";

    private const int RequestTarget = 2001;

    /// <summary>扫描出来的待处理文件。已经过文件头嗅探，全是托管程序集。</summary>
    private List<Staged> _files = new();

    private ListView _list = null!;
    private TextView _empty = null!;
    private TextView _hint = null!;
    private MaterialButton _btnStart = null!;
    private BatchAdapter _adapter = null!;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_batch);

        var toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        toolbar.SetNavigationOnClickListener(new ClickListener(() => Finish()));

        _list = FindViewById<ListView>(Resource.Id.batchList)!;
        _empty = FindViewById<TextView>(Resource.Id.txtBatchEmpty)!;
        _hint = FindViewById<TextView>(Resource.Id.txtBatchHint)!;
        _btnStart = FindViewById<MaterialButton>(Resource.Id.btnBatchStart)!;

        _adapter = new BatchAdapter(this);
        _list.Adapter = _adapter;
        _btnStart.Click += (_, _) => PickTargetFolder();

        var uri = savedInstanceState?.GetString(ExtraSourceTree)
                  ?? Intent?.GetStringExtra(ExtraSourceTree);

        if (uri == null)
        {
            Finish();
            return;
        }

        Scan(Android.Net.Uri.Parse(uri)!);
    }

    protected override void OnSaveInstanceState(Bundle outState)
    {
        base.OnSaveInstanceState(outState);
        // 转屏会重建 Activity，扫描结果没必要重跑一遍
        outState.PutString(ExtraSourceTree, _adapter.SourceUri?.ToString());
    }

    // ------------------------------------------------------------ 扫描

    /// <summary>列出文件夹里所有像 .NET 程序集的文件，逐个用文件头确认。</summary>
    private void Scan(Android.Net.Uri treeUri)
    {
        var dialog = new MaterialAlertDialogBuilder(this)!
            .SetMessage(Resource.String.batch_scanning)!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();

        Task.Run(() =>
        {
            var hits = new List<Staged>();
            var skipped = new List<string>();
            int scanned = 0, peCount = 0, skippedCount = 0;
            string? error = null;

            try
            {
                var all = SafTreeReader.ListFiles(this, treeUri);
                scanned = all.Count;

                // 所有候选统一落到同一个暂存目录：Unity 的 DLL 之间互相引用
                // （Assembly-CSharp.dll 引 UnityEngine.dll 之类），
                // 引擎会把所在目录当引用搜索路径，放在一起才解析得出来。
                var stage = PrepareStage();

                foreach (var file in all)
                {
                    // .dll / .exe 之外的一律不看：Unity 目录里 .assets / .resS
                    // 动辄上千个，每个都去读文件头纯属浪费
                    if (!LooksLikeAssembly(file.Name)) continue;
                    peCount++;

                    var local = Path.Combine(stage, UniqueName(file.Name));
                    if (!TryStage(file.Uri, local)) continue;

                    if (AssemblyStore.SniffKind(local) == AssemblyStore.FileKind.ManagedAssembly)
                    {
                        hits.Add(new Staged(file.Name, local, file.Size));
                    }
                    else if (skippedCount < 5)
                    {
                        // 只留前几个当例子，几千个原生 dll 全塞进提示里也没人看
                        skipped.Add(file.Name);
                        skippedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }

            RunOnUiThread(() =>
            {
                dialog.Dismiss();

                if (error != null)
                {
                    Toast.MakeText(this, error, ToastLength.Long)?.Show();
                    Finish();
                    return;
                }

                if (hits.Count == 0)
                {
                    new MaterialAlertDialogBuilder(this)!
                        .SetTitle(Resource.String.batch_none)!
                        .SetMessage(GetString(Resource.String.batch_none_detail, scanned, peCount))!
                        .SetPositiveButton(Resource.String.ok, (_, _) => Finish())!
                        .SetCancelable(false)!
                        .Show();
                    return;
                }

                _files = hits;
                _adapter.Submit(hits, treeUri);

                _empty.Visibility = ViewStates.Gone;
                _list.Visibility = ViewStates.Visible;
                _hint.Text = GetString(Resource.String.batch_found, hits.Count);
                _btnStart.Enabled = true;
                _btnStart.Text = GetString(Resource.String.batch_start, hits.Count);

                if (skippedCount > 0)
                {
                    Toast.MakeText(this,
                        GetString(Resource.String.batch_skipped, skippedCount,
                            string.Join("、", skipped)),
                        ToastLength.Long)?.Show();
                }
            });
        });
    }

    private static bool LooksLikeAssembly(string name) =>
        name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>扫描阶段暂存下来的一个程序集：显示名 + 暂存目录里的路径。</summary>
    private sealed record Staged(string Name, string LocalPath, long Size);

    /// <summary>建立（或清空）暂存目录，返回它的路径。</summary>
    private string PrepareStage()
    {
        var dir = Path.Combine(CacheDir!.AbsolutePath!, "batch");
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, true); }
            catch { /* 删不掉就接着用，文件名会自增绕开重名 */ }
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把 SAF 里的文件拷到本地，失败返回 false（个别读不出来不该中断整次扫描）。</summary>
    private bool TryStage(Android.Net.Uri uri, string localPath)
    {
        try
        {
            using var input = ContentResolver!.OpenInputStream(uri);
            if (input == null) return false;

            using var output = File.Create(localPath);
            input.CopyTo(output);
            return true;
        }
        catch
        {
            try { File.Delete(localPath); } catch { /* 半截文件，删不掉也无妨 */ }
            return false;
        }
    }

    /// <summary>暂存目录里的重名文件按 _2 / _3 自增让路。</summary>
    private static string UniqueName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;
        int n = 2;
        while (_stagedNames.Contains(candidate)) candidate = $"{name}_{n++}{ext}";
        _stagedNames.Add(candidate);
        return candidate;
    }

    private static readonly HashSet<string> _stagedNames = new(StringComparer.OrdinalIgnoreCase);

    private void ClearStage()
    {
        _stagedNames.Clear();
        try { Directory.Delete(Path.Combine(CacheDir!.AbsolutePath!, "batch"), true); }
        catch { /* 缓存目录，清不掉就留给系统回收 */ }
    }

    // ------------------------------------------------------------ 选目标目录

    private void PickTargetFolder()
    {
        if (_files.Count == 0) return;

        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.PutExtra("android:grantWrite", true);
        StartActivityForResult(intent, RequestTarget);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode != RequestTarget || resultCode != Result.Ok || data?.Data == null) return;
        RunBatch(data.Data);
    }

    // ------------------------------------------------------------ 批量反编译

    // 后台线程只更新这几个字段，界面由定时器按固定节奏读
    private volatile int _doneAsm;
    private volatile int _doneFiles;
    private volatile int _filesInAsm;
    private volatile int _totalInAsm;
    private volatile string _currentAsm = "";
    private volatile int _totalAsm;

    private CancellationTokenSource? _cts;
    private Java.Lang.Runnable? _ticker;

    private void RunBatch(Android.Net.Uri targetUri)
    {
        var files = _files;
        _totalAsm = files.Count;
        _doneAsm = 0;
        _doneFiles = 0;
        _filesInAsm = 0;
        _totalInAsm = 0;
        _currentAsm = files.Count > 0 ? files[0].Name : "";

        _btnStart.Enabled = false;

        var dialog = new MaterialAlertDialogBuilder(this)!
            .SetTitle(Resource.String.batch_title)!
            .SetMessage(GetString(Resource.String.batch_progress,
                _currentAsm, _totalAsm, 0, 0, 0))!
            .SetNegativeButton(Resource.String.cancel, (_, _) => _cts?.Cancel())!
            .SetCancelable(false)!
            .Create()!;
        dialog.Show();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        StartTicker(dialog, token);

        Task.Run(() =>
        {
            int okAsm = 0, okFiles = 0;
            string? firstError = null;
            string? firstName = null;

            try
            {
                // 写盘用的 tree，整批任务共用一个，内部缓存已经建过的目录
                var writer = new SafTreeWriter(this, targetUri);

                // 解包出来的程序集也落回扫描时的暂存目录：它们和别的 DLL 在
                // 同一个目录，引擎才能顺着这把引用解析出来
                var stage = Path.Combine(CacheDir!.AbsolutePath!, "batch");
                var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();

                    _currentAsm = file.Name;
                    _filesInAsm = 0;
                    _totalInAsm = 0;

                    var unpackDir = Path.Combine(stage,
                        UniqueDirName(taken, file.Name));

                    try
                    {
                        okFiles += DecompileOne(writer, file, unpackDir, token);
                        okAsm++;
                    }
                    catch (System.OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 一个 DLL 失败不该拖垮整批，记下第一个失败原因继续走
                        firstError ??= $"{ex.GetType().Name}: {ex.Message}";
                        firstName ??= file.Name;
                    }
                    finally
                    {
                        TryDeleteDir(unpackDir);
                    }

                    _doneAsm++;
                }

                var finalAsm = okAsm;
                var finalFiles = okFiles;
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishBatch();
                    if (finalAsm > 0)
                    {
                        Toast.MakeText(this,
                            GetString(Resource.String.batch_done, finalAsm, finalFiles),
                            ToastLength.Long)?.Show();
                    }
                    else
                    {
                        new MaterialAlertDialogBuilder(this)!
                            .SetTitle(Resource.String.batch_title)!
                            .SetMessage(GetString(Resource.String.batch_failed,
                                firstName ?? "?", firstError ?? "?"))!
                            .SetPositiveButton(Resource.String.ok,
                                (IDialogInterfaceOnClickListener?)null)!
                            .Show();
                    }
                });
            }
            catch (System.OperationCanceledException)
            {
                // 已经落盘的文件都是完整的，如实报个数
                var finalAsm = _doneAsm;
                var finalFiles = _doneFiles;
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishBatch();
                    Toast.MakeText(this,
                        GetString(Resource.String.batch_done_stopped, finalAsm, finalFiles),
                        ToastLength.Long)?.Show();
                });
            }
            catch (Exception ex)
            {
                RunOnUiThread(() =>
                {
                    dialog.Dismiss();
                    FinishBatch();
                    new MaterialAlertDialogBuilder(this)!
                        .SetTitle(Resource.String.batch_title)!
                        .SetMessage(ex.Message)!
                        .SetPositiveButton(Resource.String.ok,
                            (IDialogInterfaceOnClickListener?)null)!
                        .Show();
                });
            }
        }, token);
    }

    /// <summary>
    /// 反编译一个程序集并写进它在目标目录里的子目录，返回写出的文件数。
    ///
    /// 导出路径统一是「程序集名/命名空间目录/类型.cs」：外层那一层由这里加，
    /// 内层那一层由引擎给出的相对路径决定。
    /// </summary>
    private int DecompileOne(SafTreeWriter writer, Staged file, string unpackDir,
        CancellationToken token)
    {
        // 目标目录里给这个 DLL 单开一间，避免不同程序集的同名命名空间撞在一起
        var subDir = writer.EnsureChildDir(
            Path.GetFileNameWithoutExtension(file.Name));

        var path = Materialize(file, unpackDir);

        // 引擎认路径不认 content://，解出来的程序集就直接从暂存目录里读；
        // 暂存目录里所有的 DLL 会被 Load 当成引用搜索路径，互相解析得开
        using var svc = new DecompilerService();
        svc.Load(path);

        int written = 0;
        svc.ExportAllSources((rel, code) =>
        {
            token.ThrowIfCancellationRequested();

            var target = EnsureSubDir(subDir, rel);

            using var output = ContentResolver!.OpenOutputStream(
                CreateCodeDocument(target, Path.GetFileName(rel))!)!;
            var bytes = System.Text.Encoding.UTF8.GetBytes(code);
            output.Write(bytes, 0, bytes.Length);
            output.Flush();

            _filesInAsm = ++written;
            _doneFiles++;
            return true;
        },
        (current, total, _) => _totalInAsm = total,
        token);

        return written;
    }

    /// <summary>
    /// 按相对路径在某个 DLL 的子目录下逐级建目录，返回最里层那个目录的 URI。
    ///
    /// 建过的目录记在 <see cref="_subDirs"/> 里。一个程序集里同一命名空间往往
    /// 有几十个类型，不缓存就是几十次重复的 DocumentsProvider 往返 ——
    /// 整批几百个 DLL 跑下来，光建目录就能占掉大半时间。
    /// </summary>
    private Android.Net.Uri EnsureSubDir(Android.Net.Uri root, string relPath)
    {
        var relDir = Path.GetDirectoryName(relPath) ?? string.Empty;
        if (relDir.Length == 0) return root;

        var key = $"{root}|{relDir}";
        if (_subDirs.TryGetValue(key, out var cached)) return cached;

        var parent = EnsureSubDir(root, relDir);
        var uri = FindOrCreateDir(parent, Path.GetFileName(relDir));
        _subDirs[key] = uri;
        return uri;
    }

    private readonly Dictionary<string, Android.Net.Uri> _subDirs = new(StringComparer.Ordinal);

    private Android.Net.Uri FindOrCreateDir(Android.Net.Uri parent, string name)
    {
        var children = DocumentsContract.BuildChildDocumentsUriUsingTree(
            parent, DocumentsContract.GetDocumentId(parent));

        if (children != null)
        {
            string[] columns =
            {
                DocumentsContract.Document.ColumnDocumentId,
                DocumentsContract.Document.ColumnDisplayName,
            };

            using var cursor = ContentResolver!.Query(children, columns, null, null, null);
            while (cursor != null && cursor.MoveToNext())
            {
                if (!string.Equals(cursor.GetString(1), name, StringComparison.Ordinal))
                    continue;

                return DocumentsContract.BuildDocumentUriUsingTree(parent, cursor.GetString(0))!;
            }
        }

        return DocumentsContract.CreateDocument(
            ContentResolver!, parent, DocumentsContract.Document.MimeTypeDir, name)
            ?? throw new IOException($"无法创建目录 {name}");
    }

    /// <summary>
    /// 建一个 .cs 文件。用 "text/x-csharp" 而不是 "text/plain"，否则 Downloads /
    /// DocumentsUI 会按自己的 MIME 表再补一个 .txt 后缀（详见 SafTreeWriter）。
    /// 认不出这个类型的 Provider 则退回 text/plain，至少保证能写成功。
    /// </summary>
    private Android.Net.Uri CreateCodeDocument(Android.Net.Uri parent, string fileName)
    {
        try
        {
            return DocumentsContract.CreateDocument(
                ContentResolver!, parent, "text/x-csharp", fileName)!;
        }
        catch (Exception ex) when (ex is Java.Lang.IllegalArgumentException
                                      or Java.Lang.UnsupportedOperationException)
        {
            return DocumentsContract.CreateDocument(
                ContentResolver!, parent, "text/plain", fileName)!;
        }
    }

    /// <summary>
    /// 拿到一个可以送进反编译引擎的真实路径。
    ///
    /// 扫描阶段已经把文件拷进暂存目录了，这里要么原样返回（裸 dll/exe），
    /// 要么把资源包解开、返回里面的托管程序集。解出来的文件也留在暂存目录下，
    /// 这样同一批里其它程序集引用到它时同样能解析。
    /// </summary>
    private static string Materialize(Staged file, string unpackDir)
    {
        // 裸程序集会被 ExtractAll 原样返回，连目录都不用建
        if (AssemblyStore.SniffKind(file.LocalPath) == AssemblyStore.FileKind.ManagedAssembly)
            return file.LocalPath;

        Directory.CreateDirectory(unpackDir);
        var result = UnityBundleReader.ExtractAll(file.LocalPath, unpackDir);
        return result.PrimaryPath;
    }

    private static string UniqueDirName(HashSet<string> taken, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var candidate = name;
        int n = 2;
        while (!taken.Add(candidate)) candidate = $"{name}_{n++}";
        return candidate;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        catch { /* 缓存目录，清理失败不影响结果 */ }
    }

    private void FinishBatch()
    {
        _cts?.Dispose();
        _cts = null;
        _btnStart.Enabled = true;
        _files.Clear();
        _subDirs.Clear();
        ClearStage();
        _adapter.Submit(new List<Staged>(), _adapter.SourceUri);
        _list.Visibility = ViewStates.Gone;
        _empty.SetText(Resource.String.batch_done_hint);
        _empty.Visibility = ViewStates.Visible;
    }

    /// <summary>同 CodeActivity：反编译是同步的，进度只能靠定时器按节奏刷。</summary>
    private void StartTicker(AndroidX.AppCompat.App.AlertDialog dialog, CancellationToken token)
    {
        var handler = new Handler(Looper.MainLooper!);

        void Stop()
        {
            if (_ticker != null)
            {
                handler.RemoveCallbacks(_ticker);
                _ticker = null;
            }
        }

        token.Register(() => RunOnUiThread(Stop));

        _ticker = new Java.Lang.Runnable(() =>
        {
            if (token.IsCancellationRequested || !dialog.IsShowing)
            {
                Stop();
                return;
            }

            dialog.SetMessage(GetString(Resource.String.batch_progress,
                _currentAsm, _totalAsm, _doneAsm, _filesInAsm, _totalInAsm));
            handler.PostDelayed(_ticker!, 120);
        });

        handler.Post(_ticker);
    }

    private sealed class ClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }

    /// <summary>待处理 / 已完成清单。</summary>
    private sealed class BatchAdapter : BaseAdapter<string>
    {
        private readonly Activity _ctx;
        private readonly List<string> _rows = new();

        public Android.Net.Uri? SourceUri { get; private set; }

        public BatchAdapter(Activity ctx) => _ctx = ctx;

        public void Submit(List<Staged> files, Android.Net.Uri? sourceUri)
        {
            SourceUri = sourceUri;
            _rows.Clear();
            _rows.AddRange(files.Select(f => f.Size > 0
                ? $"{f.Name}  ·  {AssemblyStore.FormatSize(f.Size)}"
                : f.Name));
            NotifyDataSetChanged();
        }

        public override int Count => _rows.Count;
        public override string this[int position] => _rows[position];
        public override long GetItemId(int position) => position;

        public override View GetView(int position, View? convertView, ViewGroup? parent)
        {
            var view = convertView ?? _ctx.LayoutInflater!.Inflate(
                Android.Resource.Layout.SimpleListItem1, parent, false)!;
            view.FindViewById<TextView>(Android.Resource.Id.Text1)!.Text = _rows[position];
            return view;
        }
    }
}
