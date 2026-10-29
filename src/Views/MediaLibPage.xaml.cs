using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using R18MediaLibrary.Controls;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>媒体库页的根视图模式。</summary>
public enum MediaLibRoot
{
    /// <summary>媒体库页：媒体库 → 社团 → 作品。</summary>
    Library,
    /// <summary>标签页：标签 → 社团 → 作品。</summary>
    Genre,
    /// <summary>作品形式页：作品形式 → 社团 → 作品。</summary>
    WorkType,
    /// <summary>收藏夹页：收藏的作品平铺 → 作品详情。</summary>
    Favorite,
    /// <summary>社团页：社团（跨全部媒体库）→ 作品。</summary>
    Maker,
}

/// <summary>
/// 媒体库页（对应 Python 版 media_lib_UI.py）：媒体库卡片 → 社团卡片 → 作品卡片 → 作品详情 四级浏览。
/// root=Genre 时作为"标签页"使用（标签 → 社团 → 作品）；root=WorkType 时作为"作品形式页"使用（作品形式 → 社团 → 作品）。
/// </summary>
public partial class MediaLibPage : UserControl
{
    private const string UnknownMaker = ""; // maker_name 为空的作品归到"未知社团"

    // 卡片尺寸不再写死：列数/列宽由 CardGrid 按可用宽度算出（规则同 Web app.css，见 CLAUDE.md「UI 对齐基准」），
    // 卡片拉伸填满单元格、高度按 2:3 比例随宽度走。这里只留封面取图宽度与分组卡的固定高度。
    //
    // 封面解码宽度取图床档位 400（Web 也固定按这一档取图）：卡片宽在 150~310 之间浮动，
    // 逐宽度解码会让 ThumbnailCache 的键（含解码宽）散开、白白重复读盘。
    private const int CoverDecodeWidth = ImageHostClient.CardWidth;

    // 作品卡标题行高：字号 13.5 配 18px 行高，卡住两行（Web .wt 的 line-clamp: 2）
    private const double TitleLineHeight = 18;

    // 分组卡（媒体库/社团/标签/形式）固定高：Web 是内容高度（.group-card min-height 92），
    // 桌面这套等高网格只能取一个值，110 兼顾两行社团名。
    private const double ClickCardH = 110;

    // 虚拟化卡片网格的排布参数；作品卡（.grid.cards）与分组卡（.grid.groups）不同，按层级切换。
    public static readonly DependencyProperty CardMinWidthProperty = DependencyProperty.Register(
        nameof(CardMinWidth), typeof(double), typeof(MediaLibPage),
        new PropertyMetadata(CardGrid.MinWidth));
    public static readonly DependencyProperty CardMaxColumnsProperty = DependencyProperty.Register(
        nameof(CardMaxColumns), typeof(int), typeof(MediaLibPage),
        new PropertyMetadata(CardGrid.MaxColumns));
    public static readonly DependencyProperty CardAspectProperty = DependencyProperty.Register(
        nameof(CardAspect), typeof(double), typeof(MediaLibPage),
        new PropertyMetadata(CardGrid.Aspect));
    public static readonly DependencyProperty CardCellHeightProperty = DependencyProperty.Register(
        nameof(CardCellHeight), typeof(double), typeof(MediaLibPage),
        new PropertyMetadata(ClickCardH));

    /// <summary>列宽下限（作品卡 150 / 分组卡 220）。</summary>
    public double CardMinWidth
    {
        get => (double)GetValue(CardMinWidthProperty);
        set => SetValue(CardMinWidthProperty, value);
    }

    /// <summary>单行最多列数（作品卡 8；分组卡不限）。</summary>
    public int CardMaxColumns
    {
        get => (int)GetValue(CardMaxColumnsProperty);
        set => SetValue(CardMaxColumnsProperty, value);
    }

    /// <summary>卡片高宽比（作品卡 1.5；分组卡 0 = 用 CardCellHeight 的固定高）。</summary>
    public double CardAspect
    {
        get => (double)GetValue(CardAspectProperty);
        set => SetValue(CardAspectProperty, value);
    }

    /// <summary>分组卡的固定行高（CardAspect = 0 时生效）。</summary>
    public double CardCellHeight
    {
        get => (double)GetValue(CardCellHeightProperty);
        set => SetValue(CardCellHeightProperty, value);
    }

