using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>作品详情页尾图流里的一张图（站上的 800px 预览）。</summary>
public class FanboxImageItem : ObservableBase
{
    public string Name { get; init; } = "";
    public string ThumbUrl { get; init; } = "";

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

/// <summary>FANBOX 搜索结果里的一张卡片：作家卡（artist）或作家主页的作品卡（post）。</summary>
public class FanboxCardItem : ObservableBase
{
    /// <summary>作家号或作品号（按 Kind 解释）。</summary>
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string ThumbUrl { get; init; } = "";
    /// <summary>左上角小标：作家的 public_id。</summary>
    public string TagText { get; init; } = "";
    /// <summary>右上角小标：作品文件数 / 作家被收藏数。</summary>
    public string MetaText { get; init; } = "";
    /// <summary>本地状态（已品悦 / 下载中 / 已下载），空表示站上作品尚未下载过。</summary>
    public string State { get; init; } = "";
    /// <summary>可选中下载（未入库且不在下载队列中的作品）。</summary>
    public bool Selectable { get; init; }
    /// <summary>卡片来源：artist=作家卡（无操作按钮）/ post=作家主页作品。</summary>
    public string Kind { get; init; } = "post";

    public Visibility TagVisibility => TagText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MetaVisibility => MetaText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>状态文案：已品悦在界面上一律显示为「已下载」。</summary>
    public string StateText => State == "已品悦" ? I18n.Tr("已下载") : State;
    public Visibility StateVisibility => State.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    // 卡内按钮行二选一：未下载的作品可 选择/下载；已下载或下载中则是禁用的状态按钮。作家卡不带按钮。
    public Visibility SelectVisibility =>
        Kind == "post" && Selectable ? Visibility.Visible : Visibility.Collapsed;

    public Visibility BusyVisibility =>
        Kind == "post" && !Selectable ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>已入库/下载中的作品降亮，与 Web 的 .card.dim 等价。</summary>
    public double CardOpacity => Kind == "post" && !Selectable ? 0.45 : 1.0;

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
    public ImageSource? Thumb
    {
        get => _thumb;
        set => Set(ref _thumb, value);
    }
}

/// <summary>
/// 「作品搜索」分区中 FANBOX 来源（pawchive）的结果区：搜作家 → 作家主页选作品 → 批量/单篇下载。
/// 只做搜索与下载，不含媒体库浏览——已下载的 fanbox 作品落在磁盘的媒体库目录里，程序内不再建卡片视图。
///
/// 交互与样式以 Web 端 src/Web/js/fanbox.js 为基准：单个搜索结果直接进主页、卡内按钮选择、
/// 下拉到底自动翻页、已下载/下载中的作品置灰且不可选。
/// 工具栏（来源下拉 + 输入框 + 查询 + 返回作家列表）由宿主 <see cref="SearchPage"/> 提供。
/// 层级（_level）：artists（作家结果）/ posts（作家主页）/ detail（单篇作品内容）/ watch（作家监控）。
/// </summary>
public partial class FanboxSearchView : UserControl
{
    private readonly ObservableCollection<FanboxCardItem> _cards = [];

    private string _level = "artists";
    private int _generation;        // 请求代际：新一轮搜索/换作家即作废在途请求与缩略图加载

    // 当前作家
    private string _artistId = "";
    private string _artistName = "";
    private string _publicId = "";
    // 当前主页是否从作家列表点进来（决定「返回作家列表」是否可用）
    private bool _fromArtists;
    // 作家列表结果缓存：从主页返回时无需重新搜索
    private List<FanboxCardItem> _artistCards = [];

    // 作家主页分页（站点固定每页 50 条）
    private readonly List<PawchivePost> _posts = [];
    private int _offset;
    private bool _hasMore;
    private bool _loadingMore;

