using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Views;

/// <summary>已下载页一行数据。</summary>
public class DownloadedRow
{
    public string WorkId { get; init; } = "";
    public string WorkName { get; init; } = "";
    public string MakerName { get; init; } = "";
    public string WorkType { get; init; } = "";
    public string State { get; init; } = "";      // 数据库原始状态值（右键菜单/着色/筛选用）
    public string StateText { get; init; } = "";  // 显示译文
    public Brush StateBrush { get; init; } = Brushes.Gray;
    public string DownTime { get; init; } = "";
    public string OpenText => I18n.Tr("跳转作品");
    // 行内「标记为已品悦」：仅"已下载"状态显示（对齐 Web drawDownloaded 的 .mini 按钮；右键菜单仍保留）
    public string MarkText => I18n.Tr("标记为已品悦");
    public Visibility MarkVisibility => State == "已下载" ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>已下载页（对应 Python 版 downloaded_UI.py）。</summary>
public partial class DownloadedPage : UserControl
{
    /// <summary>懒加载每批条数。</summary>
    private const int PageSize = 50;

    // works.state（数据库原始值）-> 显示画刷（冻结后全表行共用，避免每行新建）
    private static readonly Dictionary<string, Brush> StateBrushes = new()
    {
        ["已品悦"] = MakeBrush("#4ade80"),
        ["已下载"] = MakeBrush("#60a5fa"),
        ["下载中"] = MakeBrush("#facc15"),
    };

    // 状态筛选下拉的取值（数据库原始值，空串 = 全部）
    private static readonly string[] StateFilterValues = ["", "下载中", "已下载", "已品悦"];

    private List<DownloadedRow> _all = [];                          // 数据库全量
    private List<DownloadedRow> _filtered = [];                     // 过滤 + 排序后
    private readonly ObservableCollection<DownloadedRow> _items = [];  // 已加载进列表的部分
    private int _loaded;
    private string _keyword = "";
    private string _stateFilter = "";
    private string? _sortProp;
    private ListSortDirection _sortDirection;

    // 搜索输入防抖：连续输入时只在停顿 250ms 后重建一次过滤视图，
    // 避免每敲一个字就对全量 _all 过滤+排序+重填列表。
    private readonly DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    // 表头 -> (列标题原文, 排序属性)
    private readonly (TextBlock Header, string Title, string Prop)[] _headers;

    /// <summary>点击"← 下载"按钮时触发，由主窗口切回下载视图。</summary>
    public event Action? BackToDownloadRequested;

    /// <summary>行内"跳转作品"：由主窗口切到媒体库并打开该作品详情（对齐 Web gotoWork）。</summary>
    public event Action<string>? OpenWorkRequested;

