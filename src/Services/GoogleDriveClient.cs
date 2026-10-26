using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>谷歌网盘链接的两种形态：单个文件，或一整个共享文件夹。</summary>
public enum DriveLinkKind
{
    File,
    Folder,
}

/// <summary>从正文里认出来的一条谷歌网盘链接。</summary>
public readonly record struct DriveLink(DriveLinkKind Kind, string Id)
{
    /// <summary>规范化后的链接地址（同一个网盘项目的各种写法都归一到这一种）。</summary>
    public string Url => Kind == DriveLinkKind.Folder
        ? GoogleDriveClient.FolderUrl(Id)
        : GoogleDriveClient.FileUrl(Id);
}

/// <summary>共享文件夹里的一项（文件或子文件夹）。</summary>
public readonly record struct DriveEntry(DriveLinkKind Kind, string Id, string Name);

/// <summary>解析一个网盘文件的结果：拿到直链与文件名，或者说明为什么没拿到。</summary>
public readonly record struct DriveFileLink(string Url, string FileName, string? Error)
{
    public bool Ok => Error is null && Url.Length > 0;
}

/// <summary>
/// 谷歌网盘（drive.google.com）公开分享链接的下载客户端，无需登录、无需 API Key。
///
/// FANBOX 作者常把作品本体（多为压缩包）放在网盘上，投稿正文里只留一条分享链接，
/// pawchive 归档的附件就只剩一张封面图。见 <see cref="FanboxService"/> 的入队逻辑。
///
/// 取文件的路数是站点自己的「无 Cookie 下载」通道：
///   <c>drive.usercontent.google.com/download?id=…&amp;export=download&amp;confirm=t</c>
///   · 小文件 → 直接回文件本体，文件名在 Content-Disposition 里；
///   · 大文件 → 回一张「无法进行病毒扫描」的确认页，页内的表单给出真正的下载地址
///     （多一个 uuid 参数）与文件名，照着表单再请求一次即可。
/// 直链支持 Range，故断点续传/限速/暂停这些设施照常可用。
///
/// 文件夹没有公开列目录的接口，但站点给嵌入用的 <c>embeddedfolderview</c> 会吐出一张
/// 朴素的 HTML 列表（每项一个 <c>flip-entry</c>：链接 + 文件名），照它展开成逐个文件即可。
/// </summary>
public static class GoogleDriveClient
{
    /// <summary>该文件的下载配额已用尽（站点用整页文案提示，不是 HTTP 错误码）。</summary>
    public const string QuotaError = "driveQuota";

    /// <summary>文件不存在、已删除，或分享被取消。</summary>
    public const string GoneError = "driveGone";

    /// <summary>网络层就没请求成功（超时 / 连不上，多半是代理没配好）。</summary>
    public const string FetchError = "driveFetch";

    /// <summary>无 Cookie 下载通道；drive.google.com/uc 也只是 303 跳到这里，直接用它少一跳。</summary>
    private const string DownloadEndpoint = "https://drive.usercontent.google.com/download";

    /// <summary>展开一个共享文件夹时最多深入几层（防止互相嵌套的文件夹把入队拖垮）。</summary>
    private const int MaxFolderDepth = 3;

    /// <summary>展开一个共享文件夹时最多取多少个文件。</summary>
    private const int MaxFolderFiles = 300;

    /// <summary>确认页/错误页最多读这么多字节——正常都只有几 KB，读到上限即可判定不是网页。</summary>
    private const int MaxPageBytes = 512 * 1024;

    public static string FileUrl(string fileId) => $"https://drive.google.com/file/d/{fileId}/view";

    public static string FolderUrl(string folderId) => $"https://drive.google.com/drive/folders/{folderId}";

    // ---------- 从正文里认链接 ----------

    // 网盘项目号：谷歌用的是 base64url 字母表，长度不定（28~44 常见），这里只要够长就认
    private const string IdPattern = "[A-Za-z0-9_-]{10,}";

    private static readonly Regex FilePattern = new(
        $@"^https?://(?:drive|docs)\.google\.com/(?:file/d/|open\?(?:[^#]*&)?id=|uc\?(?:[^#]*&)?id=)({IdPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex UserContentPattern = new(
        $@"^https?://drive\.usercontent\.google\.com/(?:download|uc)\?(?:[^#]*&)?id=({IdPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FolderPattern = new(
        $@"^https?://drive\.google\.com/drive/(?:u/\d+/)?(?:mobile/)?folders/({IdPattern})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 把一条地址认成谷歌网盘链接；不是网盘链接返回 null。
    ///
    /// 各种写法（/file/d/、/open?id=、/uc?id=、usercontent 的 /download?id=、/drive/folders/）
    /// 都归一到 (类型, 项目号)，末尾的 ?usp=sharing、&amp;pcid= 之类的附加参数一概不影响判定。
    /// </summary>
    public static DriveLink? Parse(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();
        if (FolderPattern.Match(url) is { Success: true } folder)
            return new DriveLink(DriveLinkKind.Folder, folder.Groups[1].Value);
        if (FilePattern.Match(url) is { Success: true } file)
            return new DriveLink(DriveLinkKind.File, file.Groups[1].Value);
        if (UserContentPattern.Match(url) is { Success: true } uc)
            return new DriveLink(DriveLinkKind.File, uc.Groups[1].Value);
        return null;
    }

    /// <summary>从一串地址里挑出谷歌网盘链接，按出现顺序去重（同一项目的多种写法只留一条）。</summary>
    public static List<DriveLink> LinksIn(IEnumerable<string> urls)
    {
        var list = new List<DriveLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var url in urls)
            if (Parse(url) is { } link && seen.Add($"{link.Kind}:{link.Id}"))
                list.Add(link);
        return list;
    }

