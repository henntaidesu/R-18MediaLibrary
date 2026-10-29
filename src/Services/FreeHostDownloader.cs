using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>网盘免费下载解析出的直链，连同取它时浏览器用的 cookie / UA / Referer（下载时原样带上）。</summary>
public sealed record FreeLink(string Url, string FileName, string Cookie, string UserAgent, string Referer);

/// <summary>
/// 一次免费解析的结果：拿到直链（Link），或要等到 RetryAt 再试（网盘的免费间隔 / 验证没过），
/// 或彻底失败（Error，记进 download_list.error，等用户「全部重新解析」）。
/// </summary>
public readonly record struct FreeResult(FreeLink? Link, DateTime? RetryAt, string? Error, string Message);

/// <summary>
/// 网盘免费下载：debrid-link 没配置 API Key、Key 失效、或会员到期（免费账户解析不了这些网盘）时，
/// 论坛源（Anime-sharing）的 rapidgator / katfile 链接改走网盘自己的免费下载通道。
///
/// 免费通道有两道坎，都决定了这里的写法：
/// 1. <b>人机验证</b>：katfile 的免费下载页是 Cloudflare Turnstile，rapidgator 是 30 秒倒计时后再过一道验证页，
///    直接发 HTTP 请求过不去。所以在一个不显示的 WebView2（与「在服务器上登录」共用浏览器环境，资料固定为
///    <see cref="Profile"/>，站点的通行 cookie 留着下次用）里打开文件页，由 <see cref="Autopilot"/> 脚本
///    每秒看一眼页面、替人点「免费下载」、等倒计时、验证通过后提交表单，直到页面给出直链；
///    浏览器自己要开始下载时（<c>DownloadStarting</c>）也截下地址。直链交回下载线程，用同一代理、同一 UA 与 cookie 下载。
/// 2. <b>下载间隔</b>：免费账户每个网盘「每 120 分钟 1 个文件」、同时只能下 1 个。故每个网盘一个预约位
///    （<see cref="TryReserve"/>，同一时刻只放一个分卷）与一个「下次可下」时间：拿到直链即顺延
///    <see cref="AppConfig.FreeHostInterval"/> 分钟；页面明说还要等多久的以页面为准。时间存在 conf 的
///    free_host 段，重启后照样等，不会一启动就去撞限额。
///
/// 只认 rapidgator / katfile；其它网盘在免费模式下直接标 <see cref="UnsupportedError"/>。
/// debrid-link 恢复可用（续费 / 换了 Key）后最多 <see cref="RecheckEvery"/> 就自动切回中转。
/// </summary>
public static class FreeHostDownloader
{
    public const string UnsupportedError = "freeUnsupported";
    public const string PremiumOnlyError = "freePremiumOnly";
    public const string CaptchaError = "freeCaptcha";
    public const string BrowserError = "freeBrowser";
    public const string FetchError = "freeFetch";

    /// <summary>浏览器资料名：所有免费下载共用一份，网盘与 Cloudflare 的通行 cookie 留在里面。</summary>
    private const string Profile = "freehost";

    private sealed record HostDef(string Key, string Label, Regex Pattern);

    private static readonly HostDef[] Hosts =
    [
        new("rapidgator", "Rapidgator", new(@"(^|\.)(rapidgator\.net|rg\.to)$", RegexOptions.Compiled)),
        // katfile 换过好几个后缀（.com 跳 .co，论坛里还有 .biz / .cloud …），后缀一律放行
        new("katfile", "Katfile", new(@"(^|\.)katfile\.[a-z]+$", RegexOptions.Compiled)),
    ];

    /// <summary>链接属于哪个支持免费下载的网盘；不支持返回 null。</summary>
    public static string? HostKey(string url)
    {
        string host;
        try { host = new Uri(url).Host.ToLowerInvariant(); }
        catch (UriFormatException) { return null; }
        return Hosts.FirstOrDefault(h => h.Pattern.IsMatch(host))?.Key;
    }

    private static string LabelOf(string hostKey) => Hosts.FirstOrDefault(h => h.Key == hostKey)?.Label ?? hostKey;