    // 作品详情
    private readonly ObservableCollection<FanboxImageItem> _detailImages = [];
    // 图流的懒加载：只下滚到跟前的图。_detailQueued 记已排过队的，避免滚动事件重复入队
    private readonly Queue<FanboxImageItem> _detailPending = new();
    private readonly HashSet<FanboxImageItem> _detailQueued = [];
    private bool _detailPumping;
    private const int DetailEagerPages = 2;        // 首屏那几张不等滚动事件，渲染完就开始下
    private const double DetailLookahead = 1.0;    // 视口上下各预取一屏
    private string _detailPostId = "";
    private string _detailState = "";
    private int _detailGen;     // 详情请求代际：与 _generation 分开，看详情不该作废主页的分页

    // 作家监控（列表与间隔在系统设置里，这里只管作家主页那颗按钮）
    private bool _watched;                      // 当前作家是否已在监控中

    private static readonly Regex ArtistUrlRe = new(@"/fanbox/user/(\d+)", RegexOptions.IgnoreCase);

    /// <summary>结果计数/提示文案变化（宿主显示在搜索框下方）。</summary>
    public event Action<string>? StatusChanged;

    /// <summary>「返回作家列表」按钮是否应该显示。</summary>
    public event Action<bool>? BackAvailabilityChanged;

    /// <summary>详情页「下载」按钮（在宿主的搜索栏上）需要重新取一次文案/可用性。</summary>
    public event Action? DetailDownloadChanged;

    /// <summary>当前是否可返回上一层（宿主切回本来源时用它恢复返回按钮）。</summary>
    public bool CanGoBack =>
        _level == "detail" || (_level == "posts" && _fromArtists);

    /// <summary>返回按钮文案：两级都只写「返回」，回哪一层看当前层级（对齐 Web fbUpdateToolbarBtns）。</summary>
    public string BackLabel => I18n.Tr("返回");

    // ---------- 详情页「下载」（按钮在宿主 SearchPage 的搜索栏上，查询 与 返回 之间）----------

    /// <summary>只在作品详情层显示。</summary>
    public bool CanDownloadDetail => _level == "detail" && _detailPostId.Length > 0;

    /// <summary>已入库/下载中时转成状态文案（同 Web 的工具栏「下载」）。</summary>
    public string DetailDownloadLabel => _detailState switch
    {
        "已品悦" => I18n.Tr("已下载"),
        "下载中" or "已下载" => _detailState,
        _ => I18n.Tr("下载"),
    };

    /// <summary>已入库/下载中的作品不必再下。</summary>
    public bool DetailDownloadEnabled => _detailState is not ("已品悦" or "下载中" or "已下载");

    /// <summary>宿主搜索栏的「下载」：入队当前详情这一篇。</summary>
    public void DownloadDetail()
    {
        if (_detailPostId.Length > 0)
            _ = EnqueueAsync([_detailPostId]);
    }

    /// <summary>宿主的返回按钮：按当前层级回上一层。</summary>
    public void GoBack()
    {
        if (_level == "detail")
            GoBackToPosts();
        else
            GoBackToArtists();
    }

    public FanboxSearchView()
    {
        InitializeComponent();
        CardList.ItemsSource = _cards;
        RetranslateUi();
        I18n.LanguageChanged += RetranslateUi;
    }

    private void RetranslateUi()
    {
        SelectAllButton.Content = I18n.Tr("全选");
        UpdateWatchButton();
        SelectNoneButton.Content = I18n.Tr("清空");
        DownloadSelectedButton.Content = I18n.Tr("下载选中");
        LoadingText.Text = I18n.Tr("正在查询…");
        UpdateSelectInfo();
    }

    /// <summary>输入框的占位提示（宿主切到本来源时取用）。</summary>
    public static string InputHint => I18n.Tr("作家名 / 作家 ID，或粘贴 pawchive 作家链接");

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
        SelectBar.Visibility = level == "posts" ? Visibility.Visible : Visibility.Collapsed;
        CardList.Visibility = level is "artists" or "posts" ? Visibility.Visible : Visibility.Collapsed;
        DetailPane.Visibility = level == "detail" ? Visibility.Visible : Visibility.Collapsed;
        BackAvailabilityChanged?.Invoke(CanGoBack);
        DetailDownloadChanged?.Invoke();
    }

