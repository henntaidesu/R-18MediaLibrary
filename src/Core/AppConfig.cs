using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace R18MediaLibrary.Core;

/// <summary>一个媒体库：名称 + 文件夹列表 + 进库默认显示的视图。</summary>
public class MediaLib
{
    public const string ViewWorks = "works";
    public const string ViewMakers = "makers";

    public string Name { get; set; } = "";
    public List<string> Folders { get; set; } = [];

    /// <summary>点进这个媒体库时先显示什么：<see cref="ViewWorks"/>（平铺作品，缺省）或 <see cref="ViewMakers"/>（社团卡片）。</summary>
    public string View { get; set; } = ViewWorks;
}

/// <summary>
/// 配置读写（对应 Python 版 conf_operate.py），数据保存在 SQLite 的 conf 表中（section / key / value）。
/// 进程级缓存一次加载，section/key 全部小写；Reload() 丢弃缓存重新读库。
/// </summary>
public static class AppConfig
{
    // 默认配置：首次运行（conf 表中无对应项）时写入
    private static readonly Dictionary<string, Dictionary<string, string>> Defaults = new()
    {
        ["processes"] = new() { ["processes"] = "5" },
        ["downpath"] = new() { ["downpath"] = "" },
        ["debrid"] = new() { ["api_key"] = "" },
        ["proxy"] = new() { ["openproxy"] = "False", ["host"] = "127.0.0.1", ["port"] = "7890", ["type"] = "http" },
        ["loglevel"] = new() { ["level"] = "info" },
        ["encoding"] = new() { ["encoding"] = "cp437" },
        // 解压密码库：用户手填，一行一个；遇到加密压缩包时按填写顺序逐个尝试
        ["unzip"] = new() { ["passwords"] = "" },
        ["down_list"] = new()
        {
            ["auto_download"] = "False", ["auto_unzip"] = "False", ["download_processes"] = "5",
            ["folder_name"] = "rj", ["min_speed"] = "256", ["speed_limit"] = "0"
        },
        ["media_lib"] = new() { ["libs"] = "[]" },
        ["language"] = new() { ["lang"] = "zh_CN" },
        // 关闭窗口时的行为：""=每次询问，"tray"=最小化到托盘，"exit"=退出程序
        ["app"] = new() { ["close_action"] = "" },
        // 作品类型优先搜索来源：SOU(音声) 可选 asmr / anime-sharing，其他类型固定 anime-sharing
        ["search"] = new() { ["sou_source"] = "asmr" },
        ["web_server"] = new() { ["enabled"] = "False", ["port"] = "8080", ["password"] = "" },
        // asmr.one 数据源：账号 + 登录后保存的 token / recommenderUuid + 镜像站选择
        ["asmr"] = new()
        {
            ["username"] = "", ["password"] = "", ["token"] = "", ["recommender_uuid"] = "",
            ["mirror_site"] = "Original",
        },
        // fanbox 数据源（pawchive 站点）：域名可换镜像，附件/缩略图子域由主域推导
        ["pawchive"] = new() { ["host"] = "pawchive.pw" },
        // FANBOX 作家监控：总开关 + 新建监控时的默认轮询间隔（分钟）。
        // 总开关只管后台自动轮询，手动「立即检查」不受它影响；没有监控项时轮询是空转。
        ["fanbox_watch"] = new() { ["enabled"] = "True", ["interval"] = "360" },
        // E-Hentai 数据源：表站(e-hentai.org)可匿名浏览，里站(exhentai.org)必须带登录 cookie；
        // original=True 时下原图（fullimg），否则下站点显示用的缩放图（省看图额度）
        ["ehentai"] = new()
        {
            ["host"] = "e-hentai.org", ["member_id"] = "", ["pass_hash"] = "", ["igneous"] = "",
            ["username"] = "",
            ["original"] = "True",
        },
        // pixiv 数据源：匿名也能搜，但结果里不含 R-18，要看 R-18 就得填浏览器里的 PHPSESSID。
        // mode=搜索分级(all/safe/r18)，s_mode=匹配方式(标签部分/完全一致、标题说明文)，
        // original=True 时下原图，否则下 1200px 缩放图
        ["pixiv"] = new()
        {
            ["php_sessid"] = "", ["mode"] = "all", ["s_mode"] = "s_tag", ["original"] = "True",
            ["username"] = "",
        },
        // FANBOX 官方站（fanbox.cc）登录：浏览器里复制的 FANBOXSESSID。
        // 未填时 FANBOX 只走 pawchive 归档；填了则作品搜索多出一个「FANBOX（账号名）」来源，直连官方 API。
        // username 只是检测登录时顺手记下的显示名，鉴权只认 sessid。
        ["fanbox"] = new() { ["sessid"] = "", ["username"] = "" },
        // 图床存储（自建 Image_hosting 服务）：开启后作品卡封面由图床直供，不再逐张唤醒 HDD
        ["image_host"] = new()
        {
            ["enabled"] = "False", ["base_url"] = "", ["project"] = "", ["token"] = "",
        },
        // 图片翻译（漫画嵌字）：默认关闭，且模型依赖没下齐之前启用也不生效
        // （生效与否一律问 Services.Translate.TranslateService.Enabled，别直接读 enabled）
        ["translate"] = new()
        {
            ["enabled"] = "False", ["model_tier"] = "full", ["model_path"] = "",
            ["target_lang"] = "zh_CN", ["device"] = "cpu", ["font"] = "",
        },
        // asmr.one 下载的文件类型过滤（仅勾选的类型会入队下载）
        ["asmr_filetype"] = new()
        {
            ["mp3"] = "True", ["mp4"] = "True", ["flac"] = "True", ["wav"] = "True",
            ["jpg"] = "True", ["png"] = "True", ["pdf"] = "True", ["txt"] = "True",
            ["vtt"] = "True", ["lrc"] = "True",
        },
    };