    // ---------- 共享文件夹 ----------

    // embeddedfolderview 的一项：<div class="flip-entry-info"><a href="…">…<div class="flip-entry-title">名字</div>
    private static readonly Regex FolderEntryPattern = new(
        @"class=""flip-entry-info""><a href=""([^""]+)"".*?class=""flip-entry-title"">([^<]*)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>
    /// 展开一个共享文件夹，返回其中所有文件（子文件夹会递归进去，只返回文件）。
    /// 站点没有公开的列目录接口，走的是给第三方嵌入用的 embeddedfolderview 页面。
    /// 取不到内容（文件夹被删/未公开分享）时返回空表。
    /// </summary>
    public static async Task<List<DriveEntry>> ListFolderFilesAsync(string folderId)
    {
        var files = new List<DriveEntry>();
        var visited = new HashSet<string>(StringComparer.Ordinal) { folderId };
        await WalkAsync(folderId, 1);
        return files;

        async Task WalkAsync(string id, int depth)
        {
            if (files.Count >= MaxFolderFiles)
                return;
            foreach (var entry in await ListFolderAsync(id))
            {
                if (files.Count >= MaxFolderFiles)
                {
                    Logger.Warning($"谷歌网盘文件夹 {folderId} 超过 {MaxFolderFiles} 个文件，只取前面这些");
                    return;
                }
                if (entry.Kind == DriveLinkKind.File)
                    files.Add(entry);
                else if (depth < MaxFolderDepth && visited.Add(entry.Id))
                    await WalkAsync(entry.Id, depth + 1);
            }
        }
    }

    /// <summary>列出一个共享文件夹的直接子项（不递归）。</summary>
    private static async Task<List<DriveEntry>> ListFolderAsync(string folderId)
    {
        var entries = new List<DriveEntry>();
        var html = await GetTextAsync($"https://drive.google.com/embeddedfolderview?id={folderId}#list");
        if (html is null)
            return entries;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in FolderEntryPattern.Matches(html))
        {
            if (Parse(WebUtility.HtmlDecode(m.Groups[1].Value)) is not { } link || !seen.Add(link.Id))
                continue;
            var name = WebUtility.HtmlDecode(m.Groups[2].Value).Trim();
            entries.Add(new DriveEntry(link.Kind, link.Id, name));
        }
        return entries;
    }

    // ---------- 单个文件 ----------

    /// <summary>供下载线程调用的同步版（下载引擎全程同步 IO，见其设计注释）。</summary>
    internal static DriveFileLink ResolveFile(string fileId) =>
        ResolveFileAsync(fileId).GetAwaiter().GetResult();

    /// <summary>
    /// 解析一个网盘文件的直链与文件名。
    ///
    /// 第一次请求就可能直接是文件本体（小文件），此时文件名在 Content-Disposition 里；
    /// 回的是「无法进行病毒扫描」确认页（大文件）时，按页内表单重建地址再来一次。
    /// 配额用尽/文件已失效都由页面文案判定——站点这两种情况都回 HTTP 200 的网页。
    /// </summary>
    public static async Task<DriveFileLink> ResolveFileAsync(string fileId)
    {
        var url = $"{DownloadEndpoint}?id={Uri.EscapeDataString(fileId)}&export=download&confirm=t";
        // 一轮请求 + 一轮按确认页表单重试；表单已给出文件名时第二轮都省了
        for (var attempt = 0; attempt < 2; attempt++)
        {
            HttpResponseMessage resp;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                resp = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
            {
                Logger.Error($"谷歌网盘请求失败: {e.Message} ({fileId})");
                return new DriveFileLink("", "", FetchError);
            }

            using (resp)
            {
                // 带 Content-Disposition 即为文件本体，这条地址就是直链
                if (FileNameOf(resp) is { Length: > 0 } name)
                    return new DriveFileLink(url, name, null);

                var html = await ReadTextAsync(resp);
                if (IsQuotaPage(html))
                    return new DriveFileLink("", "", QuotaError);
                if (attempt == 0 && TryReadConfirmForm(html, out var formUrl, out var formName))
                {
                    url = formUrl;
                    if (formName.Length > 0)
                        return new DriveFileLink(url, formName, null);
                    continue;   // 确认页没给文件名，照表单再请求一次由 Content-Disposition 取
                }
                if (!resp.IsSuccessStatusCode)
                    Logger.Error($"谷歌网盘响应 HTTP {(int)resp.StatusCode}: {fileId}");
                return new DriveFileLink("", "", GoneError);
            }
        }
        return new DriveFileLink("", "", GoneError);
    }

