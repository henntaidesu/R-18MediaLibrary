using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// 使用帮助面板（各页面的 ? 按钮）：对齐 Web 端 help.js 的右侧帮助面板——
/// 顶部「? 使用帮助 · 章节下拉 · 语言下拉 · ✕」，中间是手册对应章节，底部「‹ 上一章 / 下一章 ›」；
/// 点遮罩、✕ 或 Esc 关闭。正文来自 <see cref="HelpManual"/>（与 Web 端共用同一个解析器），这里只把块模型转成 FlowDocument。
/// topic 是手册章节号（"11"）或小节号（"11.5"，打开该章并滚到那一节）。
/// </summary>
public static class HelpPanel
{
    /// <summary>「?」按钮；点下去时才调 topic() 取章节号，页面层级 / 来源可能在建好按钮之后才切换。</summary>
    public static Button Button(Func<string> topic, bool small = false)
    {
        var b = new Button
        {
            Content = "?",
            FontWeight = FontWeights.Bold,
            Width = small ? 26 : 34,
            Height = small ? 26 : double.NaN,
            Padding = new Thickness(0),
            ToolTip = I18n.Tr("使用帮助"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => Show(b, topic());
        return b;
    }

    public static Button Button(string topic, bool small = false) => Button(() => topic, small);

    /// <summary>分区标题 + 右端的 ?（设置页各 GroupBox 的 Header，同 Web 端 helpHeading）。</summary>
    public static FrameworkElement Header(string title, string topic)
    {
        var dock = new DockPanel { LastChildFill = true };
        var b = Button(topic, small: true);
        b.FontSize = 13;
        b.FontWeight = FontWeights.Bold;
        DockPanel.SetDock(b, Dock.Right);
        dock.Children.Add(b);
        dock.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        return dock;
    }

    // 帮助语言：首次跟随界面语言，用户在面板里换过就记住（进程内）
    private static string? _lang;

    public static void Show(DependencyObject owner, string topic)
    {
        var lang = _lang ?? HelpManual.LangFor(I18n.CurrentLanguage);
        OverlayHost.ShowModal(owner, close => new PanelView(lang, topic, close));
    }

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>覆盖层本体：半透明遮罩 + 右侧定宽面板。</summary>
    private sealed class PanelView : Grid
    {
        private readonly Action<bool> _close;
        private readonly ComboBox _chapterBox = new() { MinWidth = 160 };
        private readonly ComboBox _langBox = new() { Width = 100, Margin = new Thickness(8, 0, 0, 0) };
        private readonly FlowDocumentScrollViewer _viewer = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsToolBarVisible = false,
            Focusable = false,
        };
        private readonly Button _prev = new() { Content = I18n.Tr("‹ 上一章") };
        private readonly Button _next = new() { Content = I18n.Tr("下一章 ›") };
        private string _lang;
        private List<HelpManual.Chapter> _chapters;
        private string _current = "";
        private bool _syncing;

        public PanelView(string lang, string topic, Action<bool> close)
        {
            _close = close;
            _lang = lang;
            _chapters = HelpManual.Chapters(lang);
            Focusable = true;
            Background = new SolidColorBrush(Color.FromArgb(0x73, 0, 0, 0));
            // 点遮罩关闭；面板本身吞掉点击
            MouseLeftButtonDown += (_, e) => { if (e.OriginalSource == this) _close(false); };
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    _close(false);
                }
            };

            var panel = new Border
            {
                Background = Res("CardBrush"),
                BorderBrush = Res("BorderBrush"),
                BorderThickness = new Thickness(1, 0, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                Width = 620,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 0, Opacity = .55 },
            };
            SizeChanged += (_, _) => panel.Width = Math.Min(620, Math.Max(0, ActualWidth));
            Children.Add(panel);

            var layout = new DockPanel();
            panel.Child = layout;

            // 顶栏：? 使用帮助 · 章节 · 语言 · ✕
            var head = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
            var closeBtn = new Button { Content = "✕", Margin = new Thickness(8, 0, 0, 0), ToolTip = I18n.Tr("关闭帮助") };
            closeBtn.Click += (_, _) => _close(false);
            DockPanel.SetDock(closeBtn, Dock.Right);
            DockPanel.SetDock(_langBox, Dock.Right);
            var title = new TextBlock
            {
                Text = "? " + I18n.Tr("使用帮助"),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
            };
            DockPanel.SetDock(title, Dock.Left);
            head.Children.Add(closeBtn);
            head.Children.Add(_langBox);
            head.Children.Add(title);
            head.Children.Add(_chapterBox);
            var headBorder = new Border { BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Child = head };
            DockPanel.SetDock(headBorder, Dock.Top);
            layout.Children.Add(headBorder);

            // 底栏：‹ 上一章 / 下一章 ›
            var foot = new DockPanel { Margin = new Thickness(14, 10, 14, 10), LastChildFill = false };
            DockPanel.SetDock(_prev, Dock.Left);
            DockPanel.SetDock(_next, Dock.Right);
            foot.Children.Add(_prev);
            foot.Children.Add(_next);
            _prev.Click += (_, _) => Step(-1);
            _next.Click += (_, _) => Step(1);
            var footBorder = new Border { BorderBrush = Res("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0), Child = foot };
            DockPanel.SetDock(footBorder, Dock.Bottom);
            layout.Children.Add(footBorder);

            layout.Children.Add(_viewer);

            foreach (var (code, name) in HelpManual.Languages)
                _langBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            _langBox.SelectedItem = _langBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _lang);
            _langBox.SelectionChanged += (_, _) =>
            {
                if (_syncing || _langBox.SelectedItem is not ComboBoxItem { Tag: string code } || code == _lang)
                    return;
                _lang = HelpPanel._lang = code;
                _chapters = HelpManual.Chapters(code);
                ShowChapter(_current, null);
            };
            _chapterBox.SelectionChanged += (_, _) =>
            {
                if (!_syncing && _chapterBox.SelectedItem is ComboBoxItem { Tag: string no })
                    ShowChapter(no, null);
            };

            var ch = topic.Split('.')[0];
            ShowChapter(_chapters.Any(c => c.No == ch) ? ch : _chapters.FirstOrDefault()?.No ?? "", topic);
        }

        private void Step(int delta)
        {
            var i = _chapters.FindIndex(c => c.No == _current) + delta;
            if (i >= 0 && i < _chapters.Count)
                ShowChapter(_chapters[i].No, null);
        }

        private void ShowChapter(string no, string? topic)
        {
            var idx = _chapters.FindIndex(c => c.No == no);
            if (idx < 0)
            {
                _viewer.Document = new FlowDocument(new Paragraph(new Run(I18n.Tr("帮助加载失败"))));
                return;
            }
            _current = no;
            _syncing = true;
            _chapterBox.Items.Clear();
            foreach (var c in _chapters)
                _chapterBox.Items.Add(new ComboBoxItem { Content = $"{c.No}. {c.Title}", Tag = c.No });
            _chapterBox.SelectedIndex = idx;
            _syncing = false;
            _prev.IsEnabled = idx > 0;
            _next.IsEnabled = idx < _chapters.Count - 1;

            var anchors = new Dictionary<string, Block>();
            _viewer.Document = BuildDocument(_chapters[idx], anchors);
            // 带小节号：等排版完再滚到那一节（同 Web 的 scrollIntoView）
            if (topic != null && topic.Contains('.') && anchors.TryGetValue(topic, out var target))
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ScrollTo(target)));
        }

        /// <summary>
        /// 把某个块滚到顶端。FlowDocumentScrollViewer 里 TextElement.BringIntoView 并不可靠（常常原地不动），
        /// 故直接量出该块在文档里的纵坐标，再让模板里的 ScrollViewer 滚过去。
        /// </summary>
        private void ScrollTo(Block target)
        {
            if (_viewer.Template?.FindName("PART_ContentHost", _viewer) is not ScrollViewer scroll)
            {
                target.BringIntoView();
                return;
            }
            var rect = target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty)
                return;
            scroll.ScrollToVerticalOffset(Math.Max(0, scroll.VerticalOffset + rect.Top - 8));
        }

        // ---------- 块模型 → FlowDocument ----------

        private FlowDocument BuildDocument(HelpManual.Chapter chapter, Dictionary<string, Block> anchors)
        {
            var doc = new FlowDocument
            {
                Background = Brushes.Transparent,
                Foreground = Res("TextBrush"),
                FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"),
                FontSize = 14,
                PagePadding = new Thickness(14, 6, 14, 24),
                LineHeight = 22,
                TextAlignment = TextAlignment.Left,
            };
            doc.Blocks.Add(new Paragraph(new Run($"{chapter.No}. {chapter.Title}"))
            {
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 10),
            });
            foreach (var block in chapter.Blocks)
            {
                switch (block)
                {
                    case HelpManual.Heading h:
                        var hp = new Paragraph
                        {
                            FontSize = h.Level <= 3 ? 15 : 14,
                            FontWeight = FontWeights.SemiBold,
                            Margin = new Thickness(0, 18, 0, 6),
                        };
                        AddInlines(hp.Inlines, h.Text);
                        doc.Blocks.Add(hp);
                        if (h.Id.Length > 0)
                            anchors[h.Id] = hp;
                        break;
                    case HelpManual.Para p:
                        var para = new Paragraph { Margin = new Thickness(0, 0, 0, 10) };
                        AddInlines(para.Inlines, p.Text);
                        doc.Blocks.Add(para);
                        break;
                    case HelpManual.ListBlock l:
                        var list = new List
                        {
                            MarkerStyle = l.Ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 0, 0, 10),
                            Padding = new Thickness(22, 0, 0, 0),
                        };
                        foreach (var item in l.Items)
                        {
                            var ip = new Paragraph { Margin = new Thickness(0, 0, 0, 4) };
                            AddInlines(ip.Inlines, item);
                            list.ListItems.Add(new ListItem(ip));
                        }
                        doc.Blocks.Add(list);
                        break;
                    case HelpManual.Table t:
                        doc.Blocks.Add(BuildTable(t));
                        break;
                    case HelpManual.Code c:
                        doc.Blocks.Add(new Paragraph(new Run(c.Text))
                        {
                            FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                            FontSize = 13,
                            Background = Res("WindowBrush"),
                            BorderBrush = Res("BorderBrush"),
                            BorderThickness = new Thickness(1),
                            Padding = new Thickness(10, 8, 10, 8),
                            Margin = new Thickness(0, 0, 0, 10),
                            LineHeight = 18,
                        });
                        break;
                    case HelpManual.Picture pic:
                        if (LoadImage(pic.Name) is { } src)
                            doc.Blocks.Add(new BlockUIContainer(new Border
                            {
                                BorderBrush = Res("BorderBrush"),
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(8),
                                HorizontalAlignment = HorizontalAlignment.Left,
                                Child = new Image { Source = src, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly },
                            })
                            { Margin = new Thickness(0, 6, 0, 12) });
                        break;
                }
            }
            return doc;
        }

        private Table BuildTable(HelpManual.Table t)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 12), FontSize = 13.5 };
            // 列宽按各列最长文字分配（FlowDocument 的表格不会像 HTML 那样按内容自适应，默认等分会把短的标签列撑得很宽）
            for (var c = 0; c < t.Header.Count; c++)
            {
                var col = c;
                var longest = t.Rows.Prepend(t.Header)
                    .Select(r => col < r.Count ? r[col].Sum(i => i.Text.Length) : 0)
                    .DefaultIfEmpty(0).Max();
                table.Columns.Add(new TableColumn { Width = new GridLength(Math.Clamp(longest, 4, 40) + 4, GridUnitType.Star) });
            }
            var group = new TableRowGroup();
            table.RowGroups.Add(group);
            group.Rows.Add(BuildRow(t.Header, header: true));
            foreach (var row in t.Rows)
                group.Rows.Add(BuildRow(row, header: false));
            return table;
        }

        private TableRow BuildRow(List<List<HelpManual.Inline>> cells, bool header)
        {
            var row = new TableRow();
            if (header)
            {
                row.Background = Res("ButtonBrush");
                row.FontWeight = FontWeights.SemiBold;
            }
            foreach (var cell in cells)
            {
                var p = new Paragraph { Margin = new Thickness(0), LineHeight = 20 };
                AddInlines(p.Inlines, cell);
                row.Cells.Add(new TableCell(p)
                {
                    BorderBrush = Res("BorderBrush"),
                    BorderThickness = new Thickness(0.5),
                    Padding = new Thickness(8, 5, 8, 5),
                });
            }
            return row;
        }

        private void AddInlines(InlineCollection target, List<HelpManual.Inline> inlines)
        {
            foreach (var i in inlines)
            {
                Inline run;
                if (i.IsCode)
                    run = new Run(i.Text)
                    {
                        FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"),
                        Background = Res("ButtonBrush"),
                    };
                else if (i.Chapter != null)
                {
                    var no = i.Chapter;
                    var link = new Hyperlink(new Run(i.Text)) { Foreground = Res("AccentLightBrush"), TextDecorations = null, Cursor = Cursors.Hand };
                    link.Click += (_, _) => ShowChapter(no, null);
                    run = link;
                }
                else if (i.Href != null && HelpManual.IsWebLink(i.Href))
                {
                    var href = i.Href;
                    var link = new Hyperlink(new Run(i.Text)) { Foreground = Res("AccentLightBrush"), TextDecorations = null, Cursor = Cursors.Hand };
                    link.Click += (_, _) => Process.Start(new ProcessStartInfo(href) { UseShellExecute = true });
                    run = link;
                }
                else
                    run = new Run(i.Text);
                target.Add(i.Bold ? new Bold(run) : run);
            }
        }

        private static BitmapImage? LoadImage(string name)
        {
            if (HelpManual.Image(name) is not { } bytes)
                return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
