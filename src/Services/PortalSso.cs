using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 外部访问（Web 端）接入门户单点登录：门户（D:\Project\Portal）当 OIDC 提供方，本程序当客户端，
/// 走授权码 + PKCE。配置在 conf 表的 <c>sso</c> 段（桌面设置页「门户单点登录」）。
///
/// 流程：浏览器 <c>/api/sso/login?base=…</c> → 门户 <c>/api/oidc/authorize</c>（没登录门户就先登录）
/// → 回 <c>/api/sso/callback</c> → 本机后端拿授权码去门户换 id_token → 按门户身份找/建本地用户 → 种会话 Cookie。
///
/// <b>回调地址由浏览器告诉我们</b>（<c>base</c> = 页面所在目录）：经门户代理打开时页面在
/// <c>https://门户/api/proxy/&lt;卡片&gt;/</c> 底下，局域网直连时在 <c>http://IP:端口/</c>，
/// 后端从 Host 头看不出前者。base 是调用方给的，但门户对 redirect_uri 逐字比对登记表，
/// 填一个没登记的地址只会停在门户的报错页上，送不出授权码。
///
/// 认人规则（即「校验双方用户名」）：
/// 1. 按门户的 sub（门户用户 id）找已绑定的本地用户——找到就用它，用户名随门户同步；
/// 2. 没绑定过：按门户用户名找同名本地用户，找到就绑上；
/// 3. 都没有：以门户用户名新建一个本地用户。
/// </summary>
public static class PortalSso
{
    public const string SessionCookie = "dasd_user";
    public const string StateCookie = "dasd_sso";
    private const int StateTtl = 600;                       // 从点按钮到门户跳回来
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

    /// <summary>门户多在局域网里；代理是给外网站点配的，套上反而连不通（同 ImageHostClient）。</summary>
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public sealed record WebUser(long Id, string Username, string DisplayName, bool IsAdmin, string LastLogin);

    public sealed class SsoException(string message) : Exception(message);

    /// <summary>开着且配置齐全。只开不填等于没开，免得 Web 端要求登录却一个能用的入口都没有。</summary>
    public static bool Active => AppConfig.SsoEnabled && AppConfig.SsoConfigured;

    // ---------- 授权 ----------

    /// <summary>规整浏览器给的页面目录：只认 http(s) 绝对地址，末尾补 /。不合法返回 null。</summary>
    public static string? NormalizeBase(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;
        var path = uri.AbsolutePath;
        path = path[..(path.LastIndexOf('/') + 1)];
        return $"{uri.Scheme}://{uri.Authority}{path}";
    }

    /// <summary>返回（要跳去的门户授权地址, 要种进临时 Cookie 的状态串）。</summary>
    public static (string Url, string State) Begin(string baseUrl)
    {
        var redirect = baseUrl + "api/sso/callback";
        var state = B64(RandomNumberGenerator.GetBytes(24));
        var nonce = B64(RandomNumberGenerator.GetBytes(24));
        var verifier = B64(RandomNumberGenerator.GetBytes(48));
        var challenge = B64(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

        var query = string.Join("&", new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = AppConfig.SsoClientId,
            ["redirect_uri"] = redirect,
            ["scope"] = "openid profile",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        }.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));

