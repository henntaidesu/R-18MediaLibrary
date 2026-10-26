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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>作品详情页尾图流里的一张图（站上的 1200px 预览）。</summary>
public class PixivImageItem : ObservableBase
{
    public int Index { get; init; }
    public string Url { get; init; } = "";

    private BitmapImage? _thumb;
    public BitmapImage? Thumb
    {
        get => _thumb;
        set
        {
            if (Set(ref _thumb, value))
                Raise(nameof(PlaceholderHeight));
        }
    }

    /// <summary>
    /// 还没下下来时这一格的占位高度。整列都塌成 0 高的话会一次性全落进视口、懒加载等于没做；
    /// 图一到位就归 0，改按图片真实高度排版（等价 Web 的 .fbpage { min-height } + onload 清零）。
    /// </summary>
    public double PlaceholderHeight => _thumb is null ? 320 : 0;
}

/// <summary>pixiv 搜索结果里的一张作品卡。</summary>
public class PixivCardItem : ObservableBase
{
    /// <summary>站点的作品号（illust id）；选择集合也以它为键。</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>左上角小标：形式 + 分级/AI（卡上只有左右两格，右边留给张数）。</summary>
    public string TagText { get; init; } = "";
    /// <summary>右上角小标：张数（单图不显示）。</summary>
    public string MetaText { get; init; } = "";
    /// <summary>本地状态（已品悦 / 下载中 / 已下载），空表示站上作品尚未下载过。</summary>
    public string State { get; init; } = "";
    /// <summary>可选中下载（未入库且不在下载队列中）。</summary>
    public bool Selectable { get; init; }

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
/// 「作品搜索」分区中 pixiv 来源的结果区：搜作品 → 选作品 → 批量/单件下载。
/// 与 FANBOX / E-Hentai 一样只做搜索与下载，不含媒体库浏览——下好的作品落在磁盘的媒体库目录里。
///
/// 交互与样式以 Web 端 src/Web/js/pixiv.js 为基准：卡内按钮选择、下拉到底自动翻页、
/// 已下载/下载中的置灰且不可选、点卡片本体进详情看内容、详情的整篇图流竖排在页尾懒加载。
/// 工具栏（来源下拉 + 输入框 + 查询 + 下载 + 返回）由宿主 <see cref="SearchPage"/> 提供，
/// 详情页自己不另起操作条（同 FANBOX）。
/// 层级（_level）：results（作品网格）/ detail（单件作品内容）——搜索直接出作品，没有中间层。
/// </summary>
public partial class PixivSearchView : UserControl
{
    private readonly ObservableCollection<PixivCardItem> _cards = [];
    /// <summary>已加载的作品元数据（进详情时先用列表里这份铺上，详情取回来再替换）。</summary>
    private readonly List<PixivArtwork> _artworks = [];

    private string _level = "results";
    private int _generation;        // 请求代际：新一轮搜索即作废在途请求与缩略图加载

    // 搜索与翻页（站点按页号翻页，每页 60 件）
    private string _keyword = "";
    private int _page;
    private bool _hasMore;
    private bool _loadingMore;
    private bool _anonymous;        // 未登录：站点会把 R-18 从结果里滤掉，计数行要说明

    // 作品详情
    private readonly ObservableCollection<PixivImageItem> _detailImages = [];
    // 图流的懒加载：只下滚到跟前的图。_detailQueued 记已排过队的，避免滚动事件重复入队
    private readonly Queue<PixivImageItem> _detailPending = new();
    private readonly HashSet<PixivImageItem> _detailQueued = [];
    private bool _detailPumping;
    private const int DetailEagerPages = 2;        // 首屏那几张不等滚动事件，渲染完就开始下
    private const double DetailLookahead = 1.0;    // 视口上下各预取一屏
    private PixivArtwork? _detailArtwork;
    private string _detailState = "";
    private int _detailGen;     // 详情请求代际：与 _generation 分开，看详情不该作废网格的分页

    /// <summary>结果计数/提示文案变化（宿主显示在搜索框下方）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>「返回」按钮是否应该显示。</summary>
    public event Action<bool>? BackAvailabilityChanged;

    /// <summary>详情页「下载」按钮的文案/可用性变化（宿主的工具栏按钮据此同步）。</summary>
    public event Action? DetailDownloadChanged;

    /// <summary>当前是否可返回上一层（宿主切回本来源时用它恢复返回按钮）。</summary>
    public bool CanGoBack => _level == "detail";

    /// <summary>返回按钮文案（本来源只有详情一层可返回）。</summary>
    public string BackLabel => I18n.Tr("返回");

