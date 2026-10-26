using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>画廊详情预览条里的一张缩略图 + 落到临时目录的本地副本（供看图窗口翻页）。</summary>
public class EhImageItem : ObservableBase
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>缩略图在临时目录里的本地副本路径，空表示还没下下来。</summary>
    public string LocalPath { get; set; } = "";

    private BitmapImage? _thumb;
    public BitmapImage? Thumb { get => _thumb; set => Set(ref _thumb, value); }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value))
                return;
            Raise(nameof(SelBorderBrush));
            Raise(nameof(SelOpacity));
        }
    }

    /// <summary>当前大图对应的缩略图高亮描边（等价 Web 的 .thumbs img.sel）。</summary>
    public Brush SelBorderBrush => IsSelected
        ? Application.Current?.TryFindResource("AccentLightBrush") as Brush ?? Brushes.DodgerBlue
        : Brushes.Transparent;

    public double SelOpacity => IsSelected ? 1.0 : 0.65;
}

/// <summary>E-Hentai 搜索结果里的一张画廊卡。</summary>
public class EhCardItem : ObservableBase
{
    public long Gid { get; init; }
    public string Token { get; init; } = "";
    public string Title { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>左上角小标：分类（Doujinshi / Manga …）。</summary>
    public string TagText { get; init; } = "";
    /// <summary>右上角小标：页数。</summary>
    public string MetaText { get; init; } = "";
    /// <summary>本地状态（已品悦 / 下载中 / 已下载），空表示站上画廊尚未下载过。</summary>
    public string State { get; init; } = "";
    /// <summary>可选中下载（未入库且不在下载队列中）。</summary>
    public bool Selectable { get; init; }

    /// <summary>选择集合里的键；站点用 (gid, token) 二元组定位画廊，两者缺一不可。</summary>
    public string Key => $"{Gid}:{Token}";

    public Visibility TagVisibility => TagText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MetaVisibility => MetaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>状态文案：已品悦在界面上一律显示为「已下载」。</summary>
    public string StateText => State == "已品悦" ? I18n.Tr("已下载") : State;
    public Visibility StateVisibility => State.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // 卡内按钮行二选一：未下载的可 选择/下载；已下载或下载中则是禁用的状态按钮
    public Visibility SelectVisibility => Selectable ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BusyVisibility => Selectable ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>已入库/下载中的降亮，与 Web 的 .card.dim 等价。</summary>
    public double CardOpacity => Selectable ? 1.0 : 0.45;

    private bool _isPicked;
    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (!Set(ref _isPicked, value))
                return;
            Raise(nameof(PickBorderBrush));
            Raise(nameof(PickText));
            Raise(nameof(PickBackground));
            Raise(nameof(PickForeground));
        }
    }

    /// <summary>选中的卡片用强调色描边（等价 Web 的 .card.picked）。</summary>
    public Brush PickBorderBrush => IsPicked
        ? Application.Current?.TryFindResource("AccentLightBrush") as Brush ?? Brushes.DodgerBlue
        : Brushes.Transparent;

    /// <summary>选择按钮文案：未选「选择」，已选「已选」（对齐 Web 的 .mcard-actions 按钮）。</summary>
    public string PickText => IsPicked ? I18n.Tr("已选") : I18n.Tr("选择");

    /// <summary>已选时按钮转强调色底（等价 Web 的 .mcard-actions button.primary）。</summary>
    public Brush PickBackground => IsPicked
        ? Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue
        : Application.Current?.TryFindResource("CardBrush") as Brush ?? Brushes.DimGray;

    /// <summary>强调色底上用白字，未选时用常规文字色。</summary>
    public Brush PickForeground => IsPicked
        ? Brushes.White
        : Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;

    private ImageSource? _thumb;
    public ImageSource? Thumb { get => _thumb; set => Set(ref _thumb, value); }
}