    // ---------- debrid-link 还能不能用 ----------

    /// <summary>可用性检查的缓存时长：续费后最多这么久切回中转。</summary>
    private static readonly TimeSpan RecheckEvery = TimeSpan.FromMinutes(10);

    private static readonly object DebridLock = new();
    private static bool? _debridUsable;
    private static DateTime _checkedAt = DateTime.MinValue;
    private static string _checkedKey = "";

    /// <summary>
    /// debrid-link 中转当前能不能用（下载线程上同步调用，最多每 <see cref="RecheckEvery"/> 问一次 account/infos）：
    /// 没 Key → 不能；Key 失效（badToken）→ 不能；免费账户 / 会员到期（accountType 0 且 premiumLeft 0）→ 不能。
    /// 连不上 debrid-link 不算「不能用」，沿用上次的结论，一分钟后再问。
    /// </summary>
    public static bool DebridUsable
    {
        get
        {
            var key = AppConfig.DebridApiKey;
            if (key.Length == 0)
                return false;
            lock (DebridLock)
            {
                if (_debridUsable is { } cached && key == _checkedKey && DateTime.UtcNow - _checkedAt < RecheckEvery)
                    return cached;
                var usable = ProbeDebrid(key);
                if (usable is null)
                {
                    // 连不上：沿用上次结论（没有就当能用，照旧走中转），一分钟后再问
                    _checkedAt = DateTime.UtcNow - RecheckEvery + TimeSpan.FromMinutes(1);
                    return _debridUsable ?? true;
                }
                if (usable != _debridUsable)
                    Logger.Info(usable.Value
                        ? "debrid-link 会员有效，论坛源走中转解析"
                        : "debrid-link 不可用（Key 无效或会员已到期），论坛源改走网盘免费下载");
                _debridUsable = usable;
                _checkedKey = key;
                _checkedAt = DateTime.UtcNow;
                return usable.Value;
            }
        }
    }