    /// <summary>详情页才显示「下载」按钮（对齐 Web 的 pxUpdateToolbarBtns）。</summary>
    public bool CanDownloadDetail => _level == "detail" && _detailArtwork != null;

    /// <summary>「下载」按钮文案：已入库/下载中时显示状态。</summary>
    public string DetailDownloadLabel => _detailState switch
    {
        "已品悦" => I18n.Tr("已下载"),
        { Length: > 0 } state => state,
        _ => I18n.Tr("下载"),
    };

    /// <summary>已入库或已在队列中就不能再下。</summary>
    public bool DetailDownloadEnabled => _detailState.Length == 0;

    /// <summary>宿主的返回按钮：从详情回到作品网格。</summary>
    public void GoBack()
    {
        if (_level != "detail")
            return;
        ShowLevel("results");
        SetStatus(CountText());
    }

    /// <summary>宿主工具栏的「下载」：入队当前详情这一件。</summary>
    public void DownloadDetail()
    {
        if (_detailArtwork is { } a && _detailState.Length == 0)
            _ = EnqueueAsync([a.Id]);
    }

    public PixivSearchView()
    {
        InitializeComponent();
        CardList.ItemsSource = _cards;
        DetailPages.ItemsSource = _detailImages;
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        SelectAllButton.Content = I18n.Tr("全选");
        SelectNoneButton.Content = I18n.Tr("清空");
        DownloadSelectedButton.Content = I18n.Tr("下载选中");
        DetailOpenSiteButton.Content = I18n.Tr("在 pixiv 打开");
        DetailBodyHeader.Text = I18n.Tr("说明");
        LoadingText.Text = I18n.Tr("正在查询…");
        UpdateSelectInfo();
    }

    /// <summary>输入框的占位提示（宿主切到本来源时取用）。</summary>
    public static string InputHint =>
        I18n.Tr("关键字 / 标签，或粘贴 pixiv 作品链接 / 作品号");

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
        if (level != "detail")
        {
            // 离开详情就别再为看不见的图发请求
            _detailPending.Clear();
            _detailArtwork = null;
        }
        BackAvailabilityChanged?.Invoke(CanGoBack);
        DetailDownloadChanged?.Invoke();
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 由宿主的「查询」触发。输入可以是关键字/标签，也可以直接粘贴作品链接或作品号
    /// （粘链接时跳过搜索，径直把那一件列出来）。
    /// </summary>
    public async Task RunSearchAsync(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return;

        var generation = ++_generation;
        _keyword = raw;
        _page = 1;
        _hasMore = false;
        _loadingMore = false;
        _anonymous = !PixivApi.HasCookie;
        _cards.Clear();
        _artworks.Clear();
        ShowLevel("results");
        UpdateSelectInfo();
        SetStatus("");
        LoadingText.Text = I18n.Tr("正在搜索作品…");
        LoadingOverlay.Visibility = Visibility.Visible;

        PixivSearchResult result;
        try
        {
            // 贴的是作品链接/作品号：跳过搜索，直接取这一件的详情
            if (PixivApi.ParseArtworkInput(raw) is { } direct)
            {
                var (one, error) = await PixivApi.GetArtworkAsync(direct);
                result = one is null
                    ? new PixivSearchResult { Error = error ?? I18n.Tr("未找到该作品") }
                    : new PixivSearchResult { Items = [one], Page = 1, LastPage = 1 };
            }
            else
            {
                result = await PixivApi.SearchAsync(raw, 1);
            }
        }
        finally
        {
            if (generation == _generation)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (generation != _generation)
            return;

        if (result.Error is { Length: > 0 } searchError)
        {
            SetStatus(searchError);
            return;
        }
        if (result.Items.Count == 0)
        {
            SetStatus(_anonymous
                ? I18n.Tr("没有匹配的作品（未登录，R-18 作品不会出现在结果里）")
                : I18n.Tr("没有匹配的作品"));
            return;
        }
        _page = result.Page;
        _hasMore = result.HasMore;
        AppendArtworks(result.Items);
        SetStatus(CountText());
        _ = LoadThumbnailsAsync(generation);
    }

    /// <summary>取下一页并追加到网格。</summary>
    private async Task LoadMoreAsync(int generation)
    {
        if (_loadingMore || !_hasMore)
            return;
        _loadingMore = true;
        try
        {
            var result = await PixivApi.SearchAsync(_keyword, _page + 1);
            if (generation != _generation)
                return;
            if (result.Error is { Length: > 0 } || result.Items.Count == 0)
            {
                _hasMore = false;
                SetStatus(CountText());
                return;
            }
            _page = result.Page;
            _hasMore = result.HasMore;
            AppendArtworks(result.Items);
            SetStatus(CountText());
            _ = LoadThumbnailsAsync(generation);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>把一页作品转成卡片追加进网格，并记下元数据供入队复用。</summary>
    private void AppendArtworks(IReadOnlyList<PixivArtwork> items)
    {
        var states = PixivService.ArtworkStates(items.Select(a => a.Id));
        foreach (var a in items)
        {
            _artworks.Add(a);
            var state = states.GetValueOrDefault(a.Id, "");
            _cards.Add(new PixivCardItem
            {
                Id = a.Id,
                Title = a.Title.Length > 0 ? a.Title : a.Id,
                ThumbUrl = a.Thumb,
                TagText = TagTextOf(a),
                MetaText = a.PageCount > 1 ? I18n.Format(I18n.Tr("{n} 张"), ("n", a.PageCount)) : "",
                State = state,
                Selectable = state.Length == 0,
            });
        }
    }

    /// <summary>左上角小标：形式 + 分级/AI 合成一格（对齐 Web 的 pxTagText）。</summary>
    private static string TagTextOf(PixivArtwork a)
    {
        var extra = new List<string>();
        if (a.RestrictName.Length > 0)
            extra.Add(a.RestrictName);
        if (a.IsAi)
            extra.Add("AI");
        var type = I18n.Tr(a.TypeName);
        return extra.Count > 0 ? $"{type} · {string.Join(" · ", extra)}" : type;
    }

    /// <summary>网格的计数行文案（加载完与从详情返回时共用同一处）。</summary>
    private string CountText() =>
        I18n.Format(I18n.Tr("已加载 {n} 件作品"), ("n", _cards.Count)) +
        (_hasMore ? I18n.Tr("（下拉加载更多）") : "") +
        (_anonymous ? I18n.Tr("　未登录：结果中不含 R-18") : "");

    /// <summary>滚动到接近底部时自动加载下一页（与 FANBOX / E-Hentai 网格同一交互）。</summary>
    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_level != "results" || !_hasMore || _loadingMore)
            return;
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            _ = LoadMoreAsync(_generation);
    }

    /// <summary>点卡片本体进入作品详情（选择仍由卡内「选择」按钮负责）。</summary>
    private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is not PixivCardItem item)
            return;
        CardList.SelectedItem = null;
        _ = OpenArtworkAsync(item);
    }

