using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using R18MediaLibrary.Controls;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// 作品监控（一级导航「⏱ 作品监控」，对齐 Web watch.js 的 renderWatchPage；2026-10-29 由「作家监控」改名）。
/// 页顶工具栏最前是二级导航，按来源分成两页：
/// - Pawchive（作家监控）：总开关 / 立即检查全部 / 默认间隔，其下一位作家一张小卡（轮询间隔 / 立即检查 / 暂停 / 打开主页 / 删除）。
///   数据与轮询在 <see cref="FanboxWatchService"/>；添加监控的入口仍在 作品搜索 → Pawchive 的作家主页（「+ 监控作家」）。
/// - Pixiv（收藏监控）：总开关 / 立即检查全部 / 默认间隔 / 入队间隔，其下每个已登录的 pixiv 账号一张卡，
///   未监控的卡上直接「开启监控」（可选获取历史收藏）。数据与轮询在 <see cref="PixivWatchService"/>。
/// 卡片是自适应网格里的小卡（对齐 Web 的 .wgrid / .wcard）。
/// 纯代码构建、不带 XAML：见 CLAUDE.md「XAML 里不要引用本程序集的类型」。
/// </summary>
public class WatchPage : UserControl
{
    private const string TabFanbox = "fanbox";
    private const string TabPixiv = "pixiv";

    /// <summary>Pixiv 批量入队间隔档位（秒），同 Web 的 PX_WATCH_PACES。</summary>
    private static readonly int[] PaceChoices = [1, 2, 3, 5, 10, 20, 30];

    /// <summary>FANBOX 监控卡的「打开主页」：请求宿主跨分区切到 作品搜索 → FANBOX 并打开这位作家。</summary>
    public event Action<string, string>? OpenArtistRequested;

    /// <summary>Pixiv 监控卡的「查看收藏」：请求宿主跨分区切到 作品搜索 → Pixiv（该账号）并列出收藏。</summary>
    public event Action<string>? OpenPixivFavoritesRequested;

    private string _tab = TabFanbox;
    private readonly Border _barCard;
    private readonly TextBlock _statusBlock;
    private readonly StackPanel _cardsPanel;
    private bool _filling;      // 正在填充控件（抑制 SelectionChanged 回写）
    private DispatcherTimer? _timer;
    private ResponsiveWrapPanel? _grid;

    /// <summary>小卡网格（Web .wgrid）：列宽下限 280、间距 10；卡高固定，同一行的卡片对齐。</summary>
    private const double CardHeight = 168;

    public WatchPage()
    {
        _barCard = new Border { Style = Res<Style>("ToolbarCard") };
        var panel = new StackPanel();

        // 检查状态 / 空闲时的一句概况（同 Web：忙时 ⏳ 正在检查…，闲时 IdleSummary）
        _statusBlock = new TextBlock
        {
            Foreground = Res<Brush>("CaptionBrush") ?? Brushes.Gray,
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_statusBlock);

        _cardsPanel = new StackPanel();
        panel.Children.Add(_cardsPanel);

        // 工具栏常驻页顶（同下载管理），状态与卡片在其下滚动
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(_barCard);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = panel };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;