    // ---------- 搜索 ----------

    /// <summary>
    /// 由宿主的「查询」触发。输入可以是作家名、纯数字作家号，或直接粘贴 pawchive 的作家/作品链接。
    /// </summary>
    public async Task RunSearchAsync(string raw)
    {
        raw = (raw ?? "").Trim();
        if (raw.Length == 0)
            return;

        var match = ArtistUrlRe.Match(raw);
        if (match.Success)
        {
            await OpenArtistAsync(match.Groups[1].Value, "", "", fromArtists: false);
            return;
        }
        if (raw.Length >= 4 && raw.All(char.IsAsciiDigit))
        {
            await OpenArtistAsync(raw, "", "", fromArtists: false);
            return;
        }
        await SearchArtistsAsync(raw);
    }

    private async Task SearchArtistsAsync(string keyword)
    {
        var generation = ++_generation;
        _fromArtists = false;
        ShowLevel("artists");
        _cards.Clear();
        SetStatus("");
        LoadingText.Text = PawchiveApi.CreatorsReady
            ? I18n.Tr("正在搜索作家…")
            : I18n.Tr("正在下载站点作家索引（约 15MB），首次搜索稍慢…");
        LoadingOverlay.Visibility = Visibility.Visible;

        List<PawchiveArtist> artists;
        try
        {
            artists = await PawchiveApi.SearchArtistsAsync(keyword);
        }
        finally
        {
            if (generation == _generation)
                LoadingOverlay.Visibility = Visibility.Collapsed;
        }
        if (generation != _generation)
            return;

        // 只有一个结果 → 直接进入该作家主页（对齐 Web：免去多余一次点击）
        if (artists.Count == 1)
        {
            await OpenArtistAsync(artists[0].Id, artists[0].Name, artists[0].PublicId, fromArtists: false);
            return;
        }
        if (artists.Count == 0)
        {
            SetStatus(I18n.Tr("没有匹配的作家"));
            return;
        }

        _artistCards = artists.Select(a => new FanboxCardItem
        {
            Kind = "artist",
            Id = a.Id,
            Title = a.Name.Length > 0 ? a.Name : a.PublicId,
            ThumbUrl = PawchiveApi.IconUrl(a.Service, a.Id),
            TagText = a.PublicId,
            MetaText = a.Favorited > 0 ? $"♥ {a.Favorited}" : "",
        }).ToList();
        foreach (var card in _artistCards)
            _cards.Add(card);

        var cachedAt = PawchiveApi.CreatorsCachedAt;
        SetStatus(I18n.Format(I18n.Tr("搜索到 {n} 位作家"), ("n", artists.Count)) +
            (cachedAt is { } t ? "　" + I18n.Format(I18n.Tr("索引更新于 {time}"),
                ("time", t.ToString("yyyy-MM-dd HH:mm"))) : ""));
        _ = LoadThumbnailsAsync(generation);
    }

    /// <summary>宿主的「返回作家列表」：回到上一次的作家搜索结果（结果已缓存，不重新请求）。</summary>
    public void GoBackToArtists()
    {
        if (_artistCards.Count == 0)
            return;
        var generation = ++_generation;
        _cards.Clear();
        foreach (var card in _artistCards)
            _cards.Add(card);
        _fromArtists = false;
        ShowLevel("artists");
        SetStatus(I18n.Format(I18n.Tr("搜索到 {n} 位作家"), ("n", _artistCards.Count)));
        _ = LoadThumbnailsAsync(generation);
    }

    // ---------- 作家主页 ----------