/// <summary>
/// 「作品搜索」分区中 E-Hentai 来源的结果区：搜画廊 → 选本子 → 批量/单本下载。
/// 与 FANBOX 一样只做搜索与下载，不含媒体库浏览——下好的画廊落在磁盘的媒体库目录里。
///
/// 交互与样式以 Web 端 src/Web/js/ehentai.js 为基准：卡内按钮选择、下拉到底自动翻页、
/// 已下载/下载中的置灰且不可选、点卡片本体进详情看内容。
/// 工具栏（来源下拉 + 输入框 + 查询 + 返回搜索结果）由宿主 <see cref="SearchPage"/> 提供。
/// 层级（_level）：results（画廊网格）/ detail（单本画廊内容）——站点搜索直接出画廊，没有中间层。
/// </summary>
public partial class EhentaiSearchView : UserControl
{
    private readonly ObservableCollection<EhCardItem> _cards = [];
    /// <summary>已加载的画廊元数据（入队时按 gid 回查，不必重新请求站点）。</summary>
    private readonly List<EhGallery> _galleries = [];

    private string _level = "results";
    private int _generation;        // 请求代际：新一轮搜索即作废在途请求与缩略图加载

    // 搜索与翻页（站点是游标翻页，没有页号）
    private string _keyword = "";
    private string _next = "";
    private bool _hasMore;
    private bool _loadingMore;

    // 画廊详情
    private readonly ObservableCollection<EhImageItem> _detailImages = [];
    private EhGallery? _detailGallery;
    private string _detailState = "";
    private int _detailGen;     // 详情请求代际：与 _generation 分开，看详情不该作废网格的分页

    /// <summary>缩略图的本地临时副本目录：看图窗口按路径翻页，故要先落盘。</summary>
    private static string PreviewCacheDir =>
        Path.Combine(Path.GetTempPath(), "R-18MediaLibrary", "ehentai-preview");

    /// <summary>结果计数/提示文案变化（宿主显示在搜索框下方）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>「返回搜索结果」按钮是否应该显示。</summary>
    public event Action<bool>? BackAvailabilityChanged;

    /// <summary>当前是否可返回上一层（宿主切回本来源时用它恢复返回按钮）。</summary>
    public bool CanGoBack => _level == "detail";

    /// <summary>返回按钮文案（本来源只有详情一层可返回）。</summary>
    public string BackLabel => I18n.Tr("← 返回搜索结果");

    /// <summary>宿主的返回按钮：从详情回到画廊网格。</summary>
    public void GoBack()
    {
        if (_level != "detail")
            return;
        ShowLevel("results");
        SetStatus(CountText());
    }

    public EhentaiSearchView()
    {
        InitializeComponent();
        CardList.ItemsSource = _cards;
        DetailThumbs.ItemsSource = _detailImages;
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        SelectAllButton.Content = I18n.Tr("全选");
        SelectNoneButton.Content = I18n.Tr("清空");
        DownloadSelectedButton.Content = I18n.Tr("下载选中");
        DetailOpenSiteButton.Content = I18n.Tr("在站点打开");
        LoadingText.Text = I18n.Tr("正在查询…");
        UpdateSelectInfo();
    }

    /// <summary>输入框的占位提示（宿主切到本来源时取用）。</summary>
    public static string InputHint =>
        I18n.Tr("关键字 / 标签（如 artist:xxx），或粘贴 E-Hentai 画廊链接");

    /// <summary>当前结果计数/提示文案；宿主切回本来源时用它恢复计数行。</summary>
    public string CurrentStatus { get; private set; } = "";

    private void SetStatus(string text)
    {
        CurrentStatus = text;
        StatusChanged?.Invoke(text);
    }

