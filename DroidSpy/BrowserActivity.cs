using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Android.App;
using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using DroidSpy.Adapters;
using DroidSpy.Services;
using Google.Android.Material.AppBar;
using Google.Android.Material.Tabs;
using Google.Android.Material.TextField;

namespace DroidSpy;

/// <summary>程序集浏览器：类型列表 / 搜索 / 程序集信息 三页。</summary>
[Activity(
    Label = "@string/app_name",
    Theme = "@style/Theme.DroidSpy",
    ParentActivity = typeof(MainActivity))]
public class BrowserActivity : AppCompatActivity
{
    private View _pageTypes = null!, _pageSearch = null!, _pageInfo = null!;
    private RowAdapter _typeAdapter = null!, _searchAdapter = null!;
    private TextView _txtTypeCount = null!, _txtSearchHint = null!;
    private MaterialToolbar _toolbar = null!;

    private System.Threading.CancellationTokenSource? _searchCts;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SetContentView(Resource.Layout.activity_browser);

        if (!AppState.IsLoaded)
        {
            new Google.Android.Material.Dialog.MaterialAlertDialogBuilder(this)!
                .SetTitle(Resource.String.open_failed_title)!
                .SetMessage(Resource.String.not_managed)!
                .SetPositiveButton(Resource.String.ok, (Android.Content.IDialogInterfaceOnClickListener?)null)!
                .Show();
            Finish();
            return;
        }

        _toolbar = FindViewById<MaterialToolbar>(Resource.Id.toolbar)!;
        _toolbar.SetNavigationOnClickListener(new ClickListener(Finish));

        _pageTypes = FindViewById(Resource.Id.pageTypes)!;
        _pageSearch = FindViewById(Resource.Id.pageSearch)!;
        _pageInfo = FindViewById(Resource.Id.pageInfo)!;

        _txtTypeCount = FindViewById<TextView>(Resource.Id.txtTypeCount)!;
        _txtSearchHint = FindViewById<TextView>(Resource.Id.txtSearchHint)!;

        // ---- 页 1：类型列表
        _typeAdapter = new RowAdapter();
        _typeAdapter.ItemClick += (_, item) =>
        {
            if (item.Payload is TypeEntry t)
                StartActivity(CodeActivity.IntentForType(this, t.FullName, t.Name));
        };
        var typeList = FindViewById<RecyclerView>(Resource.Id.typeList)!;
        typeList.SetLayoutManager(new LinearLayoutManager(this));
        typeList.SetAdapter(_typeAdapter);

        // ---- 页 2：搜索
        _searchAdapter = new RowAdapter();
        _searchAdapter.ItemClick += (_, item) =>
        {
            if (item.Payload is TypeEntry t)
                StartActivity(CodeActivity.IntentForType(this, t.FullName, t.Name));
            else if (item.Payload is MemberHit hit)
                StartActivity(CodeActivity.IntentForMember(this, hit.TypeFullName, hit.Name, hit.Token));
        };
        var searchList = FindViewById<RecyclerView>(Resource.Id.searchList)!;
        searchList.SetLayoutManager(new LinearLayoutManager(this));
        searchList.SetAdapter(_searchAdapter);

        var editSearch = FindViewById<TextInputEditText>(Resource.Id.editSearch)!;
        editSearch.TextChanged += (_, e) => ScheduleSearch(e?.Text?.ToString() ?? string.Empty);

        // ---- 页 3：信息
        FillInfo();

        // ---- Tab
        var tabs = FindViewById<TabLayout>(Resource.Id.tabs)!;
        tabs.AddTab(tabs.NewTab()!.SetText(Resource.String.tab_types)!);
        tabs.AddTab(tabs.NewTab()!.SetText(Resource.String.tab_search)!);
        tabs.AddTab(tabs.NewTab()!.SetText(Resource.String.tab_info)!);
        tabs.TabSelected += (_, e) => ShowPage(e.Tab!.Position);
        ShowPage(0);