    // ---------- 作品详情 ----------

    /// <summary>
    /// 点作品卡进入：拉取详情与逐页图片地址并渲染。
    /// 图一律取 1200px 的 regular——详情只是预览，原图是下载时才取的。
    /// </summary>
    private async Task OpenArtworkAsync(PixivCardItem card)
    {
        var generation = ++_detailGen;
        _detailState = card.State;
        _detailArtwork = _artworks.FirstOrDefault(a => a.Id == card.Id);
        ShowLevel("detail");
        SetStatus("");   // 标题就在详情里，顶上不再重复一遍

        // 先清空上一件的内容，免得新旧混显
        DetailTitle.Text = card.Title;
        DetailCoverImage.Source = null;
        _detailImages.Clear();
        _detailPending.Clear();
        _detailQueued.Clear();
        DetailFields.Children.Clear();
        DetailNoImage.Visibility = Visibility.Collapsed;
        DetailBodyHeader.Visibility = Visibility.Collapsed;
        DetailBody.Visibility = Visibility.Collapsed;
        DetailPagesHeader.Visibility = Visibility.Collapsed;
        DetailPane.ScrollToTop();
        LoadingText.Text = I18n.Tr("正在获取作品内容…");
        LoadingOverlay.Visibility = Visibility.Visible;

        // 搜索结果里的条目没有说明/收藏数，详情要另取一次；取不到就用列表里那份
        var (detail, error) = await PixivApi.GetArtworkAsync(card.Id);
        var artwork = detail ?? _detailArtwork;
        var pages = artwork is null ? [] : await PixivApi.GetPagesAsync(card.Id);
        if (generation != _detailGen || _level != "detail")
            return;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (artwork is null)
        {
            DetailNoImage.Text = error ?? I18n.Tr("获取作品信息失败");
            DetailNoImage.Visibility = Visibility.Visible;
            return;
        }
        _detailArtwork = artwork;
        DetailDownloadChanged?.Invoke();
        RenderDetail(artwork, pages, card.ThumbUrl);
        StartDetailImages(generation);
    }