    private void OpenWork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadedRow row)
            OpenWorkRequested?.Invoke(row.WorkId);
    }

    public DownloadedPage()
    {
        InitializeComponent();
        WorksList.ItemsSource = _items;
        _headers =
        [
            (HdrId, "RJ号", nameof(DownloadedRow.WorkId)),
            (HdrName, "作品名称", nameof(DownloadedRow.WorkName)),
            (HdrMaker, "社团", nameof(DownloadedRow.MakerName)),
            (HdrType, "类型", nameof(DownloadedRow.WorkType)),
            (HdrState, "状态", nameof(DownloadedRow.StateText)),
            (HdrTime, "下载时间", nameof(DownloadedRow.DownTime)),
        ];
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RebuildView();
        };
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private static Brush MakeBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void RetranslateUi()
    {
        RefreshButton.Content = I18n.Tr("刷新");
        BackButton.Content = "← " + I18n.Tr("下载");
        SearchBox.ToolTip = I18n.Tr("搜索 RJ号 / 作品名 / 社团");
        MarkMenuItem.Header = I18n.Tr("标记为已品悦");
        EmptyLabel.Text = I18n.Tr("没有内容");
        UpdateHeaders();

        // 重建状态筛选下拉，保持当前选中项
        var index = Array.IndexOf(StateFilterValues, _stateFilter);
        StateFilter.ItemsSource = StateFilterValues
            .Select(s => s.Length == 0 ? I18n.Tr("全部") : I18n.Tr(s)).ToList();
        StateFilter.SelectedIndex = index < 0 ? 0 : index;

        Refresh();
    }

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
            Refresh();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void BackButton_Click(object sender, RoutedEventArgs e) =>
        BackToDownloadRequested?.Invoke();

    private void Refresh()
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"state\", \"down_time\" " +
            "FROM \"works\" ORDER BY \"down_time\" DESC, \"work_id\" DESC");
        if (rows == null)
            return;
        _all = rows.Select(row =>
        {
            var state = row[4] as string ?? "";
            var downTime = row[5]?.ToString() ?? "";
            return new DownloadedRow
            {
                WorkId = row[0] as string ?? "",
                WorkName = row[1] as string ?? "",
                MakerName = row[2] as string ?? "",
                WorkType = row[3] as string ?? "",
                State = state,
                StateText = I18n.Tr(state),
                StateBrush = StateBrushes.GetValueOrDefault(state, Brushes.LightGray),
                DownTime = downTime.Length > 19 ? downTime[..19] : downTime,  // 不显示微秒
            };
        }).ToList();
        RebuildView();
    }

    /// <summary>过滤 + 排序后重置懒加载，从第一批重新填充列表。</summary>
    private void RebuildView()
    {
        IEnumerable<DownloadedRow> rows = _all.Where(FilterRow);
        if (_sortProp != null)
        {
            Func<DownloadedRow, string> key = _sortProp switch
            {
                nameof(DownloadedRow.WorkId) => r => r.WorkId,
                nameof(DownloadedRow.WorkName) => r => r.WorkName,
                nameof(DownloadedRow.MakerName) => r => r.MakerName,
                nameof(DownloadedRow.WorkType) => r => r.WorkType,
                nameof(DownloadedRow.StateText) => r => r.StateText,
                _ => r => r.DownTime,
            };
            rows = _sortDirection == ListSortDirection.Ascending
                ? rows.OrderBy(key, StringComparer.CurrentCultureIgnoreCase)
                : rows.OrderByDescending(key, StringComparer.CurrentCultureIgnoreCase);
        }
        _filtered = rows.ToList();
        _items.Clear();
        _loaded = 0;
        LoadMore();
    }

    /// <summary>追加加载下一批（每批 PageSize 条）。</summary>
    private void LoadMore()
    {
        foreach (var row in _filtered.Skip(_loaded).Take(PageSize))
            _items.Add(row);
        _loaded = Math.Min(_loaded + PageSize, _filtered.Count);
        UpdateCount();
    }

    /// <summary>滚动接近底部时加载下一批。</summary>
    private void WorksList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_loaded >= _filtered.Count)
            return;
        if (e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 300)
            LoadMore();
    }

    /// <summary>搜索关键字（RJ号/作品名/社团）+ 状态下拉的组合过滤。</summary>
    private bool FilterRow(DownloadedRow row)
    {
        if (_stateFilter.Length > 0 && row.State != _stateFilter)
            return false;
        if (_keyword.Length == 0)
            return true;
        return row.WorkId.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
               || row.WorkName.Contains(_keyword, StringComparison.OrdinalIgnoreCase)
               || row.MakerName.Contains(_keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _keyword = SearchBox.Text.Trim();
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void StateFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = StateFilter.SelectedIndex;
        _stateFilter = index >= 0 && index < StateFilterValues.Length ? StateFilterValues[index] : "";
        RebuildView();
    }

    private void UpdateCount()
    {
        var total = _all.Count;
        var matched = _filtered.Count;
        var text = matched == total
            ? I18n.Format(I18n.Tr("共 {total} 个作品"), ("total", total))
            : I18n.Format(I18n.Tr("共 {total} 个作品，匹配 {matched} 个"),
                ("total", total), ("matched", matched));
        if (_loaded < matched)
            text += I18n.Format(I18n.Tr("（已加载 {loaded}）"), ("loaded", _loaded));
        CountLabel.Text = text;
    }

    // ---------- 表头排序 ----------

    /// <summary>点击表头：同列时升/降序切换，换列时按该列升序。</summary>
    private void Header_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Tag: string prop })
            return;
        _sortDirection = _sortProp == prop && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortProp = prop;
        UpdateHeaders();
        RebuildView();
    }

    /// <summary>刷新表头文字：译文 + 当前排序列的方向箭头。</summary>
    private void UpdateHeaders()
    {
        foreach (var (header, title, prop) in _headers)
        {
            var suffix = _sortProp == prop
                ? _sortDirection == ListSortDirection.Ascending ? " ▲" : " ▼"
                : "";
            header.Text = I18n.Tr(title) + suffix;
        }
    }

    // ---------- 右键菜单 ----------

    /// <summary>右键先选中鼠标下的行，再弹菜单（ListBox 不会自动选中）。</summary>
    private void WorksList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var node = e.OriginalSource as DependencyObject;
        while (node != null && node is not ListBoxItem)
            node = VisualTreeHelper.GetParent(node);
        if (node is ListBoxItem item)
            item.IsSelected = true;
    }

    /// <summary>右键菜单仅对"已下载"状态的作品开放。</summary>
    private void WorksList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (WorksList.SelectedItem is not DownloadedRow row || row.State != "已下载")
            e.Handled = true;
    }

    private void MarkEnjoyed_Click(object sender, RoutedEventArgs e)
    {
        if (WorksList.SelectedItem is DownloadedRow row)
            MarkEnjoyed(row);
    }

    private void MarkRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DownloadedRow row })
            MarkEnjoyed(row);
    }

    private void MarkEnjoyed(DownloadedRow row)
    {
        if (row.State != "已下载")
            return;
        Db.Execute("UPDATE \"works\" SET \"state\" = '已品悦' WHERE \"work_id\" = @w", ("@w", row.WorkId));
        Refresh();
    }
}
