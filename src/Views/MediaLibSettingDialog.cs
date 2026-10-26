using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;
using Microsoft.Win32;

namespace R18MediaLibrary.Views;

/// <summary>
/// 媒体库管理（内联到系统设置页的「媒体库」分区，对齐 Web 设置页 renderMediaLibSection）：
/// 新建/删除媒体库、管理文件夹、触发扫描。扫描状态驻留在实例中，页面切换之间保持，后台扫描随实例存活。
/// （旧版为程序内覆盖层弹窗，现改为设置页内联区块。）
/// </summary>
public class MediaLibSettingDialog
{
    /// <summary>媒体库增删 / 文件夹增删。</summary>
    public event Action? LibsChanged;

    /// <summary>一次扫描结束（作品数据有更新）。</summary>
    public event Action? ScanDone;

    private List<MediaLib> _libs;
    private readonly List<(string Lib, bool Force)> _pending = [];  // 扫描队列
    private bool _scanning;
    private string _statusText = "";

    // 内联区块的实时 UI 引用（区块随设置页存活；扫描跨线程时切回 UI 线程更新）
    private TextBlock? _statusBlock;
    private StackPanel? _cardsPanel;
    private DependencyObject? _owner;

    public MediaLibSettingDialog()
    {
        _libs = AppConfig.ReadMediaLibs();
    }

    /// <summary>构建可内联到设置页的媒体库管理区块（工具栏 + 扫描状态 + 媒体库卡片 + 说明，对齐 Web）。</summary>
    public FrameworkElement BuildSection(DependencyObject? owner)
    {
        _owner = owner;
        _libs = AppConfig.ReadMediaLibs();

        var panel = new StackPanel();

        // 工具栏：新建媒体库 / 扫描全部（顺序对齐 Web renderMediaLibSection）
        var bar = new StackPanel { Orientation = Orientation.Horizontal };
        var createButton = new Button { Content = I18n.Tr("新建媒体库"), MinWidth = 108 };
        createButton.Click += (_, _) => CreateLib();
        bar.Children.Add(createButton);
        var scanAllButton = new Button { Content = I18n.Tr("扫描全部数据源"), MinWidth = 120, Margin = new Thickness(8, 0, 0, 0) };
        scanAllButton.Click += (_, _) => ScanAll();
        bar.Children.Add(scanAllButton);
        panel.Children.Add(bar);

        // 扫描状态（无扫描时隐藏）
        _statusBlock = new TextBlock
        {
            Text = _statusText,
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = Res("CaptionBrush", Brushes.Gray),
            TextWrapping = TextWrapping.Wrap,
            Visibility = string.IsNullOrEmpty(_statusText) ? Visibility.Collapsed : Visibility.Visible,
        };
        panel.Children.Add(_statusBlock);

        // 媒体库卡片列表
        _cardsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        panel.Children.Add(_cardsPanel);

        RebuildCards();
        return panel;
    }

    /// <summary>设置页每次显示时刷新媒体库列表（拾取外部改动）。</summary>
    public void RefreshLibs()
    {
        _libs = AppConfig.ReadMediaLibs();
        RebuildCards();
    }

    private void SaveLibs()
    {
        AppConfig.WriteMediaLibs(_libs);
        RebuildCards();
        LibsChanged?.Invoke();
    }

    private void RebuildCards()
    {
        if (_cardsPanel == null)
            return;
        _cardsPanel.Children.Clear();
        if (_libs.Count == 0)
        {
            _cardsPanel.Children.Add(new TextBlock
            {
                Text = I18n.Tr("还没有媒体库，点击\"新建媒体库\"创建。"),
                Foreground = Res("CaptionBrush", Brushes.Gray),
            });
            return;
        }
        foreach (var lib in _libs)
            _cardsPanel.Children.Add(BuildLibCard(lib));
    }

