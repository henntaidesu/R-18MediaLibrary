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
/// 作品搜索的来源下拉里两者是并列的两项——「FANBOX」与「FANBOX（账号名）」，后者只在「账号访问」里
/// 填了 FANBOXSESSID 时出现。两者结果面板、卡片、详情、下载流程完全相同，只是取数的地方不同，
/// 故 Web 路由与桌面视图都只调这里，按 official 分流，不各自写两套。
///
/// 两边寻址作家的键不同：pawchive 是 pixiv 用户号（纯数字），官方站是 creatorId（字符串）。
/// 界面上一律把「当前来源下的作家键」当不透明字符串传来传去；入库时落 works.maker_id 的统一是
/// <see cref="PawchiveArtist.MakerId"/>（pixiv 用户号），两边下到的同一位作家才归得到一起。
/// </summary>
public static class FanboxSource
{
    /// <summary>官方站来源是否可用（填了登录 cookie）。</summary>
    public static bool OfficialAvailable => FanboxOfficialApi.HasCookie;

    /// <summary>作家键在当前来源下是否合法（用作 URL / SQL 参数前先校验）。</summary>
    public static bool IsArtistKey(bool official, string id) =>
        official ? FanboxOfficialApi.IsCreatorId(id) : id.Length is > 0 and <= 32 && id.All(char.IsAsciiDigit);

    /// <summary>搜作家。官方站关键字为空时列出「我赞助的 + 我关注的」作家。</summary>
    public static async Task<(List<PawchiveArtist> Artists, string? Error)> SearchArtistsAsync(
        bool official, string keyword)
    {
        if (official)
            return await FanboxOfficialApi.SearchCreatorsAsync(keyword);
        return (await PawchiveApi.SearchArtistsAsync(keyword), null);
    }

    public static Task<PawchiveArtist?> GetArtistAsync(bool official, string artistKey) =>
        official
            ? FanboxOfficialApi.GetCreatorAsync(artistKey)
            : PawchiveApi.GetArtistAsync(PawchiveApi.FanboxService, artistKey);

    /// <summary>作家主页的一批作品（offset = 已加载篇数）。</summary>
    public static async Task<(List<PawchivePost>? Posts, bool HasMore, string? Error)> GetPostsAsync(
        bool official, string artistKey, int offset)
    {
        if (official)
            return await FanboxOfficialApi.GetPostsAsync(artistKey, offset);
        var posts = await PawchiveApi.GetPostsAsync(PawchiveApi.FanboxService, artistKey, offset);
        return posts is null
            ? (null, false, "获取作品列表失败")
            : (posts, posts.Count >= PawchiveApi.PageSize, null);
    }

    public static async Task<(PawchivePost? Post, string? Error)> GetPostAsync(
        bool official, string artistKey, string postId)
    {
        if (official)
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
        bool official, string artistKey, IReadOnlyCollection<string> ids, IEnumerable<PawchivePost>? known = null)
    {
        var wanted = new HashSet<string>(ids);
        var picked = new List<PawchivePost>();
        if (official)
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
    public static async Task<Dictionary<string, string>> PostStatesAsync(bool official, string artistKey)
    {
        var makerId = artistKey;
        if (official)
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
    public static async Task<(byte[] Bytes, string ContentType)> GetImageAsync(string url)
    {
        if (FanboxOfficialApi.IsOfficialUrl(url) || FanboxOfficialApi.IsPximgUrl(url))
            return await FanboxOfficialApi.GetImageAsync(url);
        using var client = Http.CreateClient(TimeSpan.FromSeconds(20), PawchiveApi.UserAgent);
        using var resp = await client.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadAsByteArrayAsync(), resp.Content.Headers.ContentType?.MediaType ?? "");
    }

    /// <summary>官方站来源在下拉里的名字：「FANBOX（账号名）」。</summary>
    public static string OfficialLabel =>
        $"FANBOX（{AccountService.DisplayName(AccountService.Fanbox)}）";
}
