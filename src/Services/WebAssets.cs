using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace R18MediaLibrary.Services;

/// <summary>
/// 外部访问网页的静态资源：响应式 SPA 的外壳 + 拆分后的样式/各页面 JS 模块，全部作为嵌入资源打包
/// （Web/index.html、Web/app.css、Web/js/*.js）。
///
/// 用一张「URL 路径 → 嵌入资源 + MIME」白名单（<see cref="Assets"/>）把资源固定映射，既避免按后缀反查的歧义，
/// 也防止外部请求读到任意嵌入资源。首次访问时读入并缓存（进程级）。
/// </summary>
internal static class WebAssets
{
    private static readonly object Sync = new();
    private static Dictionary<string, (byte[] Bytes, string ContentType)>? _cache;

    // URL 路径 → (嵌入资源名后缀, Content-Type)。后缀均唯一（MSBuild 把 Web\js\core.js 展平为 …Web.js.core.js）。
    private static readonly (string Path, string Suffix, string Type)[] Assets =
    [
        ("/index.html",     ".index.html",  "text/html; charset=utf-8"),
        ("/favicon.ico",    ".favicon.ico", "image/x-icon"),
        ("/app.css",        ".app.css",     "text/css; charset=utf-8"),
        ("/js/core.js",     ".core.js",     "application/javascript; charset=utf-8"),
        ("/js/library.js",  ".library.js",  "application/javascript; charset=utf-8"),
        ("/js/search.js",   ".search.js",   "application/javascript; charset=utf-8"),
        ("/js/fanbox.js",   ".fanbox.js",   "application/javascript; charset=utf-8"),
        ("/js/ehentai.js",  ".ehentai.js",  "application/javascript; charset=utf-8"),
        ("/js/pixiv.js",    ".pixiv.js",    "application/javascript; charset=utf-8"),
        ("/js/download.js", ".download.js", "application/javascript; charset=utf-8"),
        ("/js/settings.js", ".settings.js", "application/javascript; charset=utf-8"),
        ("/js/viewer.js",   ".viewer.js",   "application/javascript; charset=utf-8"),
        ("/js/boot.js",     ".boot.js",     "application/javascript; charset=utf-8"),
    ];

    private static Dictionary<string, (byte[], string)> Cache()
    {
        if (_cache != null)
            return _cache;
        lock (Sync)
        {
            if (_cache != null)
                return _cache;
            var asm = Assembly.GetExecutingAssembly();
            var names = asm.GetManifestResourceNames();
            var map = new Dictionary<string, (byte[], string)>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, suffix, type) in Assets)
            {
                var name = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
                byte[] bytes;
                if (name != null)
                    using (var stream = asm.GetManifestResourceStream(name))
                    using (var ms = new MemoryStream())
                    {
                        stream!.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                else
                    bytes = Encoding.UTF8.GetBytes($"/* 资源缺失：{path} */");
                map[path] = (bytes, type);
            }
            _cache = map;
            return _cache;
        }
    }

    /// <summary>按 URL 路径取静态资源；命中白名单返回 true。</summary>
    public static bool TryGet(string path, out byte[] bytes, out string contentType)
    {
        if (Cache().TryGetValue(path, out var hit))
        {
            (bytes, contentType) = hit;
            return true;
        }
        bytes = [];
        contentType = "";
        return false;
    }
}