    /// <summary>问 account/infos：能用 true、不能用 false、没问到 null。</summary>
    private static bool? ProbeDebrid(string key)
    {
        try
        {
            using var client = new DebridLinkClient(key);
            var (value, error) = client.AccountInfosDetailedAsync().GetAwaiter().GetResult();
            if (error != null)
                return IsDebridUnusableError(error) ? false : null;
            if (value is not { ValueKind: JsonValueKind.Object } v)
                return null;
            var premiumLeft = v.TryGetProperty("premiumLeft", out var p) && p.TryGetInt64(out var left) ? left : 0;
            var accountType = v.TryGetProperty("accountType", out var t) && t.TryGetInt32(out var type) ? type : 0;
            return premiumLeft > 0 || accountType > 0;
        }
        catch (Exception e)
        {
            Logger.Warning($"查询 debrid-link 账户状态失败：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// debrid-link 解析时返回的这些错误码说明中转本身用不了（Key 失效 / 免费账户解析不了该网盘），
    /// 而不是这个链接有问题：换走免费下载即可。
    /// </summary>
    public static bool IsDebridUnusableError(string? error) => error != null && DebridUnusableErrors.Contains(error);

    internal static readonly string[] DebridUnusableErrors =
        ["badToken", "hidedToken", "badSign", "notFreeHost", "hostNotFree", "accountLocked", "notPremium", "premiumOnly"];

    /// <summary>解析时撞到 <see cref="IsDebridUnusableError"/>：直接判为不可用，不等下一次定期检查。</summary>
    public static void MarkDebridUnusable(string? error)
    {
        lock (DebridLock)
        {
            if (_debridUsable != false)
                Logger.Warning($"debrid-link 返回 {error}，论坛源改走网盘免费下载");
            _debridUsable = false;
            _checkedKey = AppConfig.DebridApiKey;
            _checkedAt = DateTime.UtcNow;
        }
    }

    /// <summary>论坛源现在该不该走免费下载（会按需问一次 debrid-link，只在下载线程上调用）。</summary>
    public static bool Active => AppConfig.FreeHostEnabled && !DebridUsable;

    /// <summary>同上，但只看缓存、从不联网：界面每秒刷新时用。</summary>
    private static bool ActiveCached =>
        AppConfig.FreeHostEnabled && (AppConfig.DebridApiKey.Length == 0 || _debridUsable == false);

    // ---------- 每个网盘的预约位与下载间隔 ----------

    private static readonly object HostLock = new();
    private static readonly HashSet<string> Busy = [];
    private static Dictionary<string, DateTime>? _nextAllowed;   // 网盘 → 下次可下的 UTC 时间（懒加载自 conf）

    private static Dictionary<string, DateTime> NextAllowed
    {
        get
        {
            if (_nextAllowed != null)
                return _nextAllowed;
            var map = new Dictionary<string, DateTime>();
            foreach (var h in Hosts)
                if (DateTime.TryParse(AppConfig.Read("free_host", "next_" + h.Key), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var t))
                    map[h.Key] = t.ToUniversalTime();
            return _nextAllowed = map;
        }
    }

    /// <summary>
    /// 领取时调用（DownloadEngine.ClaimNext 的锁内）：这条能不能现在下。不支持的网盘返回 true、hostKey 为空串
    /// （领走后由下载线程标 <see cref="UnsupportedError"/>，免得它一直挂在队列里）；支持的网盘要「没人在下、间隔已过」
    /// 才返回 true，并占住该网盘的预约位，用完必须 <see cref="Release"/>。
    /// </summary>
    internal static bool TryReserve(string url, out string hostKey)
    {
        hostKey = HostKey(url) ?? "";
        if (hostKey.Length == 0)
            return true;
        lock (HostLock)
        {
            if (Busy.Contains(hostKey))
                return false;
            if (NextAllowed.TryGetValue(hostKey, out var next) && next > DateTime.UtcNow)
                return false;
            Busy.Add(hostKey);
            return true;
        }
    }

    internal static void Release(string hostKey)
    {
        if (hostKey.Length == 0)
            return;
        lock (HostLock)
            Busy.Remove(hostKey);
    }

    /// <summary>该网盘到 until 之前不再发起免费下载（持久化，重启后照样等）。</summary>
    internal static void SetCooldown(string hostKey, DateTime untilUtc)
    {
        if (hostKey.Length == 0)
            return;
        lock (HostLock)
            NextAllowed[hostKey] = untilUtc;
        AppConfig.Write("free_host", "next_" + hostKey, untilUtc.ToString("o", CultureInfo.InvariantCulture));
        Logger.Info($"{LabelOf(hostKey)} 免费下载：下一个文件 {untilUtc.ToLocalTime():MM-dd HH:mm} 后开始");
    }

    /// <summary>拿到直链、开始下载：按设置的间隔顺延该网盘的下一次。</summary>
    internal static void MarkStarted(string hostKey) =>
        SetCooldown(hostKey, DateTime.UtcNow.AddMinutes(AppConfig.FreeHostInterval));

    /// <summary>
    /// 免费模式下这条链接还要等到什么时候（本地时间）；不在免费模式、网盘不支持、或已可下载返回 null。
    /// 两端下载列表把「等待下载」显示成「免费间隔 HH:mm」，免得人以为卡住了。不联网，可每秒调用。
    /// </summary>
    public static DateTime? WaitingUntil(string url)
    {
        if (!ActiveCached || HostKey(url) is not { } host)
            return null;
        lock (HostLock)
            return NextAllowed.TryGetValue(host, out var next) && next > DateTime.UtcNow ? next.ToLocalTime() : null;
    }

    // 验证没过等「可以再试」的失败次数（按分卷）：超过就停下标失败，不无限地每 10 分钟开一次浏览器
    private static readonly ConcurrentDictionary<string, int> SoftFailures = new();
    private const int MaxSoftFailures = 3;

    /// <summary>记一次可重试的失败；超过上限返回 true（该放弃了）。</summary>
    internal static bool GiveUp(string rowKey)
    {
        var n = SoftFailures.AddOrUpdate(rowKey, 1, (_, c) => c + 1);
        if (n <= MaxSoftFailures)
            return false;
        SoftFailures.TryRemove(rowKey, out _);
        return true;
    }

    internal static void ClearFailures(string rowKey) => SoftFailures.TryRemove(rowKey, out _);

    // ---------- 浏览器里走一遍免费下载 ----------

    // 同一时刻只开一个浏览器跑免费流程（各网盘本来也只能一个一个下）
    private static readonly SemaphoreSlim BrowserGate = new(1, 1);

    /// <summary>整个免费流程（倒计时 + 验证 + 提交）的上限；rapidgator 的倒计时就要 30 秒。</summary>
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromMinutes(4);

    /// <summary>验证没过 / 页面没走通时，隔多久再试这个网盘。</summary>
    private static readonly TimeSpan SoftRetryDelay = TimeSpan.FromMinutes(10);

    /// <summary>页面说「到限额了」却没说还要等多久时，隔多久再看一次。</summary>
    private static readonly TimeSpan UnknownLimitDelay = TimeSpan.FromMinutes(30);

    /// <summary>在不显示的浏览器里走一遍该链接的免费下载流程，换出直链（下载线程上同步调用，会阻塞到流程结束）。</summary>
    public static FreeResult Resolve(string url)
    {
        if (Application.Current is not { } app)
            return new(null, null, BrowserError, "程序界面未就绪");
        if (LoginBrowser.CheckAvailable() is { } why)
            return new(null, null, BrowserError, why);
        BrowserGate.Wait();
        try
        {
            return app.Dispatcher.InvokeAsync(() => ResolveAsync(url)).Task.Unwrap().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Logger.Error(e, "网盘免费下载");
            return new(null, DateTime.UtcNow + SoftRetryDelay, FetchError, e.Message);
        }
        finally
        {
            BrowserGate.Release();
        }
    }

    /// <summary>UI 线程上：开浏览器、每秒跑一次 <see cref="Autopilot"/>，直到拿到直链或有了结论。</summary>
    private static async Task<FreeResult> ResolveAsync(string url)
    {
        CoreWebView2Controller? controller = null;
        try
        {
            controller = await LoginBrowser.HiddenControllerAsync(Profile);
            // 父窗口从不显示，这里设成「可见」只是让页面当自己在前台：Turnstile 与倒计时在隐藏页面里会停或变慢
            controller.Bounds = new System.Drawing.Rectangle(0, 0, 1280, 900);
            controller.IsVisible = true;
            var core = controller.CoreWebView2;
            core.Settings.AreDefaultScriptDialogsEnabled = false;

            var download = new TaskCompletionSource<(string Uri, string Name)>();
            string? dialog = null;
            // 下载页点按钮会顺带 window.open 广告：一律吞掉，不开新窗口
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // rapidgator 出错（如免费额度用完）用 alert 提示，内容拿来判断要等多久
            core.ScriptDialogOpening += (_, e) =>
            {
                dialog = e.Message;
                e.Accept();
            };
            // 跳转到直链时浏览器会自己开始下载：截下地址交给下载线程，浏览器这边不下
            core.DownloadStarting += (_, e) =>
            {
                e.Cancel = true;
                download.TrySetResult((e.DownloadOperation.Uri, Path.GetFileName(e.ResultFilePath ?? "")));
            };

            core.Navigate(url);
            var deadline = DateTime.UtcNow + ResolveTimeout;
            var lastStep = "";
            var restarts = 0;
            var fname = "";
            while (DateTime.UtcNow < deadline)
            {
                await Task.WhenAny(download.Task, Task.Delay(1000));
                if (download.Task.IsCompleted)
                    break;

                if (dialog is { } message)
                {
                    dialog = null;
                    Logger.Warning($"网盘免费下载页面提示：{message}");
                    if (WaitFromText(message) is { } wait)
                        return new(null, DateTime.UtcNow + wait, null, message);
                    if (LimitText.IsMatch(message))
                        return new(null, DateTime.UtcNow + UnknownLimitDelay, null, message);
                }

                JsonElement state;
                try
                {
                    using var doc = JsonDocument.Parse(await core.ExecuteScriptAsync(Autopilot));
                    state = doc.RootElement.Clone();
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                    continue;   // 页面正在跳转
                }
                if (state.ValueKind != JsonValueKind.Object)
                    continue;
                var step = Str(state, "step");
                if (Str(state, "fname") is { Length: > 0 } f)
                    fname = f;
                if (step != lastStep)
                {
                    Logger.Info($"网盘免费下载 {url}：{step}");
                    lastStep = step;
                }
                switch (step)
                {
                    case "dead":
                        return new(null, null, "fileNotFound", "文件已失效");
                    case "premium":
                        return new(null, null, PremiumOnlyError, "该文件只能会员下载（rapidgator 免费账户只能下 500MB 以内的文件）");
                    case "wait":
                        var seconds = state.TryGetProperty("secs", out var s) && s.TryGetInt32(out var n) ? n : 0;
                        var waitFor = seconds > 0 ? TimeSpan.FromSeconds(seconds + 60) : UnknownLimitDelay;
                        return new(null, DateTime.UtcNow + waitFor, null, Str(state, "msg"));
                    case "limit":
                        return new(null, DateTime.UtcNow + UnknownLimitDelay, null, Str(state, "msg"));
                    case "retry":
                        // XFS 的「Skipped countdown / Wrong captcha」：从文件页重新来一遍
                        if (++restarts > 3)
                            return new(null, DateTime.UtcNow + SoftRetryDelay, FetchError, "页面多次要求重来");
                        core.Navigate(url);
                        break;
                    case "link":
                        download.TrySetResult((Str(state, "link"), ""));
                        break;
                }
            }

            if (!download.Task.IsCompleted)
            {
                // 页面没走通：把停在哪一页、页面上写了什么记下来，网盘改版时据此调整 Autopilot
                try
                {
                    var body = JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                        "((document.body && document.body.innerText) || '').replace(/\\s+/g, ' ').slice(0, 400)")) ?? "";
                    Logger.Warning($"网盘免费下载停在 {core.Source}：{body}");
                }
                catch (Exception) { /* 只是日志 */ }
                return new(null, DateTime.UtcNow + SoftRetryDelay, CaptchaError,
                    $"{ResolveTimeout.TotalMinutes:F0} 分钟内没有走完免费下载流程（停在 {lastStep}，多半是人机验证没有自动通过）");
            }

            var (direct, suggested) = download.Task.Result;
            var cookies = await core.CookieManager.GetCookiesAsync(direct);
            var cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            var name = FirstName(suggested, NameFromUrl(direct), fname, NameFromUrl(url));
            return new(new FreeLink(direct, name, cookie, core.Settings.UserAgent, core.Source), null, null, "");
        }
        catch (Exception e)
        {
            Logger.Warning($"网盘免费下载浏览器出错：{e.Message}");
            return new(null, DateTime.UtcNow + SoftRetryDelay, FetchError, e.Message);
        }
        finally
        {
            controller?.Close();
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static readonly Regex LimitText = new(
        @"delay between downloads|download(?:s)?[- ]limit|not more than 1 file at a time|reached (?:your|the) .*limit",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DurationPart = new(
        @"(\d+)\s*(days?|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从「You have to wait 1 hour, 20 minutes till next download」这类提示里读出要等多久；读不到返回 null。</summary>
    private static TimeSpan? WaitFromText(string text)
    {
        if (!text.Contains("wait", StringComparison.OrdinalIgnoreCase))
            return null;
        double seconds = 0;
        foreach (Match m in DurationPart.Matches(text))
        {
            var unit = m.Groups[2].Value.ToLowerInvariant();
            seconds += int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * (unit[0] switch
            {
                'd' => 86400, 'h' => 3600, 'm' => 60, _ => 1,
            });
        }
        return seconds > 0 ? TimeSpan.FromSeconds(seconds + 60) : null;
    }

    /// <summary>链接路径的最后一段当文件名（去掉 katfile / rapidgator 文件页的 .html 后缀）。</summary>
    private static string NameFromUrl(string url)
    {
        try
        {
            var last = Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimEnd('/').Split('/')[^1]);
            return last.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ? last[..^5] : last;
        }
        catch (UriFormatException)
        {
            return "";
        }
    }

    /// <summary>取第一个像文件名（带扩展名）的候选，并去掉 Windows 文件名里不能有的字符。</summary>
    private static string FirstName(params string[] candidates)
    {
        var name = candidates.FirstOrDefault(c => c.Length > 0 && Path.HasExtension(c))
                   ?? candidates.FirstOrDefault(c => c.Length > 0) ?? "download.bin";
        foreach (var ch in Path.GetInvalidFileNameChars())
            name = name.Replace(ch, '_');
        return name;
    }

    /// <summary>
    /// 每秒注入一次的「自动驾驶」脚本：看当前页面处在免费下载的哪一步，能往下走就替人点一下，返回
    /// {step, ...}：dead 文件失效 / premium 只能会员下 / wait 要等 secs 秒 / limit 到限额（没说等多久）/
    /// link 拿到直链 / retry 要从头来 / 其余为过程中的状态（只用于日志）。
    /// 页面跳转后 window 上的 __rml* 标记随之清空，所以「每页只点一次」不会把下一页挡住。
    /// 已对着 2026-10 的页面核过：katfile 是 XFS（op=download1 → op=download2 + Turnstile → 直链），
    /// 限额以 #delay-box 里的 .kfa-delay / .kfa-bw / .kfa-size 卡片显示；rapidgator 是 .btn-free → 30 秒倒计时
    /// → /download/captcha 验证页 → download_link。
    /// </summary>
    private const string Autopilot = """
        (() => {
          const W = window, D = document, r = { step: 'loading' };
          if (!D.body) return r;
          const text = D.body.innerText || '';
          const vis = e => !!e && !!(e.offsetWidth || e.offsetHeight || e.getClientRects().length);
          const submit = f => { W.__rmlSubmitted = true; HTMLFormElement.prototype.submit.call(f); };
          const fnameInput = D.querySelector('input[name=fname]');
          if (fnameInput && fnameInput.value) r.fname = fnameInput.value;
          const op1 = D.querySelector('form input[name=op][value=download1]');
          const op2 = D.querySelector('form input[name=op][value=download2]');
          const free = D.querySelector('.btn-free');
          const token = D.querySelector('[name="cf-turnstile-response"],[name="g-recaptcha-response"],[name="h-captcha-response"]');

          // 直链：rapidgator 验证通过后页面上的 download_link；XFS 的结果页是一个 /d/<码>/<文件名> 的链接
          if (typeof W.download_link === 'string' && /^https?:\/\//.test(W.download_link)) { r.step = 'link'; r.link = W.download_link; return r; }
          for (const a of D.querySelectorAll('a[href]')) {
            const h = a.href;
            if (/^https?:\/\/[^\/]+(:\d+)?\/d\/[A-Za-z0-9]+\/[^?#\/]+$/.test(h)) { r.step = 'link'; r.link = h; return r; }
          }

          // katfile 的限额卡片
          const box = D.getElementById('delay-box');
          if (box && box.querySelector('.kfa-size')) { r.step = 'premium'; return r; }
          if (box && box.querySelector('.kfa-delay')) {
            r.step = 'wait'; r.msg = box.innerText;
            const t = (D.getElementById('kfaTimer') || {}).textContent || '';
            const p = t.split(':').map(x => parseInt(x, 10));
            if (p.length >= 2 && p.every(x => !isNaN(x))) r.secs = p.reduce((s, x) => s * 60 + x, 0);
            return r;
          }
          if (box && box.querySelector('.kfa-bw')) { r.step = 'limit'; r.msg = box.innerText; return r; }

          // 通用提示语（XFS 的错误页、rapidgator 的限额页）
          if (!op1 && !op2 && !free && /file (?:not found|has been removed|was deleted|is no longer available)|404 file|file does not exist/i.test(text)) { r.step = 'dead'; return r; }
          // 必须跟着 till/until next download 之类：rapidgator 的正常文件页上就写着「Wait 30 sec.」（免费倒计时）
          const wait = text.match(/wait\s+((?:\d+\s*(?:days?|hours?|hrs?|minutes?|mins?|seconds?|secs?)\b[,\s]*(?:and\s+)?)+)\s*(?:till|until|before|for (?:the )?next)/i);
          if (wait && !token) {
            r.step = 'wait'; r.msg = wait[0];
            let secs = 0; const re = /(\d+)\s*(d|h|m|s)/gi; let m;
            while ((m = re.exec(wait[1])) !== null) secs += parseInt(m[1], 10) * ({ d: 86400, h: 3600, m: 60, s: 1 })[m[2].toLowerCase()];
            r.secs = secs; return r;
          }
          const lim = text.match(/[^.\n]*(?:delay between downloads|reached (?:your|the) (?:hourly |daily )?downloads? limit|not more than 1 file at a time)[^.\n]*/i);
          if (lim) { r.step = 'limit'; r.msg = lim[0].trim(); return r; }
          if (/skipped countdown|wrong captcha|expired session/i.test(text) && !token) { r.step = 'retry'; return r; }
          if (/premium only|only for premium|for premium users only|premium members only/i.test(text) && !token && !op2) { r.step = 'premium'; return r; }

          // 倒计时没走完就等着（XFS 的 #countdown、rapidgator 的 .download-timer-seconds）
          const cd = D.querySelector('#countdown .seconds, .download-timer-seconds');
          if (cd && vis(cd) && parseInt(cd.textContent, 10) > 0) { r.step = 'countdown'; return r; }

          // 有人机验证：等它自己通过（Turnstile 在正常浏览器里多半不用点），有了令牌就提交所在表单
          if (token) {
            if (token.value && !W.__rmlSubmitted) {
              const f = token.form || token.closest('form') || D.getElementById('_mform');
              if (f) { submit(f); r.step = 'submit'; return r; }
            }
            r.step = 'captcha'; return r;
          }
          // XFS 没有验证码的 download2 页：倒计时过了直接提交（不点按钮，按钮会先弹广告）
          if (op2 && !W.__rmlSubmitted) { submit(op2.form); r.step = 'submit'; return r; }
          // XFS 文件首页：选免费下载
          if (op1 && !W.__rmlSubmitted) {
            const f = op1.form;
            if (!f.querySelector('[name=method_free]')) {
              const i = D.createElement('input'); i.type = 'hidden'; i.name = 'method_free'; i.value = 'Free Download'; f.appendChild(i);
            }
            submit(f); r.step = 'free'; return r;
          }
          // rapidgator 文件页：点「免费下载」开始 30 秒倒计时
          if (free && vis(free) && !W.__rmlClicked && W.startTimerUrl !== '') { W.__rmlClicked = true; free.click(); r.step = 'start'; return r; }
          if (W.__rmlClicked) { r.step = 'timer'; return r; }
          // rapidgator 验证通过后的页面：直链写在脚本里（location.href = '...'），点「下载」按钮才跳过去。
          // 先从源码里找下载服务器（pr-xx.rapidgator.net 之类的子域）上的 /download/ 地址；
          // 找不到就点一次下载按钮，跳转时由 DownloadStarting 截下
          const html = D.documentElement.innerHTML;
          const rg = html.match(/https?:\/\/(?!(?:www\.)?rapidgator\.net)[a-z0-9-]+\.rapidgator\.net(?::\d+)?\/download\/[^'"\s<>]+/i);
          if (rg) { r.step = 'link'; r.link = rg[0].replace(/\\\//g, '/'); return r; }
          const btn = D.querySelector('.btn-download, a.download-btn, #download-btn');
          if (btn && vis(btn) && !W.__rmlDl) { W.__rmlDl = true; btn.click(); r.step = 'click'; return r; }
          r.step = 'page';
          return r;
        })()
        """;
}
