using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// 媒体库卡片右上角 ⚙ 打开的单库设置（对齐 Web settings.js 的 openLibSettings）：
/// 名称 / 默认显示（作品或社团）/ 文件夹 / 扫描元数据 / 删除媒体库，集中在一个程序内覆盖层里。
/// 扫描走与 Web 共用的 <see cref="MediaLibScanner"/>，状态每 1.5 秒轮询一次。
/// 二次确认、输入框是叠在本层之上的覆盖层，所以本层不必像 Web 那样问完再重画。
/// </summary>
public sealed class LibSettingsDialog
{
    private readonly DependencyObject _owner;
    private string _name;
    private readonly StackPanel _body = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private Action<bool>? _close;

    private LibSettingsDialog(DependencyObject owner, string name)
    {
        _owner = owner;
        _name = name;
        _status.Style = Res<Style>("CaptionText");
        _timer.Tick += (_, _) => PollScan();
    }

    /// <summary>打开设置层，阻塞到关闭。调用方关掉后应刷新媒体库卡片（名称、文件夹数都可能变了）。</summary>
    public static void Show(DependencyObject owner, string libName)
    {
        var dialog = new LibSettingsDialog(owner, libName);
        OverlayHost.ShowModal(owner, close => dialog.Build(close));
        dialog._timer.Stop();
    }