        // 进页即重读（监控状态可能被后台轮询改过），离开即停表：没人看时不必每 1.5 秒查一次状态
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                Refresh();
            else
                StopTimer();
        };
        Refresh();
    }

    /// <summary>往当前页的卡片网格里加一张卡（首张卡时才建网格，空态不建）。</summary>
    private void AddCard(UIElement card)
    {
        if (_grid == null)
        {
            _grid = new ResponsiveWrapPanel
            {
                MinItemWidth = 280, ItemGap = 10, ItemAspect = 0, ExtraHeight = CardHeight,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _cardsPanel.Children.Add(_grid);
        }
        _grid.Children.Add(card);
    }

    private bool IsPixiv => _tab == TabPixiv;

    private (bool Busy, string Status) CurrentState() =>
        IsPixiv ? PixivWatchService.State() : FanboxWatchService.State();

    private string CurrentSummary() =>
        IsPixiv ? PixivWatchService.IdleSummary() : FanboxWatchService.IdleSummary();

    /// <summary>重建工具栏与卡片（每次进页、切二级页、以及检查结束时调一次）。</summary>
    public void Refresh()
    {
        _filling = true;
        _barCard.Child = IsPixiv ? BuildPixivBar() : BuildFanboxBar();
        _filling = false;

        var (busy, status) = CurrentState();
        _statusBlock.Text = busy
            ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…"))
            : CurrentSummary();

        _cardsPanel.Children.Clear();
        _grid = null;
        if (IsPixiv)
            FillPixivCards();
        else
            FillFanboxCards();

        if (busy)
            StartTimer();
        else
            StopTimer();
    }

    // ---------- 工具栏 ----------

    /// <summary>工具栏骨架：二级导航在最前，? 使用帮助靠右（Web 工具栏末尾的 helpButton）。</summary>
    private DockPanel WrapBar(StackPanel controls, string helpTopic)
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 0) };
        foreach (var (key, label) in new[] { (TabFanbox, "Pawchive"), (TabPixiv, "Pixiv") })
        {
            var b = new Button { Content = label, Margin = new Thickness(0, 0, 2, 0) };
            ApplyToggleState(b, _tab == key);
            b.Click += (_, _) =>
            {
                if (_tab == key)
                    return;
                _tab = key;
                Refresh();
            };
            tabs.Children.Add(b);
        }
        controls.Children.Insert(0, tabs);

        var dock = new DockPanel();
        var help = HelpPanel.Button(helpTopic);
        DockPanel.SetDock(help, Dock.Right);
        dock.Children.Add(help);
        dock.Children.Add(controls);
        return dock;
    }

    private DockPanel BuildFanboxBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(SwitchButton(AppConfig.FanboxWatchEnabled, () =>
        {
            AppConfig.FanboxWatchEnabled = !AppConfig.FanboxWatchEnabled;
            if (AppConfig.FanboxWatchEnabled)
                FanboxWatchService.Kick();
        }));
        bar.Children.Add(CheckAllButton(FanboxWatchService.All().Count > 0, () => FanboxWatchService.CheckAllAsync()));
        bar.Children.Add(Caption(I18n.Tr("默认间隔")));
        bar.Children.Add(IntervalBox(AppConfig.FanboxWatchInterval, m => AppConfig.FanboxWatchInterval = m));
        return WrapBar(bar, "12");
    }

    private DockPanel BuildPixivBar()
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        bar.Children.Add(SwitchButton(AppConfig.PixivWatchEnabled, () =>
        {
            AppConfig.PixivWatchEnabled = !AppConfig.PixivWatchEnabled;
            if (AppConfig.PixivWatchEnabled)
                PixivWatchService.Kick();
        }));
        bar.Children.Add(CheckAllButton(PixivWatchService.All().Count > 0, () => PixivWatchService.CheckAllAsync()));
        bar.Children.Add(Caption(I18n.Tr("默认间隔")));
        bar.Children.Add(IntervalBox(AppConfig.PixivWatchInterval, m => AppConfig.PixivWatchInterval = m));
        bar.Children.Add(Caption(I18n.Tr("入队间隔")));

        var pace = new ComboBox
        {
            Width = 80, VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = I18n.Tr("批量入队（尤其是获取历史收藏）时两件作品之间歇多久，太快会触发站点的 429 限流"),
        };
        var current = AppConfig.PixivWatchPace;
        var choices = PaceChoices.ToList();
        if (!choices.Contains(current))
            choices.Insert(0, current);
        foreach (var value in choices)
        {
            var item = new ComboBoxItem { Content = I18n.Format(I18n.Tr("{n} 秒"), ("n", value)), Tag = value };
            pace.Items.Add(item);
            if (value == current)
                pace.SelectedItem = item;
        }
        pace.SelectionChanged += (_, _) =>
        {
            if (!_filling && pace.SelectedItem is ComboBoxItem { Tag: int seconds })
                AppConfig.PixivWatchPace = seconds;
        };
        bar.Children.Add(pace);
        return WrapBar(bar, "12.2");
    }

    private Button SwitchButton(bool on, Action toggle)
    {
        var b = new Button
        {
            Content = on ? I18n.Tr("自动监控：开") : I18n.Tr("自动监控：关"),
            ToolTip = I18n.Tr("关闭后仅停止后台自动轮询，「立即检查」仍可用"),
        };
        ApplyToggleState(b, on);
        b.Click += (_, _) =>
        {
            toggle();
            Refresh();
        };
        return b;
    }

    private Button CheckAllButton(bool enabled, Func<Task> check)
    {
        var b = new Button { Content = I18n.Tr("立即检查全部"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = enabled };
        b.Click += (_, _) =>
        {
            _ = check();
            StartTimer();
        };
        return b;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(8, 0, 8, 0),
        FontSize = 13,
        Foreground = Res<Brush>("CaptionBrush") ?? Brushes.Gray,
    };

    // ---------- FANBOX：作家监控 ----------

    private void FillFanboxCards()
    {
        var watches = FanboxWatchService.All();
        if (watches.Count == 0)
        {
            _cardsPanel.Children.Add(EmptyText(
                I18n.Tr("还没有监控任何作家。到「作品搜索 → Pawchive」搜到作家后进入作家主页，点「+ 监控作家」即可添加。")));
            return;
        }
        foreach (var w in watches)
            AddCard(BuildFanboxCard(w));
    }

    /// <summary>一位作家一张小卡（对齐 Web fbWatchCard）。</summary>
    private Border BuildFanboxCard(FanboxWatch w) => WatchCard(
        PawchiveApi.IconUrl(w.Service, w.ArtistId), w.Display,
        on: w.Enabled, state: w.Enabled ? I18n.Tr("监控中") : I18n.Tr("已暂停"),
        new WatchInfo(w.LastCheck, FanboxWatchService.NextCheckText(w), w.Downloaded, I18n.Tr("篇"),
            w.IntervalMin, w.TargetLib, w.LastResult, minutes =>
            {
                FanboxWatchService.SetInterval(w.ArtistId, minutes);
                Refresh();
            }),
        null,
        [
            (I18n.Tr("立即检查"), () =>
            {
                _ = FanboxWatchService.CheckNowAsync(w.ArtistId);
                StartTimer();
            }, false),
            (w.Enabled ? I18n.Tr("暂停") : I18n.Tr("启用"), () =>
            {
                FanboxWatchService.SetEnabled(w.ArtistId, !w.Enabled);
                Refresh();
            }, false),
            (I18n.Tr("打开主页"), () => OpenArtistRequested?.Invoke(w.ArtistId, w.ArtistName), false),
            (I18n.Tr("删除"), () =>
            {
                if (!InAppDialog.Confirm(this,
                        I18n.Format(I18n.Tr("不再监控「{name}」？已经下载的作品不受影响。"), ("name", w.Display)),
                        I18n.Tr("确认")))
                    return;
                FanboxWatchService.Remove(w.ArtistId);
                Refresh();
            }, true),
        ]);

    // ---------- Pixiv：收藏监控 ----------

    private void FillPixivCards()
    {
        var accounts = SiteAccounts.List(AccountService.Pixiv);
        var watches = PixivWatchService.All();
        if (accounts.Count == 0 && watches.Count == 0)
        {
            _cardsPanel.Children.Add(EmptyText(
                I18n.Tr("还没有登录 Pixiv 账号。到「账号访问」登录后，这里每个账号一张卡，点「开启监控」即可在有新收藏时自动下载。")));
            return;
        }
        // 每个已登录账号一张卡（已监控 / 未监控），再接上账号已退出登录、但监控还留着的
        var byUid = watches.ToDictionary(w => w.UserId);
        foreach (var a in accounts)
            AddCard(byUid.TryGetValue(a.UserId, out var w) ? BuildPixivCard(w) : BuildPixivIdleCard(a));
        foreach (var w in watches.Where(w => !w.HasAccount))
            AddCard(BuildPixivCard(w));
    }

    /// <summary>未监控的账号：只有名字、「查看收藏」与「开启监控」。</summary>
    private Border BuildPixivIdleCard(SiteAccount a)
    {
        Button? enable = null;
        var card = WatchCard(null, a.DisplayName, on: false, state: I18n.Tr("未监控"), null,
            I18n.Tr("有新收藏时自动下载，点「开启监控」开始"),
            [
                (I18n.Tr("查看收藏"), () => OpenPixivFavoritesRequested?.Invoke(a.UserId), false),
                (I18n.Tr("开启监控"), () => _ = EnablePixivAsync(a, enable!), false),
            ]);
        enable = _lastButtons.Last();
        return card;
    }

    private Border BuildPixivCard(PixivWatch w)
    {
        var actions = new List<(string, Action, bool)>
        {
            (I18n.Tr("立即检查"), () =>
            {
                _ = PixivWatchService.CheckNowAsync(w.UserId);
                StartTimer();
            }, false),
            (w.Enabled ? I18n.Tr("暂停") : I18n.Tr("启用"), () =>
            {
                PixivWatchService.SetEnabled(w.UserId, !w.Enabled);
                Refresh();
            }, false),
        };
        if (w.HasAccount)
            actions.Add((I18n.Tr("查看收藏"), () => OpenPixivFavoritesRequested?.Invoke(w.UserId), false));
        actions.Add((I18n.Tr("删除"), () =>
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("不再监控「{name}」的收藏？已经下载的作品不受影响。"), ("name", w.Display)),
                    I18n.Tr("确认")))
                return;
            PixivWatchService.Remove(w.UserId);
            Refresh();
        }, true));

        return WatchCard(null, w.Display, on: w.Enabled && w.HasAccount,
            state: !w.HasAccount ? I18n.Tr("账号已退出") : w.Enabled ? I18n.Tr("监控中") : I18n.Tr("已暂停"),
            new WatchInfo(w.LastCheck, PixivWatchService.NextCheckText(w), w.Downloaded, I18n.Tr("件"),
                w.IntervalMin, w.TargetLib, w.LastResult, minutes =>
                {
                    PixivWatchService.SetInterval(w.UserId, minutes);
                    Refresh();
                }),
            null, actions);
    }

    /// <summary>开启监控：先选媒体库（同手动下载），再选要不要连历史收藏一起下（对齐 Web pxWatchEnable）。</summary>
    private async Task EnablePixivAsync(SiteAccount a, Button button)
    {
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        var choice = InAppDialog.Choose(this,
            I18n.Format(I18n.Tr("监控「{name}」的收藏，有新收藏时自动下载。已有的收藏要一并下载吗？"), ("name", a.DisplayName)) + "\n\n" +
            I18n.Tr("「只要新收藏」＝从现在起，只下载今后新收藏的作品") + "\n" +
            I18n.Format(I18n.Tr("「获取历史收藏」＝已有的收藏也分批加入下载队列（每件间隔 {n} 秒防 429，数量多时要跑很久）"),
                ("n", AppConfig.PixivWatchPace)),
            I18n.Tr("开启收藏监控"),
            [I18n.Tr("只要新收藏"), I18n.Tr("获取历史收藏")], out _);
        if (choice < 0)
            return;

        button.IsEnabled = false;
        button.Content = I18n.Tr("开启中…");
        var (ok, message) = await PixivWatchService.AddAsync(
            a.UserId, intervalMin: 0, includeHistory: choice == 1, targetLib, targetFolder);
        if (!ok)
        {
            button.IsEnabled = true;
            button.Content = I18n.Tr("开启监控");
            InAppDialog.Warn(this, message, I18n.Tr("提示"));
            return;
        }
        InAppDialog.Info(this, message, I18n.Tr("提示"));
        Refresh();
        StartTimer();
    }

    // ---------- 卡片部件（两页共用） ----------

    /// <summary>监控记录里小卡要画的部分（未监控的 Pixiv 账号没有）。</summary>
    private sealed record WatchInfo(string LastCheck, string Next, int Downloaded, string Unit,
        int IntervalMin, string TargetLib, string LastResult, Action<int> OnInterval);

    /// <summary>刚建好的那张卡的按钮（未监控卡要拿到「开启监控」按钮改文案）。</summary>
    private List<Button> _lastButtons = [];

    /// <summary>
    /// 两页共用的小卡（对齐 Web watchCard）：头像 + 名称 + 状态胶囊 / 检查时间 / 间隔下拉与去向 / 上次结果 / 按钮行。
    /// iconUrl 为 null 表示这类卡没有头像格（Pixiv）；已暂停的卡整体压暗（Web .wcard.off）。
    /// </summary>
    private Border WatchCard(string? iconUrl, string name, bool on, string state,
        WatchInfo? info, string? meta, IReadOnlyList<(string Text, Action OnClick, bool Danger)> actions)
    {
        var body = new DockPanel { LastChildFill = false };

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        if (iconUrl != null)
        {
            var image = new Image { Stretch = Stretch.Uniform };
            var frame = new Border
            {
                Width = 40, Height = 40, CornerRadius = new CornerRadius(6), ClipToBounds = true,
                Background = Res<Brush>("CardBrush"), Margin = new Thickness(0, 0, 10, 0), Child = image,
            };
            DockPanel.SetDock(frame, Dock.Left);
            head.Children.Add(frame);
            _ = LoadIconAsync(image, iconUrl);
        }
        var pill = new Border
        {
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 0, 8, 1), Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            BorderBrush = on ? new SolidColorBrush(Color.FromRgb(0x2f, 0x6b, 0x45)) : Res<Brush>("BorderBrush"),
            Child = new TextBlock
            {
                Text = state, FontSize = 12,
                Foreground = on ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80)) : MutedBrush(),
            },
        };
        DockPanel.SetDock(pill, Dock.Right);
        head.Children.Add(pill);
        head.Children.Add(new TextBlock
        {
            Text = name, ToolTip = name, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis,
        });
        DockPanel.SetDock(head, Dock.Top);
        body.Children.Add(head);

        if (info != null)
        {
            var line = new List<string>();
            var tip = new List<string>();
            if (info.LastCheck.Length > 0)
            {
                line.Add(I18n.Format(I18n.Tr("上次 {time}"), ("time", ShortTime(info.LastCheck))));
                tip.Add(I18n.Format(I18n.Tr("上次检查 {time}"), ("time", info.LastCheck)));
            }
            else
            {
                line.Add(I18n.Tr("尚未检查过"));
                tip.Add(I18n.Tr("尚未检查过"));
            }
            if (info.Next.Length > 0)
            {
                line.Add(I18n.Format(I18n.Tr("下次 {time}"), ("time", ShortTime(info.Next))));
                tip.Add(I18n.Format(I18n.Tr("下次检查 {time}"), ("time", info.Next)));
            }
            if (info.Downloaded > 0)
            {
                line.Add(I18n.Format(I18n.Tr("累计 {n} {unit}"), ("n", info.Downloaded), ("unit", info.Unit)));
                tip.Add(I18n.Format(I18n.Tr("累计自动下载 {n} {unit}"), ("n", info.Downloaded), ("unit", info.Unit)));
            }
            AddTop(body, MetaLine(string.Join(" · ", line), string.Join("\n", tip)));

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6), ToolTip = I18n.Tr("轮询间隔") };
            var box = IntervalBox(info.IntervalMin, minutes =>
            {
                if (minutes != info.IntervalMin)
                    info.OnInterval(minutes);
            });
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            var hasLib = info.TargetLib.Length > 0;
            var lib = MetaLine(
                hasLib ? I18n.Format(I18n.Tr("→ 媒体库「{lib}」"), ("lib", info.TargetLib)) : I18n.Tr("→ 缓存目录"),
                hasLib ? I18n.Format(I18n.Tr("自动下载到媒体库「{lib}」"), ("lib", info.TargetLib)) : I18n.Tr("自动下载到缓存目录"));
            lib.Margin = new Thickness(8, 0, 0, 0);
            lib.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(lib);
            DockPanel.SetDock(row, Dock.Top);
            body.Children.Add(row);

            var result = I18n.Format(I18n.Tr("上次结果：{text}"),
                ("text", info.LastResult.Length > 0 ? info.LastResult : "—"));
            AddTop(body, MetaLine(result, result));
        }
        else if (!string.IsNullOrEmpty(meta))
        {
            AddTop(body, MetaLine(meta, meta));
        }

        // 按钮行贴卡底（Web .wa 的 margin-top:auto），「删除」靠右
        var actionRow = new DockPanel();
        var left = new WrapPanel();
        _lastButtons = [];
        foreach (var (text, onClick, danger) in actions)
        {
            var button = new Button { Content = text, Style = Res<Style>(danger ? "MiniDangerButton" : "MiniButton") };
            button.Click += (_, _) => onClick();
            _lastButtons.Add(button);
            if (danger)
            {
                DockPanel.SetDock(button, Dock.Right);
                actionRow.Children.Add(button);
            }
            else
            {
                button.Margin = new Thickness(0, 0, 6, 0);
                left.Children.Add(button);
            }
        }
        actionRow.Children.Add(left);
        DockPanel.SetDock(actionRow, Dock.Bottom);
        body.Children.Add(actionRow);

        return new Border
        {
            Background = Res<Brush>("ButtonBrush"),
            BorderBrush = Res<Brush>("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Opacity = info != null && !on ? 0.6 : 1,
            Child = body,
        };
    }

    private static void AddTop(DockPanel panel, FrameworkElement child)
    {
        child.Margin = new Thickness(0, 0, 0, 6);
        DockPanel.SetDock(child, Dock.Top);
        panel.Children.Add(child);
    }

    private static TextBlock MetaLine(string text, string tip) => new()
    {
        Text = text, ToolTip = tip, FontSize = 12,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Foreground = MutedBrush(),
    };

    private static Brush MutedBrush() => Res<Brush>("CaptionBrush") ?? Brushes.Gray;

    /// <summary>检查时间只留「月-日 时:分」（同 Web watchShortTime；不是这个格式就原样显示）。</summary>
    private static string ShortTime(string t)
    {
        var m = System.Text.RegularExpressions.Regex.Match(t, @"^\d{4}-(\d{2}-\d{2}) (\d{2}:\d{2})");
        return m.Success ? m.Groups[1].Value + " " + m.Groups[2].Value : t;
    }

    /// <summary>作家头像：pawchive 站上的图，必须带 <see cref="PawchiveApi.UserAgent"/>；解不出（如 WebP）就留空底。</summary>
    private static async Task LoadIconAsync(Image target, string url)
    {
        var bmp = await ThumbnailCache.LoadUrlAsync(url, 80, PawchiveApi.UserAgent);
        if (bmp != null)
            target.Source = bmp;
    }

    private static TextBlock EmptyText(string text) => new()
    {
        Text = text,
        Foreground = Res<Brush>("CaptionBrush") ?? Brushes.Gray,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        Margin = new Thickness(0, 50, 0, 50),
    };

    /// <summary>间隔档位下拉，选中当前值；库里存着非档位值时补一档，免得下拉把它悄悄改掉。</summary>
    private ComboBox IntervalBox(int minutes, Action<int> onChange)
    {
        var box = new ComboBox { Width = 110, VerticalContentAlignment = VerticalAlignment.Center };
        var previous = _filling;
        _filling = true;
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
        box.SelectionChanged += (_, _) =>
        {
            if (!_filling && box.SelectedItem is ComboBoxItem { Tag: int value })
                onChange(value);
        };
        return box;
    }

    private static string IntervalLabel(int minutes) => minutes switch
    {
        < 60 => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
        < 1440 when minutes % 60 == 0 => I18n.Format(I18n.Tr("{n} 小时"), ("n", minutes / 60)),
        _ when minutes % 1440 == 0 => I18n.Format(I18n.Tr("{n} 天"), ("n", minutes / 1440)),
        _ => I18n.Format(I18n.Tr("{n} 分钟"), ("n", minutes)),
    };

    /// <summary>切换按钮的激活态外观：on=强调色底+白字（等价 Web 的 .icon-btn.on）。</summary>
    private static void ApplyToggleState(Button btn, bool on)
    {
        btn.Background = Res<Brush>(on ? "AccentBrush" : "ButtonBrush")
                         ?? (on ? Brushes.DodgerBlue : Brushes.DimGray);
        btn.BorderBrush = Res<Brush>(on ? "AccentBrush" : "BorderBrush");
        btn.Foreground = on ? Brushes.White : Res<Brush>("TextBrush") ?? Brushes.White;
    }

    private void StartTimer()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += (_, _) =>
            {
                var (busy, status) = CurrentState();
                _statusBlock.Text = busy
                    ? "⏳ " + (status.Length > 0 ? status : I18n.Tr("正在检查…"))
                    : CurrentSummary();
                // 检查结束才整表重绘：每 1.5 秒重建一次会把用户正在操作的下拉框踢掉
                if (!busy)
                {
                    StopTimer();
                    Refresh();
                }
            };
        }
        _timer.Start();
    }

    private void StopTimer() => _timer?.Stop();

    private static T? Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