    /// <summary>确认页（「无法进行病毒扫描」）里的下载表单：拼出真正的下载地址，顺带取文件名。</summary>
    private static readonly Regex ConfirmFormPattern = new(
        @"<form[^>]+id=""download-form""[^>]+action=""([^""]+)""(.*?)</form>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HiddenInputPattern = new(
        @"<input[^>]+type=""hidden""[^>]+name=""([^""]+)""[^>]+value=""([^""]*)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 页面上的「文件名 (大小)」那一行：<span class="uc-name-size"><a href="…">名字</a> (189M)</span>
    private static readonly Regex NameSizePattern = new(
        @"class=""uc-name-size""><a[^>]*>([^<]*)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool TryReadConfirmForm(string html, out string url, out string fileName)
    {
        url = "";
        fileName = "";
        if (ConfirmFormPattern.Match(html) is not { Success: true } form)
            return false;
        var action = WebUtility.HtmlDecode(form.Groups[1].Value);
        var query = new List<string>();
        foreach (Match input in HiddenInputPattern.Matches(form.Groups[2].Value))
            query.Add($"{Uri.EscapeDataString(WebUtility.HtmlDecode(input.Groups[1].Value))}=" +
                      Uri.EscapeDataString(WebUtility.HtmlDecode(input.Groups[2].Value)));
        if (query.Count == 0)
            return false;
        url = action + (action.Contains('?') ? "&" : "?") + string.Join('&', query);
        if (NameSizePattern.Match(html) is { Success: true } nm)
            fileName = WebUtility.HtmlDecode(nm.Groups[1].Value).Trim();
        return true;
    }

    /// <summary>页面是否在说「该文件近期被下载太多次，暂时无法下载」（站点用整页文案提示）。</summary>
    private static bool IsQuotaPage(string html) =>
        html.Contains("Too many users have viewed or downloaded", StringComparison.OrdinalIgnoreCase) ||
        html.Contains("you can't view or download this file at this time",
            StringComparison.OrdinalIgnoreCase) ||
        html.Contains("download quota", StringComparison.OrdinalIgnoreCase);

    /// <summary>响应头里的文件名；不是文件本体（没有 Content-Disposition）时返回空串。</summary>
    private static string FileNameOf(HttpResponseMessage resp)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd is null)
            return "";
        if (cd.FileNameStar is { Length: > 0 } star)
            return star.Trim();                       // RFC 5987，.NET 已按其声明的编码解出
        var name = (cd.FileName ?? "").Trim().Trim('"');
        return name.Length > 0 ? FixHeaderEncoding(name) : "";
    }

    /// <summary>
    /// 修复响应头里的非 ASCII 文件名。
    ///
    /// 谷歌的 <c>filename="…"</c> 直接放 UTF-8 原字节，而 .NET 按 Latin-1 解响应头，
    /// 中日文名字于是成了乱码（如「实验室」→「å®éªå®¤」）。字符全在 Latin-1 范围内、
    /// 且按 UTF-8 重解能成立时才还原，纯 ASCII 与本来就解对了的名字都原样返回。
    /// </summary>
    private static string FixHeaderEncoding(string name)
    {
        if (name.All(char.IsAscii) || name.Any(c => c > 0xFF))
            return name;
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true)
                .GetString(Encoding.Latin1.GetBytes(name));
        }
        catch (Exception e) when (e is ArgumentException or DecoderFallbackException)
        {
            return name;   // 不是 UTF-8 字节，那就是本来如此
        }
    }

    // ---------- HTTP ----------

    // 一个文件夹可能几十个文件、逐个解析直链：客户端必须复用，否则新建 HttpClient 会把本机端口耗尽。
    // 谷歌多半要走代理，代理设置变了才重建。
    private static readonly object ClientLock = new();
    private static HttpClient? _client;
    private static string _clientKey = "";

    private static HttpClient Client()
    {
        var (proxyOn, proxy) = AppConfig.ReadProxy();
        var key = proxyOn ? proxy.Address?.ToString() ?? "proxy" : "";
        lock (ClientLock)
        {
            if (_client != null && _clientKey == key)
                return _client;
            _client?.Dispose();
            _client = Http.CreateClient(TimeSpan.FromSeconds(60));
            _clientKey = key;
            return _client;
        }
    }

    private static async Task<string?> GetTextAsync(string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
            {
                Logger.Error($"谷歌网盘请求失败 HTTP {(int)resp.StatusCode}: {url}");
                return null;
            }
            return await ReadTextAsync(resp);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Logger.Error($"谷歌网盘网络请求失败: {e.Message} ({url})");
            return null;
        }
    }

    /// <summary>读响应正文，最多 <see cref="MaxPageBytes"/> 字节——超出说明这是文件本体而非网页。</summary>
    private static async Task<string> ReadTextAsync(HttpResponseMessage resp)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while (buffer.Length < MaxPageBytes &&
               (read = await stream.ReadAsync(chunk.AsMemory())) > 0)
            buffer.Write(chunk, 0, read);
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