    private void ShowLevel(string level)
    {
        _level = level;
        SelectBar.Visibility = level == "results" ? Visibility.Visible : Visibility.Collapsed;
        CardList.Visibility = level == "results" ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = level == "detail" ? Visibility.Visible : Visibility.Collapsed;
        // 操作条在滚动区之外（第 0 行），随详情面板同步显隐
        DetailBar.Visibility = DetailPane.Visibility;
        BackAvailabilityChanged?.Invoke(CanGoBack);
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 由宿主的「查询」触发。输入可以是关键字/标签，也可以直接粘贴画廊链接
    /// （粘链接时跳过搜索，径直把那一本列出来）。
    /// </summary>
    public async Task RunSearchAsync(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return;

        var generation = ++_generation;
        _keyword = raw;
        _next = "";
        _hasMore = false;
        _loadingMore = false;
        _cards.Clear();
        _galleries.Clear();
        ShowLevel("results");
        UpdateSelectInfo();
        SetStatus("");
        LoadingText.Text = I18n.Tr("正在搜索画廊…");
        LoadingOverlay.Visibility = Visibility.Visible;

        EhSearchResult result;
        try
        {
            // 贴的是画廊链接：跳过搜索，直接取这一本的元数据
            if (EhentaiApi.ParseGalleryInput(raw) is { } direct)
            {
                var one = await EhentaiApi.GetGalleryAsync(direct);
                result = one is null
                    ? new EhSearchResult { Error = I18n.Tr("未找到该画廊（可能已下架，或里站需要登录 cookie）") }
                    : new EhSearchResult { Items = [one] };
            }
            else
            {
                result = await EhentaiApi.SearchAsync(raw);
            }
        }
        finally
        {
            if (generation == _generation)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (generation != _generation)
            return;

        if (result.Error is { Length: > 0 } error)
        {
            SetStatus(error);
            return;
        }
        if (result.Items.Count == 0)
        {
            SetStatus(I18n.Tr("没有匹配的画廊"));
            return;
        }
        _next = result.Next;
        _hasMore = result.HasMore;
        AppendGalleries(result.Items);
        SetStatus(CountText());
        _ = LoadThumbnailsAsync(generation);
    }

    /// <summary>取下一页并追加到网格（站点是游标翻页，带上一页返回的游标）。</summary>
    private async Task LoadMoreAsync(int generation)
    {
        if (_loadingMore || !_hasMore)
            return;
        _loadingMore = true;
        try
        {
            var result = await EhentaiApi.SearchAsync(_keyword, _next);
            if (generation != _generation)
                return;
            if (result.Error is { Length: > 0 } || result.Items.Count == 0)
            {
                _hasMore = false;
                SetStatus(CountText());
                return;
            }
            _next = result.Next;
            _hasMore = result.HasMore;
            AppendGalleries(result.Items);
            SetStatus(CountText());
            _ = LoadThumbnailsAsync(generation);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>把一页画廊转成卡片追加进网格，并记下元数据供入队复用。</summary>
    private void AppendGalleries(IReadOnlyList<EhGallery> items)
    {
        var states = EhentaiService.GalleryStates(items.Select(g => g.Gid));
        foreach (var g in items)
        {
            _galleries.Add(g);
            var state = states.GetValueOrDefault(g.Gid.ToString(), "");
            _cards.Add(new EhCardItem
            {
                Gid = g.Gid,
                Token = g.Token,
                Title = g.DisplayTitle.Length > 0 ? g.DisplayTitle : g.Gid.ToString(),
                ThumbUrl = g.Thumb,
                TagText = g.Category,
                MetaText = g.FileCount > 0 ? I18n.Format(I18n.Tr("{n} 页"), ("n", g.FileCount)) : "",
                State = state,
                Selectable = state.Length == 0,
            });
        }
    }

    /// <summary>网格的计数行文案（加载完与从详情返回时共用同一处）。</summary>
    private string CountText() =>
        I18n.Format(I18n.Tr("已加载 {n} 本画廊"), ("n", _cards.Count)) +
        (_hasMore ? I18n.Tr("（下拉加载更多）") : "");

    /// <summary>滚动到接近底部时自动加载下一页（与 FANBOX 作品网格同一交互）。</summary>
    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_level != "results" || !_hasMore || _loadingMore)
            return;
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            _ = LoadMoreAsync(_generation);
    }

    /// <summary>点卡片本体进入画廊详情（选择仍由卡内「选择」按钮负责）。</summary>
    private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is not EhCardItem item)
            return;
        CardList.SelectedItem = null;
        _ = OpenGalleryAsync(item);
    }

    // ---------- 画廊详情 ----------