    private static readonly string[] VideoExts =
        [".mp4", ".mkv", ".avi", ".wmv", ".mov", ".flv", ".webm", ".m4v", ".ts", ".mpg", ".mpeg"];
    private static readonly string[] AudioExts =
        [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma", ".opus"];

    private static readonly Dictionary<string, string> AgeMap = new()
    {
        ["1"] = "全年龄", ["2"] = "R-15", ["3"] = "R-18",
    };

    // label → (db列名, 是否多值用 " / " 分割)
    private static readonly Dictionary<string, (string Col, bool Multi)> LinkCols = new()
    {
        ["社团"] = ("maker_name", false),
        ["系列"] = ("series", false),
        ["剧本"] = ("scenario", false),
        ["插画"] = ("illust", false),
        ["声优"] = ("voice_actor", true),
    };

    private readonly MediaLibRoot _root;

    private string _level = "libs";  // libs / makers / works / detail / genres / types / filtered_works
    private string? _currentLib;
    private string? _currentMaker;   // null=未选社团；UnknownMaker=未知社团
    private string? _currentGenre;
    private string? _currentType;    // 当前选中的作品形式（work_type）
    private string? _currentWork;
    private string? _currentWorkName;   // 当前详情作品名（删除确认文案用）
    private string? _currentWorkFolder;
    private bool _currentRead;   // 当前详情作品的"已读"状态
    private bool _currentFav;    // 当前详情作品的"收藏"状态
    private string? _filterCol;
    private string? _filterVal;
    private int _totalWorks;
    private double _worksScrollPos;

    // 搜索作用域：分组层级（媒体库首页/社团页）输入关键字时改为在当前作用域内按 RJ号/作品名 搜索作品
    private bool _searchMode;        // 当前是否处于作品搜索结果视图
    private int _scopedSearchGen;    // 作用域搜索世代号：异步查库返回后据此丢弃过期结果
    private bool _detailFromSearch;  // 当前详情是否由搜索结果打开（返回时回到搜索结果）
    private string _searchLevel = "libs";  // 进入详情前所处的搜索层级（libs/makers）
    private string _detailReturnLevel = "works";  // 进入详情前的作品列表层级（返回时复原）

    // 所有卡片层级统一的数据源：每项携带一个延迟构建器与搜索过滤键；
    // Element 供虚拟化列表在实例化容器时才真正构建卡片元素（滚出视口即释放）。
    private sealed record GridCard(Func<Border> Build, string FilterKey)
    {
        public UIElement Element => Build();
    }
    private List<GridCard> _gridCards = [];   // 当前层级全部卡片
    private List<GridCard> _shownCards = [];  // 搜索过滤后的卡片
    private bool _workLevelCount;             // 计数文案样式：true=作品卡，false=分组卡
    private string _countUnit = "";           // 分组卡层级的单位文案（个社团/个媒体库/…）

    // 排序状态：社团页按作品数，作品页按发售日（详见 MakerSortOptions / WorkSortOptions）
    private int _makerSort;   // 0=作品数↓ 1=作品数↑ 2=社团名
    private int _workSort;    // 0=发售日↓ 1=发售日↑ 2=RJ号↓ 3=RJ号↑
    private bool _suppressSort;  // 重填 SortBox 时抑制 SelectionChanged

    // 详情页下方的作品文件（原「查看作品」独立页并入详情）默认缩略图视图（false=文件列表）
    private bool _treeThumbView = true;
    // 「作品详情」折叠块默认收起；记下被展开的是哪部作品，重新可见/切语言重建详情时保持展开
    private string? _detailOpenWork;

    // 整页预览状态
    private InlineMediaPlayer? _inlinePlayer;
    private List<string> _previewImages = [];
    private List<string> _previewVideos = [];   // 视频整页预览的播放队列（同文件夹视频，按名称排序）
    private bool _previewVideoMode;              // 当前整页预览是否为视频（决定 StepPreview 切换图片还是视频）
    private int _previewIndex;

    private static readonly Brush RowHoverBrush = MakeFrozenBrush(Color.FromRgb(0x26, 0x26, 0x2B));   // Web .trow:hover = surface
    private static readonly Brush CoverBgBrush = MakeFrozenBrush(Color.FromRgb(0x15, 0x15, 0x1A));    // Web .cover / .fthumb 底色
    private static readonly Brush BadgeBgBrush = MakeFrozenBrush(Color.FromArgb(158, 0, 0, 0));       // Web .badge rgba(0,0,0,.62)
    private static readonly Brush BadgeFgBrush = MakeFrozenBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));

    private static Brush MakeFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // 底部音频播放条：代码动态加入 RootGrid 第 2 行（在文件树中就地播放 wav/mp3 等）
    private readonly AudioPlayerBar AudioBar = new() { Margin = new Thickness(0, 10, 0, 0) };

    // 搜索输入防抖：分组层级的作用域搜索会查库，逐字触发既卡 UI 又打库，
    // 停顿 250ms 后再执行一次过滤（作品/标签/形式层级则是纯内存过滤）。
    private readonly DispatcherTimer _searchDebounce =
        new() { Interval = TimeSpan.FromMilliseconds(250) };

    public MediaLibPage(MediaLibRoot root)
    {
        InitializeComponent();
        Grid.SetRow(AudioBar, 2);
        RootGrid.Children.Add(AudioBar);
        _root = root;
        // 各根视图的初始层级；媒体库设置按钮仅在媒体库首页显示（由 ClearCards/ShowLibs 控制）
        _level = root switch
        {
            MediaLibRoot.Genre => "genres",
            MediaLibRoot.WorkType => "types",
            MediaLibRoot.Favorite => "favorites",
            MediaLibRoot.Maker => "all_makers",
            _ => "libs",
        };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyCardFilter();
        };
        HelpSlot.Content = HelpPanel.Button(HelpTopic);
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    /// <summary>当前层级对应的使用手册章节（同 Web help.js 的 helpTopic）；作品文件画在详情页里，故详情即第 5 章。</summary>
    private string HelpTopic() => _level switch
    {
        "detail" => "5",
        "libs" when _root == MediaLibRoot.Library => "4",
        _ when _root == MediaLibRoot.Library => "4.1",
        _ => "4.3",
    };

    private void RetranslateUi()
    {
        BackButton.Content = I18n.Tr("← 返回");
        OpenFolderButton.Content = "📂 " + I18n.Tr("打开文件夹");
        // 图标对齐 Web library.js（移动媒体库 📦）
        MoveLibButton.Content = "📦 " + I18n.Tr("移动媒体库");
        DeleteWorkButton.Content = "🗑 " + I18n.Tr("删除作品");
        SearchBox.ToolTip = I18n.Tr("搜索 RJ号 / 作品名");
        PreviewCloseButton.Content = I18n.Tr("← 返回");
        PreviewPrevButton.Content = "‹ " + I18n.Tr("上一张");
        PreviewNextButton.Content = I18n.Tr("下一张") + " ›";
        Refresh();
    }

    private long _cfgVersionSeen = -1;

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
        {
            AudioBar.Stop();  // 离开本页时停止音频播放
            return;
        }
        // 切换到本页时：仅当配置版本变过才重读配置库；始终重查作品数据刷新当前视图
        if (AppConfig.Version != _cfgVersionSeen)
        {
            AppConfig.Reload();
            _cfgVersionSeen = AppConfig.Version;
        }
        Refresh();
    }

    // ---------- 导航 ----------

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_level)
        {
            case "filtered_works":
                ShowDetail();
                break;
            case "detail":
                _currentWork = null;
                if (_detailFromSearch)
                {
                    // 由搜索结果进入：恢复搜索层级并按搜索框内容重跑作用域搜索
                    _detailFromSearch = false;
                    _level = _searchLevel;
                    ApplyCardFilter();
                }
                else
                {
                    // 返回进入详情前的作品列表层级
                    switch (_detailReturnLevel)
                    {
                        case "lib_works": ShowLibWorks(); break;
                        case "favorites": ShowFavorites(); break;
                        case "filtered_works": ShowFilteredWorks(); break;
                        case "root": ShowRoot(); break;   // 由下载管理「跳转作品」直接打开（OpenWork）
                        default: ShowWorks(); break;
                    }
                }
                RestoreWorksScroll();
                break;
            case "lib_works" when _currentGenre != null:
                _currentGenre = null;
                ShowGenres();
                break;
            case "lib_works" when _currentType != null:
                _currentType = null;
                ShowTypes();
                break;
            case "lib_works":
                ShowLibs();
                break;
            case "works":
                _currentMaker = null;
                if (_root == MediaLibRoot.Maker)
                    ShowAllMakers();   // 社团页：作品返回社团根视图
                else
                    ShowMakers();
                break;
            case "makers":
                if (_currentGenre != null)
                {
                    _currentGenre = null;
                    ShowGenres();
                }
                else if (_currentType != null)
                {
                    _currentType = null;
                    ShowTypes();
                }
                else
                {
                    ShowLibs();
                }
                break;
            case "genres" when _root != MediaLibRoot.Genre:
                ShowLibs();
                break;
            case "types" when _root != MediaLibRoot.WorkType:
                ShowLibs();
                break;
        }
    }

    private void ReadButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWork == null)
            return;
        _currentRead = !_currentRead;
        Db.Execute("UPDATE \"works\" SET \"read_flag\" = @v WHERE \"work_id\" = @w",
            ("@v", _currentRead ? "1" : null), ("@w", _currentWork));
        UpdateReadFavButtons();
    }

    private void FavButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWork == null)
            return;
        _currentFav = !_currentFav;
        Db.Execute("UPDATE \"works\" SET \"favorite\" = @v WHERE \"work_id\" = @w",
            ("@v", _currentFav ? "1" : null), ("@w", _currentWork));
        UpdateReadFavButtons();
    }

    private void UpdateReadFavButtons()
    {
        // 图标与激活态对齐 Web 端 .toggle/.toggle.on（library.js）：已读 ★/☆、收藏 ♥/♡；
        // 激活时按钮填充强调色、文字转白（等价 Web 的 .toggle.on）
        ReadButton.Content = (_currentRead ? "★ " : "☆ ") + I18n.Tr("已读");
        FavButton.Content = (_currentFav ? "♥ " : "♡ ") + I18n.Tr("收藏");
        ApplyToggleState(ReadButton, _currentRead);
        ApplyToggleState(FavButton, _currentFav);
    }

    /// <summary>切换按钮的激活态外观：on=强调色底+白字，off=默认按钮外观（对齐 Web .toggle.on）。</summary>
    private void ApplyToggleState(Button btn, bool on)
    {
        btn.Background = (Brush)FindResource(on ? "AccentBrush" : "ButtonBrush");
        btn.BorderBrush = (Brush)FindResource(on ? "AccentBrush" : "BorderBrush");
        btn.Foreground = on ? Brushes.White : (Brush)FindResource("TextBrush");
    }

    private void LibToggleButton_Click(object sender, RoutedEventArgs e)
    {
        // 在"显示作品"（媒体库全部作品）与"显示社团"（社团卡片）之间切换
        if (_level == "lib_works")
            ShowMakers();
        else
            ShowLibWorks();
    }

    /// <summary>进入某个媒体库，或标签/形式下钻的社团页时显示"显示作品/显示社团"切换按钮。</summary>
    private void UpdateLibToggleButton()
    {
        var inLib = (_currentLib != null || _currentGenre != null || _currentType != null)
                    && _level is "lib_works" or "makers";
        LibToggleButton.Visibility = inLib ? Visibility.Visible : Visibility.Collapsed;
        if (inLib)
            LibToggleButton.Content = _level == "makers" ? "▦ " + I18n.Tr("显示作品") : "🏢 " + I18n.Tr("显示社团");
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentWorkFolder != null && Directory.Exists(_currentWorkFolder))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_currentWorkFolder}\""));
    }

    private void MoveLibButton_Click(object sender, RoutedEventArgs e)
    {
        // 把当前作品移动到另一个媒体库：移动文件夹 + 改库 + 同步元数据
        var workId = _currentWork;
        if (workId == null)
            return;
        if (_currentWorkFolder == null || !Directory.Exists(_currentWorkFolder))
        {
            InAppDialog.Warn(this, I18n.Tr("作品文件夹不存在，无法移动"), I18n.Tr("移动媒体库"));
            return;
        }
        var currentLib = Db.Scalar(
            "SELECT \"library\" FROM \"works\" WHERE \"work_id\" = @w", ("@w", workId)) as string;

        var dialog = new DownTargetDialog(currentLib, moveMode: true);
        if (!dialog.Show(this) || dialog.SelectedFolder == null)
            return;
        var targetLib = dialog.SelectedLib!;
        var targetFolder = dialog.SelectedFolder;
        if (!InAppDialog.Confirm(this,
                I18n.Format(I18n.Tr("确定将 {id} 移动到媒体库“{lib}”吗？"), ("id", workId), ("lib", targetLib)),
                I18n.Tr("移动媒体库")))
            return;

        MoveLibButton.IsEnabled = false;
        MoveLibButton.Content = I18n.Tr("移动中…");
        _ = MoveWorkAsync(workId, targetLib, targetFolder);
    }

    private async Task MoveWorkAsync(string workId, string targetLib, string targetFolder)
    {
        var (ok, message) = await Task.Run(() =>
            MediaLibraryService.MoveWorkToLibraryAsync(workId, targetLib, targetFolder));
        MoveLibButton.IsEnabled = true;
        MoveLibButton.Content = "📦 " + I18n.Tr("移动媒体库");
        if (ok)
        {
            InAppDialog.Info(this, I18n.Tr("已移动到新媒体库"), I18n.Tr("移动媒体库"));
            Refresh();  // 重新载入详情，反映新的媒体库与文件夹
        }
        else
        {
            InAppDialog.Warn(this, message, I18n.Tr("移动媒体库"));
        }
    }

    /// <summary>
    /// 删除作品（对齐 Web library.js 的「🗑 删除作品」）：二次确认后删除作品文件夹内的
    /// 全部文件与数据库记录，不可恢复。作品文件夹已不在时同样可删——正是清理孤儿记录的入口。
    /// </summary>
    private void DeleteWorkButton_Click(object sender, RoutedEventArgs e)
    {
        var workId = _currentWork;
        if (workId == null)
            return;
        var name = string.IsNullOrEmpty(_currentWorkName) ? workId : _currentWorkName;
        if (!InAppDialog.Confirm(this,
                I18n.Format(
                    I18n.Tr("确定删除《{name}》吗？\n作品文件夹内的全部文件与数据库记录都会被删除，且无法恢复。"),
                    ("name", name)),
                I18n.Tr("删除作品")))
            return;
        AudioBar.Stop();   // 正在播放本作品的音频会占着文件句柄，删之前先松手
        DeleteWorkButton.IsEnabled = false;
        DeleteWorkButton.Content = I18n.Tr("删除中…");
        _ = DeleteWorkAsync(workId);
    }

    private async Task DeleteWorkAsync(string workId)
    {
        // 整个作品目录的递归删除可能很慢（上百张图/网络盘），放后台线程，别冻界面
        var (ok, message) = await Task.Run(() => MediaLibraryService.DeleteWorkAsync(workId));
        DeleteWorkButton.IsEnabled = true;
        DeleteWorkButton.Content = "🗑 " + I18n.Tr("删除作品");
        if (!ok)
        {
            InAppDialog.Warn(this, message, I18n.Tr("删除作品"));
            return;
        }
        if (_level != "detail" || _currentWork != workId)
        {
            Refresh();   // 删除期间已导航去别处：只刷新当前视图，别把用户从新页面弹回去
            return;
        }
        // 退回进入详情前的列表层级并重新查库，被删的作品卡随之消失
        BackButton_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// 下载管理「跳转作品」：直接打开该作品详情（对齐 Web gotoWork），返回即回到本页根视图。
    /// 调用方先切导航到本页；随之而来的 IsVisibleChanged → Refresh 会按 detail 层级原样重建。
    /// </summary>
    public void OpenWork(string workId)
    {
        _currentLib = _currentMaker = _currentGenre = _currentType = null;
        _detailFromSearch = false;
        _detailReturnLevel = "root";
        _worksScrollPos = 0;
        _currentWork = workId;
        ShowDetail();
    }

    public void Refresh()
    {
        // 重新加载当前层级的视图
        var names = AppConfig.ReadMediaLibs().Select(l => l.Name).ToHashSet();
        if (_currentLib != null && !names.Contains(_currentLib))
            ShowRoot();  // 当前库已被删除/改名，回到根视图
        else if (_level == "filtered_works" && _filterCol != null)
            ShowFilteredWorks();
        else if (_level == "detail" && _currentWork != null)
            ShowDetail();
        else if (_level == "favorites")
            ShowFavorites();
        else if (_level == "all_makers")
            ShowAllMakers();
        else if (_level == "lib_works" && (_currentLib != null || _currentGenre != null || _currentType != null))
            ShowLibWorks();
        else if (_level is "works" && _currentMaker != null)
            ShowWorks();
        else if (_level == "makers" && (_currentLib != null || _currentGenre != null || _currentType != null))
            ShowMakers();
        else if (_level == "genres")
            ShowGenres();
        else if (_level == "types")
            ShowTypes();
        else
            ShowRoot();
    }

    private void ShowRoot()
    {
        switch (_root)
        {
            case MediaLibRoot.Genre:
                ShowGenres();
                break;
            case MediaLibRoot.WorkType:
                ShowTypes();
                break;
            case MediaLibRoot.Favorite:
                ShowFavorites();
                break;
            case MediaLibRoot.Maker:
                ShowAllMakers();
                break;
            default:
                ShowLibs();
                break;
        }
    }

    // ---------- 视图构建 ----------

    private void ClearCards()
    {
        OpenFolderButton.Visibility = Visibility.Collapsed;
        MoveLibButton.Visibility = Visibility.Collapsed;
        DeleteWorkButton.Visibility = Visibility.Collapsed;
        ReadButton.Visibility = Visibility.Collapsed;
        FavButton.Visibility = Visibility.Collapsed;
        LibToggleButton.Visibility = Visibility.Collapsed;   // 仅媒体库内作品/社团视图显示
        SortBox.Visibility = Visibility.Collapsed;            // 仅社团页/作品页显示，由 ApplyCardFilter 重新打开
        SearchBox.Visibility = Visibility.Visible;           // 默认显示搜索框，详情页由 ShowDetail 隐藏
        DetailActions.Visibility = Visibility.Collapsed;     // 详情操作条仅详情页显示
        // 顶栏标题（Web #title）：详情页不显示（正文已有大标题，Web .detail-mode #title { display:none }）
        var title = _level == "detail" ? "" : PageTitle();
        TitleLabel.Text = title;
        TitleLabel.Visibility = title.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _currentWorkFolder = null;
        _currentWorkName = null;
        ClosePreview();
    }

    /// <summary>
    /// 当前层级的顶栏标题，文案对齐 Web library.js 各 render* 写入 #title 的内容：
    /// 根视图是分区名，进入某库/标签/形式/社团后是它的名字。
    /// </summary>
    private string PageTitle() => _level switch
    {
        "libs" => I18n.Tr("媒体库"),
        "lib_works" => _currentGenre ?? _currentType ?? _currentLib ?? "",
        "makers" => _currentGenre ?? _currentType ?? _currentLib ?? I18n.Tr("作品社团"),
        "all_makers" => I18n.Tr("作品社团"),
        "genres" => I18n.Tr("作品标签"),
        "types" => I18n.Tr("作品形式"),
        "favorites" => I18n.Tr("我的收藏"),
        "works" => _currentMaker is null or UnknownMaker
            ? I18n.Tr("未知社团")
            : MakerAliasService.Display(_currentMaker),
        "filtered_works" => _filterCol == "age_category"
            ? I18n.Tr(AgeMap.GetValueOrDefault(_filterVal ?? "", _filterVal ?? ""))
            : _filterVal ?? "",
        _ => "",
    };

    /// <summary>切到卡片视图：显示虚拟化卡片列表、隐藏详情/文件树容器，并绑定当前层级的卡片数据源。</summary>
    private void ShowCardList()
    {
        // 作品卡 = Web 的 .grid.cards（列宽下限 150、单行最多 8 列、高宽比 1.5）；
        // 分组卡 = Web 的 .grid.groups（列宽下限 220、不限列数、内容高度）
        CardMinWidth = _workLevelCount ? CardGrid.MinWidth : CardGrid.GroupMinWidth;
        CardMaxColumns = _workLevelCount ? CardGrid.MaxColumns : 0;
        CardAspect = _workLevelCount ? CardGrid.Aspect : 0;
        CardCellHeight = ClickCardH;
        CardList.ItemsSource = null;   // 强制重建容器，避免复用旧层级容器造成错位
        CardList.ItemsSource = _shownCards;
        CardList.Visibility = Visibility.Visible;
        CardsScroll.Visibility = Visibility.Collapsed;
        ContentHost.Content = null;
    }

    /// <summary>切到详情/文件树视图：隐藏卡片列表、显示 ContentHost 容器（承载任意内容）。</summary>
    private void ShowContentHost()
    {
        CardList.ItemsSource = null;   // 释放卡片容器
        CardList.Visibility = Visibility.Collapsed;
        CardsScroll.Visibility = Visibility.Visible;
    }

    private ScrollViewer? _cardListScroll;

    /// <summary>取得卡片列表模板内的 ScrollViewer（用于记录/恢复滚动位置）。</summary>
    private ScrollViewer? CardListScrollViewer()
    {
        if (_cardListScroll != null)
            return _cardListScroll;
        CardList.ApplyTemplate();
        _cardListScroll = CardList.Template?.FindName("CardListScroll", CardList) as ScrollViewer;
        return _cardListScroll;
    }

    /// <param name="iconUrl">
    /// 分组头像的来源地址（目前只有 fanbox 社团有，指向 pawchive），空则不画头像。
    /// 画在卡片左侧、名称与作品数的左边，异步载入，取不到就保持占位底色——不阻塞卡片先出来。
    /// </param>
    /// <param name="iconHostUrl">该头像在图床上的地址；非空则优先走图床，失败再回退 <paramref name="iconUrl"/>。</param>
    /// <param name="onEdit">非空则在卡片右上角挂一个按钮（社团卡 🖊 改显示名、媒体库卡 ⚙ 设置）。</param>
    /// <param name="editIcon">右上角按钮的图标，缺省 🖊。</param>
    /// <param name="editTip">右上角按钮的提示，缺省「自定义显示名称」。</param>
    /// <param name="avatarSlot">
    /// 是否留出左侧头像位。社团卡一律传 true：没有头像的社团那一格空着，
    /// 同一行里的卡片版式才对得齐（名称起始位置一致）。
    /// </param>
    /// <param name="links">社团主页外链（FANBOX / pawchive），作品数下面一行小按钮，点了用浏览器打开。</param>
    private Border MakeClickCard(string title, string caption, Action onClick,
        string iconUrl = "", string iconHostUrl = "", Action? onEdit = null, bool avatarSlot = false,
        string editIcon = "🖊", string? editTip = null, IReadOnlyList<(string Label, string Url)>? links = null)
    {
        // 尺寸交给网格：卡片拉伸填满单元格（宽由 CardGrid 算、高为 ClickCardH），间距也由网格排
        var card = new Border
        {
            Style = (Style)FindResource("ClickCard"),
            Padding = new Thickness(14),   // Web .group-card { padding: 14px }
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            Tag = title.ToLowerInvariant(),  // 搜索过滤用
        };
        // 右侧信息块：名称 + 作品数竖排（对齐 Web 的 .ginfo）
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        FrameworkElement content = info;   // 有头像时换成「头像 | 信息」两列
        info.Children.Add(new TextBlock
        {
            Text = title, FontWeight = FontWeights.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = caption, Style = (Style)FindResource("CaptionText"), Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (links is { Count: > 0 })
        {
            // 对齐 Web 的 .glinks：描边小标签，悬停提亮。Button 自己吞掉 MouseLeftButtonUp，点它不会进入该社团
            var linkRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (var (label, url) in links)
            {
                var link = new Button
                {
                    Content = label, ToolTip = url, Style = (Style)FindResource("LinkChip"),
                    Margin = new Thickness(0, 0, 6, 0),
                };
                link.Click += (_, e) =>
                {
                    e.Handled = true;
                    try
                    {
                        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"打开社团主页失败 {url}: {ex.Message}");
                    }
                };
                linkRow.Children.Add(link);
            }
            info.Children.Add(linkRow);
        }
        if (avatarSlot)
        {
            // 左头像 | 右信息，按 2:8 分栏（对齐 Web 的 .group-card.has-icon）。
            // 左栏 MinWidth 取头像边长：窄卡片上纯按比例分会让左栏小于头像，把头像挤裁掉
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition
                { Width = new GridLength(2, GridUnitType.Star), MinWidth = 82 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8, GridUnitType.Star) });
            // 正方形，边长吃满卡片可用高度 82 = 卡高 110 − 上下内距 14×2。
            // 卡片高度固定，所以边长只能由高度决定；左栏比它宽的部分留白，靠居中吃掉
            // 没有头像的社团：这一格留着但什么都不画（底色透明），只占位
            var avatar = new Border
            {
                Width = 82, Height = 82, CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = iconUrl.Length > 0
                    ? (Brush)FindResource("BorderBrush")   // 载入中的占位底色
                    : Brushes.Transparent,
            };
            Grid.SetColumn(info, 1);
            row.Children.Add(avatar);
            row.Children.Add(info);
            content = row;
            if (iconUrl.Length > 0)
                _ = LoadGroupIconAsync(avatar, iconUrl, iconHostUrl);
        }

        if (onEdit == null)
        {
            card.Child = content;
        }
        else
        {
            // 内容与右上角按钮叠放。Button 自己会把 MouseLeftButtonUp 标记为已处理，
            // 所以点按钮不会顺带触发卡片的"进入该分组"
            var overlay = new Grid();
            overlay.Children.Add(content);
            var edit = new Button
            {
                Content = editIcon,
                ToolTip = editTip ?? I18n.Tr("自定义显示名称"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0),   // 抵消卡片 14px 内距，贴近右上角
                Padding = new Thickness(6, 2, 6, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = (Brush)FindResource("CaptionBrush"),
                FontSize = 13,
                Cursor = Cursors.Hand,
                // Web .gedit：常驻但压暗（.35），悬停卡片时提亮（.9），悬停按钮本身文字转正文色
                Opacity = 0.35,
            };
            card.MouseEnter += (_, _) => edit.Opacity = 0.9;
            card.MouseLeave += (_, _) => edit.Opacity = 0.35;
            edit.MouseEnter += (_, _) => edit.Foreground = (Brush)FindResource("TextBrush");
            edit.MouseLeave += (_, _) => edit.Foreground = (Brush)FindResource("CaptionBrush");
            edit.Click += (_, e) =>
            {
                e.Handled = true;
                onEdit();
            };
            overlay.Children.Add(edit);
            card.Child = overlay;
        }
        card.MouseLeftButtonUp += (_, _) => onClick();
        return card;
    }

    /// <summary>
    /// 右上角 🖊：社团设置——显示名称 + FANBOX / pawchive 主页（对齐 Web library.js 的 editMaker）。
    /// 显示名只写映射表，works.maker_name 这个真名不动，分组与筛选继续按真名走
    /// （见 <see cref="MakerAliasService"/> / <see cref="MakerHomeService"/>）。
    /// </summary>
    private void EditMaker(string maker)
    {
        var display = MakerAliasService.Display(maker);
        var home = MakerHomeService.Get(maker);
        var values = InAppDialog.Form(this, I18n.Tr("社团设置"),
            I18n.Format(I18n.Tr("社团「{maker}」（不会改动作品数据）"), ("maker", maker)),
            [
                (I18n.Tr("显示名称（留空恢复原名）"), display == maker ? "" : display),
                (I18n.Tr("FANBOX 主页"), home.Fanbox),
                (I18n.Tr("pawchive 主页"), home.Pawchive),
            ]);
        if (values == null)
            return;   // 取消
        // 先存主页（会校验地址），不合法就什么都不写，免得显示名存了、主页没存
        if (MakerHomeService.Set(maker, values[1], values[2]) is { } err)
        {
            InAppDialog.Info(this, I18n.Tr(err), I18n.Tr("社团设置"));
            return;
        }
        MakerAliasService.Set(maker, values[0]);
        if (_level == "all_makers")
            ShowAllMakers();
        else
            ShowMakers();
    }

    /// <summary>社团卡上的主页外链（FANBOX / pawchive），没有设置的不显示。</summary>
    private static List<(string Label, string Url)> MakerLinks(string maker)
    {
        var home = MakerHomeService.Get(maker);
        var links = new List<(string, string)>();
        if (home.Fanbox.Length > 0)
            links.Add(("FANBOX", home.Fanbox));
        if (home.Pawchive.Length > 0)
            links.Add(("pawchive", home.Pawchive));
        return links;
    }

    /// <summary>
    /// 异步载入分组头像，走 ThumbnailCache（进程级缓存 + 并发上限）。
    ///
    /// 优先图床：那是用户自己机器上的服务，不必回源 pawchive，也不需要中性 UA；
    /// 图床没有该头像（或取图失败）才回退站点直链，此时必须带 <see cref="PawchiveApi.UserAgent"/>
    /// ——pawchive 的防护网关会拦浏览器 UA。
    /// </summary>
    private static async Task LoadGroupIconAsync(Border target, string url, string hostUrl)
    {
        // 82pt 的正方形头像，按 2 倍解码保证高 DPI 下不糊
        var bmp = hostUrl.Length > 0 ? await ThumbnailCache.LoadUrlAsync(hostUrl, 164) : null;
        bmp ??= await ThumbnailCache.LoadUrlAsync(url, 164, PawchiveApi.UserAgent);
        // Uniform 而非 UniformToFill：非正方形的头像也完整显示、不裁切（对齐 Web 的 object-fit: contain）
        if (bmp != null)
            target.Background = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
    }

    /// <summary>一级：媒体库卡片。</summary>
    private void ShowLibs()
    {
        _level = "libs";
        _currentLib = null;
        _currentMaker = null;
        var counts = new Dictionary<string, long>();
        var rows = Db.Select(
            "SELECT \"library\", COUNT(*) FROM \"works\" WHERE \"state\" = '已品悦' GROUP BY \"library\"");
        if (rows != null)
            foreach (var row in rows)
                counts[row[0] as string ?? ""] = Convert.ToInt64(row[1]);
        _totalWorks = (int)counts.Values.Sum();
        _workLevelCount = false;
        _countUnit = I18n.Tr("个媒体库");
        _gridCards = AppConfig.ReadMediaLibs().Select(lib =>
        {
            var name = lib.Name;
            var caption = I18n.Format(I18n.Tr("{works} 个作品 · {folders} 个文件夹"),
                ("works", counts.GetValueOrDefault(name)), ("folders", lib.Folders.Count));
            var view = lib.View;
            return new GridCard(() => MakeClickCard(name, caption, () =>
            {
                _currentLib = name;
                _currentMaker = null;
                _currentGenre = null;
                _currentType = null;
                // 进库先显示什么由该库的「默认显示」决定（缺省作品），进去后仍可就地切换
                if (view == MediaLib.ViewMakers)
                    ShowMakers();
                else
                    ShowLibWorks();
            }, onEdit: () =>
            {
                LibSettingsDialog.Show(this, name);
                if (_level == "libs")
                    ShowLibs();   // 名称 / 文件夹数 / 库本身都可能变了
            }, editIcon: "⚙", editTip: I18n.Tr("媒体库设置")), name.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    /// <summary>
    /// 媒体库首页网格末尾的虚线「新建媒体库」卡（对齐 Web drawGroups 的 add-card）。
    /// 不放进 _gridCards，免得算进「共 N 个媒体库」；由 ApplyCardFilter 追加在过滤结果之后。
    /// Border 画不了虚线，所以底下垫一个带 StrokeDashArray 的圆角 Rectangle 当边框，悬停时换强调色。
    /// </summary>
    private Border MakeAddLibCard()
    {
        var dash = new System.Windows.Shapes.Rectangle
        {
            RadiusX = 10, RadiusY = 10, StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            Stroke = (Brush)FindResource("BorderBrush"),
        };
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var plus = new TextBlock { Text = "+", FontSize = 26, VerticalAlignment = VerticalAlignment.Center };
        var text = new TextBlock
        {
            Text = I18n.Tr("新建媒体库"), FontSize = 15, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        label.Children.Add(plus);
        label.Children.Add(text);
        var layer = new Grid();
        layer.Children.Add(dash);
        layer.Children.Add(label);
        var card = new Border
        {
            Background = Brushes.Transparent,   // 透明但可命中，整卡都能点
            Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = layer,
        };
        void Paint(bool hover)
        {
            dash.Stroke = (Brush)FindResource(hover ? "AccentLightBrush" : "BorderBrush");
            var fg = (Brush)FindResource(hover ? "TextBrush" : "CaptionBrush");
            plus.Foreground = fg;
            text.Foreground = fg;
        }
        Paint(false);
        card.MouseEnter += (_, _) => Paint(true);
        card.MouseLeave += (_, _) => Paint(false);
        card.MouseLeftButtonUp += (_, _) => CreateLibFromHome();
        return card;
    }

    /// <summary>新建媒体库（对齐 Web createLibFromHome）：建好后直接打开该库的设置，好接着添加文件夹。</summary>
    private void CreateLibFromHome()
    {
        var name = InAppDialog.Prompt(this, I18n.Tr("媒体库名称："), I18n.Tr("新建媒体库"))?.Trim();
        if (string.IsNullOrEmpty(name))
            return;
        var libs = AppConfig.ReadMediaLibs();
        if (libs.Any(l => l.Name == name))
        {
            InAppDialog.Info(this, I18n.Tr("已存在同名媒体库。"), I18n.Tr("媒体库"));
            return;
        }
        libs.Add(new MediaLib { Name = name });
        AppConfig.WriteMediaLibs(libs);
        ShowLibs();
        LibSettingsDialog.Show(this, name);
        if (_level == "libs")
            ShowLibs();
    }

    /// <summary>二级：当前媒体库（或当前标签）下的社团卡片，懒加载，可按作品数/社团名排序。</summary>
    private void ShowMakers()
    {
        _level = "makers";
        DlsiteMakerIcon.Kick();   // 后台补齐还没查过的社团头像（已查过的不会重复请求）
        _currentMaker = null;
        List<object?[]>? rows;
        if (_currentGenre != null)
            rows = Db.Select(
                "SELECT w.\"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("w.") + ", " + DlsiteMakerIcon.MakerIdExpr("w.") + " FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " +
                "GROUP BY w.\"maker_name\" " + MakerOrderClause("w."),
                ("@g", _currentGenre));
        else if (_currentType != null)
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"work_type\" = @t " +
                "GROUP BY \"maker_name\" " + MakerOrderClause(),
                ("@t", _currentType));
        else
            rows = Db.Select(
                "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
                "WHERE \"state\" = '已品悦' AND \"library\" = @lib " +
                "GROUP BY \"maker_name\" " + MakerOrderClause(),
                ("@lib", _currentLib ?? ""));
        if (rows == null)
            return;
        _totalWorks = (int)rows.Sum(r => Convert.ToInt64(r[1]));
        _workLevelCount = false;
        _countUnit = I18n.Tr("个社团");
        _gridCards = rows.Select(row =>
        {
            var maker = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            // 显示名走映射；真名只用于分组跳转与筛选
            var display = MakerAliasService.Display(maker);
            var title = display.Length > 0 ? display : I18n.Tr("未知社团");
            // fanbox 作家头像来自 pawchive，DLsite 社团头像来自 ci-en；都没有就留空位
            var fanboxId = row.Length > 2 ? row[2] as string ?? "" : "";
            var dlsiteId = row.Length > 3 ? row[3] as string ?? "" : "";
            var (icon, iconHost) = MakerIcons.Resolve(fanboxId, dlsiteId);
            return new GridCard(() => MakeClickCard(
                title, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () =>
                {
                    _currentMaker = maker.Length > 0 ? maker : UnknownMaker;
                    ShowWorks();
                }, icon, iconHost,
                maker.Length > 0 ? () => EditMaker(maker) : null,
                avatarSlot: true, editTip: I18n.Tr("社团设置"), links: MakerLinks(maker)),
                // 搜索过滤同时匹配真名与显示名
                $"{maker} {display}".ToLowerInvariant());
        }).ToList();
        PopulateSortBox(MakerSortOptions, _makerSort);
        ApplyCardFilter();
    }

    /// <summary>社团根视图：跨全部媒体库按社团分组的社团卡片，可按作品数/社团名排序。</summary>
    private void ShowAllMakers()
    {
        _level = "all_makers";
        DlsiteMakerIcon.Kick();   // 后台补齐还没查过的社团头像（已查过的不会重复请求）
        _currentLib = null;
        _currentGenre = null;
        _currentType = null;
        _currentMaker = null;
        var rows = Db.Select(
            "SELECT \"maker_name\", COUNT(*), " + FanboxService.MakerIdExpr("") + ", " + DlsiteMakerIcon.MakerIdExpr("") + " FROM \"works\" " +
            "WHERE \"state\" = '已品悦' GROUP BY \"maker_name\" " + MakerOrderClause());
        if (rows == null)
            return;
        _totalWorks = (int)rows.Sum(r => Convert.ToInt64(r[1]));
        _workLevelCount = false;
        _countUnit = I18n.Tr("个社团");
        _gridCards = rows.Select(row =>
        {
            var maker = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            // 显示名走映射；真名只用于分组跳转与筛选
            var display = MakerAliasService.Display(maker);
            var title = display.Length > 0 ? display : I18n.Tr("未知社团");
            // fanbox 作家头像来自 pawchive，DLsite 社团头像来自 ci-en；都没有就留空位
            var fanboxId = row.Length > 2 ? row[2] as string ?? "" : "";
            var dlsiteId = row.Length > 3 ? row[3] as string ?? "" : "";
            var (icon, iconHost) = MakerIcons.Resolve(fanboxId, dlsiteId);
            return new GridCard(() => MakeClickCard(
                title, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () =>
                {
                    _currentMaker = maker.Length > 0 ? maker : UnknownMaker;
                    ShowWorks();
                }, icon, iconHost,
                maker.Length > 0 ? () => EditMaker(maker) : null,
                avatarSlot: true, editTip: I18n.Tr("社团设置"), links: MakerLinks(maker)),
                // 搜索过滤同时匹配真名与显示名
                $"{maker} {display}".ToLowerInvariant());
        }).ToList();
        PopulateSortBox(MakerSortOptions, _makerSort);
        ApplyCardFilter();
    }

    /// <summary>标签视图：全部已品悦作品的ジャンル标签卡片。</summary>
    private void ShowGenres()
    {
        _level = "genres";
        var rows = Db.Select(
            "SELECT g.\"genre\", COUNT(*) FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" " +
            "WHERE w.\"state\" = '已品悦' GROUP BY g.\"genre\" ORDER BY COUNT(*) DESC");
        if (rows == null)
            return;
        var total = Db.Scalar(
            "SELECT COUNT(DISTINCT g.\"work_id\") FROM \"work_genres\" g " +
            "JOIN \"works\" w ON w.\"work_id\" = g.\"work_id\" WHERE w.\"state\" = '已品悦'");
        _totalWorks = total != null ? (int)Convert.ToInt64(total) : 0;
        _workLevelCount = false;
        _countUnit = I18n.Tr("个标签");
        _gridCards = rows.Select(row =>
        {
            var genre = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            return new GridCard(() => MakeClickCard(
                genre, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () => OpenGenre(genre)), genre.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    private void OpenGenre(string genre)
    {
        _currentGenre = genre;
        ShowMakers();
    }

    /// <summary>作品形式视图：全部已品悦作品按 work_type 分组的形式卡片。</summary>
    private void ShowTypes()
    {
        _level = "types";
        var rows = Db.Select(
            "SELECT \"work_type\", COUNT(*) FROM \"works\" " +
            "WHERE \"state\" = '已品悦' AND \"work_type\" IS NOT NULL AND \"work_type\" <> '' " +
            "GROUP BY \"work_type\" ORDER BY COUNT(*) DESC");
        if (rows == null)
            return;
        _totalWorks = (int)rows.Sum(r => Convert.ToInt64(r[1]));
        _workLevelCount = false;
        _countUnit = I18n.Tr("个形式");
        _gridCards = rows.Select(row =>
        {
            var type = row[0] as string ?? "";
            var count = Convert.ToInt64(row[1]);
            return new GridCard(() => MakeClickCard(
                type, I18n.Format(I18n.Tr("{count} 个作品"), ("count", count)),
                () => OpenType(type)), type.ToLowerInvariant());
        }).ToList();
        ApplyCardFilter();
    }

    private void OpenType(string type)
    {
        _currentType = type;
        _currentGenre = null;
        _currentLib = null;
        _currentMaker = null;
        ShowMakers();
    }

    /// <summary>三级：当前社团的作品卡片（标签流程下为 标签+社团），默认按发售日由新到旧。</summary>
    private void ShowWorks()
    {
        _level = "works";
        List<object?[]>? rows;
        var makerCond = _currentMaker == UnknownMaker
            ? "(\"maker_name\" IS NULL OR \"maker_name\" = '')"
            : "\"maker_name\" = @maker";
        if (_currentGenre != null)
            rows = Db.Select(
                "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", " +
                "w.\"age_category\", w.\"cover\" FROM \"works\" w " +
                "JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                $"WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g AND {makerCond.Replace("\"maker_name\"", "w.\"maker_name\"")} " +
                WorkOrderClause("w."),
                ("@g", _currentGenre), ("@maker", _currentMaker ?? ""));
        else if (_currentType != null)
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                $"FROM \"works\" WHERE \"state\" = '已品悦' AND \"work_type\" = @t AND {makerCond} " +
                WorkOrderClause(),
                ("@t", _currentType), ("@maker", _currentMaker ?? ""));
        else if (_currentLib != null)
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                $"FROM \"works\" WHERE \"state\" = '已品悦' AND \"library\" = @lib AND {makerCond} " +
                WorkOrderClause(),
                ("@lib", _currentLib ?? ""), ("@maker", _currentMaker ?? ""));
        else
            // 社团根视图：按社团跨全部媒体库
            rows = Db.Select(
                "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
                $"FROM \"works\" WHERE \"state\" = '已品悦' AND {makerCond} " + WorkOrderClause(),
                ("@maker", _currentMaker ?? ""));
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>按单列值过滤的作品视图（跨所有媒体库），由详情页可点击字段触发。</summary>
    private void ShowFilteredWorks()
    {
        _level = "filtered_works";
        var cond = _filterCol == "voice_actor"
            ? "\"voice_actor\" LIKE @val"
            : $"\"{_filterCol}\" = @val";
        var value = _filterCol == "voice_actor" ? $"%{_filterVal}%" : _filterVal;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            $"FROM \"works\" WHERE \"state\" = '已品悦' AND {cond} " + WorkOrderClause(),
            ("@val", value));
        if (rows != null)
            SetWorkCards(rows);
    }

    private void OpenFilter(string column, string value)
    {
        _filterCol = column;
        _filterVal = value;
        ShowFilteredWorks();
    }

    /// <summary>收藏夹视图：平铺所有已收藏的作品（懒加载，复用作品卡片流）。</summary>
    private void ShowFavorites()
    {
        _level = "favorites";
        _currentMaker = null;
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"work_type\", \"age_category\", \"cover\" " +
            "FROM \"works\" WHERE \"favorite\" = '1' " + WorkOrderClause());
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>
    /// 平铺作品视图（可切换到社团视图）：进入媒体库后的默认视图，也是标签/形式社团页的「显示作品」
    /// （对齐 Web renderMakers 的 mode=works）。范围优先级与 ShowMakers 一致：标签 → 形式 → 媒体库。
    /// </summary>
    private void ShowLibWorks()
    {
        _level = "lib_works";
        _currentMaker = null;
        const string cols = "SELECT w.\"work_id\", w.\"work_name\", w.\"maker_name\", w.\"work_type\", w.\"age_category\", w.\"cover\" ";
        var rows = _currentGenre != null
            ? Db.Select(cols + "FROM \"works\" w JOIN \"work_genres\" g ON g.\"work_id\" = w.\"work_id\" " +
                "WHERE w.\"state\" = '已品悦' AND g.\"genre\" = @g " + WorkOrderClause("w."), ("@g", _currentGenre))
            : _currentType != null
            ? Db.Select(cols + "FROM \"works\" w WHERE w.\"state\" = '已品悦' AND w.\"work_type\" = @t " + WorkOrderClause("w."),
                ("@t", _currentType))
            : Db.Select(cols + "FROM \"works\" w WHERE w.\"state\" = '已品悦' AND w.\"library\" = @lib " + WorkOrderClause("w."),
                ("@lib", _currentLib ?? ""));
        if (rows != null)
            SetWorkCards(rows);
    }

    /// <summary>作品卡层级共用：把查询结果转成懒加载卡片项并刷新 SortBox。</summary>
    private void SetWorkCards(List<object?[]> rows)
    {
        _workLevelCount = true;
        _gridCards = rows.Select(row => new GridCard(
            () => MakeWorkCard(row),
            $"{row[0]} {row[1]} {row[2]}".ToLowerInvariant())).ToList();
        PopulateSortBox(WorkSortOptions, _workSort);
        ApplyCardFilter();
    }

    // ---------- 排序 ----------

    private string[] MakerSortOptions =>
    [
        I18n.Tr("作品数 多→少"), I18n.Tr("作品数 少→多"), I18n.Tr("社团名 A→Z"),
    ];

    private string[] WorkSortOptions =>
    [
        I18n.Tr("发售日 新→旧"), I18n.Tr("发售日 旧→新"), I18n.Tr("RJ号 新→旧"), I18n.Tr("RJ号 旧→新"),
    ];

    /// <summary>社团排序的 ORDER BY 子句；prefix 为表别名（标签流程下为 "w."）。</summary>
    private string MakerOrderClause(string prefix = "") => _makerSort switch
    {
        1 => "ORDER BY COUNT(*) ASC",
        2 => $"ORDER BY {prefix}\"maker_name\" COLLATE NOCASE ASC",
        _ => "ORDER BY COUNT(*) DESC",
    };

    /// <summary>作品排序的 ORDER BY 子句；prefix 为表别名（标签流程下为 "w."）。</summary>
    private string WorkOrderClause(string prefix = "") => _workSort switch
    {
        1 => $"ORDER BY {prefix}\"sell_date\" ASC",
        2 => $"ORDER BY {prefix}\"work_id\" DESC",
        3 => $"ORDER BY {prefix}\"work_id\" ASC",
        _ => $"ORDER BY {prefix}\"sell_date\" DESC",
    };

    /// <summary>用指定选项重填 SortBox 并选中当前排序，抑制由此触发的事件。</summary>
    private void PopulateSortBox(string[] options, int selected)
    {
        _suppressSort = true;
        SortBox.Items.Clear();
        foreach (var option in options)
            SortBox.Items.Add(new ComboBoxItem { Content = option });
        SortBox.SelectedIndex = Math.Min(Math.Max(selected, 0), options.Length - 1);
        _suppressSort = false;
    }

    private void SortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSort || SortBox.SelectedIndex < 0)
            return;
        var idx = SortBox.SelectedIndex;
        if (_searchMode)
        {
            // 搜索结果按作品排序重排：直接重跑作用域搜索
            _workSort = idx;
            ApplyCardFilter();
            return;
        }
        switch (_level)
        {
            case "makers":
                _makerSort = idx;
                ShowMakers();
                break;
            case "all_makers":
                _makerSort = idx;
                ShowAllMakers();
                break;
            case "works":
                _workSort = idx;
                ShowWorks();
                break;
            case "filtered_works":
                _workSort = idx;
                ShowFilteredWorks();
                break;
            case "favorites":
                _workSort = idx;
                ShowFavorites();
                break;
            case "lib_works":
                _workSort = idx;
                ShowLibWorks();
                break;
        }
    }

    // ---------- 过滤与懒加载 ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void ApplyCardFilter()
    {
        if (_level == "detail")
            return;
        var keyword = SearchBox.Text.Trim();
        // 分组层级（媒体库首页 / 社团页）输入关键字时，改为在当前作用域内按 RJ号/作品名 搜索作品
        if (keyword.Length > 0 && _level is "libs" or "makers" or "all_makers")
        {
            ShowScopedSearch(keyword);
            return;
        }
        _searchMode = false;
        // 其余层级（作品卡 / 标签 / 形式）：在全量卡片项上按关键字过滤（内存过滤），由虚拟化列表按需渲染
        var key = keyword.ToLowerInvariant();
        _shownCards = key.Length == 0
            ? _gridCards.ToList()
            : _gridCards.Where(c => c.FilterKey.Contains(key)).ToList();
        if (_level == "libs")
            _shownCards.Add(new GridCard(MakeAddLibCard, ""));   // 末尾虚线「新建媒体库」卡
        ClearCards();   // 统一关闭详情按钮
        BackButton.Visibility = ShouldShowBack() ? Visibility.Visible : Visibility.Collapsed;
        SortBox.Visibility = _level is "makers" or "all_makers" or "works" or "filtered_works" or "favorites" or "lib_works"
            ? Visibility.Visible : Visibility.Collapsed;
        UpdateLibToggleButton();
        ShowCardList();
        UpdateGridCountLabel();
    }

    /// <summary>
    /// 作用域搜索：在当前层级对应的范围内按 RJ号/作品名 搜索作品并平铺为作品卡。
    /// 媒体库首页搜全部库；进入某媒体库后限当前库；标签/形式下的社团页限对应分组。
    /// 不改动 _gridCards，清空搜索框后即可回到原分组视图。
    /// </summary>
    private async void ShowScopedSearch(string keyword)
    {
        _searchMode = true;
        var conds = new List<string> { "\"state\" = '已品悦'", "(\"work_id\" LIKE @kw OR \"work_name\" LIKE @kw)" };
        var pars = new List<(string, object?)> { ("@kw", $"%{keyword}%") };
        var join = "";
        if (_level == "makers")
        {
            if (_currentGenre != null)
            {
                join = "JOIN \"work_genres\" g ON g.\"work_id\" = \"works\".\"work_id\" ";
                conds.Add("g.\"genre\" = @g");
                pars.Add(("@g", _currentGenre));
            }
            else if (_currentType != null)
            {
                conds.Add("\"work_type\" = @t");
                pars.Add(("@t", _currentType));
            }
            else
            {
                conds.Add("\"library\" = @lib");
                pars.Add(("@lib", _currentLib ?? ""));
            }
        }
        var sql = "SELECT \"works\".\"work_id\", \"works\".\"work_name\", \"works\".\"maker_name\", " +
                  "\"works\".\"work_type\", \"works\".\"age_category\", \"works\".\"cover\" FROM \"works\" " +
                  join + "WHERE " + string.Join(" AND ", conds) + " " + WorkOrderClause("\"works\".");
        // 查库放后台线程，避免逐字搜索阻塞 UI；用世代号丢弃已被更新搜索取代的结果
        var gen = ++_scopedSearchGen;
        var pary = pars.ToArray();
        var rows = await Task.Run(() => Db.Select(sql, pary)) ?? [];
        if (gen != _scopedSearchGen || _level != "makers" && _level != "libs" && _level != "all_makers")
            return;

        _workLevelCount = true;
        _shownCards = rows.Select(row => new GridCard(
            () => MakeWorkCard(row),
            $"{row[0]} {row[1]} {row[2]}".ToLowerInvariant())).ToList();
        PopulateSortBox(WorkSortOptions, _workSort);
        ClearCards();
        BackButton.Visibility = ShouldShowBack() ? Visibility.Visible : Visibility.Collapsed;
        SortBox.Visibility = Visibility.Collapsed;   // Web scopedWorkSearch：搜索结果不给排序
        UpdateLibToggleButton();
        ShowCardList();
        UpdateGridCountLabel();
    }

    /// <summary>返回按钮在当前层级是否显示（根视图层级不显示）。</summary>
    private bool ShouldShowBack() => _level switch
    {
        "libs" => false,
        "favorites" => false,
        "all_makers" => false,
        "genres" => _root != MediaLibRoot.Genre,
        "types" => _root != MediaLibRoot.WorkType,
        _ => true,
    };

    /// <summary>刷新计数文案：作品卡显示作品总数，分组卡显示分组数+作品数（虚拟化后不再有"已加载"概念）。</summary>
    private void UpdateGridCountLabel()
    {
        var keyword = SearchBox.Text.Trim();
        if (_searchMode)
        {
            // 作用域搜索：仅统计命中的作品数
            CountLabel.Text = I18n.Format(I18n.Tr("搜索到 {count} 个作品"), ("count", _shownCards.Count));
            return;
        }
        var total = _gridCards.Count;
        var shown = _shownCards.Count;
        if (_workLevelCount)
        {
            CountLabel.Text = keyword.Length > 0
                ? I18n.Format(I18n.Tr("共 {total} 个作品，匹配 {matched} 个"), ("total", total), ("matched", shown))
                : I18n.Format(I18n.Tr("共 {total} 个作品"), ("total", total));
        }
        else
        {
            CountLabel.Text = keyword.Length > 0
                ? I18n.Format(I18n.Tr("共 {total} {unit}，匹配 {shown} 个"),
                    ("total", total), ("unit", _countUnit), ("shown", shown))
                : I18n.Format(I18n.Tr("共 {total} {unit}，{works} 个作品"),
                    ("total", total), ("unit", _countUnit), ("works", _totalWorks));
        }
    }

    private void RestoreWorksScroll()
    {
        var target = _worksScrollPos;
        if (target <= 0)
            return;
        // 虚拟化列表：直接恢复滚动偏移即可（可见卡片按需实例化，无需预加载）
        Dispatcher.BeginInvoke(() => CardListScrollViewer()?.ScrollToVerticalOffset(target),
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// 后台解码封面/缩略图并在完成后赋值（先显示占位底色，避免 UI 线程逐张同步解码卡顿）。
    /// 用 image.Tag == path 作有效性判据：容器被回收改指其它图片时丢弃过期结果。
    ///
    /// hostUrl 非空时优先向图床取图——媒体库多在 HDD 上，成批卡片逐张随机读会明显卡顿；
    /// 图床取不到（未同步到 / 服务没开）再回退本地原图，保证任何情况下都不少图。
    /// </summary>
    private static async void LoadImageAsync(Image image, string path, int decodePixelWidth, string? hostUrl = null)
    {
        BitmapSource? bmp = null;
        if (hostUrl != null)
            bmp = await ThumbnailCache.LoadUrlAsync(hostUrl, decodePixelWidth);
        bmp ??= await ThumbnailCache.LoadAsync(path, decodePixelWidth);
        if (bmp != null && ReferenceEquals(image.Tag, path))
            image.Source = bmp;
    }

    /// <summary>
    /// 同 <see cref="LoadImageAsync"/>，但把图贴成 Border 的背景画刷。
    /// 卡片封面是满幅的（对齐 Web 的 .cover），用带 CornerRadius 的 Border 画才能被圆角裁掉——
    /// Border 的 ClipToBounds 只按矩形裁，塞一个 Image 进去顶角会戳出圆角之外。
    /// </summary>
    private static async void LoadCoverAsync(Border target, string path, int decodePixelWidth, string? hostUrl = null)
    {
        BitmapSource? bmp = null;
        if (hostUrl != null)
            bmp = await ThumbnailCache.LoadUrlAsync(hostUrl, decodePixelWidth);
        bmp ??= await ThumbnailCache.LoadAsync(path, decodePixelWidth);
        if (bmp == null || !ReferenceEquals(target.Tag, path))
            return;
        var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        target.Background = brush;
    }

    /// <summary>单个作品卡片：封面 + 角标（RJ号/形式）+ 标题。</summary>
    private Border MakeWorkCard(object?[] row)
    {
        var workId = row[0] as string ?? "";
        var workName = row[1] as string ?? "";
        var makerName = row[2] as string ?? "";
        var workType = row[3] as string ?? "";
        var cover = row[5] as string;

        // 尺寸全交给网格：卡片拉伸填满单元格（宽由 CardGrid 算、高 = 宽 × 1.5），内部按 Web 的
        // .cover(flex:2) / .wt(flex:1) 分成 2:1 两行；封面满幅无内边距（Web .card 也没有）
        var card = new Border
        {
            Style = (Style)FindResource("ClickCard"),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            ToolTip = workName.Length > 0 ? workName : workId,
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CardGrid.CoverFlex, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CardGrid.TextFlex, GridUnitType.Star) });

        // 封面区：满幅拉伸、按比例铺满裁切（Web .cover img { object-fit: cover }），无封面时显示纯色底。
        // 上两角随卡片圆角裁切，下两角为直角（下面紧接标题区）。
        var coverGrid = new Grid { ClipToBounds = true };
        // Web .cover { background: #15151a }；上角圆角 = 卡片圆角 10 − 描边 1
        var coverFill = new Border
        {
            Background = CoverBgBrush,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Tag = cover,
        };
        coverGrid.Children.Add(coverFill);
        if (cover != null)
            // 封面后台解码：存在性检查也并入后台流程（LoadAsync→ThumbnailCache 内做 FileInfo）
            LoadCoverAsync(coverFill, cover, CoverDecodeWidth, ImageHostService.CoverUrl(workId));
        // RJ 号：封面左上角；作品形式：右上角。RJ 号定长按需占位，作品形式吃剩下的宽度、超出省略——
        // 等价 Web 的 .badge { max-width: 70% }，且窄卡片上两个角标不会叠在一起
        var badgeBar = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6) };
        badgeBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        badgeBar.ColumnDefinitions.Add(new ColumnDefinition());
        var badgeStyleBg = BadgeBgBrush;   // Web .badge { background: rgba(0,0,0,.62); border-radius: 6px }
        var rjBadge = new Border
        {
            Background = badgeStyleBg, CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1, 7, 1),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = workId, FontSize = 11, Foreground = BadgeFgBrush,
                TextTrimming = TextTrimming.CharacterEllipsis,
            },
        };
        badgeBar.Children.Add(rjBadge);
        if (workType.Length > 0)
        {
            var typeBadge = new Border
            {
                Background = badgeStyleBg, CornerRadius = new CornerRadius(6),
                Padding = new Thickness(7, 1, 7, 1),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = workType, FontSize = 11, Foreground = BadgeFgBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };
            Grid.SetColumn(typeBadge, 1);
            badgeBar.Children.Add(typeBadge);
        }
        coverGrid.Children.Add(badgeBar);
        panel.Children.Add(coverGrid);

        // 标题区：占卡片高度 1/3，最多两行、超出省略（Web .wt 的 -webkit-line-clamp: 2）
        var title = new TextBlock
        {
            Text = workName.Length > 0 ? workName : workId,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            LineHeight = TitleLineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            MaxHeight = TitleLineHeight * 2,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(10, 8, 10, 10),   // Web .wt { padding: 8px 10px 10px }
        };
        Grid.SetRow(title, 1);
        panel.Children.Add(title);
        card.Child = panel;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _worksScrollPos = CardListScrollViewer()?.VerticalOffset ?? 0;
            _currentWork = workId;
            // 记录进入详情前的层级，以便返回时回到原作品列表 / 搜索视图
            _detailFromSearch = _searchMode;
            _searchLevel = _level;
            _detailReturnLevel = _level;
            ShowDetail();
        };
        return card;
    }

    // ---------- 作品详情 ----------

    /// <summary>正文/轮播图后台加载的产物：正文块（文本/图片）与轮播图路径。</summary>
    private sealed record DetailContent(List<(string Kind, string Value)> BodyBlocks, List<string> SliderPaths);

    private async void ShowDetail()
    {
        var rows = Db.Select(
            "SELECT \"work_id\", \"work_name\", \"maker_name\", \"sell_date\", \"series\", " +
            "\"scenario\", \"illust\", \"voice_actor\", \"age_category\", \"work_type\", " +
            "\"genre\", \"file_size\", \"intro_s\", \"folder\", \"read_flag\", \"favorite\", " +
            "\"maker_id\", \"cover\", \"eh_token\" " +   // eh_token: E-Hentai 画廊令牌，拼站点链接要用
            "FROM \"works\" WHERE \"work_id\" = @w",
            ("@w", _currentWork));
        if (rows is not { Count: > 0 })
            return;
        var r = rows[0];
        var workId = r[0] as string ?? "";
        var workName = r[1] as string ?? "";
        var workFolder = r[13] as string;
        _currentWorkName = workName;

        _level = "detail";
        BackButton.Visibility = Visibility.Visible;
        ClearCards();
        ShowContentHost();   // 详情用 ContentHost 承载，隐藏卡片列表
        CountLabel.Text = "";
        // 详情页不显示搜索框，操作条紧跟返回键（Web .detail-mode）
        SearchBox.Visibility = Visibility.Collapsed;
        DetailActions.Visibility = Visibility.Visible;
        // 作品号：字段表第一行，点击在浏览器打开站上的作品页（Web 详情 .fields 的「作品号」）
        var isFanbox = FanboxService.IsFanboxWorkId(workId);
        var isEhentai = EhentaiService.IsEhentaiWorkId(workId);
        var isPixiv = PixivService.IsPixivWorkId(workId);
        var isFantia = FanboxService.IsFantiaWorkId(workId);
        // fanbox / Fantia / E-Hentai / pixiv 的 work_id 是内部键（FB/FT/EH/PX + 站上的号），展示与跳转都用站上的号
        var linkText = isFanbox || isFantia ? FanboxService.PostIdOf(workId)
            : isEhentai ? EhentaiService.GidOf(workId)
            : isPixiv ? PixivService.IllustIdOf(workId)
            : workId;
        var linkUrl = isFanbox
            ? $"https://{AppConfig.PawchiveHost}/{PawchiveApi.FanboxService}/user/" +
              $"{r[16] as string ?? ""}/post/{FanboxService.PostIdOf(workId)}"   // r[16]=maker_id=作家号
            : isEhentai
            // 画廊地址要 gid + token，token 入队时存在 works.eh_token 里（r[18]）
            ? EhentaiApi.GalleryUrl(long.TryParse(EhentaiService.GidOf(workId), out var gid) ? gid : 0,
                r[18] as string ?? "")
            : isPixiv
            ? PixivApi.ArtworkUrl(PixivService.IllustIdOf(workId))
            : isFantia
            ? FantiaApi.PostPageUrl(FanboxService.PostIdOf(workId))
            : $"https://www.dlsite.com/maniax/work/=/product_id/{workId}.html";
        var rjLink = new Hyperlink(new Run(linkText))
        {
            Foreground = (Brush)FindResource("AccentLightBrush"),
            TextDecorations = null,
        };
        rjLink.Click += (_, _) => Process.Start(new ProcessStartInfo(linkUrl) { UseShellExecute = true });
        var rjBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };
        rjBlock.Inlines.Add(rjLink);
        if (workFolder != null && Directory.Exists(workFolder))
        {
            _currentWorkFolder = workFolder;
            OpenFolderButton.Visibility = Visibility.Visible;
            MoveLibButton.Visibility = Visibility.Visible;
        }
        // 删除作品：文件夹不在了也要能删（清理孤儿记录），故不放进上面的文件夹分支
        DeleteWorkButton.Visibility = Visibility.Visible;
        // 已读 / 收藏 切换按钮
        _currentRead = r[14] as string == "1";
        _currentFav = r[15] as string == "1";
        ReadButton.Visibility = Visibility.Visible;
        FavButton.Visibility = Visibility.Visible;
        UpdateReadFavButtons();

        // Web .detail { padding: 16px } + h1 { font-size: 20px; margin: 0 0 12px }
        var root = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(16),
        };
        var layout = new StackPanel();
        root.Child = layout;

        layout.Children.Add(new TextBlock
        {
            Text = workName.Length > 0 ? workName : workId,
            FontWeight = FontWeights.SemiBold, FontSize = 20, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        // 「▸ 作品详情」折叠块（对齐 Web .dfold）：封面/字段/正文都收在里面，默认收起；
        // 没有作品文件可看时详情就是全部内容，直接展开
        var hasFiles = _currentWorkFolder != null;
        var foldBody = new StackPanel
        {
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = !hasFiles || _detailOpenWork == workId ? Visibility.Visible : Visibility.Collapsed,
        };
        var foldArrow = new TextBlock
        {
            Text = foldBody.Visibility == Visibility.Visible ? "▾" : "▸",
            Width = 14, Foreground = (Brush)FindResource("CaptionBrush"),
        };
        // Web .dfold-head { padding: 6px 0 }（h1 下 12px 由标题的 6 + 这里的 6 凑出）
        var foldHead = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 6, 0, 6),
            Background = Brushes.Transparent,   // 整行可点，不只是文字
            Cursor = Cursors.Hand,
        };
        foldHead.Children.Add(foldArrow);
        foldHead.Children.Add(new TextBlock
        {
            Text = I18n.Tr("作品详情"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0),
        });
        var makerName = r[2] as string ?? "";
        foldHead.Children.Add(new TextBlock
        {
            Text = makerName.Length > 0 ? $"{workId} · {makerName}" : workId,
            Style = (Style)FindResource("CaptionText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        foldHead.MouseLeftButtonUp += (_, _) =>
        {
            var open = foldBody.Visibility != Visibility.Visible;
            foldBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            foldArrow.Text = open ? "▾" : "▸";
            _detailOpenWork = open ? workId : null;
        };
        layout.Children.Add(foldHead);
        layout.Children.Add(foldBody);

        // fanbox / E-Hentai 作品的图片就摊在作品目录里（没有 DLsite 那套 DataSource 子目录与正文占位图）
        var isFlat = MediaLibraryService.IsFlatImageWork(workId);
        var folder = workFolder ?? "";
        if (!isFlat)
        {
            folder = workFolder != null ? Path.Combine(workFolder, DlsitePage.DataSourceDir) : "";
            if (!Directory.Exists(folder))
                folder = Path.Combine(DlsitePage.ImagesDir, workId);
        }

        // 字段网格（带可点击链接）：右侧详情先建好，正文/轮播图随后台读盘完成再补
        var infoPanel = BuildDetailFields(workId, r, rjBlock);
        // Web .dtop：图 : 字段 = 1 : 1.6，间距 18
        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.6, GridUnitType.Star) });
        var rightHost = new Grid { Margin = new Thickness(18, 0, 0, 0) };
        rightHost.Children.Add(infoPanel);
        Grid.SetColumn(rightHost, 1);
        content.Children.Add(rightHost);
        foldBody.Children.Add(content);
        // 先显示标题 + 字段，正文与轮播图在读盘完成后填充（大量文件/长正文时不冻结 UI）。
        // 作品文件（原「查看作品」页）接在详情卡下方，对齐 Web renderDetail → drawFiles
        var page = new StackPanel();
        page.Children.Add(root);
        if (hasFiles)
        {
            var files = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            page.Children.Add(files);
            BuildDetailFiles(files, workId, _currentWorkFolder!);
        }
        ContentHost.Content = page;
        CardsScroll.ScrollToVerticalOffset(0);

        // 正文文本与轮播图列表在后台线程读盘/解析。
        // fanbox 投稿 / E-Hentai 画廊的目录里是整篇的图（动辄上百张），进详情只挂封面一张、不翻目录，
        // 要看图走「查看作品」（对齐 Web：/api/detail 对这两种来源不返回轮播图列表）
        var coverPath = r[17] as string ?? "";
        var detail = await Task.Run(() => isFlat
            ? new DetailContent([], File.Exists(coverPath) ? [coverPath] : [])
            : LoadDetailContent(folder));
        if (_currentWork != workId || _level != "detail")
            return;   // 加载期间已导航离开，丢弃结果

        if (detail.SliderPaths.Count > 0)
        {
            var slider = new ImageSliderControl(detail.SliderPaths) { VerticalAlignment = VerticalAlignment.Top };
            Grid.SetColumn(slider, 0);
            content.Children.Add(slider);
        }

        // 正文：文本与图片按原文顺序嵌入
        var maxWidth = Math.Max(360, CardsScroll.ViewportWidth - 100);
        foreach (var (kind, value) in detail.BodyBlocks)
        {
            if (kind == "text")
            {
                if (value.Trim().Length == 0)
                    continue;
                foldBody.Children.Add(new TextBox
                {
                    Text = value,
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    Margin = new Thickness(0, 10, 0, 0),
                });
            }
            else
            {
                var path = Path.Combine(folder, value);
                var img = new Image
                {
                    MaxWidth = maxWidth,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(0, 10, 0, 0),
                    Tag = path,
                };
                foldBody.Children.Add(img);   // 正文图后台解码（缺失/损坏时留空占位，不影响其它块）
                LoadImageAsync(img, path, (int)Math.Ceiling(maxWidth * 2));
            }
        }
    }

    /// <summary>后台读取作品正文（拆成文本/图片块）与轮播图文件列表（除正文图外，主图排最前）。</summary>
    private static DetailContent LoadDetailContent(string folder)
    {
        var bodyBlocks = new List<(string Kind, string Value)>();
        var bodyFiles = new HashSet<string>();
        var txtPath = Path.Combine(folder, DlsitePage.DescriptionTxt);
        if (File.Exists(txtPath))
        {
            string bodyText;
            try
            {
                bodyText = File.ReadAllText(txtPath).Trim();
            }
            catch (IOException)
            {
                bodyText = "";
            }
            var buf = new List<string>();
            foreach (var line in bodyText.Split('\n'))
            {
                var match = DlsitePage.BodyImageRe.Match(line.Trim());
                if (match.Success)
                {
                    if (buf.Count > 0)
                    {
                        bodyBlocks.Add(("text", string.Join('\n', buf)));
                        buf.Clear();
                    }
                    bodyBlocks.Add(("image", match.Groups[1].Value));
                    bodyFiles.Add(match.Groups[1].Value);
                }
                else
                {
                    buf.Add(line);
                }
            }
            if (buf.Count > 0)
                bodyBlocks.Add(("text", string.Join('\n', buf)));
        }

        var sliderPaths = new List<string>();
        if (Directory.Exists(folder))
        {
            var names = Directory.GetFiles(folder)
                .Select(Path.GetFileName)
                .Where(f => f != null &&
                            DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()) &&
                            !bodyFiles.Contains(f))
                .Select(f => f!)
                .OrderBy(f => f.Contains("img_main", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
            sliderPaths = names.Select(f => Path.Combine(folder, f)).ToList();
        }
        return new DetailContent(bodyBlocks, sliderPaths);
    }

    /// <summary>详情字段网格：社团/声优等渲染为可点击链接，标签来自 work_genres 表。</summary>
    private FrameworkElement BuildDetailFields(string workId, object?[] r, FrameworkElement idLink)
    {
        var fields = new (string Label, string? Value)[]
        {
            ("作品号", workId), ("社团", r[2] as string), ("販売日", r[3] as string),
            ("系列", r[4] as string), ("剧本", r[5] as string), ("插画", r[6] as string),
            ("声优", r[7] as string), ("年龄分级", r[8]?.ToString()), ("作品形式", r[9] as string),
            ("类型", r[10] as string), ("文件容量", r[11] as string), ("简介", r[12] as string),
        };
        var tags = Db.Select(
            "SELECT \"genre\" FROM \"work_genres\" WHERE \"work_id\" = @w", ("@w", workId));

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, value) in fields)
        {
            FrameworkElement valueElement;
            if (label == "作品号")
            {
                valueElement = idLink;
            }
            else if (label == "类型" && tags is { Count: > 0 })
            {
                // 标签渲染为可点击链接
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                foreach (var tagRow in tags)
                {
                    var tag = tagRow[0] as string ?? "";
                    var link = new Hyperlink(new Run(tag))
                    {
                        Foreground = (Brush)FindResource("AccentLightBrush"),
                        TextDecorations = null,
                    };
                    var captured = tag;
                    link.Click += (_, _) =>
                    {
                        _currentGenre = captured;
                        _currentLib = null;
                        _currentMaker = null;
                        _currentType = null;
                        ShowMakers();
                    };
                    tb.Inlines.Add(link);
                    tb.Inlines.Add(new Run("　"));
                }
                valueElement = tb;
            }
            else if (string.IsNullOrEmpty(value))
            {
                continue;
            }
            else if (label == "年龄分级")
            {
                var display = I18n.Tr(AgeMap.GetValueOrDefault(value, value));
                valueElement = MakeLinkBlock(display, () => OpenFilter("age_category", value));
            }
            else if (LinkCols.TryGetValue(label, out var linkInfo))
            {
                var parts = linkInfo.Multi
                    ? value.Split(" / ").Select(p => p.Trim()).Where(p => p.Length > 0).ToList()
                    : [value.Trim()];
                var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
                for (var i = 0; i < parts.Count; i++)
                {
                    var part = parts[i];
                    var link = new Hyperlink(new Run(part))
                    {
                        Foreground = (Brush)FindResource("AccentLightBrush"),
                        TextDecorations = null,
                    };
                    link.Click += (_, _) => OpenFilter(linkInfo.Col, part);
                    tb.Inlines.Add(link);
                    if (i < parts.Count - 1)
                        tb.Inlines.Add(new Run(" / "));
                }
                valueElement = tb;
            }
            else
            {
                valueElement = new TextBox
                {
                    Text = value, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                };
            }

            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keyLabel = new TextBlock
            {
                Text = I18n.Tr(label),
                // Web .fields .k：--muted 13px；行距 6、列距 14
                Foreground = (Brush)FindResource("CaptionBrush"),
                FontSize = 13,
                Margin = new Thickness(0, 3, 14, 3),
                VerticalAlignment = VerticalAlignment.Top,
            };
            Grid.SetRow(keyLabel, row);
            Grid.SetColumn(keyLabel, 0);
            grid.Children.Add(keyLabel);
            valueElement.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(valueElement, row);
            Grid.SetColumn(valueElement, 1);
            grid.Children.Add(valueElement);
            row++;
        }
        return grid;
    }

    private TextBlock MakeLinkBlock(string text, Action onClick)
    {
        var link = new Hyperlink(new Run(text))
        {
            Foreground = (Brush)FindResource("AccentLightBrush"),
            TextDecorations = null,
        };
        link.Click += (_, _) => onClick();
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap };
        tb.Inlines.Add(link);
        return tb;
    }

    // ---------- 作品文件（详情页下方，多级折叠列表 / 缩略图墙） ----------

    private const double TreeIndentStep = 18;    // 每深一级的左缩进（Web buildTreeLevel：8 + depth × 18）
    private const double TreeRowBaseIndent = 8;  // 行基础左缩进
    private static readonly FontFamily EmojiFont = new("Segoe UI Emoji");

    /// <summary>
    /// 详情页下方的「作品文件」小节（原「查看作品」独立页并入详情，对齐 Web renderDetail → drawFiles）：
    /// 标题 + 缩略图/文件列表切换，内容在后台枚举完再填入。
    /// </summary>
    private void BuildDetailFiles(StackPanel section, string workId, string folder)
    {
        section.Children.Clear();
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        // 选中的视图用主色按钮（Web .icon-btn.primary）
        var thumbBtn = new Button
        {
            Content = "▦ " + I18n.Tr("缩略图"),
            Style = (Style)FindResource(_treeThumbView ? "AccentButton" : typeof(Button)),
        };
        var listBtn = new Button
        {
            Content = "☰ " + I18n.Tr("文件列表"),
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource(_treeThumbView ? typeof(Button) : "AccentButton"),
        };
        thumbBtn.Click += (_, _) =>
        {
            if (_treeThumbView)
                return;
            _treeThumbView = true;
            BuildDetailFiles(section, workId, folder);
        };
        listBtn.Click += (_, _) =>
        {
            if (!_treeThumbView)
                return;
            _treeThumbView = false;
            BuildDetailFiles(section, workId, folder);
        };
        buttons.Children.Add(thumbBtn);
        buttons.Children.Add(listBtn);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock
        {
            Text = I18n.Tr("作品文件"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
        });
        section.Children.Add(bar);
        var body = new ContentControl();
        section.Children.Add(body);
        FillDetailFiles(body, workId, folder, _treeThumbView);
    }

    private async void FillDetailFiles(ContentControl host, string workId, string folder, bool thumbView)
    {
        // 枚举期间已离开详情 / 换了作品 / 又切了视图：丢弃结果
        bool Stale() => _level != "detail" || _currentWork != workId || _treeThumbView != thumbView;
        if (thumbView)
        {
            // 递归收集图片（可能上千文件）放后台线程，避免枚举时冻结 UI
            var images = await Task.Run(() => CollectThumbImages(folder));
            if (Stale())
                return;
            host.Content = BuildThumbWrap(images);
        }
        else
        {
            // 仅根层级在后台枚举；子文件夹在首次展开时才按需枚举（懒展开），不一次性递归建满整棵树
            var level = await Task.Run(() => ListLevel(folder, isRoot: true));
            if (Stale())
                return;
            var list = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            BuildLevelInto(list, level, depth: 0);
            if (list.Children.Count == 0)
                list.Children.Add(new TextBlock
                {
                    Text = I18n.Tr("此作品文件夹为空"),
                    Style = (Style)FindResource("CaptionText"),
                    Margin = new Thickness(TreeRowBaseIndent, 8, 0, 0),
                });
            host.Content = list;
        }
    }

    // 缩略图墙对齐 Web 的 .fgrid：列宽下限 150、单行最多 10 列、gap 10、缩略图 1:1，
    // 文件名行 ThumbNameH 固定高（Web .fname 单行省略号）。解码宽仍取 2 倍档位，避免随列宽散开。
    private const double ThumbNameH = 31;   // Web .fname：padding 6 ×2 + 12.5px 字一行
    private const int ThumbDecodeWidth = 320;

    /// <summary>后台线程：递归收集作品内所有图片（排除 DataSource）并排序（可能上千文件）。</summary>
    private static List<string> CollectThumbImages(string folder)
    {
        List<string> images = [];
        try
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                // 排除根目录下的 DataSource（封面/描述图等元数据资源）
                var rel = Path.GetRelativePath(folder, f);
                if (rel.StartsWith(DlsitePage.DataSourceDir + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                if (DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    images.Add(f);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        images.Sort((a, b) => string.Compare(a, b, StringComparison.OrdinalIgnoreCase));
        return images;
    }

    /// <summary>缩略图视图：把后台收集到的图片平铺成缩略图网格（缩略图本身再后台解码），单击整页预览。</summary>
    private FrameworkElement BuildThumbWrap(List<string> images)
    {
        if (images.Count == 0)
            return new TextBlock
            {
                Text = I18n.Tr("此作品没有可预览的图片"),
                Style = (Style)FindResource("CaptionText"),
                Margin = new Thickness(TreeRowBaseIndent, 8, 0, 0),
            };

        var wrap = new ResponsiveWrapPanel
        {
            Margin = new Thickness(0, 0, 0, 12),
            MinItemWidth = CardGrid.FileMinWidth,
            MaxColumns = CardGrid.FileMaxColumns,
            ItemGap = CardGrid.FileGap,
            ItemAspect = 1,              // 缩略图正方形（Web .fthumb { aspect-ratio: 1 / 1 }）
            ExtraHeight = ThumbNameH,    // 再加文件名行
        };
        var thumbs = new List<(Border Thumb, string Path)>(images.Count);
        foreach (var path in images)
            wrap.Children.Add(MakeThumbTile(path, thumbs));
        LoadThumbsInOrderAsync(wrap, thumbs);
        return wrap;
    }

    /// <summary>
    /// 缩略图按文件顺序逐张解码（对齐 Web viewer.js 的 thumbQueue）：整墙同时开解，谁先完成谁先出现，
    /// 画面东一张西一张；媒体库又多在 HDD 上，并发读只会多添寻道。缩略图墙被换掉（离开视觉树）即停。
    /// </summary>
    private static async void LoadThumbsInOrderAsync(FrameworkElement wrap, List<(Border Thumb, string Path)> thumbs)
    {
        // 墙是建好后才挂进 ContentControl 的，而 ContentControl 的内容要到布局（Measure）时才进视觉树。
        // 只让出一轮（Task.Yield）不够：续体按 Normal 优先级跑、早于布局，此时 FromVisual 仍为 null，
        // 循环一上来就退出、整墙一张图都不画。故等它真正 Loaded 再开始。
        if (!wrap.IsLoaded)
        {
            var loaded = new TaskCompletionSource();
            void OnLoaded(object? s, RoutedEventArgs e)
            {
                wrap.Loaded -= OnLoaded;
                loaded.TrySetResult();
            }
            wrap.Loaded += OnLoaded;
            await loaded.Task;
        }
        foreach (var (thumb, path) in thumbs)
        {
            if (PresentationSource.FromVisual(wrap) == null)
                return;
            var bmp = await ThumbnailCache.LoadAsync(path, ThumbDecodeWidth);
            if (bmp == null) continue;
            var brush = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
            brush.Freeze();
            thumb.Background = brush;
        }
    }

    /// <summary>单张缩略图：图片填充裁切 + 文件名，单击整页预览。</summary>
    private Border MakeThumbTile(string path, List<(Border Thumb, string Path)> thumbs)
    {
        // 尺寸由 ResponsiveWrapPanel 给：整块拉伸填满单元格，缩略图占正方形那部分、文件名占固定一行
        var tile = new Border
        {
            Style = (Style)FindResource("ClickCard"),   // Web .fcard：面底色 + 描边 + 圆角，悬停描边强调
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ClipToBounds = true,
            ToolTip = Path.GetFileName(path),
        };
        var panel = new Grid();
        panel.RowDefinitions.Add(new RowDefinition());
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ThumbNameH) });
        var thumb = new Border
        {
            Background = CoverBgBrush,
            CornerRadius = new CornerRadius(9, 9, 0, 0),
            Tag = path,
        };
        thumbs.Add((thumb, path));   // 由 LoadThumbsInOrderAsync 按顺序后台解码
        panel.Children.Add(thumb);
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            FontSize = 12.5,   // Web .fname：12.5px 正文色，padding 6px 8px
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetRow(name, 1);
        panel.Children.Add(name);
        tile.Child = panel;
        tile.MouseLeftButtonUp += (_, _) => ShowPreview(path);
        return tile;
    }

    /// <summary>单层文件树内容：子文件夹（含其项数）与文件（含大小），均已排序、已过滤根目录 DataSource。</summary>
    private sealed record TreeLevel(List<(string Dir, int Count)> Dirs, List<(string Path, long Size)> Files);

    /// <summary>后台线程：枚举 dir 的单层内容，并顺带 stat 子文件夹项数/文件大小（避免建行时在 UI 线程逐个打盘）。</summary>
    private static TreeLevel ListLevel(string dir, bool isRoot)
    {
        string[] subDirs = [], files = [];
        try
        {
            subDirs = Directory.GetDirectories(dir);
            files = Directory.GetFiles(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TreeLevel([], []);
        }
        var dirs = subDirs
            .Where(d => !(isRoot && string.Equals(Path.GetFileName(d), DlsitePage.DataSourceDir,
                StringComparison.OrdinalIgnoreCase)))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var count = 0;
                try { count = Directory.GetFileSystemEntries(d).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return (d, count);
            })
            .ToList();
        var fileList = files
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                long size = 0;
                try { size = new FileInfo(f).Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                return (f, size);
            })
            .ToList();
        return new TreeLevel(dirs, fileList);
    }

    /// <summary>把一层内容填入面板：子文件夹（默认折叠、懒展开）在前，文件在后。</summary>
    private void BuildLevelInto(StackPanel panel, TreeLevel level, int depth)
    {
        foreach (var (sub, count) in level.Dirs)
        {
            var children = new StackPanel { Visibility = Visibility.Collapsed };  // 默认折叠，首次展开时才枚举
            panel.Children.Add(MakeTreeFolderRow(sub, count, children, depth));
            panel.Children.Add(children);
        }
        foreach (var (file, size) in level.Files)
            panel.Children.Add(MakeTreeFileRow(file, size, depth));
    }

    /// <summary>文件夹行：箭头 + 📁 + 名称 + 项数，点击整行折叠/展开；子级在首次展开时才后台枚举并填充。</summary>
    private Border MakeTreeFolderRow(string dir, int count, StackPanel children, int depth)
    {
        var row = new Border
        {
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(TreeRowBaseIndent + depth * TreeIndentStep, 6, 10, 6),   // Web .trow { padding: 6px 10px }
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });   // 箭头 14 + 间距 8
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var arrow = new TextBlock
        {
            Text = "▸",
            Foreground = (Brush)FindResource("CaptionBrush"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(arrow);
        var icon = new TextBlock
        {
            Text = "📁", FontFamily = EmojiFont,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);
        var name = new TextBlock
        {
            Text = Path.GetFileName(dir),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 2);
        grid.Children.Add(name);
        var countText = new TextBlock
        {
            Text = I18n.Format(I18n.Tr("{count} 项"), ("count", count)),
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(countText, 3);
        grid.Children.Add(countText);
        row.Child = grid;

        var built = false;   // 子级是否已枚举填充（懒展开：首次展开时才后台枚举）
        row.MouseLeftButtonUp += async (_, _) =>
        {
            if (!built)
            {
                built = true;
                var level = await Task.Run(() => ListLevel(dir, isRoot: false));
                BuildLevelInto(children, level, depth + 1);
            }
            var show = children.Visibility != Visibility.Visible;
            children.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            arrow.Text = show ? "▾" : "▸";
        };
        row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>文件行：类型图标 + 名称 + 大小。图片/视频单击整页预览；音频单击底部播放条播放；其它双击打开。</summary>
    private Border MakeTreeFileRow(string path, long size, int depth)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var previewable = DlsitePage.ImageExts.Contains(ext) || VideoExts.Contains(ext);
        var isAudio = AudioExts.Contains(ext);
        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(6),
            // 文件让出箭头那一格（Web：8 + depth × 18 + 18）
            Padding = new Thickness(TreeRowBaseIndent + depth * TreeIndentStep + 18, 6, 10, 6),
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new TextBlock
        {
            Text = FileIcon(ext), FontFamily = EmojiFont,
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);
        var name = new TextBlock
        {
            Text = Path.GetFileName(path),
            Foreground = (Brush)FindResource("TextBrush"),   // Web .trow.file .tname 一律正文色
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        grid.Children.Add(name);
        var sizeText = new TextBlock
        {
            Text = FormatSize(size),
            Style = (Style)FindResource("CaptionText"),
            Margin = new Thickness(12, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeText, 2);
        grid.Children.Add(sizeText);
        row.Child = grid;

        if (previewable)
        {
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => ShowPreview(path);
        }
        else if (isAudio)
        {
            // 音频单击：在底部播放条就地播放（wav/mp3 等），并以同文件夹音频组成播放队列
            row.Cursor = Cursors.Hand;
            row.MouseLeftButtonUp += (_, _) => PlayAudioFromTree(path);
        }
        else
        {
            row.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2)
                    return;
                e.Handled = true;
                OpenTreeFile(path);
            };
        }
        row.MouseEnter += (_, _) => row.Background = RowHoverBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        return row;
    }

    /// <summary>点击音频：把同文件夹内的所有音频按名称排序组成播放队列，从点击的这首开始播放。</summary>
    private void PlayAudioFromTree(string path)
    {
        List<(string Path, string Title)> queue = [];
        var index = 0;
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
        {
            try
            {
                var files = Directory.GetFiles(dir)
                    .Where(f => AudioExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                index = files.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
                if (index < 0)
                    index = 0;
                queue = files.Select(f => (f, Path.GetFileName(f))).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 枚举失败时退回单文件播放
            }
        }
        if (queue.Count == 0)
            queue = [(path, Path.GetFileName(path))];
        AudioBar.PlayQueue(queue, index);
    }

    /// <summary>按扩展名选择文件类型图标。</summary>
    private static string FileIcon(string ext)
    {
        if (DlsitePage.ImageExts.Contains(ext)) return "🖼️";
        if (VideoExts.Contains(ext)) return "🎬";
        if (AudioExts.Contains(ext)) return "🎵";
        if (ext == ".exe") return "🎮";
        return "📄";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.#} KB",
        _ => $"{bytes} B",
    };

    /// <summary>双击文件：音频→播放器弹窗，其它→系统默认程序（图片/视频由单击整页预览）。</summary>
    private void OpenTreeFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (AudioExts.Contains(ext))
            new MediaPlayerDialog(path, isVideo: false) { Owner = Window.GetWindow(this) }
                .ShowDialog();
        else
            // 工作目录设成文件所在目录：游戏类 exe 多按相对路径读自己的资源包（同 Web 端 /api/runexe）
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            });
    }

    // ---------- 整页预览 ----------

    /// <summary>整页覆盖预览：图片支持同文件夹左右切换，视频用内嵌播放器。</summary>
    private void ShowPreview(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
            _inlinePlayer = null;
        }
        // 先显示整页遮罩，使随后挂入的视频播放器在“已加载且可见”的状态下开始播放（否则画面黑屏）
        PreviewOverlay.Visibility = Visibility.Visible;
        if (DlsitePage.ImageExts.Contains(ext))
        {
            _previewVideoMode = false;
            _previewVideos = [];
            var dir = Path.GetDirectoryName(path)!;
            try
            {
                _previewImages = Directory.GetFiles(dir)
                    .Where(f => DlsitePage.ImageExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _previewImages = [path];
            }
            _previewIndex = Math.Max(0, _previewImages.IndexOf(path));
            PreviewPrevButton.Content = "‹ " + I18n.Tr("上一张");
            PreviewNextButton.Content = I18n.Tr("下一张") + " ›";
            SetPreviewNavVisible(_previewImages.Count > 1);
            LoadPreviewImage();
        }
        else if (VideoExts.Contains(ext))
        {
            AudioBar.Stop();   // 视频自带声音：开始播视频前停掉底部音频，避免两路声音重叠
            _previewImages = [];
            _previewVideoMode = true;
            // 同文件夹视频按名称排序组成播放队列，从点击的这首开始，支持上一/下一个与自动续播
            var dir = Path.GetDirectoryName(path)!;
            try
            {
                _previewVideos = Directory.GetFiles(dir)
                    .Where(f => VideoExts.Contains(Path.GetExtension(f).ToLowerInvariant()))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _previewVideos = [path];
            }
            _previewIndex = Math.Max(0, _previewVideos.IndexOf(path));
            PreviewPrevButton.Content = "‹ " + I18n.Tr("上一个");
            PreviewNextButton.Content = I18n.Tr("下一个") + " ›";
            SetPreviewNavVisible(_previewVideos.Count > 1);
            LoadPreviewVideo();
        }
        else
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        PreviewOverlay.Focus();
    }

    /// <summary>统一切换顶部"上一张/下一张"与图片两侧覆盖箭头的显示。</summary>
    private void SetPreviewNavVisible(bool visible)
    {
        var v = visible ? Visibility.Visible : Visibility.Collapsed;
        PreviewNavBar.Visibility = v;
        PreviewPrevArrow.Visibility = v;
        PreviewNextArrow.Visibility = v;
    }

    private void LoadPreviewImage()
    {
        if (_previewImages.Count == 0)
            return;
        var path = _previewImages[_previewIndex];
        PreviewTitle.Text = _previewImages.Count > 1
            ? $"{Path.GetFileName(path)}　{_previewIndex + 1} / {_previewImages.Count}"
            : Path.GetFileName(path);
        var img = new Image { Stretch = Stretch.Uniform, Tag = path };
        PreviewContent.Child = img;
        LoadImageAsync(img, path, 0);   // 整页预览用原始尺寸，后台解码避免大图卡顿
    }

    /// <summary>加载视频播放队列中当前索引的视频；订阅自然结束以自动续播下一个。</summary>
    private void LoadPreviewVideo()
    {
        if (_previewVideos.Count == 0)
            return;
        var path = _previewVideos[_previewIndex];
        PreviewTitle.Text = _previewVideos.Count > 1
            ? $"{Path.GetFileName(path)}　{_previewIndex + 1} / {_previewVideos.Count}"
            : Path.GetFileName(path);
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
        }
        _inlinePlayer = new InlineMediaPlayer(path);
        _inlinePlayer.Ended += OnPreviewVideoEnded;
        PreviewContent.Child = _inlinePlayer;
    }

    /// <summary>视频自然播放结束：未到队尾则自动续播下一个。</summary>
    private void OnPreviewVideoEnded()
    {
        if (_previewVideoMode && _previewIndex < _previewVideos.Count - 1)
            StepPreview(1);
    }

    private void StepPreview(int delta)
    {
        if (_previewVideoMode)
        {
            if (_previewVideos.Count == 0)
                return;
            _previewIndex = ((_previewIndex + delta) % _previewVideos.Count + _previewVideos.Count)
                            % _previewVideos.Count;
            LoadPreviewVideo();
            return;
        }
        if (_previewImages.Count == 0)
            return;
        _previewIndex = ((_previewIndex + delta) % _previewImages.Count + _previewImages.Count)
                        % _previewImages.Count;
        LoadPreviewImage();
    }

    /// <summary>关闭整页预览：停止播放、释放图片，回到详情页。</summary>
    private void ClosePreview()
    {
        if (_inlinePlayer != null)
        {
            _inlinePlayer.Ended -= OnPreviewVideoEnded;
            _inlinePlayer.Shutdown();
            _inlinePlayer = null;
        }
        _previewImages = [];
        _previewVideos = [];
        _previewVideoMode = false;
        PreviewContent.Child = null;
        PreviewOverlay.Visibility = Visibility.Collapsed;
    }

    private void PreviewClose_Click(object sender, RoutedEventArgs e) => ClosePreview();

    private void PreviewPrev_Click(object sender, RoutedEventArgs e) => StepPreview(-1);

    private void PreviewNext_Click(object sender, RoutedEventArgs e) => StepPreview(1);

    private void PreviewOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                ClosePreview();
                e.Handled = true;
                break;
            case Key.Left:
                StepPreview(-1);
                e.Handled = true;
                break;
            case Key.Right:
                StepPreview(1);
                e.Handled = true;
                break;
        }
    }
}
