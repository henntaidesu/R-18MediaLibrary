using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>debrid-link.com 下载中转站 API 客户端（对应 Python 版 DebridLink 类）。</summary>
public class DebridLinkClient : IDisposable
{
    private const string ApiBase = "https://debrid-link.fr/api/v2";

    private readonly HttpClient _client;

    // API 不可用（无 key / 网络异常）时的兜底域名列表
    private static readonly string[] FallbackDomains =
    [
        "katfile.com", "rapidgator.net", "rg.to", "mexa.sh", "mexashare.com",
        "fikper.com", "ddownload.com", "1fichier.com", "turbobit.net",
        "nitroflare.com", "hitfile.net", "uploady.io", "filefactory.com",
        "mega.nz", "filespayout.com", "filepv.com",
    ];

    private static List<string>? _domainCache;
    private static readonly object DomainLock = new();

    /// <summary>apiKey 为 null 时取配置中的 key；设置页"测试"按钮可传入临时 key。</summary>
    public DebridLinkClient(string? apiKey = null)
    {
        _client = Http.CreateClient(TimeSpan.FromSeconds(30));
        var key = apiKey ?? AppConfig.DebridApiKey;
        if (!string.IsNullOrEmpty(key))
            _client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
    }

    private async Task<JsonElement?> RequestAsync(
        HttpMethod method, string path, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, $"{ApiBase}/{path}");
        if (form != null)
            request.Content = new FormUrlEncodedContent(form);
        using var response = await _client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
            Logger.Error($"debrid-link API 错误: {path} -> {error}");
            return null;
        }
        return root.TryGetProperty("value", out var value) ? value.Clone() : null;
    }

    public Task<JsonElement?> AccountInfosAsync() => RequestAsync(HttpMethod.Get, "account/infos");

    /// <summary>下载流量使用情况：usagePercent / nextResetSeconds 等。</summary>
    public Task<JsonElement?> DownloadLimitsAsync() => RequestAsync(HttpMethod.Get, "downloader/limits");

    /// <summary>debrid-link 支持解析的网盘域名列表。</summary>
    public async Task<List<string>> GetDomainsAsync()
    {
        var value = await RequestAsync(HttpMethod.Get, "downloader/domains");
        var domains = new List<string>();
        if (value is { } v)
        {
            if (v.ValueKind == JsonValueKind.Object)
                foreach (var item in v.EnumerateObject())
                {
                    if (item.Value.ValueKind == JsonValueKind.Array)
                        foreach (var d in item.Value.EnumerateArray())
                            domains.Add(d.ToString());
                    else
                        domains.Add(item.Value.ToString());
                }
            else if (v.ValueKind == JsonValueKind.Array)
                foreach (var d in v.EnumerateArray())
                    domains.Add(d.ToString());
        }
        return domains;
    }

    /// <summary>提交网盘链接，返回包含 downloadUrl / name / size 的 JSON。</summary>
    public Task<JsonElement?> AddDownloadAsync(string url, string? password = null)
    {
        var form = new Dictionary<string, string> { ["url"] = url };
        if (!string.IsNullOrEmpty(password))
            form["password"] = password;
        return RequestAsync(HttpMethod.Post, "downloader/add", form);
    }

    /// <summary>提交网盘链接并返回 (结果, 错误码)：解析失败时 error 为 debrid-link 返回的错误码。</summary>
    public async Task<(JsonElement? Value, string? Error)> AddDownloadDetailedAsync(
        string url, string? password = null, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["url"] = url };
        if (!string.IsNullOrEmpty(password))
            form["password"] = password;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/downloader/add")
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
            Logger.Error($"debrid-link API 错误: downloader/add -> {error}");
            return (null, error);
        }
        return (root.TryGetProperty("value", out var value) ? value.Clone() : null, null);
    }

    /// <summary>
    /// 账户信息并带回错误码（Key 失效时为 badToken）。value 里 accountType 0 = 免费账户、premiumLeft = 会员剩余秒数，
    /// 会员到期后两者都是 0——据此判断还能不能走中转（见 <see cref="FreeHostDownloader.DebridUsable"/>）。
    /// </summary>
    public async Task<(JsonElement? Value, string? Error)> AccountInfosDetailedAsync(CancellationToken ct = default)
    {
        using var response = await _client.GetAsync($"{ApiBase}/account/infos", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("success", out var success) || !success.GetBoolean())
            return (null, root.TryGetProperty("error", out var e) ? e.ToString() : "unknown");
        return (root.TryGetProperty("value", out var value) ? value.Clone() : null, null);
    }

    /// <summary>debrid-link 支持的域名列表（每次运行只请求一次 API，失败用兜底列表）。</summary>
    public static async Task<List<string>> SupportedDomainsAsync()
    {
        if (_domainCache != null)
            return _domainCache;
        List<string> domains = [];
        try
        {
            using var client = new DebridLinkClient();
            domains = await client.GetDomainsAsync();
        }
        catch (Exception e)
        {
            Logger.Error(e, "supported_domains");
        }
        lock (DomainLock)
        {
            _domainCache ??= domains.Count > 0
                ? domains.ConvertAll(d => d.ToLowerInvariant())
                : [.. FallbackDomains];
            return _domainCache;
        }
    }

    /// <summary>链接所属域名是否被 debrid-link 支持。</summary>
    public static bool IsSupported(string url, List<string> domains)
    {
        string host;
        try
        {
            host = new Uri(url).Host.ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            return false;
        }
        if (host.Length == 0)
            return false;
        if (host.StartsWith("www."))
            host = host[4..];
        foreach (var domain in domains)
            if (host == domain || host.EndsWith("." + domain))
                return true;
        return false;
    }

    public void Dispose() => _client.Dispose();
}