    private async Task OpenArtistAsync(string artistId, string artistName, string publicId, bool fromArtists)
    {
        var generation = ++_generation;
        _artistId = artistId;
        _artistName = artistName;
        _publicId = publicId;
        _fromArtists = fromArtists;
        _posts.Clear();
        _cards.Clear();
        _offset = 0;
        _hasMore = true;
        _loadingMore = false;
        ShowLevel("posts");
        UpdateSelectInfo();
        _watched = FanboxWatchService.IsWatched(artistId);
        UpdateWatchButton();
        SetStatus("");
        LoadingText.Text = I18n.Tr("正在获取作品…");
        LoadingOverlay.Visibility = Visibility.Visible;

        // 作家名未知（直接输入作家号/链接进来）时补一次作家信息
        if (_artistName.Length == 0 &&
            await PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, artistId) is { } profile)
        {
            if (generation != _generation)
                return;
            _artistName = profile.Name;
            _publicId = profile.PublicId;
        }

        await LoadMorePostsAsync(generation);
        if (generation == _generation)
            LoadingOverlay.Visibility = Visibility.Collapsed;
    }

    /// <summary>取下一页作品并追加到网格；每页 50 条，取不满一页即到底。</summary>
    private async Task LoadMorePostsAsync(int generation)
    {
        if (_loadingMore || !_hasMore)
            return;
        _loadingMore = true;
        try
        {
            var batch = await PawchiveApi.GetPostsAsync(PawchiveApi.FanboxService, _artistId, _offset);
            if (generation != _generation)
                return;
            if (batch is null)
            {
                SetStatus(I18n.Tr("获取作品列表失败"));
                _hasMore = false;
                return;
            }
            _offset += batch.Count;
            _hasMore = batch.Count >= PawchiveApi.PageSize;
            _posts.AddRange(batch);

            var states = await Task.Run(() => FanboxService.PostStates(_artistId));
            if (generation != _generation)
                return;
            foreach (var p in batch)
            {
                var state = states.GetValueOrDefault(p.Id, "");
                // 作品本体常常只放在网盘上（站上只归档一张封面图）：这类作品照样可选可下
                var drive = GoogleDriveClient.LinksIn(p.Links).Count;
                _cards.Add(new FanboxCardItem
                {
                    Kind = "post",
                    Id = p.Id,
                    Title = p.Title.Length > 0 ? p.Title : p.Id,
                    ThumbUrl = p.CoverPath.Length > 0 ? PawchiveApi.ThumbUrl(p.CoverPath) : "",
                    TagText = drive > 0 ? I18n.Tr("☁ 网盘") : "",
                    MetaText = p.Files.Count > 0
                        ? I18n.Format(I18n.Tr("{n} 文件"), ("n", p.Files.Count)) : "",
                    State = state,
                    Selectable = state.Length == 0 && (p.Files.Count > 0 || drive > 0),
                });
            }
            SetStatus(ArtistLineText());
            _ = LoadThumbnailsAsync(generation);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    /// <summary>滚动到接近底部时自动加载下一页（与 DLsite 社团作品网格同一交互）。</summary>
    private void CardList_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_level != "posts" || !_hasMore || _loadingMore)
            return;
        if (e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 2)
            _ = LoadMorePostsAsync(_generation);
    }

    private void CardList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CardList.SelectedItem is not FanboxCardItem item)
            return;
        CardList.SelectedItem = null;
        if (item.Kind == "artist")
        {
            _ = OpenArtistAsync(item.Id, item.Title, item.TagText, fromArtists: true);
        }
        else
        {
            // 点卡片本体进入作品详情查看内容（对齐 Web：选择仍由卡内「选择」按钮负责）。
            // 已下载/下载中的作品同样能看，只是不能再选。
            _ = OpenPostAsync(item);
        }
    }

    // ---------- 作品详情 ----------

    /// <summary>从详情返回作家主页：网格与已翻页数据都还在，恢复标题行即可。</summary>
    private void GoBackToPosts()
    {
        ShowLevel("posts");
        SetStatus(ArtistLineText());
    }

    /// <summary>
    /// 作家主页的标题行：只写这是谁的主页。
    /// 已加载多少篇不显示——下拉到底自动续页，这个数字随时在变、对用户也没用。
    /// </summary>
    private string ArtistLineText() => _artistName.Length > 0 ? _artistName : _artistId;

    /// <summary>点作品卡进入：拉取正文与附件清单并渲染。</summary>
    private async Task OpenPostAsync(FanboxCardItem card)
    {
        var generation = ++_detailGen;
        _detailPostId = card.Id;
        _detailState = card.State;
        ShowLevel("detail");
        SetStatus("");   // 标题就在下面的详情里，计数行不再重复一遍

        // 先清空上一篇的内容，免得新旧混显
        DetailTitle.Text = card.Title;
        DetailCoverImage.Source = null;
        _detailImages.Clear();
        _detailPending.Clear();
        _detailQueued.Clear();
        DetailPages.ItemsSource = _detailImages;
        DetailPagesHeader.Visibility = Visibility.Collapsed;
        DetailFields.Children.Clear();
        DetailOthers.ItemsSource = null;
        DetailLinks.Children.Clear();
        DetailBodyHeader.Visibility = DetailBody.Visibility = Visibility.Collapsed;
        DetailOthersHeader.Visibility = Visibility.Collapsed;
        DetailLinksHeader.Visibility = DetailLinksNote.Visibility = Visibility.Collapsed;
        DetailNoImage.Visibility = Visibility.Collapsed;
        DetailPane.ScrollToTop();
        LoadingOverlay.Visibility = Visibility.Visible;

        var post = await PawchiveApi.GetPostAsync(PawchiveApi.FanboxService, _artistId, card.Id);
        if (generation != _detailGen || _level != "detail")
            return;
        LoadingOverlay.Visibility = Visibility.Collapsed;
        if (post is null)
        {
            DetailNoImage.Text = I18n.Tr("获取作品详情失败");
            DetailNoImage.Visibility = Visibility.Visible;
            return;
        }
        RenderDetail(post);
        StartDetailImages(generation);
    }

    /// <summary>渲染详情：标题 + 字段 + 正文 + 其他附件 + 图集占位（图片随后异步填充）。</summary>
    private void RenderDetail(PawchivePost post)
    {
        var done = _detailState == "已品悦";
        var busy = _detailState is "下载中" or "已下载";

        DetailTitle.Text = post.Title.Length > 0 ? post.Title : post.Id;
        DetailDownloadChanged?.Invoke();   // 「下载」按钮在宿主搜索栏上，由它重新取文案/可用性

        var images = post.Files.Where(f => PawchiveApi.IsImageName(f.Name)).ToList();
        var others = post.Files.Where(f => !PawchiveApi.IsImageName(f.Name)).ToList();
        var links = GoogleDriveClient.LinksIn(post.Links);

        AddDetailField(I18n.Tr("作家"), _artistName.Length > 0 ? _artistName : _artistId);
        AddDetailField(I18n.Tr("发布"), FormatPublished(post.Published));
        if (post.Tags.Count > 0)
            AddDetailField(I18n.Tr("标签"), string.Join(" / ", post.Tags));
        AddDetailField(I18n.Tr("文件"),
            I18n.Format(I18n.Tr("{n} 个（图片 {m}）"), ("n", post.Files.Count), ("m", images.Count)) +
            (links.Count > 0 ? "　" + I18n.Format(I18n.Tr("网盘 {n} 条"), ("n", links.Count)) : ""));
        AddDetailField(I18n.Tr("状态"), done ? I18n.Tr("已下载")
            : _detailState.Length > 0 ? _detailState : I18n.Tr("未下载"));

        if (post.Content.Length > 0)
        {
            DetailBody.Text = post.Content;
            DetailBodyHeader.Text = I18n.Tr("正文");
            DetailBodyHeader.Visibility = DetailBody.Visibility = Visibility.Visible;
        }
        if (others.Count > 0)
        {
            DetailOthers.ItemsSource = others.Select(f => f.Name).ToList();
            DetailOthersHeader.Text = I18n.Format(I18n.Tr("其他附件（{n}）"), ("n", others.Count));
            DetailOthersHeader.Visibility = Visibility.Visible;
        }
        RenderDriveLinks(links);

        foreach (var f in images)
            _detailImages.Add(new FanboxImageItem
            {
                Name = f.Name,
                ThumbUrl = PawchiveApi.ThumbUrl(f.Path),
            });
        if (images.Count == 0)
        {
            DetailNoImage.Text = I18n.Tr("这篇没有图片");
            DetailNoImage.Visibility = Visibility.Visible;
        }
        else
        {
            DetailPagesHeader.Text = I18n.Format(I18n.Tr("全部图片（{n} 张）"), ("n", images.Count));
            DetailPagesHeader.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 渲染「网盘链接」区块（对齐 Web 的同名清单）：一行一条，可点开浏览器看原页面。
    ///
    /// 作者把作品本体（多为压缩包）放在谷歌网盘、站上只归档一张封面图时才有内容；
    /// 下载本篇会把这些文件一并入队（共享文件夹先展开成逐个文件），下完按设置自动解压。
    /// </summary>
    private void RenderDriveLinks(List<DriveLink> links)
    {
        DetailLinks.Children.Clear();
        if (links.Count == 0)
        {
            DetailLinksHeader.Visibility = DetailLinksNote.Visibility = Visibility.Collapsed;
            return;
        }
        DetailLinksHeader.Text = I18n.Format(I18n.Tr("网盘链接（{n}）"), ("n", links.Count));
        DetailLinksHeader.Visibility = Visibility.Visible;
        foreach (var link in links)
        {
            var url = link.Url;
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            text.Inlines.Add(new System.Windows.Documents.Run(
                link.Kind == DriveLinkKind.Folder ? "📁 " : "📦 "));
            var hyperlink = new System.Windows.Documents.Hyperlink(
                new System.Windows.Documents.Run(url))
            {
                Foreground = (Brush)FindResource("AccentLightBrush"),
                TextDecorations = null,
            };
            hyperlink.Click += (_, _) => System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            text.Inlines.Add(hyperlink);
            DetailLinks.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x22)),
                BorderThickness = new Thickness(1),
                BorderBrush = (Brush)FindResource("BorderBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = text,
            });
        }
        DetailLinksNote.Text = I18n.Tr("下载本篇时会一并下载网盘里的文件，完成后按设置自动解压");
        DetailLinksNote.Visibility = Visibility.Visible;
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

    /// <summary>站点给的是 2026-01-23T08:00:00 这种，去掉中间的 T。</summary>
    private static string FormatPublished(string raw) =>
        raw.Length == 0 ? "" : raw.Replace('T', ' ');

    /// <summary>
    /// 后台下载图集的预览图。
    ///
    /// 详情只展示 800px 预览图（img.&lt;host&gt;），不取原图：原图动辄数 MB，而且源站未归档时
    /// 一律 404（见 DownloadEngine 的预览图回退）。要原图请下载入库。
    /// 预览图同时在临时目录存一份本地副本，供看图窗口按路径翻页。
    /// </summary>
    // ---------- 页尾图流的懒加载 ----------
    //
    // 只下载滚到跟前的图：一篇动辄三四十张，进详情就整篇拉下来会把带宽占满、首屏反而最慢。
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

    private void EnqueueDetailImage(FanboxImageItem item, int generation)
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
            // pawchive 的防护网关拦浏览器 UA，取预览图同样要用中性 UA
            using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
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

    private async Task LoadDetailImageAsync(HttpClient client, FanboxImageItem item, int generation)
    {
        try
        {
            var bytes = await client.GetByteArrayAsync(item.ThumbUrl);
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
        e.Handled = true;   // 不让点击冒泡成 ListBoxItem 选中，否则会被再翻一次
        if ((sender as FrameworkElement)?.DataContext is not FanboxCardItem card)
            return;
        card.IsPicked = !card.IsPicked;
        UpdateSelectInfo();
    }

    /// <summary>卡内「下载」：只入队这一篇。</summary>
    private void DownloadOne_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is FanboxCardItem card)
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
        SelectInfo.Text = I18n.Format(I18n.Tr("已选 {n} 篇"), ("n", count));
        DownloadSelectedButton.IsEnabled = count > 0;
    }

    /// <summary>操作条「下载选中」：批量入队当前选中的作品。</summary>
    private void DownloadSelected_Click(object sender, RoutedEventArgs e) =>
        _ = EnqueueAsync(_cards.Where(c => c.Selectable && c.IsPicked).Select(c => c.Id).ToList());

    /// <summary>
    /// 选媒体库 → 入队 → 提示 → 把入队成功的作品就地转为"下载中"（对齐 Web：无需重新拉列表）。
    /// 操作条的批量下载与卡内单篇下载共用此流程。
    /// </summary>
    private async Task EnqueueAsync(IReadOnlyList<string> ids)
    {
        var wanted = ids.ToHashSet();
        var posts = _posts.Where(p => wanted.Contains(p.Id)).ToList();
        if (posts.Count == 0)
            return;

        // 有媒体库配置时先选下载目标（与 DLsite 搜索的入队流程一致）
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

        var artist = new PawchiveArtist
        {
            Id = _artistId, Service = PawchiveApi.FanboxService,
            Name = _artistName, PublicId = _publicId,
        };
        var result = await FanboxService.EnqueuePostsAsync(artist, posts, targetFolder, targetLib);
        LoadingOverlay.Visibility = Visibility.Collapsed;

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
            _cards[i] = new FanboxCardItem
            {
                Kind = _cards[i].Kind,
                Id = _cards[i].Id, Title = _cards[i].Title, ThumbUrl = _cards[i].ThumbUrl,
                MetaText = _cards[i].MetaText, State = "下载中", Selectable = false,
                Thumb = _cards[i].Thumb,
            };
        }
        // 正停在被入队作品的详情页时，搜索栏那颗「下载」同步转为禁用的状态按钮
        if (_level == "detail" && wanted.Contains(_detailPostId))
        {
            _detailState = "下载中";
            DetailDownloadChanged?.Invoke();
        }
        UpdateSelectInfo();
        InAppDialog.Info(this,
            I18n.Format(I18n.Tr("已加入下载：{posts} 篇作品 / {files} 个文件"),
                ("posts", result.PostCount), ("files", result.FileCount)) +
            (result.Skipped > 0
                ? I18n.Format(I18n.Tr("，跳过 {n} 篇"), ("n", result.Skipped))
                : ""),
            I18n.Tr("提示"));
    }

    // ---------- 作家监控（有新作品自动下载） ----------
    //
    // 监控列表、轮询间隔与总开关都在「系统设置 → FANBOX 作家监控」（FanboxWatchSettings），
    // 本视图只留两处：作家主页选择条上的「+ 监控作家 / ✓ 已监控」，以及设置页「打开主页」回跳到这里。
    // 数据与轮询都在 FanboxWatchService。

    /// <summary>由设置页的监控卡「打开主页」经宿主转来：直接进这位作家的作品网格。</summary>
    public void OpenArtist(string artistId, string artistName) =>
        _ = OpenArtistAsync(artistId, artistName, "", fromArtists: false);

    /// <summary>切换按钮的激活态外观：on=强调色底+白字（等价 Web 的 .icon-btn.on，同媒体库的 ★/♥ 切换）。</summary>
    private void ApplyToggleState(Button btn, bool on)
    {
        btn.Background = TryFindResource(on ? "AccentBrush" : "ButtonBrush") as Brush
                         ?? (on ? Brushes.DodgerBlue : Brushes.DimGray);
        btn.Foreground = on
            ? Brushes.White
            : TryFindResource("TextBrush") as Brush ?? Brushes.White;
    }

    // ---------- 作家主页的监控开关 ----------

    private void UpdateWatchButton()
    {
        WatchToggleButton.Content = _watched ? I18n.Tr("✓ 已监控") : I18n.Tr("+ 监控作家");
        ApplyToggleState(WatchToggleButton, _watched);
        WatchToggleButton.ToolTip = _watched
            ? I18n.Tr("点击取消监控这位作家")
            : I18n.Tr("添加后按设定的间隔轮询，这位作家发新作品时自动下载");
    }

    private void WatchToggle_Click(object sender, RoutedEventArgs e) => _ = ToggleWatchAsync();

    private async Task ToggleWatchAsync()
    {
        if (_artistId.Length == 0)
            return;
        var who = _artistName.Length > 0 ? _artistName : _artistId;

        if (_watched)
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("不再监控「{name}」？已经下载的作品不受影响。"), ("name", who)),
                    I18n.Tr("确认")))
                return;
            FanboxWatchService.Remove(_artistId);
            _watched = false;
            UpdateWatchButton();
            return;
        }

        // 自动下载要有个落点：和手动下载一样先选媒体库，之后每次自动入队都往这里放
        string? targetFolder = null, targetLib = null;
        if (AppConfig.ReadMediaLibs().Count > 0)
        {
            var dialog = new DownTargetDialog();
            if (!dialog.Show(Window.GetWindow(this)))
                return;
            targetFolder = dialog.SelectedFolder;
            targetLib = dialog.SelectedLib;
        }

        // 现有作品下不下。Web 端是两选一的 confirm（取消即「只要新作品」），
        // 桌面端多一个 Esc 关闭态，按本程序的一贯约定当作取消整个操作。
        var choice = InAppDialog.Choose(this,
            I18n.Format(I18n.Tr("监控「{name}」。这位作家站上已有的作品要一并下载吗？"), ("name", who)) + "\n\n" +
            I18n.Tr("「全部下载」＝把现有作品也加入下载队列（数量可能很多）") + "\n" +
            I18n.Tr("「只要新作品」＝从现在起，只下载今后新发布的作品"),
            I18n.Tr("添加监控"),
            [I18n.Tr("全部下载"), I18n.Tr("只要新作品")], out _);
        if (choice < 0)
            return;

        WatchToggleButton.IsEnabled = false;
        LoadingText.Text = I18n.Tr("正在添加监控…");
        LoadingOverlay.Visibility = Visibility.Visible;
        var artist = new PawchiveArtist
        {
            Id = _artistId, Service = PawchiveApi.FanboxService,
            Name = _artistName, PublicId = _publicId,
        };
        var (ok, message) = await FanboxWatchService.AddAsync(
            artist, intervalMin: 0, includeExisting: choice == 0, targetLib, targetFolder);
        LoadingOverlay.Visibility = Visibility.Collapsed;
        WatchToggleButton.IsEnabled = true;

        if (!ok)
        {
            InAppDialog.Warn(this, message, I18n.Tr("提示"));
            return;
        }
        _watched = true;
        UpdateWatchButton();
        InAppDialog.Info(this, message, I18n.Tr("提示"));
    }

    // ---------- 站上缩略图 ----------

    /// <summary>后台下载卡片缩略图（作家头像 / 作品封面），新一轮请求即作废旧加载。</summary>
    private async Task LoadThumbnailsAsync(int generation)
    {
        // pawchive 的防护网关拦浏览器 UA，取头像/缩略图同样要用中性 UA
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        foreach (var card in _cards.ToList())
        {
            if (generation != _generation)
                return;
            if (card.Thumb != null || card.ThumbUrl.Length == 0)
                continue;
            try
            {
                var bytes = await client.GetByteArrayAsync(card.ThumbUrl);
                if (generation != _generation)
                    return;
                var image = new BitmapImage();
                using (var ms = new MemoryStream(bytes))
                {
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    // 卡片宽随窗口在 150~310 之间浮动，解码宽取固定一档 400（同媒体库作品卡的封面档位）
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