        var packed = Pack(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["state"] = state, ["nonce"] = nonce, ["verifier"] = verifier, ["redirect"] = redirect,
            ["exp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + StateTtl,
        }));
        return ($"{AppConfig.SsoPortalUrl}/api/oidc/authorize?{query}", packed);
    }

    /// <summary>门户跳回来之后：校验 state、换 token、验 id_token、找/建本地用户。失败抛 <see cref="SsoException"/>（消息给人看）。</summary>
    public static async Task<WebUser> CompleteAsync(string code, string state, string packedState)
    {
        var saved = Unpack(packedState) ?? throw new SsoException("登录状态丢了或已过期，请重新点一次门户登录");
        using var savedDoc = JsonDocument.Parse(saved);
        var s = savedDoc.RootElement;
        if (s.GetProperty("exp").GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new SsoException("登录状态已过期，请重新点一次门户登录");
        // state 对不上 = 这次回调不是我们发出去的那一次（CSRF，或两个标签页各点了一次）
        if (state.Length == 0 || state != s.GetProperty("state").GetString())
            throw new SsoException("state 对不上，这次登录被丢弃");

        var tokenUrl = (AppConfig.SsoInternalUrl.Length > 0 ? AppConfig.SsoInternalUrl : AppConfig.SsoPortalUrl)
                       + "/api/oidc/token";
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = s.GetProperty("redirect").GetString() ?? "",
            ["client_id"] = AppConfig.SsoClientId,
            ["client_secret"] = AppConfig.SsoClientSecret,
            ["code_verifier"] = s.GetProperty("verifier").GetString() ?? "",
        });
        string body;
        try
        {
            using var resp = await Client.PostAsync(tokenUrl, form);
            body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                throw new SsoException($"门户拒绝换 token（{(int)resp.StatusCode}）：{Shorten(body)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new SsoException($"连不上门户（{tokenUrl}）：{e.Message}");
        }

        string idToken;
        try
        {
            using var doc = JsonDocument.Parse(body);
            idToken = doc.RootElement.GetProperty("id_token").GetString() ?? "";
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new SsoException("门户返回的不是有效的 token 响应");
        }

        var claims = VerifyIdToken(idToken, s.GetProperty("nonce").GetString() ?? "");
        return Resolve(claims);
    }

    /// <summary>
    /// 门户的 id_token 是拿本客户端的 client_secret 做 HS256 签的，这里有那把钥匙，所以签名照验——
    /// 再加 iss / aud / exp / nonce 四项，分别防指到假门户、拿别的系统的 token、过期票、重放。
    /// </summary>
    private static JsonElement VerifyIdToken(string idToken, string nonce)
    {
        var parts = idToken.Split('.');
        if (parts.Length != 3)
            throw new SsoException("id_token 的形状不对");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppConfig.SsoClientSecret));
        var expect = hmac.ComputeHash(Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]));
        byte[] sig;
        try { sig = UnB64(parts[2]); }
        catch (FormatException) { throw new SsoException("id_token 签名解不开"); }
        if (!CryptographicOperations.FixedTimeEquals(expect, sig))
            throw new SsoException("id_token 签名校验不过：确认这里的 Client Secret 与门户 conf.json 里登记的一致");

        JsonElement c;
        try { c = JsonDocument.Parse(UnB64(parts[1])).RootElement.Clone(); }
        catch (Exception e) when (e is JsonException or FormatException) { throw new SsoException("id_token 解不开"); }

        if ((Str(c, "iss") ?? "").TrimEnd('/') != AppConfig.SsoPortalUrl)
            throw new SsoException($"id_token 的 iss（{Str(c, "iss")}）与这里填的门户地址（{AppConfig.SsoPortalUrl}）对不上：" +
                                   "两边必须一字不差，门户那边是右上角菜单 → 单点登录 里的门户地址");
        if (Str(c, "aud") != AppConfig.SsoClientId)
            throw new SsoException("id_token 不是发给本程序的（aud 对不上）");
        if (!c.TryGetProperty("exp", out var exp) || exp.GetInt64() < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            throw new SsoException("id_token 已过期");
        if (Str(c, "nonce") != nonce)
            throw new SsoException("nonce 对不上，这次登录被丢弃");
        if (string.IsNullOrEmpty(Str(c, "sub")))
            throw new SsoException("id_token 里没有 sub");
        return c;
    }

    // ---------- 门户身份 → 本地用户 ----------

    private static WebUser Resolve(JsonElement claims)
    {
        var sub = Str(claims, "sub")!;
        var username = (Str(claims, "preferred_username") ?? "").Trim();
        if (username.Length == 0)
            username = "portal-" + sub;
        var display = (Str(claims, "name") ?? "").Trim();
        if (display.Length == 0)
            display = username;
        var isAdmin = claims.TryGetProperty("groups", out var g) && g.ValueKind == JsonValueKind.Array &&
                      g.EnumerateArray().Any(x => x.GetString() == "admin");
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        lock (ResolveLock)
        {
            var user = BySub(sub);
            if (user != null)
            {
                // 已绑定：沿用这个人。门户里改了名就跟着改——除非新名字已被本地另一个用户占着
                var name = user.Username;
                if (!string.Equals(name, username, StringComparison.Ordinal))
                {
                    var other = ByUsername(username);
                    if (other == null || other.Id == user.Id)
                    {
                        Logger.Info($"门户单点登录：用户 {name} 在门户改名为 {username}，本地随之同步");
                        name = username;
                    }
                    else
                    {
                        Logger.Warning($"门户单点登录：门户用户 {username} 想改名，但本地已有同名用户，保留原名 {name}");
                    }
                }
                Db.Execute("UPDATE \"web_users\" SET \"username\" = @u, \"display_name\" = @d, \"is_admin\" = @a, " +
                           "\"last_login\" = @t WHERE \"id\" = @id",
                    ("@u", name), ("@d", display), ("@a", isAdmin ? "1" : "0"), ("@t", now), ("@id", user.Id));
                return ById(user.Id)!;
            }

            var same = ByUsername(username);
            if (same != null)
            {
                var boundTo = Db.Scalar("SELECT \"portal_sub\" FROM \"web_users\" WHERE \"id\" = @id", ("@id", same.Id)) as string;
                if (!string.IsNullOrEmpty(boundTo))
                    // 同名用户已绑着另一个门户账号（多半是门户里删掉重建过）。不替人做主改绑，
                    // 否则拿到同名账号的人就能顶替原来那个人
                    throw new SsoException($"本程序里的用户 {same.Username} 已绑定另一个门户账号。" +
                                           "如确是同一个人，请在桌面端「系统设置 → 门户单点登录」删掉该用户后重新登录");
                Db.Execute("UPDATE \"web_users\" SET \"portal_sub\" = @s, \"display_name\" = @d, \"is_admin\" = @a, " +
                           "\"last_login\" = @t WHERE \"id\" = @id",
                    ("@s", sub), ("@d", display), ("@a", isAdmin ? "1" : "0"), ("@t", now), ("@id", same.Id));
                Logger.Info($"门户单点登录：门户用户 {username} 绑定到本地同名用户");
                return ById(same.Id)!;
            }

            Db.Execute("INSERT INTO \"web_users\" (\"username\", \"display_name\", \"portal_sub\", \"is_admin\", " +
                       "\"created_at\", \"last_login\") VALUES (@u, @d, @s, @a, @t, @t)",
                ("@u", username), ("@d", display), ("@s", sub), ("@a", isAdmin ? "1" : "0"), ("@t", now));
            var created = BySub(sub) ?? throw new SsoException("本地用户创建失败，详见日志");
            Logger.Info($"门户单点登录：本程序没有用户 {username}，已自动创建");
            return created;
        }
    }

    private static readonly object ResolveLock = new();

    private const string Cols = "\"id\", \"username\", \"display_name\", \"is_admin\", \"last_login\"";

    private static WebUser? One(string where, params (string, object?)[] args)
    {
        var rows = Db.Select($"SELECT {Cols} FROM \"web_users\" WHERE {where}", args);
        if (rows is not { Count: > 0 })
            return null;
        var r = rows[0];
        var name = r[1] as string ?? "";
        return new WebUser(Convert.ToInt64(r[0]), name, r[2] as string is { Length: > 0 } d ? d : name,
            r[3] as string == "1", r[4] as string ?? "");
    }

    public static WebUser? ById(long id) => One("\"id\" = @id", ("@id", id));
    private static WebUser? BySub(string sub) => One("\"portal_sub\" = @s", ("@s", sub));
    private static WebUser? ByUsername(string name) => One("\"username\" = @u", ("@u", name));

    /// <summary>设置页用户列表。</summary>
    public static List<WebUser> ListUsers()
    {
        var list = new List<WebUser>();
        foreach (var r in Db.Select($"SELECT {Cols} FROM \"web_users\" ORDER BY \"id\"") ?? [])
        {
            var name = r[1] as string ?? "";
            list.Add(new WebUser(Convert.ToInt64(r[0]), name, r[2] as string is { Length: > 0 } d ? d : name,
                r[3] as string == "1", r[4] as string ?? ""));
        }
        return list;
    }

    /// <summary>删掉本地用户。其会话当场失效（会话校验要查到这一行）；下次从门户登录会重新建出来。</summary>
    public static void DeleteUser(long id) =>
        Db.Execute("DELETE FROM \"web_users\" WHERE \"id\" = @id", ("@id", id));

    // ---------- 会话 ----------

    /// <summary>会话 Cookie：<c>&lt;用户 id&gt;.&lt;过期时间戳&gt;.&lt;HMAC&gt;</c>，服务端不存会话。</summary>
    public static string MakeSession(WebUser user)
    {
        var payload = $"{user.Id}.{DateTimeOffset.UtcNow.Add(SessionTtl).ToUnixTimeSeconds()}";
        return payload + "." + Sign("session:" + payload);
    }

    public static int SessionMaxAge => (int)SessionTtl.TotalSeconds;

    /// <summary>认就返回用户，不认返回 null（不区分原因）。用户被删掉的会话当场失效。</summary>
    public static WebUser? UserFromSession(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3 || !long.TryParse(parts[0], out var id) || !long.TryParse(parts[1], out var exp))
            return null;
        if (exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return null;
        var expect = Sign($"session:{parts[0]}.{parts[1]}");
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(parts[2])))
            return null;
        return ById(id);
    }

    // ---------- 工具 ----------

    private static string Sign(string text)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppConfig.SsoSessionSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string Pack(string json)
    {
        var body = B64(Encoding.UTF8.GetBytes(json));
        return body + "." + Sign("state:" + body);
    }

    private static string? Unpack(string raw)
    {
        var dot = raw.IndexOf('.');
        if (dot <= 0)
            return null;
        var body = raw[..dot];
        var expect = Sign("state:" + body);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expect), Encoding.ASCII.GetBytes(raw[(dot + 1)..])))
            return null;
        try { return Encoding.UTF8.GetString(UnB64(body)); }
        catch (FormatException) { return null; }
    }

    private static string B64(byte[] raw) => Convert.ToBase64String(raw).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] UnB64(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s + new string('=', (4 - s.Length % 4) % 4));
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString() : null;

    private static string Shorten(string s) => s.Length > 200 ? s[..200] : s;
}