    /// <summary>
    /// 点画廊卡进入：拉取元数据与缩略图清单并渲染。
    ///
    /// 这里只给缩略图：站点每张图的直链都要单独进它的图片页才拿得到，
    /// 为看一眼详情解析几百页既慢又白耗看图额度——要原图就走下载。
    /// </summary>
    private async Task OpenGalleryAsync(EhCardItem card)
    {
        var generation = ++_detailGen;
        _detailState = card.State;
        _detailGallery = _galleries.FirstOrDefault(g => g.Gid == card.Gid);
        ShowLevel("detail");
        SetStatus(card.Title);

        // 先清空上一本的内容，免得新旧混显
        DetailTitle.Text = card.Title;
        DetailMainImage.Source = null;
        _detailImages.Clear();
        DetailFields.Children.Clear();
        DetailNote.Visibility = Visibility.Collapsed;
        DetailNoImage.Visibility = Visibility.Collapsed;
        DetailPane.ScrollToTop();
        LoadingText.Text = I18n.Tr("正在获取画廊内容…");
        LoadingOverlay.Visibility = Visibility.Visible;

        var gref = new EhGalleryRef(card.Gid, card.Token);
        var gallery = _detailGallery ?? await EhentaiApi.GetGalleryAsync(gref);
        // 详情只看头两页缩略图（约 40 张）：够翻阅了，又不必为此把整本画廊页翻一遍
        var pages = gallery is null
            ? []
            : await EhentaiApi.GetImagePagesAsync(gref, Math.Min(gallery.FileCount, 40), maxPages: 2);
        if (generation != _detailGen || _level != "detail")
            return;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (gallery is null)
        {
            DetailNoImage.Text = I18n.Tr("获取画廊信息失败");
            DetailNoImage.Visibility = Visibility.Visible;
            return;
        }
        _detailGallery = gallery;
        RenderDetail(gallery, pages, card.ThumbUrl);
        _ = LoadDetailImagesAsync(generation);
    }

    /// <summary>渲染详情：标题 + 字段 + 预览占位（缩略图随后异步填充）。</summary>
    private void RenderDetail(EhGallery gallery, IReadOnlyList<EhImagePage> pages, string coverUrl)
    {
        var done = _detailState == "已品悦";
        var busy = _detailState is "下载中" or "已下载";

        DetailTitle.Text = gallery.DisplayTitle;
        DetailDownloadButton.Content = done ? I18n.Tr("已下载")
            : busy ? _detailState : I18n.Tr("下载本篇");
        DetailDownloadButton.IsEnabled = !done && !busy;

        if (gallery.Title.Length > 0 && gallery.Title != gallery.DisplayTitle)
            AddDetailField(I18n.Tr("原名"), gallery.Title);
        AddDetailField(I18n.Tr("社团/作者"), EhentaiService.MakerNameOf(gallery));
        AddDetailField(I18n.Tr("分类"), gallery.Category);
        AddDetailField(I18n.Tr("投稿者"), gallery.Uploader);
        AddDetailField(I18n.Tr("投稿"),
            gallery.PostedUnix > 0 ? gallery.Posted.ToString("yyyy-MM-dd HH:mm") : "");
        AddDetailField(I18n.Tr("评分"), gallery.Rating);
        AddDetailField(I18n.Tr("页数"),
            gallery.FileCount > 0 ? I18n.Format(I18n.Tr("{n} 页"), ("n", gallery.FileCount)) : "");
        if (gallery.Tags.Count > 0)
            AddDetailField(I18n.Tr("标签"), string.Join(" / ", gallery.Tags));
        AddDetailField(I18n.Tr("状态"), done ? I18n.Tr("已下载")
            : _detailState.Length > 0 ? _detailState : I18n.Tr("未下载"));
        if (gallery.Expunged)
            AddDetailField(I18n.Tr("提示"), I18n.Tr("该画廊已在站点被删除，内容可能不全"));

        var shots = pages.Where(x => x.Thumb.Length > 0).ToList();
        if (shots.Count > 0)
        {
            foreach (var p in shots)
                _detailImages.Add(new EhImageItem { Index = p.Index, Name = p.FileName, ThumbUrl = p.Thumb });
            if (gallery.FileCount > shots.Count)
            {
                DetailNote.Text = I18n.Format(
                    I18n.Tr("仅预览前 {n} 页，共 {m} 页；下载可取全本。"),
                    ("n", shots.Count), ("m", gallery.FileCount));
                DetailNote.Visibility = Visibility.Visible;
            }
        }
        else if (coverUrl.Length > 0 || gallery.Thumb.Length > 0)
        {
            // 匿名访问时站点只给雪碧图、没有逐张缩略图地址——退回只显示封面，并说明一句
            _detailImages.Add(new EhImageItem
            {
                Index = 1,
                ThumbUrl = coverUrl.Length > 0 ? coverUrl : gallery.Thumb,
            });
            DetailNote.Text = I18n.Tr("站点未提供逐页缩略图（登录后可在站点把画廊版式设为大缩略图），此处只显示封面。");
            DetailNote.Visibility = Visibility.Visible;
        }
        else
        {
            DetailNoImage.Text = I18n.Tr("取不到预览图");
            DetailNoImage.Visibility = Visibility.Visible;
        }
    }

