using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>下载页子行：单个分卷文件。</summary>
public class DownloadFileItem : ObservableBase
{
    public string Uuid { get; init; } = "";

    /// <summary>原始网盘下载链接（点击"复制链接"复制此值）。</summary>
    public string Url { get; set; } = "";

    private string _fileName = "";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    /// <summary>作品内相对路径（含子目录），用于构建目录树；论坛源即文件名。</summary>
    public string RelPath { get; set; } = "";

    private double _pct;
    public double Pct { get => _pct; set => Set(ref _pct, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    // 解析失败原因（仅解析失败的分卷显示）
    private string _errorReason = "";
    public string ErrorReason { get => _errorReason; set => Set(ref _errorReason, value); }

    private Visibility _errorVisibility = Visibility.Collapsed;
    public Visibility ErrorVisibility { get => _errorVisibility; set => Set(ref _errorVisibility, value); }

    public string CopyUrlText => I18n.Tr("复制链接");
}

/// <summary>下载页目录树的文件夹节点（子项可为文件夹或文件，递归展示作品内目录层级）。</summary>
public class DownloadFolderItem : ObservableBase
{
    public string Name { get; init; } = "";

    /// <summary>子节点：DownloadFolderItem（子目录）或 DownloadFileItem（文件）混合。</summary>
    public ObservableCollection<object> Children { get; } = [];

    private bool _isExpanded = true;   // 默认展开，便于直接看到文件
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                Raise(nameof(ChildrenVisibility));
        }
    }

    public Visibility ChildrenVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 下载页一级：下载源分区（对齐 Web .dlsec）。各源并行下载（见 DownloadLanes），分区默认折叠，
/// 标题行显示该源的汇总：下载中/等待/失败的文件数、已完成数、作品数与合计速度。
/// </summary>
public class DownloadSectionItem : ObservableBase
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                Raise(nameof(Arrow));
        }
    }

    public string Arrow => IsExpanded ? "▾" : "▸";

    private string _downloadingText = "";
    public string DownloadingText
    {
        get => _downloadingText;
        set
        {
            if (Set(ref _downloadingText, value))
                Raise(nameof(DownloadingVisibility));
        }
    }

    public Visibility DownloadingVisibility => DownloadingText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _waitingText = "";
    public string WaitingText
    {
        get => _waitingText;
        set
        {
            if (Set(ref _waitingText, value))
                Raise(nameof(WaitingVisibility));
        }
    }

    public Visibility WaitingVisibility => WaitingText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _failedText = "";
    public string FailedText
    {
        get => _failedText;
        set
        {
            if (Set(ref _failedText, value))
                Raise(nameof(FailedVisibility));
        }
    }

    public Visibility FailedVisibility => FailedText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private string _doneText = "";
    public string DoneText { get => _doneText; set => Set(ref _doneText, value); }

    private string _worksText = "";
    public string WorksText { get => _worksText; set => Set(ref _worksText, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }
}

/// <summary>下载页二级：番号分组（作品卡）。</summary>
public class DownloadGroupItem : ObservableBase
{
    public string WorkId { get; init; } = "";

    /// <summary>所属下载源分区（DownloadLanes 的车道键）。</summary>
    public string Section { get; set; } = "";

    /// <summary>
    /// 列表上显示的名称：DLsite 作品直接用番号；fanbox 作品的 work_id 是内部键（fb_+作品号），
    /// 对用户无意义，改显示"作家 · 标题"。
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>全部分卷文件（扁平，按 UUID 就地更新进度/状态）。</summary>
    public ObservableCollection<DownloadFileItem> Children { get; } = [];

    /// <summary>展示用目录树根节点（文件夹/文件混合），仅在文件集合结构变化时重建。</summary>
    public ObservableCollection<object> Nodes { get; } = [];

    /// <summary>上次构建目录树时的结构签名（相对路径序列），用于判断是否需要重建。</summary>
    public string? TreeSignature { get; set; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                Raise(nameof(ChildrenVisibility));
        }
    }

    public Visibility ChildrenVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    private double _pct;
    public double Pct
    {
        get => _pct;
        set
        {
            if (Set(ref _pct, value))
                Raise(nameof(PctText));
        }
    }

    public string PctText => $"{(int)Pct}%";

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => Set(ref _speedText, value); }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private Brush _statusBrush = Brushes.Gray;
    public Brush StatusBrush { get => _statusBrush; set => Set(ref _statusBrush, value); }

    private Visibility _actionsVisibility = Visibility.Collapsed;
    public Visibility ActionsVisibility { get => _actionsVisibility; set => Set(ref _actionsVisibility, value); }

    // 单独"停止"（有待下载/下载中分卷时显示）/"下载"（有已暂停分卷时显示）/"删除"（始终显示）
    private Visibility _pauseVisibility = Visibility.Collapsed;
    public Visibility PauseVisibility { get => _pauseVisibility; set => Set(ref _pauseVisibility, value); }

    private Visibility _resumeVisibility = Visibility.Collapsed;
    public Visibility ResumeVisibility { get => _resumeVisibility; set => Set(ref _resumeVisibility, value); }

    private Visibility _openVisibility = Visibility.Collapsed;
    public Visibility OpenVisibility { get => _openVisibility; set => Set(ref _openVisibility, value); }

    public string OpenText => I18n.Tr("跳转作品");
    public string ReparseText => I18n.Tr("重新解析");
    public string ResearchText => I18n.Tr("重新搜索");
    public string DownloadText => I18n.Tr("下载");
    public string StopText => I18n.Tr("停止");
    public string DeleteText => I18n.Tr("删除");
}

