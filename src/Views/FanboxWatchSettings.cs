using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// FANBOX 作家监控（内联到系统设置页的同名分区，对齐 Web 设置页的 renderFanboxWatchSection）：
/// 总开关 / 立即检查全部 / 默认间隔，其下一位作家一张卡（轮询间隔 / 立即检查 / 暂停 / 打开主页 / 删除）。
/// 数据与轮询都在 <see cref="FanboxWatchService"/>，本区块只负责显示与操作。
/// 添加监控的入口仍在 作品搜索 → FANBOX 的作家主页（「+ 监控作家」）——加监控本就要先选到作家。
/// （旧版入口是搜索工具栏的「⏱ 监控」，2026-09-22 起改到设置页。）
/// </summary>
public class FanboxWatchSettings
{
    /// <summary>监控卡的「打开主页」：请求宿主跨分区切到 作品搜索 → FANBOX 并打开这位作家。</summary>
    public event Action<string, string>? OpenArtistRequested;

    private DependencyObject? _owner;
    private Button? _switchButton;
    private Button? _checkAllButton;
    private ComboBox? _defaultIntervalBox;
    private TextBlock? _statusBlock;
    private StackPanel? _cardsPanel;
    private bool _filling;      // 正在填充控件（抑制 SelectionChanged 回写）
    private DispatcherTimer? _timer;
    private bool _timerHooked;

    /// <summary>构建可内联到设置页的监控区块（操作条 + 状态 + 作家卡片 + 说明，对齐 Web）。</summary>
    public FrameworkElement BuildSection(DependencyObject? owner)
    {
        _owner = owner;
        var panel = new StackPanel();

        // 操作条：总开关 / 立即检查全部 / 默认间隔（顺序对齐 Web）
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        _switchButton = new Button { MinWidth = 124 };
        _switchButton.Click += (_, _) =>
        {
            AppConfig.FanboxWatchEnabled = !AppConfig.FanboxWatchEnabled;
            if (AppConfig.FanboxWatchEnabled)
                FanboxWatchService.Kick();
            Refresh();
        };
        bar.Children.Add(_switchButton);

        _checkAllButton = new Button { MinWidth = 110, Margin = new Thickness(8, 0, 0, 0) };
        _checkAllButton.Click += (_, _) =>
        {
            _ = FanboxWatchService.CheckAllAsync();
            StartTimer();
        };
        bar.Children.Add(_checkAllButton);

        bar.Children.Add(new TextBlock
        {
            Text = I18n.Tr("默认间隔"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 8, 0),
            Style = Res<Style>("CaptionText"),
        });
        _defaultIntervalBox = new ComboBox { Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
        _defaultIntervalBox.SelectionChanged += (_, _) =>
        {
            if (_filling || _defaultIntervalBox.SelectedItem is not ComboBoxItem { Tag: int minutes })
                return;
            AppConfig.FanboxWatchInterval = minutes;
        };
        bar.Children.Add(_defaultIntervalBox);
        panel.Children.Add(bar);

        // 检查状态 / 空闲时的一句概况（同 Web：忙时 ⏳ 正在检查…，闲时 IdleSummary）
        _statusBlock = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Res<Brush>("CaptionBrush") ?? Brushes.Gray,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_statusBlock);

        _cardsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_cardsPanel);

