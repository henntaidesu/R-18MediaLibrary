using System;
using System.Net;
using System.Net.Http;

namespace R18MediaLibrary.Core;

/// <summary>HttpClient 工厂：按当前代理设置创建客户端（对应 Python 各模块的 requests.Session）。</summary>
public static class Http
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    /// <summary>
    /// 创建客户端。userAgent 非空时覆盖默认的 Chrome UA——个别站点的防护网关
    /// （如 pawchive 前置的 DDoS-Guard）会专门拦截"浏览器 UA 但没有浏览器握手"的请求，
    /// 这类站点反而要用中性 UA 才放行，见 <see cref="Services.PawchiveApi.UserAgent"/>。
    /// cookies 非空时改用给定的 cookie 罐：需要跨请求收集 Set-Cookie 的流程（如 E-Hentai 的
    /// 账号登录：论坛下发 ipb_* 后还要带着它们去里站领 igneous）必须共用同一个罐。
    /// </summary>
    public static HttpClient CreateClient(
        TimeSpan? timeout = null, string? userAgent = null, CookieContainer? cookies = null)
    {
        var (enabled, proxy) = AppConfig.ReadProxy();
        // 像浏览器一样自动协商并解压 gzip/deflate/br，否则 Cloudflare 等可能返回压缩内容导致读到乱码
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
        };
        if (cookies != null)
            handler.CookieContainer = cookies;
        if (enabled)
        {
            handler.Proxy = proxy;
            handler.UseProxy = true;
        }
        var client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(userAgent ?? UserAgent);
        return client;
    }
}