    private static Dictionary<string, Dictionary<string, string>>? _cache;
    private static readonly object Lock = new();

    /// <summary>配置版本戳：每次写入/失效自增。页面据此判断"配置是否变过"，未变则跳过 Reload。</summary>
    public static long Version { get; private set; }

    /// <summary>Logger 用的级别快捷缓存（避免日志路径反查数据库造成递归）。</summary>
    internal static string LogLevelCached { get; private set; } = "info";

    private static Dictionary<string, Dictionary<string, string>> Cache
    {
        get
        {
            if (_cache is null)
                lock (Lock)
                    _cache ??= Load();
            return _cache;
        }
    }

    /// <summary>丢弃缓存重新从数据库加载（页面切换时调用，保证读到最新值）。</summary>
    public static void Reload()
    {
        lock (Lock)
            _cache = Load();
    }

    /// <summary>惰性失效：仅丢弃缓存并自增版本戳，下次读取时才真正 Load（不立即打库）。</summary>
    public static void Invalidate()
    {
        lock (Lock)
        {
            _cache = null;
            Version++;
        }
    }

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        Db.EnsureTables();

        var conf = new Dictionary<string, Dictionary<string, string>>();
        var rows = Db.Select("SELECT \"section\", \"key\", \"value\" FROM \"conf\"");
        if (rows != null)
            foreach (var row in rows)
            {
                var section = (string)row[0]!;
                var key = (string)row[1]!;
                var value = row[2] as string ?? "";
                if (!conf.TryGetValue(section, out var dict))
                    conf[section] = dict = new Dictionary<string, string>();
                dict[key] = value;
            }

        // 仅补齐缺失的默认项（首次运行或新增配置项时才写库），
        // 避免每次 Load/Reload 都盲发数十条 INSERT——切页触发 Reload 时是纯读。
        foreach (var (section, items) in Defaults)
        {
            if (!conf.TryGetValue(section, out var dict))
                conf[section] = dict = new Dictionary<string, string>();
            foreach (var (key, value) in items)
            {
                if (dict.ContainsKey(key))
                    continue;
                Db.Execute(
                    "INSERT OR IGNORE INTO \"conf\" (\"section\", \"key\", \"value\") VALUES (@s, @k, @v)",
                    ("@s", section), ("@k", key), ("@v", value));
                dict[key] = value;   // 同步进本次缓存，免二次查询
            }
        }