/// <summary>下载页（对应 Python 版 download_UI.py）。</summary>
public partial class DownloadPage : UserControl
{
    /// <summary>解析失败的番号点击"重新搜索"时触发，由主窗口切到"DLsite 搜索"分区。</summary>
    public event Action<string>? ResearchRequested;

    /// <summary>点击"已下载"按钮时触发，由主窗口切到已下载视图。</summary>
    public event Action? ShowDownloadedRequested;

    /// <summary>作品行的"跳转作品"：由主窗口切到媒体库并打开该作品详情（对齐 Web gotoWork）。</summary>
    public event Action<string>? OpenWorkRequested;

    // download_list.status -> 显示文本（中文原文，渲染时经 Tr 翻译）与颜色
    private static readonly Dictionary<string, (string Text, string Color)> StatusMap = new()
    {
        ["0"] = ("等待下载", "#facc15"),
        ["3"] = ("下载中", "#60a5fa"),
        ["1"] = ("已完成", "#4ade80"),
        ["2"] = ("解析失败", "#f87171"),
        ["4"] = ("已暂停", "#9aa4b2"),
        ["5"] = ("搜索可用下载连接", "#facc15"),
        ["6"] = ("无可用下载连接", "#f87171"),
    };

    private readonly List<DownloadGroupItem> _groups = [];
    // 下载源分区（键 = 车道键）；折叠状态挂在分区对象上，刷新时保留
    private readonly Dictionary<string, DownloadSectionItem> _sections = new();
    // 实际绑定到列表的摊平序列：分区标题，展开的分区后面跟着它的作品卡（见 SyncItems）
    private readonly ObservableCollection<object> _items = [];
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _usageTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private bool _usageLoading;
    // 最近一次拉取到的用量，供点击"使用量"弹出详情用
    private double? _usagePercent;
    private double _usageReset;
    private readonly Dictionary<string, double> _hostUsage = new();   // 单网盘用量%（若 API 提供）

    public DownloadPage()
    {
        InitializeComponent();
        HelpSlot.Content = HelpPanel.Button("11");
        GroupList.ItemsSource = _items;
        _refreshTimer.Tick += (_, _) => Refresh();
        _usageTimer.Tick += (_, _) => _ = FetchUsageAsync();
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        ClearDoneButton.Content = I18n.Tr("清除已完成");
        ClearNoLinkButton.Content = I18n.Tr("清除无可用连接");
        ClearAllButton.Content = I18n.Tr("清空列表");
        ReparseAllButton.Content = I18n.Tr("全部重新解析");
        ShowDownloadedButton.Content = I18n.Tr("已下载");
        UsageLabel.ToolTip = UsageBar.ToolTip = I18n.Tr("点击查看各网盘流量详情");
        EmptyLabel.Text = I18n.Tr("下载列表为空");
        UpdateStartButton();
        Refresh();
    }