        Refresh();
        return panel;
    }

    /// <summary>重新拉取监控列表并重建卡片（设置页每次显示、以及检查进行中每 1.5 秒调一次）。</summary>
    public void Refresh()
    {
        if (_cardsPanel == null)
            return;

        var watches = FanboxWatchService.All();
        var (busy, status) = FanboxWatchService.State();

        _filling = true;
        if (_switchButton != null)
        {
            _switchButton.Content = AppConfig.FanboxWatchEnabled
                ? I18n.Tr("自动监控：开") : I18n.Tr("自动监控：关");
            ApplyToggleState(_switchButton, AppConfig.FanboxWatchEnabled);
            _switchButton.ToolTip = I18n.Tr("关闭后仅停止后台自动轮询，「立即检查」仍可用");
        }
        if (_checkAllButton != null)
        {
            _checkAllButton.Content = I18n.Tr("立即检查全部");
            _checkAllButton.IsEnabled = watches.Count > 0;
        }
        if (_defaultIntervalBox != null)
            FillIntervalBox(_defaultIntervalBox, AppConfig.FanboxWatchInterval);
        _filling = false;

        if (_statusBlock != null)
            _statusBlock.Text = busy
                ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…"))
                : FanboxWatchService.IdleSummary();

        _cardsPanel.Children.Clear();
        if (watches.Count == 0)
        {
            _cardsPanel.Children.Add(new TextBlock
            {
                Text = I18n.Tr("还没有监控任何作家。到「作品搜索 → FANBOX」搜到作家后进入作家主页，点「+ 监控作家」即可添加。"),
                Foreground = Res<Brush>("CaptionBrush") ?? Brushes.Gray,
                TextWrapping = TextWrapping.Wrap,
            });
        }
        else
        {
            foreach (var w in watches)
                _cardsPanel.Children.Add(BuildWatchCard(w));
        }

        if (busy)
            StartTimer();
        else
            StopTimer();
    }

    /// <summary>离开设置页时停表：没人看的时候不必每 1.5 秒查一次状态。</summary>
    public void Suspend() => StopTimer();

    /// <summary>一位作家一张卡（版式对齐 Web 的 .libcard：标题行 + 间隔行 + 上次结果）。</summary>
    private Border BuildWatchCard(FanboxWatch w)
    {
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        head.Children.Add(new TextBlock
        {
            Text = w.Display, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });

        var sub = new List<string>();
        if (!w.Enabled)
            sub.Add(I18n.Tr("已暂停"));
        sub.Add(w.LastCheck.Length > 0
            ? I18n.Format(I18n.Tr("上次检查 {time}"), ("time", w.LastCheck))
            : I18n.Tr("尚未检查过"));
        if (FanboxWatchService.NextCheckText(w) is { Length: > 0 } next)
            sub.Add(I18n.Format(I18n.Tr("下次 {time}"), ("time", next)));
        if (w.Downloaded > 0)
            sub.Add(I18n.Format(I18n.Tr("累计自动下载 {n} 篇"), ("n", w.Downloaded)));
        var meta = new TextBlock
        {
            Text = string.Join(" · ", sub),
            Margin = new Thickness(10, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = Res<Style>("CaptionText"),
        };
        Grid.SetColumn(meta, 1);
        head.Children.Add(meta);

        AddAction(head, 2, I18n.Tr("立即检查"), () =>
        {
            _ = FanboxWatchService.CheckNowAsync(w.ArtistId);
            StartTimer();
        });
        AddAction(head, 3, w.Enabled ? I18n.Tr("暂停") : I18n.Tr("启用"), () =>
        {
            FanboxWatchService.SetEnabled(w.ArtistId, !w.Enabled);
            Refresh();
        });
        AddAction(head, 4, I18n.Tr("打开主页"), () => OpenArtistRequested?.Invoke(w.ArtistId, w.ArtistName));
        AddAction(head, 5, I18n.Tr("删除"), () =>
        {
            if (!InAppDialog.Confirm(_owner,
                    I18n.Format(I18n.Tr("不再监控「{name}」？已经下载的作品不受影响。"), ("name", w.Display)),
                    I18n.Tr("确认")))
                return;
            FanboxWatchService.Remove(w.ArtistId);
            Refresh();
        }, danger: true);

        // 间隔行：轮询间隔下拉 + 自动下载去向
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        row.Children.Add(new TextBlock
        {
            Text = I18n.Tr("轮询间隔"), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Style = Res<Style>("CaptionText"),
        });
        var box = new ComboBox { Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
        FillIntervalBox(box, w.IntervalMin);
        box.SelectionChanged += (_, _) =>
        {
            if (_filling || box.SelectedItem is not ComboBoxItem { Tag: int minutes })
                return;
            if (minutes == w.IntervalMin)
                return;
            FanboxWatchService.SetInterval(w.ArtistId, minutes);
            Refresh();
        };
        row.Children.Add(box);
        row.Children.Add(new TextBlock
        {
            Text = w.TargetLib.Length > 0
                ? I18n.Format(I18n.Tr("自动下载到媒体库「{lib}」"), ("lib", w.TargetLib))
                : I18n.Tr("自动下载到缓存目录"),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Style = Res<Style>("CaptionText"),
        });

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(row);
        if (w.LastResult.Length > 0)
            body.Children.Add(new TextBlock
            {
                Text = I18n.Format(I18n.Tr("上次结果：{text}"), ("text", w.LastResult)),
                Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap,
                Style = Res<Style>("CaptionText"),
            });

        return new Border
        {
            Style = Res<Style>("Card"),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    private void AddAction(Grid host, int column, string text, Action onClick, bool danger = false)
    {
        var button = new Button
        {
            Content = text,
            MinWidth = 76,
            Margin = new Thickness(8, 0, 0, 0),
            Style = danger ? Res<Style>("DangerButton") : null,
        };
        button.Click += (_, _) => onClick();
        Grid.SetColumn(button, column);
        host.Children.Add(button);
    }

    /// <summary>把间隔档位填进下拉并选中当前值；库里存着非档位值时补一档，免得下拉把它悄悄改掉。</summary>
    private void FillIntervalBox(ComboBox box, int minutes)
    {
        var previous = _filling;
        _filling = true;
        box.Items.Clear();
        var choices = FanboxWatchService.IntervalChoices.ToList();
        if (!choices.Contains(minutes))
            choices.Insert(0, minutes);
        foreach (var value in choices)
        {
            var item = new ComboBoxItem { Content = IntervalLabel(value), Tag = value };
            box.Items.Add(item);
            if (value == minutes)
                box.SelectedItem = item;
        }
        _filling = previous;
    }

    private static string IntervalLabel(int minutes) => minutes switch
    {
        < 60 => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
        < 1440 when minutes % 60 == 0 => I18n.Format(I18n.Tr("{n} 小时"), ("n", minutes / 60)),
        _ when minutes % 1440 == 0 => I18n.Format(I18n.Tr("{n} 天"), ("n", minutes / 1440)),
        _ => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
    };

    /// <summary>切换按钮的激活态外观：on=强调色底+白字（等价 Web 的 .icon-btn.on）。</summary>
    private void ApplyToggleState(Button btn, bool on)
    {
        btn.Background = Res<Brush>(on ? "AccentBrush" : "ButtonBrush")
                         ?? (on ? Brushes.DodgerBlue : Brushes.DimGray);
        btn.Foreground = on ? Brushes.White : Res<Brush>("TextBrush") ?? Brushes.White;
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        if (!_timerHooked)
        {
            _timer.Tick += (_, _) =>
            {
                var (busy, status) = FanboxWatchService.State();
                if (_statusBlock != null)
                    _statusBlock.Text = busy
                        ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…"))
                        : FanboxWatchService.IdleSummary();
                // 检查结束才整表重绘：每 1.5 秒重建一次会把用户正在操作的下拉框踢掉
                if (!busy)
                {
                    StopTimer();
                    Refresh();
                }
            };
            _timerHooked = true;
        }
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private static T? Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
