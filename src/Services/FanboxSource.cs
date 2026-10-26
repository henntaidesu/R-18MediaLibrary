using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// FANBOX 的两个数据源合在一处：pawchive 归档（匿名，<c>official=false</c>）与 fanbox.cc 官方站（要登录，<c>official=true</c>）。
///
/// 作品搜索的来源下拉里两者并列——「FANBOX」与每个已登录账号各一项「FANBOX（账号名）」（多账号并行使用）。
/// 两者结果面板、卡片、详情、下载流程完全相同，只是取数的地方不同，故 Web 路由与桌面视图都只调这里，
/// 按 <c>account</c> 分流：null = pawchive，否则 = 官方站上用该账号（pixiv 用户号）访问，不各自写两套。
/// 每个方法开头把账号设进 <see cref="SiteAccounts"/> 的上下文，往下的 FanboxOfficialApi 取 cookie 都认它。
///
/// 两边寻址作家的键不同：pawchive 是 pixiv 用户号（纯数字），官方站是 creatorId（字符串）。
/// 界面上一律把「当前来源下的作家键」当不透明字符串传来传去；入库时落 works.maker_id 的统一是
/// <see cref="PawchiveArtist.MakerId"/>（pixiv 用户号），两边下到的同一位作家才归得到一起。
/// </summary>
public static class FanboxSource
{
    /// <summary>已登录的 FANBOX 账号（每个在来源下拉里各占一项）。</summary>
    public static List<SiteAccount> OfficialAccounts => SiteAccounts.List(AccountService.Fanbox);

    /// <summary>官方站来源在下拉里的名字：「FANBOX（账号名）」。</summary>
    public static string OfficialLabel(SiteAccount a) => $"FANBOX（{a.DisplayName}）";

    /// <summary>把这次调用的账号设进上下文（只在调用方的 async 方法里生效）。</summary>
    private static void Use(string? account)
    {
        if (account != null)
            SiteAccounts.Use(AccountService.Fanbox, account);
    }

    /// <summary>作家键在当前来源下是否合法（用作 URL / SQL 参数前先校验）。</summary>
    public static bool IsArtistKey(bool official, string id) =>
        official ? FanboxOfficialApi.IsCreatorId(id) : id.Length is > 0 and <= 32 && id.All(char.IsAsciiDigit);

    /// <summary>搜作家。官方站关键字为空时列出「我赞助的 + 我关注的」作家。</summary>
    public static async Task<(List<PawchiveArtist> Artists, string? Error)> SearchArtistsAsync(
        string? account, string keyword)
    {
        Use(account);
        if (account != null)
            return await FanboxOfficialApi.SearchCreatorsAsync(keyword);
        return (await PawchiveApi.SearchArtistsAsync(keyword), null);
    }

    public static async Task<PawchiveArtist?> GetArtistAsync(string? account, string artistKey)
    {
        Use(account);
        return account != null
            ? await FanboxOfficialApi.GetCreatorAsync(artistKey)
            : await PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, artistKey);
    }

    /// <summary>作家主页的一批作品（offset = 已加载篇数）。</summary>
    public static async Task<(List<PawchivePost>? Posts, bool HasMore, string? Error)> GetPostsAsync(
        string? account, string artistKey, int offset)
    {
        Use(account);
        if (account != null)
            return await FanboxOfficialApi.GetPostsAsync(artistKey, offset);
        var posts = await PawchiveApi.GetPostsAsync(PawchiveApi.FanboxService, artistKey, offset);
        return posts is null
            ? (null, false, "获取作品列表失败")
            : (posts, posts.Count >= PawchiveApi.PageSize, null);
    }

    public static async Task<(PawchivePost? Post, string? Error)> GetPostAsync(
        string? account, string artistKey, string postId)
    {
        Use(account);
        if (account != null)
            return await FanboxOfficialApi.GetPostAsync(postId);
        var post = await PawchiveApi.GetPostAsync(PawchiveApi.FanboxService, artistKey, postId);
        return post is null ? (null, "获取作品详情失败") : (post, null);
    }

    /// <summary>
    /// 入队前凑齐选中作品的完整信息（文件清单）。
    ///
    /// pawchive 的列表项本身就带全了附件清单，<paramref name="known"/> 里有就直接用，缺的再翻页补；
    /// 官方站的列表只是摘要，必须逐篇取详情（未赞助的档位取回来是 Locked、没有文件，入队时会被跳过）。
    /// </summary>
    public static async Task<List<PawchivePost>> ResolveForEnqueueAsync(
        string? account, string artistKey, IReadOnlyCollection<string> ids, IEnumerable<PawchivePost>? known = null)
    {
        Use(account);
        var wanted = new HashSet<string>(ids);
        var picked = new List<PawchivePost>();
        if (account != null)
        {
            foreach (var id in ids)
                if ((await FanboxOfficialApi.GetPostAsync(id)).Post is { } post)
                    picked.Add(post);
            return picked;
        }

        foreach (var p in known ?? [])
            if (!p.Partial && wanted.Remove(p.Id))
                picked.Add(p);
        for (var page = 0; page < 200 && wanted.Count > 0; page++)
        {
            var batch = await PawchiveApi.GetPostsAsync(
                PawchiveApi.FanboxService, artistKey, page * PawchiveApi.PageSize);
            if (batch is null || batch.Count == 0)
                break;
            foreach (var p in batch)
                if (wanted.Remove(p.Id))
                    picked.Add(p);
            if (batch.Count < PawchiveApi.PageSize)
                break;
        }
        return picked;
    }

    /// <summary>
    /// 作家名下已入库/下载中的作品状态（作品号 → state）。
    /// 状态按 works.maker_id（pixiv 用户号）查，官方站要先由 creatorId 换出用户号。
    /// </summary>
    public static async Task<Dictionary<string, string>> PostStatesAsync(string? account, string artistKey)
    {
        Use(account);
        var makerId = artistKey;
        if (account != null)
            makerId = (await FanboxOfficialApi.GetCreatorAsync(artistKey))?.MakerId ?? "";
        return makerId.Length == 0 ? [] : await Task.Run(() => FanboxService.PostStates(makerId));
    }

    /// <summary>作品卡能不能选来下载：没入库、没在队列里，且确实有东西可下。</summary>
    public static bool IsSelectable(PawchivePost p, string state) =>
        state.Length == 0 && !p.Locked &&
        (p.Partial || p.Files.Count > 0 || GoogleDriveClient.LinksIn(p.Links).Count > 0);

    // ---------- 图片 ----------

    /// <summary>站上图片代理（Web 端 /api/fanbox/image）只放行这几类地址。</summary>
    public static bool IsImageUrl(string url) =>
        FanboxOfficialApi.IsOfficialUrl(url) || FanboxOfficialApi.IsPximgUrl(url) || IsPawchiveUrl(url);

    private static bool IsPawchiveUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return false;
        var baseHost = AppConfig.PawchiveHost;
        return string.Equals(uri.Host, baseHost, StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith("." + baseHost, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 取站上图片（头像 / 封面 / 预览）。两个来源要的请求头正好相反：pawchive 必须用中性 UA
    /// （见 <see cref="PawchiveApi.UserAgent"/>），官方站要浏览器 UA + 登录 cookie + fanbox 的 Referer。
    /// </summary>
    public static async Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url, string? account = null)
    {
        Use(account);
        if (FanboxOfficialApi.IsOfficialUrl(url) || FanboxOfficialApi.IsPximgUrl(url))
            return await FanboxOfficialApi.GetImageAsync(url);
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType ?? "");
    }
}