        LogLevelCached = conf.GetValueOrDefault("loglevel")?.GetValueOrDefault("level") ?? "info";
        return conf;
    }

    // ---------- 通用读写 ----------

    public static string? Read(string section, string key, string? fallback = null) =>
        Cache.GetValueOrDefault(section.ToLowerInvariant())?.GetValueOrDefault(key.ToLowerInvariant())
        ?? fallback;

    public static void Write(string section, string key, string value)
    {
        section = section.ToLowerInvariant();
        key = key.ToLowerInvariant();
        Db.Execute(
            "INSERT OR REPLACE INTO \"conf\" (\"section\", \"key\", \"value\") VALUES (@s, @k, @v)",
            ("@s", section), ("@k", key), ("@v", value));
        lock (Lock)
        {
            if (!Cache.TryGetValue(section, out var dict))
                Cache[section] = dict = new Dictionary<string, string>();
            dict[key] = value;
            if (section == "loglevel" && key == "level")
                LogLevelCached = value;
            Version++;   // 通知各页"配置已变更"，下次可见时才 Reload
        }
    }

    private static int ReadInt(string section, string key, int fallback) =>
        int.TryParse(Read(section, key), out var v) ? Math.Max(0, v) : fallback;

    // ---------- 读取 ----------

    public static string DownloadPath => Read("downpath", "downpath", "") ?? "";

    /// <summary>代理设置：开关 + WebProxy（供 HttpClient 使用）。</summary>
    public static (bool Enabled, WebProxy Proxy) ReadProxy()
    {
        var enabled = Read("proxy", "openproxy") == "True";
        var host = Read("proxy", "host", "127.0.0.1");
        var port = Read("proxy", "port", "7890");
        return (enabled, new WebProxy($"http://{host}:{port}"));
    }

    public static (string Enabled, string Host, string Port, string Type) ReadProxySetting() =>
        (Read("proxy", "openproxy", "False")!, Read("proxy", "host", "")!,
         Read("proxy", "port", "")!, Read("proxy", "type", "http")!);

    public static string DebridApiKey => Read("debrid", "api_key", "") ?? "";

    public static bool AutoDownload => Read("down_list", "auto_download") == "True";

    public static bool AutoUnzip => Read("down_list", "auto_unzip") == "True";

    /// <summary>单文件并发分段数（1 = 不分段，最大 16）。</summary>
    public static int DownloadProcesses => Math.Clamp(ReadInt("down_list", "download_processes", 1), 1, 6);

    /// <summary>下载文件夹命名方式："rj"=按RJ号，"work_name"=按作品名称。</summary>
    public static string FolderNameMode => Read("down_list", "folder_name", "rj") ?? "rj";

    /// <summary>最低下载速度阈值（KB/s），持续低于此值 30 秒后重试；0 表示不限制。</summary>
    public static int MinSpeedKb => ReadInt("down_list", "min_speed", 256);

    /// <summary>下载总速度上限（KB/s），所有并发下载共享；0 表示不限速。</summary>
    public static int SpeedLimitKb => ReadInt("down_list", "speed_limit", 0);

    public static string SysEncoding => Read("encoding", "encoding", "cp437") ?? "cp437";

    /// <summary>解压密码库原文（一行一个，供设置页编辑）。</summary>
    public static string UnzipPasswordsText => Read("unzip", "passwords", "") ?? "";

    /// <summary>解压密码库：按行拆开去空去重，顺序即尝试顺序。</summary>
    public static List<string> UnzipPasswords =>
        UnzipPasswordsText.Split('\n')
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .Distinct()
            .ToList();

    public static string Language => Read("language", "lang", "zh_CN") ?? "zh_CN";

    /// <summary>点击关闭按钮时的行为：""=每次询问，"tray"=最小化到托盘，"exit"=退出程序。</summary>
    public static string CloseAction
    {
        get => Read("app", "close_action", "") ?? "";
        set => Write("app", "close_action", value);
    }

    /// <summary>SOU(音声) 作品优先搜索来源："asmr" = asmr.one 直链；"anime-sharing" = 论坛+debrid。</summary>
    public static string SouSearchSource => Read("search", "sou_source", "asmr") ?? "asmr";

    /// <summary>SOU 作品是否走 asmr.one 直链下载（由优先搜索设置决定）。</summary>
    public static bool SouUsesAsmr => SouSearchSource == "asmr";

    // ---------- 外部访问（内嵌 Web 服务）----------

    /// <summary>是否开启外部访问（内嵌 HTTP 服务，手机/电脑浏览器可访问媒体库）。</summary>
    public static bool WebEnabled => Read("web_server", "enabled") == "True";

    /// <summary>外部访问端口（1-65535）。</summary>
    public static int WebPort
    {
        get
        {
            var port = ReadInt("web_server", "port", 8080);
            return port is >= 1 and <= 65535 ? port : 8080;
        }
    }

    /// <summary>外部访问密码；为空表示不鉴权。</summary>
    public static string WebPassword => Read("web_server", "password", "") ?? "";

    // ---------- 门户单点登录（Portal 当 OIDC 提供方，Services/PortalSso）----------

    /// <summary>外部访问是否接入门户单点登录。开着时 Web 端一律要登录（不论访问密码是否为空）。</summary>
    public static bool SsoEnabled => Read("sso", "enabled") == "True";

    /// <summary>
    /// 浏览器看到的门户地址（如 https://portal.example.com）。授权跳转去这里，
    /// 也是 id_token 的 iss——必须与门户 conf.json 的 idp.issuer 一字不差。
    /// </summary>
    public static string SsoPortalUrl => NormalizeUrl(Read("sso", "portal_url", "") ?? "");

    /// <summary>
    /// 本机访问门户用的地址（如 http://10.0.10.5:9921），用于换 token。留空则同门户地址。
    /// 门户挂在 Cloudflare 之类后面时，从本机直连内网地址更快也更稳。
    /// </summary>
    public static string SsoInternalUrl => NormalizeUrl(Read("sso", "internal_url", "") ?? "");

    public static string SsoClientId => (Read("sso", "client_id", "") ?? "").Trim();
    public static string SsoClientSecret => (Read("sso", "client_secret", "") ?? "").Trim();

    /// <summary>四项必填齐了没有（内网地址可选）。没齐时即使开着也不生效。</summary>
    public static bool SsoConfigured =>
        SsoPortalUrl.Length > 0 && SsoClientId.Length > 0 && SsoClientSecret.Length > 0;

    /// <summary>
    /// Web 端登录会话的签名密钥，首次用到时随机生成并落库。不做成设置项：让人填多半会被填成弱口令；
    /// 换掉它等于让所有已登录的浏览器下线。
    /// </summary>
    public static string SsoSessionSecret
    {
        get
        {
            var key = Read("sso", "session_secret", "") ?? "";
            if (key.Length >= 32)
                return key;
            key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            Write("sso", "session_secret", key);
            return key;
        }
    }

    private static string NormalizeUrl(string raw)
    {
        var url = raw.Trim().TrimEnd('/');
        if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                           && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url;
    }

    /// <summary>媒体库列表（media_lib.libs，JSON）。</summary>
    public static List<MediaLib> ReadMediaLibs()
    {
        List<MediaLib> libs = [];
        try
        {
            var raw = Read("media_lib", "libs", "[]") ?? "[]";
            using var doc = JsonDocument.Parse(raw);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (string.IsNullOrEmpty(name) || !item.TryGetProperty("folders", out var f) ||
                    f.ValueKind != JsonValueKind.Array)
                    continue;
                var lib = new MediaLib { Name = name };
                if (item.TryGetProperty("view", out var v) && v.GetString() == MediaLib.ViewMakers)
                    lib.View = MediaLib.ViewMakers;
                foreach (var folder in f.EnumerateArray())
                    if (folder.GetString() is { Length: > 0 } path)
                        lib.Folders.Add(path);
                libs.Add(lib);
            }
        }
        catch (JsonException)
        {
            libs = [];
        }
        return libs;
    }

    public static void WriteMediaLibs(List<MediaLib> libs)
    {
        var json = JsonSerializer.Serialize(
            libs.ConvertAll(l => new { name = l.Name, folders = l.Folders, view = l.View }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Write("media_lib", "libs", json);
    }

    // ---------- asmr.one 数据源 ----------

    public static string AsmrUsername => Read("asmr", "username", "") ?? "";
    public static string AsmrPassword => Read("asmr", "password", "") ?? "";
    public static string AsmrToken => Read("asmr", "token", "") ?? "";
    public static string AsmrRecommenderUuid => Read("asmr", "recommender_uuid", "") ?? "";

    /// <summary>登录成功后保存 token 与 recommenderUuid。</summary>
    public static void WriteAsmrToken(string recommenderUuid, string token)
    {
        Write("asmr", "recommender_uuid", recommenderUuid);
        Write("asmr", "token", token);
    }

    /// <summary>镜像站选择（Original / Mirror-1 / Mirror-2 / Mirror-3）。</summary>
    public static string AsmrMirrorSite => Read("asmr", "mirror_site", "Original") ?? "Original";

    /// <summary>当前镜像站对应的 API 域名（asmr.one / asmr-100/200/300.com）。</summary>
    public static string AsmrApiHost => AsmrMirrorSite switch
    {
        "Mirror-1" => "asmr-100.com",
        "Mirror-2" => "asmr-200.com",
        "Mirror-3" => "asmr-300.com",
        _ => "asmr.one",
    };

    /// <summary>某文件扩展名（不含点，小写）是否启用下载。</summary>
    public static bool AsmrFileTypeEnabled(string ext) =>
        Read("asmr_filetype", ext.ToLowerInvariant()) != "False";

    /// <summary>全部受支持的 asmr 文件类型（用于设置页勾选 + 默认放行未知扩展名）。</summary>
    public static readonly string[] AsmrFileTypes =
        ["mp3", "mp4", "flac", "wav", "jpg", "png", "pdf", "txt", "vtt", "lrc"];

    // ---------- fanbox 数据源（pawchive）----------

    /// <summary>pawchive 主域名（换镜像站时改此项；附件 file.&lt;host&gt; / 缩略图 img.&lt;host&gt; 由它推导）。</summary>
    public static string PawchiveHost
    {
        get
        {
            var host = (Read("pawchive", "host", "pawchive.pw") ?? "").Trim();
            return host.Length > 0 ? host : "pawchive.pw";
        }
        set => Write("pawchive", "host", value.Trim());
    }

    /// <summary>FANBOX 作家监控的后台轮询总开关（关掉只停自动轮询，手动「立即检查」照常可用）。</summary>
    public static bool FanboxWatchEnabled
    {
        get => Read("fanbox_watch", "enabled", "True") != "False";
        set => Write("fanbox_watch", "enabled", value ? "True" : "False");
    }

    /// <summary>新建作家监控时的默认轮询间隔（分钟）；已有监控各自存自己的间隔。</summary>
    public static int FanboxWatchInterval
    {
        get => Math.Clamp(ReadInt("fanbox_watch", "interval", 360), 10, 7 * 24 * 60);
        set => Write("fanbox_watch", "interval",
            Math.Clamp(value, 10, 7 * 24 * 60).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---------- E-Hentai 数据源 ----------

    /// <summary>
    /// 站点域名：e-hentai.org（表站，可匿名浏览）或 exhentai.org（里站，必须带登录 cookie）。
    /// 元数据 API 固定走 api.e-hentai.org，两站通用，不随此项变化。
    /// </summary>
    public static string EhentaiHost
    {
        get
        {
            var host = (Read("ehentai", "host", "e-hentai.org") ?? "").Trim();
            return host.Length > 0 ? host : "e-hentai.org";
        }
        set => Write("ehentai", "host", value.Trim());
    }

    /// <summary>
    /// 站点账号名。只为「登录并获取 Cookie」按钮回填输入框而存，鉴权一律用下面三个 cookie。
    /// 密码有意不存：cookie 本身就是长期凭据，留一份明文口令没有额外用处。
    /// </summary>
    public static string EhentaiUsername => (Read("ehentai", "username", "") ?? "").Trim();

    /// <summary>登录 cookie 的 ipb_member_id（浏览器登录后从 Cookie 里复制）。</summary>
    public static string EhentaiMemberId => (Read("ehentai", "member_id", "") ?? "").Trim();

    /// <summary>登录 cookie 的 ipb_pass_hash。</summary>
    public static string EhentaiPassHash => (Read("ehentai", "pass_hash", "") ?? "").Trim();

    /// <summary>登录 cookie 的 igneous（仅 exhentai 需要，表站留空即可）。</summary>
    public static string EhentaiIgneous => (Read("ehentai", "igneous", "") ?? "").Trim();

    /// <summary>
    /// 是否下载原图。关闭时下站点显示用的缩放图（默认 1280px 宽）——画质略低，
    /// 但每张只算一次看图额度，大批量下载不容易触发站点的限额封锁。
    ///
    /// 注意：原图走站点的 fullimg 接口，**必须登录**（填了 member_id / pass_hash）。
    /// 未登录时 EhentaiApi 会自动退回显示图，不会因此下载失败。
    /// </summary>
    public static bool EhentaiOriginal => Read("ehentai", "original", "True") != "False";

    // ---------- pixiv 数据源 ----------

    /// <summary>
    /// 登录 cookie 的 PHPSESSID（浏览器登录 pixiv 后从 Cookie 里复制，形如 <c>12345678_abcdef…</c>）。
    /// 留空即匿名浏览：仍能搜索，但站点会把 R-18 作品从结果里滤掉，单独打开也会被拒。
    /// 只存这一个 cookie：pixiv 的鉴权只认它，别的 cookie 存了也没用。
    /// </summary>
    public static string PixivSessionId => (Read("pixiv", "php_sessid", "") ?? "").Trim();

    /// <summary>
    /// 搜索分级（站点的 mode 参数）：all=全部 / safe=全年龄 / r18=仅 R-18。
    /// 未登录时选 all 或 r18 都只会拿到全年龄作品，那是站点在过滤，不是这里没生效。
    /// </summary>
    public static string PixivSearchMode
    {
        get
        {
            var mode = (Read("pixiv", "mode", "all") ?? "").Trim();
            return mode is "all" or "safe" or "r18" ? mode : "all";
        }
    }

    /// <summary>
    /// 匹配方式（站点的 s_mode 参数）：s_tag=标签部分一致 / s_tag_full=标签完全一致 /
    /// s_tc=标题与说明文。默认部分一致——搜索框里多是随手输入的词，完全一致常常一条都搜不到。
    /// </summary>
    public static string PixivTagMatch
    {
        get
        {
            var mode = (Read("pixiv", "s_mode", "s_tag") ?? "").Trim();
            return mode is "s_tag" or "s_tag_full" or "s_tc" ? mode : "s_tag";
        }
    }

    /// <summary>
    /// 是否下载原图。关闭时下站点的 1200px 缩放图——体积小得多，但漫画的文字会糊。
    /// 与 E-Hentai 不同，pixiv 的原图不需要登录（只有 R-18 作品本身才需要）。
    /// </summary>
    public static bool PixivOriginal => Read("pixiv", "original", "True") != "False";

    /// <summary>检测登录时记下的 pixiv 显示名（只用于「账号访问」页与来源下拉的展示，鉴权只认 PHPSESSID）。</summary>
    public static string PixivUsername => (Read("pixiv", "username", "") ?? "").Trim();

    // ---------- FANBOX 官方站（fanbox.cc）----------

    /// <summary>
    /// 登录 cookie 的 FANBOXSESSID（浏览器登录 fanbox.cc 后从 Cookie 里复制，形如 <c>12345678_abcdef…</c>）。
    /// 留空即不登录：FANBOX 只能经 pawchive 归档访问。
    /// </summary>
    public static string FanboxSessionId => (Read("fanbox", "sessid", "") ?? "").Trim();

    /// <summary>检测登录时记下的 FANBOX 显示名（来源下拉写成「FANBOX（显示名）」）。</summary>
    public static string FanboxUsername => (Read("fanbox", "username", "") ?? "").Trim();

    // ---------- 图床存储（自建 Image_hosting）----------

    /// <summary>是否启用图床存储（作品卡封面改由图床直供，媒体库翻页不再读 HDD 原图）。</summary>
    public static bool ImageHostEnabled => Read("image_host", "enabled") == "True";

    /// <summary>
    /// 图床服务地址，如 http://192.168.1.5:9990。桌面端与手机浏览器共用这一个地址——
    /// 填 127.0.0.1 手机就取不到图，故局域网场景要填本机局域网 IP，
    /// 并在图床「系统设置 → 附加访问主机名」里放行该地址（否则图床按非法主机 400）。
    /// </summary>
    public static string ImageHostBaseUrl
    {
        get
        {
            var url = (Read("image_host", "base_url", "") ?? "").Trim().TrimEnd('/');
            // 只填了 IP:端口时补上 http://，省得每个调用点自己判
            if (url.Length > 0 && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                               && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                url = "http://" + url;
            return url;
        }
        set => Write("image_host", "base_url", value.Trim());
    }

    /// <summary>图床上的项目标识（图床管理端创建项目时的 slug）。</summary>
    public static string ImageHostProject => (Read("image_host", "project", "") ?? "").Trim();

    /// <summary>图床项目的 API Token（图床项目详情页复制）。</summary>
    public static string ImageHostToken => (Read("image_host", "token", "") ?? "").Trim();

    /// <summary>配置是否齐全（地址/项目/Token 缺一不可），与"是否启用"分开判断。</summary>
    public static bool ImageHostConfigured =>
        ImageHostBaseUrl.Length > 0 && ImageHostProject.Length > 0 && ImageHostToken.Length > 0;

    // ---------- 图片翻译（漫画嵌字）----------

    /// <summary>
    /// 用户在设置页勾的那个开关——**不代表功能真的可用**。
    /// 模型依赖没下齐时这里为 True 也不该翻译，故各调用点一律问
    /// <c>Services.Translate.TranslateService.Enabled</c>，不要直接读本属性。
    /// </summary>
    public static bool TranslateEnabledSetting
    {
        get => Read("translate", "enabled") == "True";
        set => Write("translate", "enabled", value ? "True" : "False");
    }

    /// <summary>模型档位 id（对应依赖清单 tiers 里的 id，如 lite / full）。</summary>
    public static string TranslateModelTier
    {
        get => (Read("translate", "model_tier", "full") ?? "full").Trim();
        set => Write("translate", "model_tier", value.Trim());
    }

    /// <summary>模型存放目录；留空表示用默认的「工作目录/models/translate」。</summary>
    public static string TranslateModelPath
    {
        get => (Read("translate", "model_path", "") ?? "").Trim();
        set => Write("translate", "model_path", value.Trim());
    }

    /// <summary>译文目标语言（沿用 I18n 的语言代码）。</summary>
    public static string TranslateTargetLang
    {
        get => (Read("translate", "target_lang", "zh_CN") ?? "zh_CN").Trim();
        set => Write("translate", "target_lang", value.Trim());
    }

    /// <summary>推理设备："cpu" 或 "gpu"（无可用显卡时由引擎自行退回 CPU）。</summary>
    public static string TranslateDevice
    {
        get => (Read("translate", "device", "cpu") ?? "cpu").Trim();
        set => Write("translate", "device", value.Trim());
    }

    /// <summary>嵌字用的字体名；留空表示用系统默认中文字体。</summary>
    public static string TranslateFont
    {
        get => (Read("translate", "font", "") ?? "").Trim();
        set => Write("translate", "font", value.Trim());
    }
}