    /// <summary>渲染详情：标题 + 字段 + 说明 + 页尾图流占位（图随后按可见性异步填充）。</summary>
    private void RenderDetail(PixivArtwork a, IReadOnlyList<PixivPageImage> pages, string coverUrl)
    {
        var done = _detailState == "已品悦";

        DetailTitle.Text = a.Title.Length > 0 ? a.Title : a.Id;
        AddDetailField(I18n.Tr("作者"), PixivService.MakerNameOf(a));
        AddDetailField(I18n.Tr("投稿"), a.Created is { } t ? t.ToString("yyyy-MM-dd HH:mm") : "");
        AddDetailField(I18n.Tr("形式"), TagTextOf(a));
        AddDetailField(I18n.Tr("张数"),
            a.PageCount > 0 ? I18n.Format(I18n.Tr("{n} 张"), ("n", a.PageCount)) : "");
        AddDetailField(I18n.Tr("尺寸"), a.Width > 0 && a.Height > 0 ? $"{a.Width} × {a.Height}" : "");
        AddDetailField(I18n.Tr("收藏"), a.BookmarkCount > 0 ? a.BookmarkCount.ToString() : "");
        if (a.Tags.Count > 0)
            AddDetailField(I18n.Tr("标签"), string.Join(" / ", a.Tags));
        AddDetailField(I18n.Tr("状态"), done ? I18n.Tr("已下载")
            : _detailState.Length > 0 ? _detailState : I18n.Tr("未下载"));

        if (a.Description.Length > 0)
        {
            DetailBody.Text = a.Description;
            DetailBodyHeader.Visibility = Visibility.Visible;
            DetailBody.Visibility = Visibility.Visible;
        }

        foreach (var p in pages)
        {
            if (p.Regular.Length > 0)
                _detailImages.Add(new PixivImageItem { Index = p.Index, Url = p.Regular });
        }
        if (_detailImages.Count > 0)
        {
            DetailPagesHeader.Text =
                I18n.Format(I18n.Tr("全部图片（{n} 张）"), ("n", _detailImages.Count));
            DetailPagesHeader.Visibility = Visibility.Visible;
        }
        else if (coverUrl.Length > 0)
        {
            // 取不到逐页地址（作品被删/需要登录）时退回列表里那张缩略图，总好过一片空白
            _detailImages.Add(new PixivImageItem { Index = 1, Url = coverUrl });
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

    /// <summary>详情页「在 pixiv 打开」：交给系统默认浏览器（桌面固有能力，Web 端是普通超链接）。</summary>
    private void DetailOpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (_detailArtwork is not { } a)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(a.PageUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "打开 pixiv 作品页");
        }
    }

    // ---------- 页尾图流的懒加载 ----------
    //
    // 只下载滚到跟前的图：一篇漫画动辄几十张，进详情就整篇拉下来会把带宽占满、首屏反而最慢。
    // 首屏那几张（DetailEagerPages）不等滚动事件，渲染完就开始下；其余由 DetailPane 的
    // ScrollChanged 按可见性补队（等价 Web 的 IntersectionObserver + rootMargin）。

    /// <summary>详情渲染完：先把首屏那几张排进队，再等布局完成按可见性补一批。</summary>
    private void StartDetailImages(int generation)
    {
        for (var i = 0; i < Math.Min(DetailEagerPages, _detailImages.Count); i++)
            EnqueueDetailImage(_detailImages[i], generation);
        // 容器要等一轮布局才生成，布局完再按可见性补队
        Dispatcher.BeginInvoke(new Action(QueueVisibleDetailImages),
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void DetailPane_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_level == "detail")
            QueueVisibleDetailImages();
    }

    /// <summary>把视口上下一屏范围内、还没下的图排进队（每张图加载完会撑高内容、再次触发本方法）。</summary>
    private void QueueVisibleDetailImages()
    {
        if (_level != "detail" || _detailImages.Count == 0)
            return;
        var viewport = DetailPane.ViewportHeight;
        if (viewport <= 0)
            return;
        var margin = viewport * DetailLookahead;
        var generation = _detailGen;
        for (var i = 0; i < _detailImages.Count; i++)
        {
            var item = _detailImages[i];
            if (item.Thumb != null || _detailQueued.Contains(item))
                continue;
            if (DetailPages.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement cell)
                continue;
            double y;
            try
            {
                y = cell.TransformToAncestor(DetailPane).Transform(default).Y;
            }
            catch (InvalidOperationException)
            {
                continue;   // 这一格还没排完版，等下一次滚动/布局再说
            }
            if (y > viewport + margin || y + cell.ActualHeight < -margin)
                continue;
            EnqueueDetailImage(item, generation);
        }
    }