        FillTypes();
    }

    private void ShowPage(int index)
    {
        _pageTypes.Visibility = index == 0 ? ViewStates.Visible : ViewStates.Gone;
        _pageSearch.Visibility = index == 1 ? ViewStates.Visible : ViewStates.Gone;
        _pageInfo.Visibility = index == 2 ? ViewStates.Visible : ViewStates.Gone;

        _toolbar.Subtitle = index switch
        {
            0 => AppState.Summary?.FileName,
            1 => GetString(Resource.String.tab_search),
            _ => GetString(Resource.String.tab_info),
        };
    }

    private void FillTypes()
    {
        var types = AppState.Types ?? new List<TypeEntry>();
        _txtTypeCount.Text = GetString(Resource.String.type_count, types.Count);

        var rows = new List<RowItem>(types.Count);
        foreach (var t in types)
        {
            rows.Add(new RowItem(
                Badge: ShortBadge(t.Kind),
                Title: t.Name,
                Subtitle: string.IsNullOrEmpty(t.Namespace) ? t.Kind : $"{t.Namespace} · {t.Kind}",
                Payload: t));
        }

        _typeAdapter.Submit(rows);
    }

    private static string ShortBadge(string kind) => kind switch
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

    // ------------------------------------------------------------ 搜索

    private void ScheduleSearch(string query)
    {
        _searchCts?.Cancel();
        _searchCts = new System.Threading.CancellationTokenSource();
        var token = _searchCts.Token;

        if (string.IsNullOrWhiteSpace(query))
        {
            _txtSearchHint.SetText(Resource.String.search_tip);
            _searchAdapter.Submit(Array.Empty<RowItem>());
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await Task.Delay(260, token); // 防抖
                if (token.IsCancellationRequested) return;

                var rows = new List<RowItem>();
                var svc = AppState.Decompiler;
                if (svc == null) return;

                foreach (var t in svc.SearchTypes(query))
                {
                    rows.Add(new RowItem(
                        Badge: ShortBadge(t.Kind),
                        Title: t.Name,
                        Subtitle: t.FullName,
                        Payload: t));
                }

                foreach (var (typeFullName, member) in svc.SearchMembers(query))
                {
                    rows.Add(new RowItem(
                        Badge: "MEM",
                        Title: member.Name,
                        Subtitle: $"{typeFullName} · {member.Kind}",
                        Payload: new MemberHit(typeFullName, member.Name, member.MetadataToken)));
                }

                if (token.IsCancellationRequested) return;

                RunOnUiThread(() =>
                {
                    if (token.IsCancellationRequested) return;
                    _searchAdapter.Submit(rows);
                    _txtSearchHint.Text = rows.Count == 0
                        ? GetString(Resource.String.no_result)
                        : GetString(Resource.String.search_result_count, rows.Count);
                });
            }
            catch (TaskCanceledException)
            {
                // 输入变化导致的取消，忽略
            }
            catch (Exception ex)
            {
                RunOnUiThread(() => _txtSearchHint.Text = ex.Message);
            }
        }, token);
    }

    public sealed record MemberHit(string TypeFullName, string Name, int Token);

    // ------------------------------------------------------------ 信息页

    private void FillInfo()
    {
        var s = AppState.Summary;
        if (s == null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"文件：{s.FileName}");
        sb.AppendLine($"大小：{AssemblyStore.FormatSize(s.FileSize)}");
        sb.AppendLine($"元数据版本：{s.RuntimeVersion}");
        sb.AppendLine($"目标框架：{s.TargetFramework}");
        sb.AppendLine($"体系结构：{s.Architecture}");
        sb.AppendLine($"强名称签名：{(s.IsSigned ? "是" : "否")}");
        sb.AppendLine($"类型数：{s.TypeCount}");
        sb.AppendLine($"方法数：{s.MethodCount}");
        sb.AppendLine($"字段数：{s.FieldCount}");
        sb.AppendLine($"引用程序集数：{s.ReferenceCount}");

        FindViewById<TextView>(Resource.Id.txtInfoBasic)!.Text = sb.ToString();

        var refs = new StringBuilder();
        if (s.ReferencedAssemblies.Count == 0)
            refs.Append("（无）");
        else
            foreach (var r in s.ReferencedAssemblies)
                refs.AppendLine(r);

        FindViewById<TextView>(Resource.Id.txtRefs)!.Text = refs.ToString();
    }

    private sealed class ClickListener : Java.Lang.Object, View.IOnClickListener
    {
        private readonly Action _action;
        public ClickListener(Action action) => _action = action;
        public void OnClick(View? v) => _action();
    }
}