    private FrameworkElement Build(Action<bool> close)
    {
        _close = close;
        var dim = new Grid { Background = new SolidColorBrush(Color.FromArgb(0x9A, 0, 0, 0)), Focusable = true };
        var card = new Border
        {
            Background = Res<Brush>("CardBrush") ?? Brushes.Black,
            BorderBrush = Res<Brush>("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(24, 20, 24, 18),
            Width = 640,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 640, Content = _body },
        };
        dim.Children.Add(card);
        dim.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            close(true);
            e.Handled = true;
        };
        Rebuild();
        PollScan();
        return dim;
    }

    /// <summary>按配置重画整层（改名 / 增删文件夹之后）。</summary>
    private void Rebuild()
    {
        _body.Children.Clear();
        var lib = AppConfig.ReadMediaLibs().FirstOrDefault(l => l.Name == _name);
        if (lib == null)
        {
            _close?.Invoke(true);
            return;
        }

        _body.Children.Add(new TextBlock
        {
            Text = I18n.Tr("媒体库设置"), FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 0, 0, 14),
        });

        // 名称：失焦 / 回车即改名（库名同时是作品记录里的值，改名由 MediaLibraryService 一并改掉）
        var nameBox = new TextBox { Text = lib.Name };
        void CommitName()
        {
            var v = nameBox.Text.Trim();
            if (v.Length == 0 || v == _name)
            {
                nameBox.Text = _name;
                return;
            }
            if (MediaLibraryService.RenameLibrary(_name, v) is { } err)
            {
                InAppDialog.Info(_owner, I18n.Tr(err), I18n.Tr("媒体库"));
                nameBox.Text = _name;
                return;
            }
            _name = v;
            Rebuild();
        }
        nameBox.LostFocus += (_, _) => CommitName();
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            CommitName();
            e.Handled = true;
        };
        _body.Children.Add(Row(I18n.Tr("名称"), nameBox, stretch: true));

        var viewBox = new ComboBox { Width = 120 };
        viewBox.Items.Add(new ComboBoxItem { Content = I18n.Tr("作品"), Tag = MediaLib.ViewWorks });
        viewBox.Items.Add(new ComboBoxItem { Content = I18n.Tr("社团"), Tag = MediaLib.ViewMakers });
        viewBox.SelectedIndex = lib.View == MediaLib.ViewMakers ? 1 : 0;
        viewBox.SelectionChanged += (_, _) =>
        {
            if (viewBox.SelectedItem is ComboBoxItem { Tag: string view })
                MediaLibraryService.SetLibraryView(_name, view);
        };
        _body.Children.Add(Row(I18n.Tr("默认显示"), viewBox));

        // 文件夹：一张卡片一个（编辑 / 移除），末尾同尺寸的虚线「+」卡片添加（同 Web .foldcards）；
        // 编辑与添加都走系统文件夹选择框（桌面固有）
        var folders = new UniformGrid { Columns = 2, Margin = new Thickness(0, 0, -10, -10) };
        foreach (var folder in lib.Folders)
            folders.Children.Add(FolderCard(folder));
        folders.Children.Add(AddFolderCard());
        _body.Children.Add(Row(I18n.Tr("文件夹"), folders, stretch: true, top: true));

        var scan = new Button { Content = I18n.Tr("扫描元数据") };
        scan.Click += (_, _) => StartScan(force: false);
        var rescan = new Button { Content = I18n.Tr("重新扫描元数据"), Margin = new Thickness(8, 0, 0, 0) };
        rescan.Click += (_, _) =>
        {
            if (InAppDialog.Confirm(_owner,
                    I18n.Format(I18n.Tr("重新扫描\"{name}\"会强制重新抓取全部元数据，较慢。继续？"), ("name", _name)),
                    I18n.Tr("确认")))
                StartScan(force: true);
        };
        var scanRow = Row(I18n.Tr("元数据"), scan);
        ((Panel)scanRow.Children[1]).Children.Add(rescan);
        _body.Children.Add(scanRow);
        _body.Children.Add(_status);

        // 底部：删除（左，红）/ 关闭（右，蓝），同 Web 的 .modal-btns
        var bottom = new DockPanel { Margin = new Thickness(0, 18, 0, 0), LastChildFill = false };
        var delete = new Button { Content = I18n.Tr("删除媒体库"), Style = Res<Style>("DangerButton") };
        delete.Click += (_, _) => DeleteLib();
        DockPanel.SetDock(delete, Dock.Left);
        bottom.Children.Add(delete);
        var closeButton = new Button { Content = I18n.Tr("关闭"), Style = Res<Style>("AccentButton"), MinWidth = 72 };
        closeButton.Click += (_, _) => _close?.Invoke(true);
        DockPanel.SetDock(closeButton, Dock.Right);
        bottom.Children.Add(closeButton);
        _body.Children.Add(bottom);
    }

    /// <summary>一行：120px 标签 + 控件区（同设置页的 FieldLabel 栅格）。</summary>
    private static Grid Row(string label, FrameworkElement control, bool stretch = false, bool top = false)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(new TextBlock
        {
            Text = label, Style = Res<Style>("CaptionText"),
            VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = new Thickness(0, top ? 4 : 0, 0, 0),
        });
        FrameworkElement host = control;
        if (!stretch)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(control);
            host = line;
        }
        Grid.SetColumn(host, 1);
        row.Children.Add(host);
        return row;
    }

    private void UpdateLib(Func<MediaLib, bool> change)
    {
        var libs = AppConfig.ReadMediaLibs();
        if (libs.FirstOrDefault(l => l.Name == _name) is { } lib && change(lib))
            AppConfig.WriteMediaLibs(libs);
        Rebuild();
    }

    private const double FolderCardHeight = 104;

    private Border FolderCard(string folder)
    {
        var edit = new Button { Content = I18n.Tr("编辑") };
        edit.Click += (_, _) => EditFolder(folder);
        var rm = new Button { Content = I18n.Tr("移除"), Style = Res<Style>("DangerButton"), Margin = new Thickness(6, 0, 0, 0) };
        rm.Click += (_, _) => UpdateLib(l => l.Folders.Remove(folder));
        var acts = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        acts.Children.Add(edit);
        acts.Children.Add(rm);
        DockPanel.SetDock(acts, Dock.Bottom);

        var panel = new DockPanel();
        panel.Children.Add(acts);
        panel.Children.Add(new TextBlock
        {
            Text = folder, ToolTip = folder, FontSize = 13, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 36,
        });
        return new Border
        {
            Background = Res<Brush>("ButtonBrush") ?? Res<Brush>("CardBrush"),
            BorderBrush = Res<Brush>("BorderBrush") ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 10, 10),
            Height = FolderCardHeight,
            Child = panel,
        };
    }

    private FrameworkElement AddFolderCard()
    {
        var muted = Res<Brush>("CaptionBrush") ?? Brushes.Gray;
        var content = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var plus = new TextBlock { Text = "+", FontSize = 28, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center };
        var label = new TextBlock { Text = I18n.Tr("添加文件夹"), FontSize = 12.5, Foreground = muted, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(plus);
        content.Children.Add(label);
        var border = new System.Windows.Shapes.Rectangle
        {
            Stroke = Res<Brush>("BorderBrush") ?? Brushes.Gray, StrokeThickness = 1, StrokeDashArray = [4, 3],
            RadiusX = 8, RadiusY = 8,
        };
        var card = new Grid
        {
            Background = Brushes.Transparent, Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 10), Height = FolderCardHeight,
        };
        card.Children.Add(border);
        card.Children.Add(content);
        var accent = Res<Brush>("AccentBrush") ?? Brushes.CornflowerBlue;
        card.MouseEnter += (_, _) => { border.Stroke = accent; plus.Foreground = label.Foreground = accent; };
        card.MouseLeave += (_, _) =>
        {
            border.Stroke = Res<Brush>("BorderBrush") ?? Brushes.Gray;
            plus.Foreground = label.Foreground = muted;
        };
        card.MouseLeftButtonUp += (_, _) => AddFolder();
        return card;
    }

    private string? PickFolder(string? initial = null)
    {
        var dialog = new OpenFolderDialog { Title = I18n.Tr("选择媒体库文件夹") };
        if (initial != null && Directory.Exists(initial))
            dialog.InitialDirectory = initial;
        var win = Window.GetWindow(_owner);
        return (win != null ? dialog.ShowDialog(win) : dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }

    /// <summary>改路径：原位替换，旧路径下的作品记录（作品目录/封面）一并改到新路径。</summary>
    private void EditFolder(string folder)
    {
        if (PickFolder(folder) is not { } picked)
            return;
        if (MediaLibraryService.ChangeLibraryFolder(_name, folder, picked) is { } err)
            InAppDialog.Info(_owner, I18n.Tr(err), I18n.Tr("媒体库"));
        Rebuild();
    }

    private void AddFolder()
    {
        if (PickFolder() is not { } picked)
            return;
        var path = Path.GetFullPath(picked);
        if (AppConfig.ReadMediaLibs().FirstOrDefault(l => l.Folders.Contains(path)) is { } other)
        {
            InAppDialog.Info(_owner,
                I18n.Format(I18n.Tr("该文件夹已在媒体库\"{name}\"中。"), ("name", other.Name)), I18n.Tr("媒体库"));
            return;
        }
        // 只登记文件夹，扫描由"扫描元数据"按钮触发（同设置页）
        UpdateLib(l =>
        {
            l.Folders.Add(path);
            return true;
        });
    }

    private void DeleteLib()
    {
        if (!InAppDialog.Confirm(_owner,
                I18n.Format(I18n.Tr("确定删除媒体库\"{name}\"吗？\n不会删除本地文件，已导入的作品记录保留。"), ("name", _name)),
                I18n.Tr("删除媒体库")))
            return;
        var libs = AppConfig.ReadMediaLibs();
        if (libs.RemoveAll(l => l.Name == _name) > 0)
        {
            AppConfig.WriteMediaLibs(libs);
            Db.Execute("UPDATE \"works\" SET \"library\" = NULL WHERE \"library\" = @l", ("@l", _name));
        }
        _close?.Invoke(true);
    }

    private void StartScan(bool force)
    {
        MediaLibScanner.Scan(_name, force);
        PollScan();
    }

    private void PollScan()
    {
        var (scanning, status) = MediaLibScanner.State();
        _status.Text = status.Length == 0 ? "" : (scanning ? "⏳ " : "✓ ") + status;
        _status.Visibility = status.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (scanning)
            _timer.Start();
        else
            _timer.Stop();
    }

    private static T? Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