    private void ShowDownloadedButton_Click(object sender, RoutedEventArgs e) =>
        ShowDownloadedRequested?.Invoke();

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            Refresh();
            _refreshTimer.Start();
            _ = FetchUsageAsync();
            _usageTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            _usageTimer.Stop();
        }
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        // 开始/暂停切换
        if (DownloadEngine.IsRunning)
            DownloadEngine.Stop();
        else
            DownloadEngine.Start();
        UpdateStartButton();
    }

    /// <summary>
    /// 开始/暂停按钮：样式恒为蓝色主按钮，只切文案（对齐 Web 的 .icon-btn.primary，不再绿↔红换色）。
    /// 已请求暂停时禁用，等当前文件停到断点。
    /// </summary>
    private void UpdateStartButton()
    {
        if (!DownloadEngine.IsRunning)
        {
            StartButton.Content = I18n.Tr("开始下载");
            StartButton.IsEnabled = true;
            return;
        }
        StartButton.Content = DownloadEngine.StopRequested ? I18n.Tr("暂停中…") : I18n.Tr("暂停下载");
        StartButton.IsEnabled = !DownloadEngine.StopRequested;
    }

    /// <summary>
    /// 工具栏其余按钮按列表内容灰显：空列表点「清空列表」、没有失败项点「全部重新解析」都不会有任何反应。
    /// 判定规则与 Web 同源（<see cref="DownloadListActions.FlagsOf"/>，Web 由 /api/downloads 回传）。
    /// </summary>
    private void UpdateToolbarButtons(IEnumerable<(string Status, string? Error)> items)
    {
        var flags = DownloadListActions.FlagsOf(items);
        ClearDoneButton.IsEnabled = flags.HasDone;
        ClearNoLinkButton.IsEnabled = flags.HasNoLink;
        ClearAllButton.IsEnabled = flags.HasRows;
        ReparseAllButton.IsEnabled = flags.CanReparse;
    }

    // ---------- debrid-link 用量 ----------

    private async Task FetchUsageAsync()
    {
        if (_usageLoading)
            return;
        _usageLoading = true;
        try
        {
            JsonElement? value;
            using (var client = new DebridLinkClient())
                value = await client.DownloadLimitsAsync();
            UpdateUsage(value);
        }
        catch (Exception)
        {
            UpdateUsage(null);
        }
        finally
        {
            _usageLoading = false;
        }
    }

    private void UpdateUsage(JsonElement? value)
    {
        double? current = null;
        double resetSeconds = 0;
        _hostUsage.Clear();
        if (value is { } v && v.ValueKind == JsonValueKind.Object)
        {
            if (v.TryGetProperty("usagePercent", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("current", out var cur) &&
                    cur.ValueKind == JsonValueKind.Number)
                    current = cur.GetDouble();
                // 逐网盘用量：debrid-link 免费账户按网盘计流量，域名键（含'.'）即该网盘用量百分比
                foreach (var prop in usage.EnumerateObject())
                    if (prop.Name.Contains('.') && prop.Value.ValueKind == JsonValueKind.Number)
                        _hostUsage[prop.Name] = prop.Value.GetDouble();
            }
            if (v.TryGetProperty("nextResetSeconds", out var reset) &&
                reset.ValueKind == JsonValueKind.Object &&
                reset.TryGetProperty("value", out var rv) &&
                rv.ValueKind == JsonValueKind.Number)
                resetSeconds = rv.GetDouble();
        }
        _usagePercent = current;
        _usageReset = resetSeconds;
        if (current is null)
        {
            UsageLabel.Text = I18n.Tr("debrid-link 使用量 --");
            UsageBar.Value = 0;
            return;
        }
        UsageBar.Value = Math.Min(100, Math.Round(current.Value));
        var text = I18n.Tr("debrid-link 使用量");
        var reset2 = FormatReset(resetSeconds);
        if (reset2.Length > 0)
            text += I18n.Format(I18n.Tr(" · {reset} 后重置"), ("reset", reset2));
        UsageLabel.Text = text;
    }

    /// <summary>把距离流量重置的秒数格式化为 Xh Ym（不足 1 分钟显示 &lt;1m）。</summary>
    private static string FormatReset(double seconds)
    {
        var s = (long)seconds;
        if (s <= 0)
            return "";
        var hours = s / 3600;
        var minutes = s % 3600 / 60;
        if (hours > 0)
            return $"{hours}h{minutes}m";
        return minutes > 0 ? $"{minutes}m" : "<1m";
    }

    // ---------- 列表刷新 ----------

    private static string FormatSpeed(double speed)
    {
        if (speed >= 1024 * 1024)
            return $"{speed / 1024 / 1024:F1} MB/s";
        if (speed >= 1024)
            return $"{speed / 1024:F0} KB/s";
        return $"{speed:F0} B/s";
    }

    /// <summary>从下载链接提取文件名（前台不直接显示链接）。</summary>
    private static string FileNameOf(string url)
    {
        if (url.StartsWith("placeholder://", StringComparison.Ordinal))
            return "";   // 自动下载占位哨兵 url：无真实文件名，展示为空（分组状态已说明"搜索/无可用下载连接"）
        var raw = url.TrimEnd('/').Split('/')[^1].Split('?')[0];
        // 直链文件名常为 URL 百分号编码（asmr.one 的日文名尤其如此），解码为明文显示；
        // 普通未编码文件名解码后保持不变
        try { return Uri.UnescapeDataString(raw); }
        catch (Exception) { return raw; }
    }

    /// <summary>返回 (进度百分比, 实时速度 B/s 或 null)。</summary>
    private static (int Pct, double? Speed) FileProgress(string uuid, string status, string dbLong)
    {
        if (status == "1")
            return (100, null);
        if (status == "3" && DownloadEngine.DownloadProgress.TryGetValue(uuid, out var info) &&
            info.Total > 0)
            return ((int)(info.Downloaded * 100 / info.Total), info.Speed);
        // 等待/失败/无实时数据时，退回数据库中记录的进度（断点续传的已完成部分）
        var pct = int.TryParse(dbLong, out var p) ? p : 0;
        return (Math.Min(pct, 100), null);
    }

    private (string Text, string Color) AggregateStatus(
        string workId, List<(string Status, string? Error)> items)
    {
        var statuses = items.Select(i => i.Status).ToList();
        var done = statuses.Count(s => s == "1");
        if (statuses.Contains("3"))
            return (I18n.Format(I18n.Tr("下载中 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#60a5fa");
        if (statuses.Contains("5"))
            return (I18n.Tr("搜索可用下载连接"), "#facc15");
        if (statuses.Contains("0"))
            return (I18n.Format(I18n.Tr("等待下载 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#facc15");
        if (statuses.Contains("4"))
            return (I18n.Format(I18n.Tr("已暂停 {done}/{total}"),
                ("done", done), ("total", statuses.Count)), "#9aa4b2");
        // 源站没有的文件（'2' + skipped）是已终结的跳过项，不是待处理的失败：
        // 只要还有别的文件下成了，作品照常走完并入库，状态里只标注跳过了几个
        var skipped = items.Count(i => i.Status == "2" && DownloadEngine.IsSkipped(i.Error));
        var fails = items.Where(i => i.Status == "2" && !DownloadEngine.IsSkipped(i.Error)).ToList();
        if (fails.Count > 0)
        {
            // 失败原因一致 → 直接显示该原因（如"文件失效"/"流量用尽"）；原因不一 → 显示失败数
            var labels = fails.Select(f => ParseErrorLabel(f.Error)).Distinct().ToList();
            if (labels.Count == 1)
                return (labels[0], "#f87171");
            return (I18n.Format(I18n.Tr("{n} 个解析失败"), ("n", fails.Count)), "#f87171");
        }
        if (skipped > 0 && done == 0)
            return (I18n.Tr("源站无文件"), "#f87171");   // 整篇都没归档：没有任何文件可下，不会入库
        if (statuses.Contains("6"))
            return (I18n.Tr("无可用下载连接"), "#f87171");
        // 全部分卷已下载完成：解压前/解压中/移动中显示对应状态
        if (DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip))
        {
            if (unzip.State == "pending")
                return (I18n.Tr("待解压"), "#facc15");
            if (unzip.State == "movewait")
                return (I18n.Tr("等待移动"), "#facc15");
            if (unzip.State == "moving")
                return (I18n.Format(I18n.Tr("移动中 {pct}%"), ("pct", unzip.Pct)), "#a78bfa");
            return (I18n.Format(I18n.Tr("解压中 {pct}%"), ("pct", unzip.Pct)), "#60a5fa");
        }
        return (skipped > 0
            ? I18n.Format(I18n.Tr("已完成（跳过 {n}）"), ("n", skipped))
            : I18n.Tr("已完成"), "#4ade80");
    }

    // 状态颜色只有固定几种，冻结并按 hex 缓存复用，避免每秒每条目重复分配画刷；
    // 同一 hex 返回同一实例，使未变化的 StatusBrush 赋值命中相等短路、不触发多余通知。
    private static readonly Dictionary<string, Brush> BrushCache = new();
    private static Brush BrushOf(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached))
            return cached;
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }

    private bool _refreshing;   // 上一次刷新（含后台查询）未完成时跳过本次，防止 tick 重入堆积

    private async void Refresh()
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            UpdateStartButton();
            // 全表读放到后台线程，避免每秒在 UI 线程阻塞（连接池 + WAL 下并发安全）
            var (rows, works) = await Task.Run(() => (
                Db.Select(
                    "SELECT \"UUID\", \"work_id\", \"url\", \"status\", \"long\", \"error\", \"sub_path\", \"source\" FROM \"download_list\" ORDER BY rowid"),
                // 「跳转作品」只给 works 里已有记录的番号（同 Web 的 canOpen：论坛源刚入队的占位番号可能还没有）
                Db.Select(
                    "SELECT \"work_id\" FROM \"works\" WHERE \"work_id\" IN (SELECT DISTINCT \"work_id\" FROM \"download_list\")")));
            if (rows != null)
                ApplyRows(rows, (works ?? []).Select(w => w[0] as string ?? "").ToHashSet());   // 集合改动回到 UI 线程执行（await 后续接续在 UI 上下文）
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyRows(List<object?[]> rows, HashSet<string> inWorks)
    {
        // 按番号分组，同一番号合并为一个父条目
        var groups = new Dictionary<string, List<(string Uuid, string Url, string Status, string Long, string? Error, string? SubPath)>>();
        var order = new List<string>();
        var sourceOf = new Dictionary<string, string?>();   // 作品 → 下载源（同一作品的文件来源一致，取首行）
        foreach (var row in rows)
        {
            var workId = row[1] as string ?? "";
            if (!groups.TryGetValue(workId, out var list))
            {
                groups[workId] = list = [];
                order.Add(workId);
                sourceOf[workId] = row[7] as string;
            }
            list.Add((row[0] as string ?? "", row[2] as string ?? "",
                row[3] as string ?? "", row[4]?.ToString() ?? "", row[5] as string, row[6] as string));
        }
        // 工具栏按钮灰显跟着列表内容走（整表已在手上，不再额外查库）
        UpdateToolbarButtons(rows.Select(r => (r[3] as string ?? "", r[5] as string)));

        // 同步到现有集合（保留展开状态与滚动位置）
        var anyActive = false;   // 是否存在下载中/等待/解压中的任务，用于空闲降频
        var existing = _groups.ToDictionary(g => g.WorkId);
        for (var i = _groups.Count - 1; i >= 0; i--)
            if (!groups.ContainsKey(_groups[i].WorkId))
                _groups.RemoveAt(i);

        foreach (var workId in order)
        {
            var items = groups[workId];
            if (!existing.TryGetValue(workId, out var group))
            {
                group = new DownloadGroupItem
                {
                    WorkId = workId,
                    DisplayName = workId,
                };
                _groups.Add(group);
            }
            group.Section = DownloadLanes.SectionOf(sourceOf[workId], items.Select(it => it.Url));

            var statuses = items.Select(it => it.Status).ToList();
            var (aggText, aggColor) = AggregateStatus(
                workId, items.Select(it => (it.Status, it.Error)).ToList());
            group.StatusText = aggText;
            group.StatusBrush = BrushOf(aggColor);
            // 该番号有分卷解析失败时显示"重新解析/重新搜索"按钮
            group.ActionsVisibility = statuses.Contains("2") && workId.Length > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            // 单独控制按钮：有下载中/等待分卷时可"停止"；有已暂停分卷时可"下载"（继续）
            var hasActive = statuses.Any(s => s is "0" or "3");
            var hasPaused = statuses.Contains("4");
            anyActive |= hasActive || DownloadEngine.UnzipProgress.ContainsKey(workId);
            group.PauseVisibility = hasActive && workId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            group.ResumeVisibility = hasPaused && workId.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            group.OpenVisibility = inWorks.Contains(workId) ? Visibility.Visible : Visibility.Collapsed;

            var totalPct = 0;
            var totalSpeed = 0.0;
            var children = group.Children;
            var existingChildren = children.ToDictionary(c => c.Uuid);
            for (var i = children.Count - 1; i >= 0; i--)
                if (!items.Any(it => it.Uuid == children[i].Uuid))
                    children.RemoveAt(i);
            foreach (var it in items)
            {
                var (pct, speed) = FileProgress(it.Uuid, it.Status, it.Long);
                totalPct += pct;
                if (speed is { } s)
                    totalSpeed += s;

                var (rawText, color) = StatusMap.TryGetValue(it.Status, out var mapped)
                    ? mapped
                    : ((string?)null, "#cdd3de");
                var text = rawText != null
                    ? I18n.Tr(rawText)
                    : I18n.Format(I18n.Tr("未知({status})"), ("status", it.Status));
                // 解析失败：状态列直接显示具体原因的简短标签（如"文件失效"/"流量用尽"），完整说明见下方 ErrorReason
                if (it.Status == "2")
                    text = ParseErrorLabel(it.Error);
                // 已完成、但存的是源站预览图而非原图
                else if (it.Status == "1" && DownloadEngine.IsThumb(it.Error))
                    text = I18n.Tr("已完成（预览图）");

                if (!existingChildren.TryGetValue(it.Uuid, out var child))
                {
                    child = new DownloadFileItem { Uuid = it.Uuid };
                    children.Add(child);
                }
                child.Url = it.Url;
                // asmr 直链有作品内目录层级（sub_path 含子目录），用于构建目录树；
                // 论坛源无 sub_path，回退到 URL 解码后的文件名（位于根层级）
                var rel = string.IsNullOrEmpty(it.SubPath) ? FileNameOf(it.Url) : it.SubPath!;
                child.RelPath = rel;
                // 树中文件夹单独成节点，文件叶子只显示最后一段文件名
                var slash = rel.LastIndexOf('/');
                child.FileName = slash >= 0 ? rel[(slash + 1)..] : rel;
                child.Pct = pct;
                child.SpeedText = speed is { } sp ? FormatSpeed(sp) : "";
                child.StatusText = text;
                child.StatusBrush = BrushOf(color);
                // 解析失败的分卷显示失败原因
                if (it.Status == "2")
                {
                    child.ErrorReason = MapParseError(it.Error);
                    child.ErrorVisibility = Visibility.Visible;
                }
                else
                {
                    child.ErrorVisibility = Visibility.Collapsed;
                }
            }

            group.SpeedText = totalSpeed > 0 ? FormatSpeed(totalSpeed) : "";
            // 解压中时父进度条改为显示解压进度，否则显示分卷下载进度均值
            group.Pct = DownloadEngine.UnzipProgress.TryGetValue(workId, out var unzip)
                ? unzip.Pct
                : (double)totalPct / Math.Max(1, items.Count);

            // 目录树仅在文件集合结构变化时重建（保留文件夹展开状态、避免每秒刷新闪烁）；
            // 结构不变时叶子对象就地更新进度，树中引用同一对象自动刷新
            var signature = string.Join("|", group.Children.Select(c => c.RelPath));
            if (group.TreeSignature != signature)
            {
                group.TreeSignature = signature;
                RebuildTree(group);
            }
        }

        // 下载源分区汇总（同 Web /api/downloads 的 sections）
        var bySection = _groups.GroupBy(g => g.Section).ToDictionary(x => x.Key, x => x.ToList());
        foreach (var key in _sections.Keys.Where(k => !bySection.ContainsKey(k)).ToList())
            _sections.Remove(key);
        foreach (var (key, label) in DownloadLanes.All)
        {
            if (!bySection.TryGetValue(key, out var works))
                continue;
            if (!_sections.TryGetValue(key, out var section))
                _sections[key] = section = new DownloadSectionItem { Key = key, Label = I18n.Tr(label) };
            var files = works.SelectMany(g => groups[g.WorkId]).ToList();
            var downloading = files.Count(f => f.Status == "3");
            var waiting = files.Count(f => f.Status == "0");
            var failed = files.Count(f => f.Status == "2" && !DownloadEngine.IsSkipped(f.Error));
            section.DownloadingText = downloading > 0 ? $"{I18n.Tr("下载中")} {downloading}" : "";
            section.WaitingText = waiting > 0 ? $"{I18n.Tr("等待")} {waiting}" : "";
            section.FailedText = failed > 0 ? $"{I18n.Tr("失败")} {failed}" : "";
            section.DoneText = $"{I18n.Tr("已完成")} {files.Count(f => f.Status == "1")}/{files.Count}";
            section.WorksText = I18n.Format(I18n.Tr("{count} 个作品"), ("count", works.Count.ToString()));
            var speed = files.Sum(f => FileProgress(f.Uuid, f.Status, f.Long).Speed ?? 0);
            section.SpeedText = speed > 0 ? FormatSpeed(speed) : "";
        }
        SyncItems();

        // 有活动任务时每秒刷新（进度平滑）；全部完成/失败/空闲时降到 3 秒，减少空转开销
        var wanted = TimeSpan.FromSeconds(anyActive ? 1 : 3);
        if (_refreshTimer.Interval != wanted)
            _refreshTimer.Interval = wanted;
    }

    /// <summary>
    /// 把「分区标题 + 展开分区下的作品卡」摊平同步进绑定列表。逐位比对、只移动/插入/删除有变化的项，
    /// 未变的行不重建（保留作品卡展开状态与滚动位置，同原先按番号就地更新的做法）。
    /// </summary>
    private void SyncItems()
    {
        var wanted = new List<object>();
        foreach (var (key, _) in DownloadLanes.All)
        {
            if (!_sections.TryGetValue(key, out var section))
                continue;
            wanted.Add(section);
            if (section.IsExpanded)
                wanted.AddRange(_groups.Where(g => g.Section == key));
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], wanted[i]))
                continue;
            var at = -1;
            for (var j = i + 1; j < _items.Count; j++)
                if (ReferenceEquals(_items[j], wanted[i]))
                {
                    at = j;
                    break;
                }
            if (at >= 0)
                _items.Move(at, i);
            else
                _items.Insert(i, wanted[i]);
        }
        while (_items.Count > wanted.Count)
            _items.RemoveAt(_items.Count - 1);
    }

    /// <summary>点分区标题：展开/收起该下载源（默认折叠）。</summary>
    private void Section_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadSectionItem section)
            return;
        section.IsExpanded = !section.IsExpanded;
        SyncItems();
    }

    /// <summary>按各分卷的相对路径（含子目录）把扁平文件列表重建为文件夹/文件目录树。</summary>
    private static void RebuildTree(DownloadGroupItem group)
    {
        group.Nodes.Clear();
        // 按完整子目录路径缓存文件夹节点；空路径表示根（直接放入 group.Nodes）
        var folderByPath = new Dictionary<string, DownloadFolderItem>(StringComparer.Ordinal);
        foreach (var child in group.Children)
        {
            var parts = child.RelPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            ObservableCollection<object> parent = group.Nodes;
            var accum = "";
            for (var i = 0; i < parts.Length - 1; i++)   // 末段为文件名，前面均为目录
            {
                accum = accum.Length == 0 ? parts[i] : accum + "/" + parts[i];
                if (!folderByPath.TryGetValue(accum, out var folder))
                {
                    folder = new DownloadFolderItem { Name = parts[i] };
                    folderByPath[accum] = folder;
                    parent.Add(folder);
                }
                parent = folder.Children;
            }
            parent.Add(child);
        }
    }

    // ---------- 操作 ----------

    /// <summary>重新解析：把该作品解析失败的分卷重新排队（已完成的保留不动）。</summary>
    private void Reparse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        Db.Execute(
            "UPDATE \"download_list\" SET \"status\" = '0', \"error\" = NULL WHERE \"work_id\" = @w AND \"status\" = '2'",
            ("@w", group.WorkId));
        DownloadEngine.Start();
        Refresh();
    }

    /// <summary>复制该分卷的原始网盘下载链接到剪贴板。</summary>
    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadFileItem file || file.Url.Length == 0)
            return;
        try
        {
            Clipboard.SetText(file.Url);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "复制下载链接");
        }
    }

    /// <summary>把 debrid-link 解析失败错误码翻译为可读说明。</summary>
    private static string MapParseError(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return I18n.Tr("解析失败（原因未知，可能是链接失效或网盘不支持）");
        return raw switch
        {
            // 直链源（asmr / fanbox）源站 404：不是解析失败，是源站压根没有这个文件
            "skipped" => I18n.Tr("源站没有这个文件，已跳过"),
            "badToken" => I18n.Tr("debrid-link API Key 无效或已过期"),
            "maxData" or "maxDataHost" => I18n.Tr("debrid-link 流量额度已用尽"),
            "maxLink" or "maxLinkHost" => I18n.Tr("链接数超过 debrid-link 限制"),
            "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer"
                => I18n.Tr("该网盘不被 debrid-link 支持"),
            "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost"
                => I18n.Tr("该网盘需要会员或已停用"),
            "fileNotFound" or "fileUnavailable" or "notFound" or "fileError"
                => I18n.Tr("文件已失效或被删除"),
            // fanbox 投稿正文里的谷歌网盘链接（见 GoogleDriveClient）
            GoogleDriveClient.QuotaError => I18n.Tr("该文件的谷歌网盘下载配额已用尽，通常 24 小时后恢复"),
            GoogleDriveClient.GoneError => I18n.Tr("谷歌网盘文件已失效、被删除或未公开分享"),
            GoogleDriveClient.FetchError => I18n.Tr("连不上谷歌网盘（检查代理设置）"),
            DownloadEngine.RetryFailedError => I18n.Tr("下载超时或连接中断，已自动重试 3 次仍失败（可点「全部重新解析」再试）"),
            "floodDetected" => I18n.Tr("请求过于频繁，请稍后再试"),
            "badFileType" => I18n.Tr("不支持的文件类型"),
            _ => I18n.Format(I18n.Tr("解析失败（{code}）"), ("code", raw)),
        };
    }

    /// <summary>该失败错误码是否为 debrid-link 流量额度用尽（maxData=账户级，maxDataHost=单网盘级）。</summary>
    internal static bool IsTrafficError(string? error) => error is "maxData" or "maxDataHost";

    /// <summary>把 debrid-link 解析失败错误码翻译成简短状态标签（用于状态列；完整说明见 MapParseError）。</summary>
    internal static string ParseErrorLabel(string? raw) => raw switch
    {
        "skipped" => I18n.Tr("源站无此文件"),
        "maxData" or "maxDataHost" => I18n.Tr("流量用尽"),
        "fileNotFound" or "fileUnavailable" or "notFound" or "fileError" => I18n.Tr("文件失效"),
        GoogleDriveClient.QuotaError => I18n.Tr("云盘限额"),
        GoogleDriveClient.GoneError => I18n.Tr("云盘文件失效"),
        GoogleDriveClient.FetchError => I18n.Tr("云盘连不上"),
        "hostUnsupported" or "notDebrid" or "hostNotValid" or "noServer" => I18n.Tr("网盘不支持"),
        "notFreeHost" or "hostNotFree" or "disabledHost" or "disabledServerHost" => I18n.Tr("需会员"),
        "badToken" => I18n.Tr("Key 无效"),
        DownloadEngine.RetryFailedError => I18n.Tr("重试失败"),
        "floodDetected" => I18n.Tr("请求频繁"),
        "badFileType" => I18n.Tr("类型不支持"),
        "maxLink" or "maxLinkHost" => I18n.Tr("超链接数"),
        _ => I18n.Tr("解析失败"),
    };

    /// <summary>从下载链接取主机名（去 www. 前缀），失败返回空串。</summary>
    private static string HostOf(string url)
    {
        try
        {
            var h = new Uri(url).Host.ToLowerInvariant();
            return h.StartsWith("www.") ? h[4..] : h;
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>当前下载列表中因流量用尽而解析失败的网盘：AccountFull=账户总流量用尽(maxData)，Hosts=各单网盘(maxDataHost)。</summary>
    private static (bool AccountFull, List<string> Hosts) ExhaustedHosts()
    {
        var rows = Db.Select(
            "SELECT DISTINCT \"url\", \"error\" FROM \"download_list\" WHERE \"status\" = '2' AND \"error\" IN ('maxData', 'maxDataHost')");
        var accountFull = false;
        var hosts = new List<string>();
        foreach (var r in rows ?? [])
        {
            var err = r[1] as string ?? "";
            if (err == "maxData")
                accountFull = true;
            else
            {
                var h = HostOf(r[0] as string ?? "");
                if (h.Length > 0 && !hosts.Contains(h))
                    hosts.Add(h);
            }
        }
        return (accountFull, hosts);
    }

    /// <summary>点击顶部"使用量"：弹出各网盘流量详情，标出流量已用尽的网盘。</summary>
    private void Usage_Click(object sender, MouseButtonEventArgs e)
    {
        var sb = new StringBuilder();
        if (_usagePercent is { } p)
        {
            sb.Append(I18n.Format(I18n.Tr("总用量 {pct}%"), ("pct", (int)Math.Round(p))));
            var reset = FormatReset(_usageReset);
            if (reset.Length > 0)
                sb.Append(I18n.Format(I18n.Tr("（{reset} 后重置）"), ("reset", reset)));
            sb.Append("\n\n");
        }
        // debrid-link API 若返回逐网盘用量则完整列出（按用量降序）
        if (_hostUsage.Count > 0)
        {
            sb.Append(I18n.Tr("各网盘用量：")).Append('\n');
            foreach (var kv in _hostUsage.OrderByDescending(k => k.Value))
                sb.Append($"    {kv.Key}  {(int)Math.Round(kv.Value)}%\n");
            sb.Append('\n');
        }
        // 依据实际解析失败记录列出已用尽流量的网盘（无论 API 是否提供逐网盘数据都可靠）
        var (accountFull, hosts) = ExhaustedHosts();
        if (accountFull || hosts.Count > 0)
        {
            sb.Append(I18n.Tr("流量已用尽的网盘：")).Append('\n');
            if (accountFull)
                sb.Append("    ").Append(I18n.Tr("账户总流量（所有网盘）")).Append('\n');
            foreach (var h in hosts)
                sb.Append("    ").Append(h).Append('\n');
        }
        else if (_hostUsage.Count == 0)
        {
            sb.Append(I18n.Tr("暂无网盘流量用尽"));
        }
        InAppDialog.Info(this, sb.ToString().TrimEnd(), I18n.Tr("debrid-link 流量详情"));
    }

    /// <summary>重新搜索：先确认并删除已下载的分卷与文件夹，再切回搜索页重新搜索。</summary>
    private void Research_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("将删除 {id} 已下载的分卷与文件夹，并重新搜索。是否继续？"),
                    ("id", group.WorkId)),
                I18n.Tr("重新搜索")))
            return;
        DownloadEngine.PurgeWorkDownload(group.WorkId);
        Refresh();
        ResearchRequested?.Invoke(group.WorkId);
    }

    private void OpenWork_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadGroupItem group)
            OpenWorkRequested?.Invoke(group.WorkId);
    }

    /// <summary>单独下载（继续）该作品：解除暂停并启动下载。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        DownloadEngine.ResumeWork(group.WorkId);
        UpdateStartButton();
        Refresh();
    }

    /// <summary>单独停止该作品的下载（停到断点）。</summary>
    private void StopOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        DownloadEngine.PauseWork(group.WorkId);
        Refresh();
    }

    /// <summary>单独删除该作品：从下载列表移除，并删除 works 表记录。</summary>
    private void DeleteOne_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DownloadGroupItem group)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("将从下载列表删除 {id}，并删除其作品记录与下载缓存。是否继续？"),
                    ("id", group.WorkId)),
                I18n.Tr("删除")))
            return;
        DownloadEngine.DeleteWork(group.WorkId);
        Refresh();
    }

    // 四个批量动作都走 DownloadListActions（与 Web 的 /api/cleardone|clearnolink|clearall|reparseall 同一实现）
    private void ClearDoneButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadListActions.ClearDone();
        Refresh();
    }

    private void ClearNoLinkButton_Click(object sender, RoutedEventArgs e)
    {
        DownloadListActions.ClearNoLink();
        Refresh();
    }

    private void ClearAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_groups.Count == 0)
            return;
        if (!InAppDialog.Confirm(this,
                I18n.Tr("确定要清空整个下载列表吗？等待中的任务也会被删除。"),
                I18n.Tr("清空下载列表")))
            return;
        DownloadListActions.ClearAll();
        Refresh();
    }

    /// <summary>全部重新解析：解析失败分卷重新排队，「无可用下载连接」占位行重新自动解析。</summary>
    private void ReparseAllButton_Click(object sender, RoutedEventArgs e)
    {
        ReparseAllButton.IsEnabled = false;   // 请求在途时按住，下一次刷新再按状态回填
        DownloadListActions.ReparseAll();
        Refresh();
    }
}