    private Border BuildLibCard(MediaLib lib)
    {
        var card = new Border
        {
            Background = Res("CardBrush", Brushes.Black),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
        };
        var panel = new StackPanel();

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var headerText = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        headerText.Children.Add(new TextBlock
        {
            Text = lib.Name, FontWeight = FontWeights.SemiBold, FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
        });
        headerText.Children.Add(new TextBlock
        {
            Text = I18n.Format(I18n.Tr("{n} 个文件夹"), ("n", lib.Folders.Count)),
            Foreground = Res("CaptionBrush", Brushes.Gray),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(headerText);

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal };
        var addButton = new Button { Content = I18n.Tr("添加文件夹") };
        addButton.Click += (_, _) => AddFolder(lib.Name);
        headerActions.Children.Add(addButton);
        var scanButton = new Button { Content = I18n.Tr("扫描元数据"), Margin = new Thickness(6, 0, 0, 0) };
        scanButton.Click += (_, _) => ScanLib(lib.Name, force: false);
        headerActions.Children.Add(scanButton);
        var rescanButton = new Button { Content = I18n.Tr("重新扫描元数据"), Margin = new Thickness(6, 0, 0, 0) };
        rescanButton.Click += (_, _) => ScanLib(lib.Name, force: true);
        headerActions.Children.Add(rescanButton);
        var deleteButton = new Button { Content = I18n.Tr("删除"), Margin = new Thickness(6, 0, 0, 0) };
        deleteButton.Click += (_, _) => DeleteLib(lib.Name);
        headerActions.Children.Add(deleteButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        panel.Children.Add(header);

        foreach (var folder in lib.Folders)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new TextBlock
            {
                Text = folder,
                Foreground = Res("CaptionBrush", Brushes.Gray),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var removeButton = new Button
            {
                Content = I18n.Tr("移除"), Width = 56, Padding = new Thickness(4, 2, 4, 2),
            };
            var libName = lib.Name;
            var captured = folder;
            removeButton.Click += (_, _) => RemoveFolder(libName, captured);
            Grid.SetColumn(removeButton, 1);
            row.Children.Add(removeButton);
            panel.Children.Add(row);
        }
        card.Child = panel;
        return card;
    }

    private MediaLib? FindLib(string name) => _libs.FirstOrDefault(l => l.Name == name);

    private Window? OwnerWindow() =>
        (_owner != null ? Window.GetWindow(_owner) : null) ?? Application.Current?.MainWindow;

    private void CreateLib()
    {
        var name = InAppDialog.Prompt(_owner, I18n.Tr("媒体库名称："), I18n.Tr("新建媒体库"))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        if (FindLib(name) != null)
        {
            InAppDialog.Info(_owner, I18n.Tr("已存在同名媒体库。"), I18n.Tr("媒体库"));
            return;
        }
        _libs.Add(new MediaLib { Name = name });
        SaveLibs();
    }

    private void AddFolder(string libName)
    {
        var lib = FindLib(libName);
        if (lib == null)
            return;
        var dialog = new OpenFolderDialog { Title = I18n.Tr("选择媒体库文件夹") };
        var win = OwnerWindow();
        var picked = win != null ? dialog.ShowDialog(win) : dialog.ShowDialog();
        if (picked != true)
            return;
        var path = Path.GetFullPath(dialog.FolderName);
        foreach (var other in _libs)
            if (other.Folders.Contains(path))
            {
                InAppDialog.Info(_owner,
                    I18n.Format(I18n.Tr("该文件夹已在媒体库\"{name}\"中。"), ("name", other.Name)),
                    I18n.Tr("媒体库"));
                return;
            }
        // 只登记文件夹，扫描由"扫描元数据"按钮触发
        lib.Folders.Add(path);
        SaveLibs();
    }

    private void RemoveFolder(string libName, string folder)
    {
        var lib = FindLib(libName);
        if (lib == null || !lib.Folders.Remove(folder))
            return;
        SaveLibs();
    }

    private void DeleteLib(string libName)
    {
        var lib = FindLib(libName);
        if (lib == null)
            return;
        if (!InAppDialog.Confirm(_owner,
                I18n.Format(I18n.Tr("确定删除媒体库\"{name}\"吗？\n不会删除本地文件，已导入的作品记录保留。"),
                    ("name", libName)),
                I18n.Tr("删除媒体库")))
            return;
        _libs.Remove(lib);
        _pending.RemoveAll(p => p.Lib == libName);
        SaveLibs();
        Db.Execute("UPDATE \"works\" SET \"library\" = NULL WHERE \"library\" = @l", ("@l", libName));
    }

    /// <summary>对所有媒体库依次触发数据源扫描。</summary>
    private void ScanAll()
    {
        foreach (var lib in _libs)
            if (lib.Folders.Count > 0)
                ScanLib(lib.Name, force: false);
    }

    /// <summary>扫描媒体库下的全部文件夹；force 为 True 时强制重新获取全部元数据。</summary>
    private void ScanLib(string libName, bool force)
    {
        var lib = FindLib(libName);
        if (lib == null || lib.Folders.Count == 0)
            return;
        if (_scanning)
        {
            if (!_pending.Contains((libName, force)))
                _pending.Add((libName, force));
        }
        else
        {
            _ = RunScanAsync(libName, force);
        }
    }

    /// <summary>后台扫描媒体库：导入 RJ 号、剔除已缺失作品，再调用 DL API + 作品页补全（文件夹列表在启动时快照）。</summary>
    private async Task RunScanAsync(string libName, bool force)
    {
        var lib = FindLib(libName);
        if (lib == null || lib.Folders.Count == 0)
        {
            DequeueNext();
            return;
        }
        _scanning = true;
        SetStatus(I18n.Format(I18n.Tr("正在扫描媒体库\"{name}\"…"), ("name", libName)));
        var folders = lib.Folders.ToList();
        try
        {
            var (added, total, removed) = await Task.Run(() =>
            {
                int addedSum = 0, totalSum = 0;
                foreach (var folder in folders)
                {
                    var (a, t) = MediaLibraryService.ImportMediaLib(folder, libName);
                    addedSum += a;
                    totalSum += t;
                }
                // 导入后剔除磁盘上已缺失的作品（同步删除 works / work_genres 记录）
                var pruned = MediaLibraryService.PruneMissingWorks(libName, folders);
                return (addedSum, totalSum, pruned.Count);
            });
            SetStatus(I18n.Format(I18n.Tr("已导入 {total} 个作品（新增 {added}，剔除 {removed}），正在从 DL API 补全数据…"),
                ("total", total), ("added", added), ("removed", removed)));

            var (filled, _, _) = await MediaLibraryService.BackfillWorksFromApiAsync(
                delaySeconds: 0.5,
                progress: (i, totalCount, _, _) =>
                {
                    if (i % 10 == 0 || i == totalCount)
                        SetStatus(I18n.Format(I18n.Tr("DL API 数据补全中… {i}/{total}"),
                            ("i", i), ("total", totalCount)));
                },
                library: libName, force: force);

            SetStatus(I18n.Tr("正在抓取 DLsite 作品页元数据与图片…"));
            var (pageFilled, pageMissed, _) = await MediaLibraryService.BackfillWorkPagesAsync(
                delaySeconds: 1.0,
                progress: (i, totalCount, rj, _) =>
                    SetStatus(I18n.Format(I18n.Tr("作品页抓取中… {i}/{total}（{rj}）"),
                        ("i", i), ("total", totalCount), ("rj", rj))),
                library: libName, force: force);

            SetStatus(I18n.Format(
                I18n.Tr("扫描完成：导入 {total} 个，剔除 {removed} 个，API 补全 {filled} 个，作品页补全 {page_filled} 个（失败 {page_missed}）"),
                ("total", total), ("removed", removed), ("filled", filled),
                ("page_filled", pageFilled), ("page_missed", pageMissed)));
        }
        catch (Exception e)
        {
            Logger.Error(e, "媒体库扫描");
            SetStatus(e.Message);
        }
        finally
        {
            _scanning = false;
            ScanDone?.Invoke();
            DequeueNext();
        }
    }

    private void DequeueNext()
    {
        if (_pending.Count == 0)
            return;
        var (lib, force) = _pending[0];
        _pending.RemoveAt(0);
        _ = RunScanAsync(lib, force);
    }

    /// <summary>更新状态：始终记入字符串，区块存在时同步更新实时文本并显示（跨线程时切回 UI 线程）。</summary>
    private void SetStatus(string text)
    {
        _statusText = text;
        void Apply()
        {
            if (_statusBlock == null)
                return;
            _statusBlock.Text = text;
            _statusBlock.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher != null && !dispatcher.CheckAccess())
            dispatcher.Invoke(Apply);
        else
            Apply();
    }

    private static Brush Res(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;
}

/// <summary>简单文本输入对话框（系统窗口兜底；正常走 InAppDialog.Prompt 程序内覆盖层）。</summary>
public class InputDialog : Window
{
    public string Value { get; private set; } = "";

    public InputDialog(string title, string prompt)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["WindowBrush"];
        ResizeMode = ResizeMode.NoResize;

        var panel = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
        var input = new TextBox();
        panel.Children.Add(input);
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var ok = new Button { Content = "OK", MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) =>
        {
            Value = input.Text;
            DialogResult = true;
        };
        var cancel = new Button
        {
            Content = I18n.Tr("取消"), MinWidth = 80, Margin = new Thickness(8, 0, 0, 0), IsCancel = true,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
        Loaded += (_, _) => input.Focus();
    }
}
