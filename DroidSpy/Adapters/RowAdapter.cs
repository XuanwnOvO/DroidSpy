using System;
using System.Collections.Generic;
using Android.Views;
using Android.Widget;
using AndroidX.RecyclerView.Widget;

namespace DroidSpy.Adapters;

/// <summary>列表行的数据。</summary>
public sealed record RowItem(string Badge, string Title, string Subtitle, object? Payload = null);

/// <summary>通用行列表适配器，供最近文件 / 类型 / 成员 / 搜索结果共用。</summary>
public sealed class RowAdapter : RecyclerView.Adapter
{
    private readonly List<RowItem> _items = new();

    public event EventHandler<RowItem>? ItemClick;

    public override int ItemCount => _items.Count;

    public void Submit(IEnumerable<RowItem> items)
    {
        _items.Clear();
        _items.AddRange(items);
        NotifyDataSetChanged();
    }

    public override RecyclerView.ViewHolder OnCreateViewHolder(ViewGroup parent, int viewType)
    {
        var view = LayoutInflater.From(parent.Context)!
            .Inflate(Resource.Layout.item_row, parent, false)!;
        return new RowHolder(view, OnRowClick);
    }

    public override void OnBindViewHolder(RecyclerView.ViewHolder holder, int position)
    {
        var h = (RowHolder)holder;
        var item = _items[position];
        h.Bind(item);
    }

    private void OnRowClick(RowItem item) => ItemClick?.Invoke(this, item);

    private sealed class RowHolder : RecyclerView.ViewHolder
    {
        private readonly TextView _badge;
        private readonly TextView _title;
        private readonly TextView _subtitle;
        private readonly Action<RowItem> _callback;
        private RowItem? _current;

        public RowHolder(View view, Action<RowItem> callback) : base(view)
        {
            _badge = view.FindViewById<TextView>(Resource.Id.rowBadge)!;
            _title = view.FindViewById<TextView>(Resource.Id.rowTitle)!;
            _subtitle = view.FindViewById<TextView>(Resource.Id.rowSubtitle)!;
            _callback = callback;

            // 用 ViewHolder 缓存的数据回调，避免复用时的 position 错位
            view.Clickable = true;
            view.Click += (_, _) =>
            {
                if (_current != null) _callback(_current);
            };
        }

        public void Bind(RowItem item)
        {
            _current = item;
            _badge.Text = item.Badge;
            _title.Text = item.Title;
            _subtitle.Text = item.Subtitle;
            _subtitle.Visibility = string.IsNullOrEmpty(item.Subtitle)
                ? ViewStates.Gone
                : ViewStates.Visible;
        }
    }
}