    private void AddDetailField(string key, string value)
    {
        if (value.Length == 0)
            return;
        DetailFields.Children.Add(new TextBlock
        {
            Text = key,
            Margin = new Thickness(0, 0, 0, 2),
            Style = TryFindResource("CaptionText") as Style,
        });
        DetailFields.Children.Add(new TextBlock
        {
            Text = value,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13.5,
            Margin = new Thickness(0, 0, 0, 12),
        });
    }

    /// <summary>后台下载详情预览的缩略图，同时在临时目录存一份本地副本供看图窗口翻页。</summary>
    private async Task LoadDetailImagesAsync(int generation)
    {
        var dir = _detailGallery is null
            ? ""
            : Path.Combine(PreviewCacheDir, _detailGallery.Gid.ToString());
        if (dir.Length > 0)
        {
            try { Directory.CreateDirectory(dir); }
            catch (IOException) { dir = ""; }
            catch (UnauthorizedAccessException) { dir = ""; }
        }

        using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
        for (var i = 0; i < _detailImages.Count; i++)
        {
            if (generation != _detailGen)
                return;
            var item = _detailImages[i];
            try
            {
                var bytes = await EhentaiApi.GetImageBytesAsync(client, item.ThumbUrl);
                if (generation != _detailGen)
                    return;
                if (dir.Length > 0)
                {
                    // 站点缩略图一律是 jpeg，本地副本按页码命名，看图窗口据此按顺序翻页
                    var local = Path.Combine(dir, item.Index.ToString("D3") + ".jpg");
                    try
                    {
                        await File.WriteAllBytesAsync(local, bytes);
                        item.LocalPath = local;
                    }
                    catch (IOException) { }
                }
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.StreamSource = ms;
                    image.EndInit();
                }
                image.Freeze();
                item.Thumb = image;
                if (i == 0)
                    SelectDetailImage(item);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                          or NotSupportedException or ArgumentException)
            {
                // 单张失败不影响其它
            }
        }
    }

    /// <summary>把某张图设为大图，并把缩略图条的高亮挪过去。</summary>
    private void SelectDetailImage(EhImageItem item)
    {
        foreach (var x in _detailImages)
            x.IsSelected = ReferenceEquals(x, item);
        DetailMainImage.Source = item.Thumb;
    }

    private void DetailThumb_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is EhImageItem item)
            SelectDetailImage(item);
    }

    /// <summary>点大图：用程序内看图窗口翻看这一本的全部预览图（对齐 Web 的灯箱）。</summary>
    private void DetailMainImage_Click(object sender, MouseButtonEventArgs e)
    {
        var paths = _detailImages.Where(x => x.LocalPath.Length > 0).Select(x => x.LocalPath).ToList();
        if (paths.Count == 0)
            return;
        var current = _detailImages.FirstOrDefault(x => x.IsSelected);
        var index = current is null ? 0 : Math.Max(0, paths.IndexOf(current.LocalPath));
        new ImageViewerDialog(paths, index) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    /// <summary>详情页「在站点打开」：交给系统默认浏览器（桌面固有能力，Web 端是普通超链接）。</summary>
    private void DetailOpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (_detailGallery is null)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(_detailGallery.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "打开画廊页");
        }
    }

    /// <summary>详情页「下载本篇」。</summary>
    private void DetailDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_detailGallery is { } g)
            _ = EnqueueAsync([$"{g.Gid}:{g.Token}"]);
    }

    // ---------- 选择与下载 ----------

    /// <summary>卡内「选择/已选」：翻转该卡片的选中态。</summary>
    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 不让点击冒泡成 ListBoxItem 选中，否则会顺带进详情
        if ((sender as FrameworkElement)?.DataContext is not EhCardItem card)
            return;
        card.IsPicked = !card.IsPicked;
        UpdateSelectInfo();
    }

    /// <summary>卡内「下载」：只入队这一本。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is EhCardItem card)
            _ = EnqueueAsync([card.Key]);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetAllPicked(true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetAllPicked(false);

    private void SetAllPicked(bool picked)
    {
        foreach (var card in _cards.Where(c => c.Selectable))
            card.IsPicked = picked;
        UpdateSelectInfo();
    }

    private void UpdateSelectInfo()
    {
        var count = _cards.Count(c => c.Selectable && c.IsPicked);
        SelectInfo.Text = I18n.Format(I18n.Tr("已选 {n} 本"), ("n", count));
        DownloadSelectedButton.IsEnabled = count > 0;
    }

    /// <summary>操作条「下载选中」：批量入队当前选中的画廊。</summary>
    private void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        _ = EnqueueAsync(_cards.Where(c => c.Selectable && c.IsPicked).Select(c => c.Key).ToList());

    /// <summary>
    /// 选媒体库 → 入队 → 提示 → 把入队成功的画廊就地转为"下载中"（对齐 Web：无需重新搜索）。
    /// 入队要现抓每本的图片页清单（一本几百张图要翻十几页），故进度提示报到第几本。
    /// </summary>
    private async Task EnqueueAsync(IReadOnlyList<string> keys)
    {
        var wanted = keys.ToHashSet();
        var picked = _cards.Where(c => wanted.Contains(c.Key)).ToList();
        var galleries = _galleries
            .Where(g => picked.Any(c => c.Gid == g.Gid))
            .ToList();
        if (galleries.Count == 0)
            return;

        // 有媒体库配置时先选下载目标（与其它来源的入队流程一致）
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        DownloadSelectedButton.IsEnabled = false;
        LoadingText.Text = I18n.Tr("正在加入下载队列…");
        LoadingOverlay.Visibility = Visibility.Visible;

        var result = await EhentaiService.EnqueueGalleriesAsync(
            galleries, targetFolder, targetLib,
            (index, total, title) => Dispatcher.Invoke(() =>
                LoadingText.Text = I18n.Format(
                    I18n.Tr("正在抓取图片清单 {i}/{n}：{title}"),
                    ("i", index), ("n", total), ("title", title))));
        LoadingOverlay.Visibility = Visibility.Collapsed;
        LoadingText.Text = I18n.Tr("正在查询…");

        if (!result.Ok)
        {
            InAppDialog.Warn(this, result.Error ?? I18n.Tr("加入下载失败"), I18n.Tr("提示"));
            UpdateSelectInfo();
            return;
        }

        for (var i = 0; i < _cards.Count; i++)
        {
            if (!wanted.Contains(_cards[i].Key))
                continue;
            _cards[i] = new EhCardItem
            {
                Gid = _cards[i].Gid, Token = _cards[i].Token, Title = _cards[i].Title,
                ThumbUrl = _cards[i].ThumbUrl, TagText = _cards[i].TagText,
                MetaText = _cards[i].MetaText, State = "下载中", Selectable = false,
                Thumb = _cards[i].Thumb,
            };
        }
        // 正停在被入队画廊的详情页时，「下载本篇」同步转为禁用的状态按钮
        if (_level == "detail" && _detailGallery is { } dg && wanted.Contains($"{dg.Gid}:{dg.Token}"))
        {
            _detailState = "下载中";
            DetailDownloadButton.Content = _detailState;
            DetailDownloadButton.IsEnabled = false;
        }
        UpdateSelectInfo();
        InAppDialog.Info(this,
            I18n.Format(I18n.Tr("已加入下载：{galleries} 本画廊 / {files} 张图片"),
                ("galleries", result.GalleryCount), ("files", result.FileCount)) +
            (result.Skipped > 0
                ? I18n.Format(I18n.Tr("，跳过 {n} 本"), ("n", result.Skipped))
                : ""),
            I18n.Tr("提示"));
    }

    // ---------- 站上缩略图 ----------

    /// <summary>后台下载卡片封面，新一轮请求即作废旧加载。</summary>
    private async Task LoadThumbnailsAsync(int generation)
    {
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
        foreach (var card in _cards.ToList())
        {
            if (generation != _generation)
                return;
            if (card.Thumb != null || card.ThumbUrl.Length == 0)
                continue;
            try
            {
                var bytes = await EhentaiApi.GetImageBytesAsync(client, card.ThumbUrl);
                if (generation != _generation)
                    return;
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    // 卡片宽随窗口浮动，解码宽取固定一档 400（同媒体库作品卡的封面档位）
                    image.DecodePixelWidth = 400;
                    image.StreamSource = ms;
                    image.EndInit();
                }
                image.Freeze();
                card.Thumb = image;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                          or NotSupportedException or ArgumentException)
            {
                // 单张缩略图失败不影响其它
            }
        }
    }
}