    private void EnqueueDetailImage(PixivImageItem item, int generation)
    {
        if (!_detailQueued.Add(item))
            return;
        _detailPending.Enqueue(item);
        if (!_detailPumping)
            _ = PumpDetailImagesAsync(generation);
    }

    /// <summary>串行下载队列里的图：并发拉几十张只会互相抢带宽，看的人始终只等最上面那张。</summary>
    private async Task PumpDetailImagesAsync(int generation)
    {
        _detailPumping = true;
        try
        {
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20));
            while (_detailPending.Count > 0)
            {
                if (generation != _detailGen)
                    return;
                var item = _detailPending.Dequeue();
                await LoadDetailImageAsync(client, item, generation);
            }
        }
        finally
        {
            _detailPumping = false;
        }
    }

    private async Task LoadDetailImageAsync(HttpClient client, PixivImageItem item, int generation)
    {
        try
        {
            var bytes = await GetImageBytesAsync(client, item.Url);
            if (generation != _detailGen)
                return;
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
            // 首图顺带当封面（上半那格），省一次重复下载
            if (ReferenceEquals(item, _detailImages.FirstOrDefault()))
                DetailCoverImage.Source = image;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException
                                      or NotSupportedException or ArgumentException)
        {
            // 单张失败不影响其它；失败的那格留占位底色
        }
    }

    // ---------- 选择与下载 ----------

    /// <summary>卡内「选择/已选」：翻转该卡片的选中态。</summary>
    private void PickButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;   // 不让点击冒泡成 ListBoxItem 选中，否则会顺带进详情
        if ((sender as FrameworkElement)?.DataContext is not PixivCardItem card)
            return;
        card.IsPicked = !card.IsPicked;
        UpdateSelectInfo();
    }

    /// <summary>卡内「下载」：只入队这一件。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is PixivCardItem card)
            _ = EnqueueAsync([card.Id]);
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
        SelectInfo.Text = I18n.Format(I18n.Tr("已选 {n} 件"), ("n", count));
        DownloadSelectedButton.IsEnabled = count > 0;
    }

    /// <summary>操作条「下载选中」：批量入队当前选中的作品。</summary>
    private void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        _ = EnqueueAsync(_cards.Where(c => c.Selectable && c.IsPicked).Select(c => c.Id).ToList());

    /// <summary>
    /// 选媒体库 → 入队 → 提示 → 把入队成功的作品就地转为"下载中"（对齐 Web：无需重新搜索）。
    /// 入队要逐件现取元数据与图片清单，故进度提示报到第几件。
    /// 只递作品号：元数据由 <see cref="PixivService.EnqueueByIdsAsync"/> 统一现取，
    /// 免得从网格入队的作品比从详情页入队的少一截信息（列表接口不给说明文与收藏数）。
    /// </summary>
    private async Task EnqueueAsync(IReadOnlyList<string> ids)
    {
        var wanted = ids.ToHashSet();
        if (wanted.Count == 0)
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

        var result = await PixivService.EnqueueByIdsAsync(
            ids, targetFolder, targetLib,
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
            if (!wanted.Contains(_cards[i].Id))
                continue;
            _cards[i] = new PixivCardItem
            {
                Id = _cards[i].Id, Title = _cards[i].Title, ThumbUrl = _cards[i].ThumbUrl,
                TagText = _cards[i].TagText, MetaText = _cards[i].MetaText,
                State = "下载中", Selectable = false, Thumb = _cards[i].Thumb,
            };
        }
        // 正停在被入队作品的详情页时，工具栏「下载」同步转为禁用的状态按钮
        if (_level == "detail" && _detailArtwork is { } da && wanted.Contains(da.Id))
        {
            _detailState = "下载中";
            DetailDownloadChanged?.Invoke();
        }
        UpdateSelectInfo();
        InAppDialog.Info(this,
            I18n.Format(I18n.Tr("已加入下载：{artworks} 件作品 / {files} 个文件"),
                ("artworks", result.ArtworkCount), ("files", result.FileCount)) +
            (result.Skipped > 0
                ? I18n.Format(I18n.Tr("，跳过 {n} 件"), ("n", result.Skipped))
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
                var bytes = await GetImageBytesAsync(client, card.ThumbUrl);
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

    /// <summary>
    /// 取站上的一张图。i.pximg.net 带防盗链，没有 pixiv 的 Referer 一律 403，
    /// 故每个请求都要过 <see cref="PixivApi.ApplyHeaders"/>。
    /// </summary>
    private static async Task<byte[]> GetImageBytesAsync(HttpClient client, string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        PixivApi.ApplyHeaders(request);
        using var resp = await client.SendAsync(request);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync();
    }
}
