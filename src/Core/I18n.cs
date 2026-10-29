using System;
using System.Collections.Generic;

namespace R18MediaLibrary.Core;

/// <summary>
/// 界面多语言支持（对应 Python 版 i18n.py）。
///
/// 以简体中文原文作为翻译键（key），运行时按当前语言返回对应译文：
/// - 当前语言为 zh_CN 时，Tr() 直接返回原文；
/// - 其他语言在 Translations 中查表，缺失时回退到原文。
///
/// 含占位符的文案（如 '共 {n} 个作品'）由调用方用 Format() 替换，
/// 各语言译文需保留相同的占位符名称。
///
/// 注意：仅用于"显示"的中文才走 Tr()。数据库中存取的中文值
/// （works.state 的 '已品悦' / '已下载' / '下载中' 等）必须保持原样，不要翻译。
/// </summary>
public static class I18n
{
    /// <summary>语言代码 -> 该语言自我标注的名称（用于设置页下拉框）。</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("zh_CN", "简体中文"),
        ("zh_TW", "繁體中文"),
        ("ja", "日本語"),
        ("en", "English"),
    ];

    private static readonly HashSet<string> Valid = ["zh_CN", "zh_TW", "ja", "en"];

    private static string _current = "zh_CN";

    /// <summary>语言切换广播：各窗口订阅后实时重译界面。</summary>
    public static event Action? LanguageChanged;

    public static string CurrentLanguage => _current;

    /// <summary>仅更新内存中的当前语言（不写库、不发事件）。</summary>
    public static void SetLanguage(string lang) =>
        _current = Valid.Contains(lang) ? lang : "zh_CN";

    /// <summary>从配置读取已保存的语言；在创建任何窗口之前调用。</summary>
    public static void InitLanguage() => SetLanguage(AppConfig.Language);

    /// <summary>切换语言：写入配置、更新当前语言并广播给所有窗口实时重译。</summary>
    public static void ApplyLanguage(string lang)
    {
        SetLanguage(lang);
        AppConfig.Write("language", "lang", _current);
        LanguageChanged?.Invoke();
    }

    /// <summary>按当前语言返回译文；zh_CN 或查不到时返回原文。</summary>
    public static string Tr(string text)
    {
        if (_current == "zh_CN")
            return text;
        return Translations.TryGetValue(text, out var entry) &&
               entry.TryGetValue(_current, out var translated)
            ? translated
            : text;
    }

    /// <summary>替换 {name} 形式的占位符：I18n.Format(Tr("共 {n} 个作品"), ("n", 5))。</summary>
    public static string Format(string template, params (string Name, object? Value)[] args)
    {
        foreach (var (name, value) in args)
            template = template.Replace("{" + name + "}", value?.ToString() ?? "");
        return template;
    }

    // 简体中文原文 -> {语言代码: 译文}
    private static readonly Dictionary<string, Dictionary<string, string>> Translations = new()
    {
        // ---------- 使用帮助（各页面的 ? 按钮 / HelpPanel）----------
        ["使用帮助"] = new() { ["zh_TW"] = "使用說明", ["ja"] = "ヘルプ", ["en"] = "Help" },
        ["关闭帮助"] = new() { ["zh_TW"] = "關閉說明", ["ja"] = "ヘルプを閉じる", ["en"] = "Close help" },
        ["‹ 上一章"] = new() { ["zh_TW"] = "‹ 上一章", ["ja"] = "‹ 前の章", ["en"] = "‹ Previous" },
        ["下一章 ›"] = new() { ["zh_TW"] = "下一章 ›", ["ja"] = "次の章 ›", ["en"] = "Next ›" },
        ["帮助加载失败"] = new() { ["zh_TW"] = "說明載入失敗", ["ja"] = "ヘルプを読み込めませんでした", ["en"] = "Failed to load help" },
        // ---------- E-Hentai 数据源 ----------
        ["站点未提供逐页缩略图（登录后可在站点把画廊版式设为大缩略图），此处只显示封面。"] = new()
        {
            ["zh_TW"] = "站點未提供逐頁縮圖（登入後可在站點把畫廊版式設為大縮圖），此處只顯示封面。",
            ["ja"] = "サイトがページごとのサムネイルを返しません（ログイン後、ギャラリー表示を大サムネイルに設定すると取得できます）。ここでは表紙のみ表示します。",
            ["en"] = "The site returns no per-page thumbnails (sign in and set the gallery layout to large thumbnails); showing the cover only.",
        },
        ["关键字 / 标签（如 artist:xxx），或粘贴 E-Hentai 画廊链接"] = new()
        {
            ["zh_TW"] = "關鍵字 / 標籤（如 artist:xxx），或貼上 E-Hentai 畫廊連結",
            ["ja"] = "キーワード / タグ（artist:xxx など）、または E-Hentai のギャラリー URL",
            ["en"] = "Keyword / tag (e.g. artist:xxx), or paste an E-Hentai gallery link",
        },
        ["← 返回搜索结果"] = new() { ["zh_TW"] = "← 返回搜尋結果", ["ja"] = "← 検索結果に戻る", ["en"] = "← Back to results" },
        ["正在搜索画廊…"] = new() { ["zh_TW"] = "正在搜尋畫廊…", ["ja"] = "ギャラリーを検索中…", ["en"] = "Searching galleries…" },
        ["未找到该画廊（可能已下架，或里站需要登录 cookie）"] = new()
        {
            ["zh_TW"] = "找不到該畫廊（可能已下架，或裏站需要登入 cookie）",
            ["ja"] = "ギャラリーが見つかりません（削除済み、または ExHentai のログイン Cookie が必要）",
            ["en"] = "Gallery not found (removed, or ExHentai needs login cookies)",
        },
        ["没有匹配的画廊"] = new() { ["zh_TW"] = "沒有符合的畫廊", ["ja"] = "一致するギャラリーがありません", ["en"] = "No matching galleries" },
        ["已加载 {n} 本画廊"] = new()
        {
            ["zh_TW"] = "已載入 {n} 本畫廊",
            ["ja"] = "{n} 件のギャラリーを読み込み済み",
            ["en"] = "{n} galleries loaded",
        },
        ["{n} 页"] = new() { ["zh_TW"] = "{n} 頁", ["ja"] = "{n} ページ", ["en"] = "{n} pages" },
        ["已选 {n} 本"] = new() { ["zh_TW"] = "已選 {n} 本", ["ja"] = "{n} 件選択中", ["en"] = "{n} selected" },
        ["正在获取画廊内容…"] = new()
        {
            ["zh_TW"] = "正在取得畫廊內容…",
            ["ja"] = "ギャラリーの内容を取得中…",
            ["en"] = "Loading gallery contents…",
        },
        ["获取画廊信息失败"] = new()
        {
            ["zh_TW"] = "取得畫廊資訊失敗",
            ["ja"] = "ギャラリー情報の取得に失敗しました",
            ["en"] = "Failed to load gallery info",
        },
        ["取不到预览图"] = new() { ["zh_TW"] = "取不到預覽圖", ["ja"] = "プレビュー画像を取得できません", ["en"] = "No preview available" },
        ["原名"] = new() { ["zh_TW"] = "原名", ["ja"] = "原題", ["en"] = "Original title" },
        ["社团/作者"] = new() { ["zh_TW"] = "社團/作者", ["ja"] = "サークル/作者", ["en"] = "Circle / artist" },
        ["分类"] = new() { ["zh_TW"] = "分類", ["ja"] = "カテゴリ", ["en"] = "Category" },
        ["投稿者"] = new() { ["zh_TW"] = "投稿者", ["ja"] = "投稿者", ["en"] = "Uploader" },
        ["投稿"] = new() { ["zh_TW"] = "投稿", ["ja"] = "投稿日", ["en"] = "Posted" },
        ["评分"] = new() { ["zh_TW"] = "評分", ["ja"] = "評価", ["en"] = "Rating" },
        ["页数"] = new() { ["zh_TW"] = "頁數", ["ja"] = "ページ数", ["en"] = "Pages" },
        ["该画廊已在站点被删除，内容可能不全"] = new()
        {
            ["zh_TW"] = "該畫廊已在站點被刪除，內容可能不全",
            ["ja"] = "このギャラリーはサイト上で削除済みです。内容が不完全な場合があります",
            ["en"] = "This gallery was expunged on the site; contents may be incomplete",
        },
        ["仅预览前 {n} 页，共 {m} 页；下载可取全本。"] = new()
        {
            ["zh_TW"] = "僅預覽前 {n} 頁，共 {m} 頁；下載可取全本。",
            ["ja"] = "プレビューは先頭 {n} ページのみ（全 {m} ページ）。ダウンロードすれば全ページ取得できます。",
            ["en"] = "Preview shows the first {n} of {m} pages; download to get them all.",
        },
        ["在站点打开"] = new() { ["zh_TW"] = "在站點開啟", ["ja"] = "サイトで開く", ["en"] = "Open on site" },
        ["正在抓取图片清单 {i}/{n}：{title}"] = new()
        {
            ["zh_TW"] = "正在抓取圖片清單 {i}/{n}：{title}",
            ["ja"] = "画像リストを取得中 {i}/{n}：{title}",
            ["en"] = "Fetching image list {i}/{n}: {title}",
        },
        ["已加入下载：{galleries} 本画廊 / {files} 张图片"] = new()
        {
            ["zh_TW"] = "已加入下載：{galleries} 本畫廊 / {files} 張圖片",
            ["ja"] = "ダウンロードに追加：{galleries} 件のギャラリー / {files} 枚の画像",
            ["en"] = "Queued: {galleries} galleries / {files} images",
        },
        ["，跳过 {n} 本"] = new() { ["zh_TW"] = "，跳過 {n} 本", ["ja"] = "、{n} 件をスキップ", ["en"] = ", skipped {n}" },
        ["站点"] = new() { ["zh_TW"] = "站點", ["ja"] = "サイト", ["en"] = "Site" },
        ["图片画质"] = new() { ["zh_TW"] = "圖片畫質", ["ja"] = "画質", ["en"] = "Image quality" },
        ["原图"] = new() { ["zh_TW"] = "原圖", ["ja"] = "オリジナル", ["en"] = "Original" },
        ["站点显示图（省额度）"] = new()
        {
            ["zh_TW"] = "站點顯示圖（省額度）",
            ["ja"] = "サイト表示サイズ（制限節約）",
            ["en"] = "Resampled (saves quota)",
        },
        ["连接测试"] = new() { ["zh_TW"] = "連線測試", ["ja"] = "接続テスト", ["en"] = "Test connection" },
        ["从浏览器登录后的 Cookie 中复制；exhentai(里站) 三项缺一不可，e-hentai(表站) 可匿名浏览"] = new()
        {
            ["zh_TW"] = "從瀏覽器登入後的 Cookie 中複製；exhentai(裏站) 三項缺一不可，e-hentai(表站) 可匿名瀏覽",
            ["ja"] = "ブラウザでログイン後の Cookie からコピー。ExHentai は 3 項目すべて必須、E-Hentai は未設定でも閲覧可。",
            ["en"] = "Copy from your browser cookies after signing in; ExHentai needs all three, E-Hentai works anonymously.",
        },
        // ---------- FANBOX（pawchive 数据源）----------
        // 社团显示名映射（卡片右上角 🖊）
        ["自定义显示名称"] = new()
            { ["zh_TW"] = "自訂顯示名稱", ["ja"] = "表示名を変更", ["en"] = "Rename (display only)" },
        ["自定义社团名称"] = new()
            { ["zh_TW"] = "自訂社團名稱", ["ja"] = "サークル表示名", ["en"] = "Custom maker name" },
        ["社团「{maker}」的显示名称（留空恢复原名，不会改动作品数据）"] = new()
        {
            ["zh_TW"] = "社團「{maker}」的顯示名稱（留空恢復原名，不會改動作品資料）",
            ["ja"] = "サークル「{maker}」の表示名（空欄で元に戻す。作品データは変更されません）",
            ["en"] = "Display name for \"{maker}\" (leave empty to restore; work data is unchanged)",
        },
        // 作品详情（查看内容）
        ["下载本篇"] = new() { ["zh_TW"] = "下載本篇", ["ja"] = "この投稿を保存", ["en"] = "Download post" },
        ["正文"] = new() { ["zh_TW"] = "正文", ["ja"] = "本文", ["en"] = "Body" },
        ["发布"] = new() { ["zh_TW"] = "發佈", ["ja"] = "公開日", ["en"] = "Published" },
        ["文件"] = new() { ["zh_TW"] = "檔案", ["ja"] = "ファイル", ["en"] = "Files" },
        ["未下载"] = new() { ["zh_TW"] = "未下載", ["ja"] = "未取得", ["en"] = "Not downloaded" },
        ["这篇没有图片"] = new()
            { ["zh_TW"] = "這篇沒有圖片", ["ja"] = "画像はありません", ["en"] = "No images in this post" },
        ["获取作品详情失败"] = new()
            { ["zh_TW"] = "取得作品詳情失敗", ["ja"] = "投稿の取得に失敗しました", ["en"] = "Failed to load post" },
        ["其他附件（{n}）"] = new()
            { ["zh_TW"] = "其他附件（{n}）", ["ja"] = "その他の添付（{n}）", ["en"] = "Other files ({n})" },
        ["{n} 个（图片 {m}）"] = new()
            { ["zh_TW"] = "{n} 個（圖片 {m}）", ["ja"] = "{n} 件（画像 {m}）", ["en"] = "{n} ({m} images)" },
        ["FANBOX"] = new() { ["zh_TW"] = "FANBOX", ["ja"] = "FANBOX", ["en"] = "FANBOX" },
        ["📚 我的库"] = new() { ["zh_TW"] = "📚 我的庫", ["ja"] = "📚 マイライブラリ", ["en"] = "📚 My Library" },
        ["输入作家名 / 作家 ID，或粘贴 pawchive 作家链接"] = new()
        {
            ["zh_TW"] = "輸入作家名 / 作家 ID，或貼上 pawchive 作家連結",
            ["ja"] = "作家名／作家 ID を入力、または pawchive の作家リンクを貼り付け",
            ["en"] = "Enter an artist name / ID, or paste a pawchive artist link",
        },
        ["正在搜索作家…"] = new() { ["zh_TW"] = "正在搜尋作家…", ["ja"] = "作家を検索中…", ["en"] = "Searching artists…" },
        ["正在下载站点作家索引（约 15MB），首次搜索稍慢…"] = new()
        {
            ["zh_TW"] = "正在下載站點作家索引（約 15MB），首次搜尋較慢…",
            ["ja"] = "サイトの作家インデックス（約 15MB）をダウンロード中。初回検索は時間がかかります…",
            ["en"] = "Downloading the site artist index (~15MB); the first search is slower…",
        },
        ["搜索到 {n} 位作家"] = new() { ["zh_TW"] = "搜尋到 {n} 位作家", ["ja"] = "{n} 人の作家が見つかりました", ["en"] = "Found {n} artists" },
        ["索引更新于 {time}"] = new() { ["zh_TW"] = "索引更新於 {time}", ["ja"] = "インデックス更新: {time}", ["en"] = "Index updated {time}" },
        ["没有匹配的作家"] = new() { ["zh_TW"] = "沒有符合的作家", ["ja"] = "一致する作家がありません", ["en"] = "No matching artists" },
        ["正在获取作品…"] = new() { ["zh_TW"] = "正在取得作品…", ["ja"] = "作品を取得中…", ["en"] = "Loading posts…" },
        ["获取作品列表失败"] = new() { ["zh_TW"] = "取得作品清單失敗", ["ja"] = "作品リストの取得に失敗しました", ["en"] = "Failed to load the post list" },
        ["全部图片（{n} 张）"] = new() { ["zh_TW"] = "全部圖片（{n} 張）", ["ja"] = "画像すべて（{n} 枚）", ["en"] = "All images ({n})" },
        ["（下拉加载更多）"] = new() { ["zh_TW"] = "（下拉載入更多）", ["ja"] = "（スクロールで続きを読み込み）", ["en"] = " (scroll for more)" },
        ["{n} 文件"] = new() { ["zh_TW"] = "{n} 檔案", ["ja"] = "{n} ファイル", ["en"] = "{n} files" },
        ["已选 {n} 篇"] = new() { ["zh_TW"] = "已選 {n} 篇", ["ja"] = "{n} 件選択中", ["en"] = "{n} selected" },
        ["选择"] = new() { ["zh_TW"] = "選擇", ["ja"] = "選択", ["en"] = "Select" },
        ["已选"] = new() { ["zh_TW"] = "已選", ["ja"] = "選択中", ["en"] = "Selected" },
        ["查看"] = new() { ["zh_TW"] = "檢視", ["ja"] = "表示", ["en"] = "View" },
        ["全选"] = new() { ["zh_TW"] = "全選", ["ja"] = "すべて選択", ["en"] = "Select all" },
        ["清空"] = new() { ["zh_TW"] = "清空", ["ja"] = "クリア", ["en"] = "Clear" },
        ["下载选中"] = new() { ["zh_TW"] = "下載選取", ["ja"] = "選択をダウンロード", ["en"] = "Download selected" },
        ["正在加入下载队列…"] = new() { ["zh_TW"] = "正在加入下載佇列…", ["ja"] = "ダウンロードキューに追加中…", ["en"] = "Adding to the download queue…" },
        ["已加入下载：{posts} 篇作品 / {files} 个文件"] = new()
        {
            ["zh_TW"] = "已加入下載：{posts} 篇作品 / {files} 個檔案",
            ["ja"] = "ダウンロードに追加：{posts} 件の作品 / {files} ファイル",
            ["en"] = "Queued {posts} posts / {files} files",
        },
        ["，跳过 {n} 篇"] = new() { ["zh_TW"] = "，略過 {n} 篇", ["ja"] = "、{n} 件をスキップ", ["en"] = ", skipped {n}" },
        ["加入下载失败"] = new() { ["zh_TW"] = "加入下載失敗", ["ja"] = "ダウンロードへの追加に失敗しました", ["en"] = "Failed to queue the download" },
        ["{n} 篇已入库 · 共 {total} 篇"] = new()
        {
            ["zh_TW"] = "{n} 篇已入庫 · 共 {total} 篇",
            ["ja"] = "{n} 件が取り込み済み · 全 {total} 件",
            ["en"] = "{n} in library · {total} total",
        },
        ["共 {n} 位作家"] = new() { ["zh_TW"] = "共 {n} 位作家", ["ja"] = "作家 {n} 人", ["en"] = "{n} artists" },
        ["共 {n} 篇作品"] = new() { ["zh_TW"] = "共 {n} 篇作品", ["ja"] = "作品 {n} 件", ["en"] = "{n} posts" },
        ["还没有下载过 fanbox 作品，用上方搜索框找作家吧"] = new()
        {
            ["zh_TW"] = "還沒有下載過 fanbox 作品，用上方搜尋框找作家吧",
            ["ja"] = "まだ fanbox 作品をダウンロードしていません。上の検索欄から作家を探してください",
            ["en"] = "No fanbox posts downloaded yet — find an artist with the search box above",
        },
        ["作品不存在"] = new() { ["zh_TW"] = "作品不存在", ["ja"] = "作品が存在しません", ["en"] = "Post not found" },
        ["作家"] = new() { ["zh_TW"] = "作家", ["ja"] = "作家", ["en"] = "Artist" },
        ["发布日"] = new() { ["zh_TW"] = "發佈日", ["ja"] = "公開日", ["en"] = "Published" },
        ["目录"] = new() { ["zh_TW"] = "目錄", ["ja"] = "フォルダ", ["en"] = "Folder" },
        ["附件"] = new() { ["zh_TW"] = "附件", ["ja"] = "添付ファイル", ["en"] = "Attachments" },

        // ---------- 导航 / 通用 ----------
        ["R18媒体库"] = new() { ["zh_TW"] = "R18媒體庫", ["ja"] = "R18メディアライブラリ", ["en"] = "R18 Media Library" },
        ["搜索"] = new() { ["zh_TW"] = "搜尋", ["ja"] = "検索", ["en"] = "Search" },
        ["下载"] = new() { ["zh_TW"] = "下載", ["ja"] = "ダウンロード", ["en"] = "Download" },
        // 左导航文案对齐 Web 端 SECTIONS (src/Web/js/core.js)
        ["DLsite 搜索"] = new() { ["zh_TW"] = "DLsite 搜尋", ["ja"] = "DLsite 検索", ["en"] = "DLsite Search" },
        ["下载管理"] = new() { ["zh_TW"] = "下載管理", ["ja"] = "ダウンロード管理", ["en"] = "Downloads" },
        ["作品社团"] = new() { ["zh_TW"] = "作品社團", ["ja"] = "サークル", ["en"] = "Makers" },
        ["我的收藏"] = new() { ["zh_TW"] = "我的收藏", ["ja"] = "お気に入り", ["en"] = "Favorites" },
        ["系统设置"] = new() { ["zh_TW"] = "系統設定", ["ja"] = "システム設定", ["en"] = "Settings" },
        ["已下载"] = new() { ["zh_TW"] = "已下載", ["ja"] = "ダウンロード済み", ["en"] = "Downloaded" },
        ["媒体库"] = new() { ["zh_TW"] = "媒體庫", ["ja"] = "ライブラリ", ["en"] = "Library" },
        ["标签"] = new() { ["zh_TW"] = "標籤", ["ja"] = "タグ", ["en"] = "Tags" },
        ["收藏夹"] = new() { ["zh_TW"] = "收藏夾", ["ja"] = "お気に入り", ["en"] = "Favorites" },
        ["已读"] = new() { ["zh_TW"] = "已讀", ["ja"] = "既読", ["en"] = "Read" },
        ["收藏"] = new() { ["zh_TW"] = "收藏", ["ja"] = "お気に入り", ["en"] = "Favorite" },
        ["设置"] = new() { ["zh_TW"] = "設定", ["ja"] = "設定", ["en"] = "Settings" },
        ["保存"] = new() { ["zh_TW"] = "儲存", ["ja"] = "保存", ["en"] = "Save" },
        ["取消"] = new() { ["zh_TW"] = "取消", ["ja"] = "キャンセル", ["en"] = "Cancel" },
        ["删除"] = new() { ["zh_TW"] = "刪除", ["ja"] = "削除", ["en"] = "Delete" },
        ["移除"] = new() { ["zh_TW"] = "移除", ["ja"] = "削除", ["en"] = "Remove" },
        ["刷新"] = new() { ["zh_TW"] = "重新整理", ["ja"] = "更新", ["en"] = "Refresh" },
        ["测试"] = new() { ["zh_TW"] = "測試", ["ja"] = "テスト", ["en"] = "Test" },
        ["提示"] = new() { ["zh_TW"] = "提示", ["ja"] = "お知らせ", ["en"] = "Notice" },
        ["← 返回"] = new() { ["zh_TW"] = "← 返回", ["ja"] = "← 戻る", ["en"] = "← Back" },

        // ---------- 搜索页 ----------
        ["输入作品番号，例如 RJ01234567"] = new()
        {
            ["zh_TW"] = "輸入作品番號，例如 RJ01234567",
            ["ja"] = "作品番号を入力（例：RJ01234567）",
            ["en"] = "Enter work ID, e.g. RJ01234567"
        },
        ["查询"] = new() { ["zh_TW"] = "查詢", ["ja"] = "検索", ["en"] = "Search" },
        ["← 返回结果"] = new() { ["zh_TW"] = "← 返回結果", ["ja"] = "← 結果に戻る", ["en"] = "← Back to results" },
        ["返回"] = new() { ["zh_TW"] = "返回", ["ja"] = "戻る", ["en"] = "Back" },
        ["← 返回作品列表"] = new() { ["zh_TW"] = "← 返回作品列表", ["ja"] = "← 作品一覧に戻る", ["en"] = "← Back to works" },
        ["未找到匹配的作品"] = new() { ["zh_TW"] = "未找到相符的作品", ["ja"] = "一致する作品が見つかりません", ["en"] = "No matching works found" },
        ["选择下载位置"] = new() { ["zh_TW"] = "選擇下載位置", ["ja"] = "ダウンロード先を選択", ["en"] = "Choose download location" },
        ["选择下载到哪个媒体库"] = new()
        {
            ["zh_TW"] = "選擇下載到哪個媒體庫",
            ["ja"] = "ダウンロードするライブラリを選択",
            ["en"] = "Choose which library to download to"
        },
        ["选择下载文件夹"] = new() { ["zh_TW"] = "選擇下載資料夾", ["ja"] = "ダウンロードフォルダを選択", ["en"] = "Choose download folder" },
        ["正在查询…"] = new() { ["zh_TW"] = "正在查詢…", ["ja"] = "検索中…", ["en"] = "Searching…" },
        ["{count} 个文件"] = new() { ["zh_TW"] = "{count} 個檔案", ["ja"] = "{count} 個のファイル", ["en"] = "{count} files" },
        ["检测中…"] = new() { ["zh_TW"] = "檢測中…", ["ja"] = "確認中…", ["en"] = "Checking…" },
        ["检测中… {checked}/{total}"] = new()
        {
            ["zh_TW"] = "檢測中… {checked}/{total}",
            ["ja"] = "確認中… {checked}/{total}",
            ["en"] = "Checking… {checked}/{total}"
        },
        ["有效"] = new() { ["zh_TW"] = "有效", ["ja"] = "有効", ["en"] = "Valid" },
        ["已暂停校验"] = new() { ["zh_TW"] = "已暫停校驗", ["ja"] = "検証を一時停止", ["en"] = "Check paused" },
        ["失效"] = new() { ["zh_TW"] = "失效", ["ja"] = "無効", ["en"] = "Invalid" },
        ["待检测"] = new() { ["zh_TW"] = "待檢測", ["ja"] = "確認待ち", ["en"] = "Pending" },
        ["已暂停"] = new() { ["zh_TW"] = "已暫停", ["ja"] = "一時停止", ["en"] = "Paused" },
        ["部分有效 {valid}/{total}"] = new()
        {
            ["zh_TW"] = "部分有效 {valid}/{total}",
            ["ja"] = "一部有効 {valid}/{total}",
            ["en"] = "Partly valid {valid}/{total}"
        },
        ["已加入下载"] = new() { ["zh_TW"] = "已加入下載", ["ja"] = "ダウンロードに追加", ["en"] = "Added to downloads" },
        ["番号错误"] = new() { ["zh_TW"] = "番號錯誤", ["ja"] = "番号エラー", ["en"] = "Invalid number" },
        ["{id} 不是有效的番号（格式：RJ/BJ/VJ + 数字）"] = new()
        {
            ["zh_TW"] = "{id} 不是有效的番號（格式：RJ/BJ/VJ + 數字）",
            ["ja"] = "{id} は有効な番号ではありません（形式：RJ/BJ/VJ + 数字）",
            ["en"] = "{id} is not a valid number (format: RJ/BJ/VJ + digits)"
        },
        ["{id} {name}\n该作品已于 {time} 加入过下载，是否继续搜索？"] = new()
        {
            ["zh_TW"] = "{id} {name}\n該作品已於 {time} 加入過下載，是否繼續搜尋？",
            ["ja"] = "{id} {name}\nこの作品は {time} にダウンロードへ追加済みです。検索を続けますか？",
            ["en"] = "{id} {name}\nThis work was added to downloads on {time}. Continue searching?"
        },
        ["无匹配数据"] = new() { ["zh_TW"] = "無相符資料", ["ja"] = "一致するデータがありません", ["en"] = "No matching data" },
        ["该帖子中没有找到下载链接"] = new()
        {
            ["zh_TW"] = "該帖子中沒有找到下載連結",
            ["ja"] = "このスレッドにダウンロードリンクが見つかりません",
            ["en"] = "No download links found in this thread"
        },

        // ---------- 下载页 ----------
        ["debrid-link 使用量"] = new() { ["zh_TW"] = "debrid-link 使用量", ["ja"] = "debrid-link 使用量", ["en"] = "debrid-link usage" },
        ["debrid-link 使用量 --"] = new() { ["zh_TW"] = "debrid-link 使用量 --", ["ja"] = "debrid-link 使用量 --", ["en"] = "debrid-link usage --" },
        [" · {reset} 后重置"] = new() { ["zh_TW"] = " · {reset} 後重置", ["ja"] = " · {reset} 後にリセット", ["en"] = " · resets in {reset}" },
        ["开始下载"] = new() { ["zh_TW"] = "開始下載", ["ja"] = "ダウンロード開始", ["en"] = "Start" },
        ["暂停下载"] = new() { ["zh_TW"] = "暫停下載", ["ja"] = "ダウンロード停止", ["en"] = "Pause" },
        ["暂停中…"] = new() { ["zh_TW"] = "暫停中…", ["ja"] = "停止中…", ["en"] = "Pausing…" },
        ["清除已完成"] = new() { ["zh_TW"] = "清除已完成", ["ja"] = "完了分を消去", ["en"] = "Clear completed" },
        ["清空列表"] = new() { ["zh_TW"] = "清空列表", ["ja"] = "リストをクリア", ["en"] = "Clear all" },
        ["清除无可用连接"] = new() { ["zh_TW"] = "清除無可用連結", ["ja"] = "利用可能なリンクなしを消去", ["en"] = "Clear no-link" },
        ["全部重新解析"] = new() { ["zh_TW"] = "全部重新解析", ["ja"] = "すべて再解析", ["en"] = "Re-resolve all" },
        ["等待下载"] = new() { ["zh_TW"] = "等待下載", ["ja"] = "待機中", ["en"] = "Waiting" },
        ["下载中"] = new() { ["zh_TW"] = "下載中", ["ja"] = "ダウンロード中", ["en"] = "Downloading" },
        ["已完成"] = new() { ["zh_TW"] = "已完成", ["ja"] = "完了", ["en"] = "Completed" },
        // 下载列表的下载源分区标题（DownloadSectionItem）
        ["等待"] = new() { ["zh_TW"] = "等待", ["ja"] = "待機", ["en"] = "Waiting" },
        ["失败"] = new() { ["zh_TW"] = "失敗", ["ja"] = "失敗", ["en"] = "Failed" },
        ["谷歌网盘"] = new() { ["zh_TW"] = "Google 雲端硬碟", ["ja"] = "Google ドライブ", ["en"] = "Google Drive" },
        ["待解压"] = new() { ["zh_TW"] = "待解壓", ["ja"] = "解凍待ち", ["en"] = "Pending extract" },
        ["等待移动"] = new() { ["zh_TW"] = "等待移動", ["ja"] = "移動待ち", ["en"] = "Pending move" },
        ["解压中 {pct}%"] = new() { ["zh_TW"] = "解壓中 {pct}%", ["ja"] = "解凍中 {pct}%", ["en"] = "Extracting {pct}%" },
        ["移动中 {pct}%"] = new() { ["zh_TW"] = "移動中 {pct}%", ["ja"] = "移動中 {pct}%", ["en"] = "Moving {pct}%" },
        ["解析失败"] = new() { ["zh_TW"] = "解析失敗", ["ja"] = "解析失敗", ["en"] = "Parse failed" },
        ["下载中 {done}/{total}"] = new()
        {
            ["zh_TW"] = "下載中 {done}/{total}", ["ja"] = "ダウンロード中 {done}/{total}", ["en"] = "Downloading {done}/{total}"
        },
        ["等待下载 {done}/{total}"] = new()
        {
            ["zh_TW"] = "等待下載 {done}/{total}", ["ja"] = "待機中 {done}/{total}", ["en"] = "Waiting {done}/{total}"
        },
        ["{n} 个解析失败"] = new() { ["zh_TW"] = "{n} 個解析失敗", ["ja"] = "{n} 件解析失敗", ["en"] = "{n} parse failed" },
        ["流量用尽"] = new() { ["zh_TW"] = "流量用盡", ["ja"] = "通信量超過", ["en"] = "Data limit reached" },
        ["文件失效"] = new() { ["zh_TW"] = "檔案失效", ["ja"] = "ファイル失効", ["en"] = "File dead" },
        // 超时 / 连接中断自动重试 3 次仍失败（DownloadEngine.RetryFailedError）
        ["重试失败"] = new() { ["zh_TW"] = "重試失敗", ["ja"] = "再試行失敗", ["en"] = "Retries failed" },
        ["下载超时或连接中断，已自动重试 3 次仍失败（可点「全部重新解析」再试）"] = new()
        {
            ["zh_TW"] = "下載逾時或連線中斷，已自動重試 3 次仍失敗（可點「全部重新解析」再試）",
            ["ja"] = "タイムアウトまたは接続切断。自動で 3 回再試行しましたが失敗しました（「すべて再解析」で再試行できます）",
            ["en"] = "Timed out or connection dropped; failed after 3 automatic retries (use \"Re-parse all\" to try again)",
        },
        // 直链源（asmr / fanbox）源站 404：文件不存在，跳过后继续下别的文件
        ["已完成（预览图）"] = new()
        {
            ["zh_TW"] = "已完成（預覽圖）",
            ["ja"] = "完了（プレビュー画像）",
            ["en"] = "Completed (preview)",
        },
        ["源站无此文件"] = new()
            { ["zh_TW"] = "來源站無此檔案", ["ja"] = "配信元に無し", ["en"] = "Not on source" },
        ["源站没有这个文件，已跳过"] = new()
        {
            ["zh_TW"] = "來源站沒有這個檔案，已略過",
            ["ja"] = "配信元にこのファイルが存在しないためスキップしました",
            ["en"] = "Not available on the source site, skipped",
        },
        ["源站无文件"] = new()
            { ["zh_TW"] = "來源站無檔案", ["ja"] = "配信元にファイル無し", ["en"] = "No files on source" },
        ["已完成（跳过 {n}）"] = new()
        {
            ["zh_TW"] = "已完成（略過 {n}）",
            ["ja"] = "完了（{n} 件スキップ）",
            ["en"] = "Completed ({n} skipped)",
        },
        ["网盘不支持"] = new() { ["zh_TW"] = "網盤不支援", ["ja"] = "非対応ホスト", ["en"] = "Host unsupported" },
        ["需会员"] = new() { ["zh_TW"] = "需會員", ["ja"] = "会員限定", ["en"] = "Premium only" },
        ["Key 无效"] = new() { ["zh_TW"] = "Key 無效", ["ja"] = "Key 無効", ["en"] = "Bad API key" },
        ["请求频繁"] = new() { ["zh_TW"] = "請求頻繁", ["ja"] = "リクエスト過多", ["en"] = "Rate limited" },
        ["类型不支持"] = new() { ["zh_TW"] = "類型不支援", ["ja"] = "非対応形式", ["en"] = "Bad file type" },
        ["超链接数"] = new() { ["zh_TW"] = "超連結數", ["ja"] = "リンク数超過", ["en"] = "Too many links" },
        // 网盘免费下载（debrid-link 不可用时，见 FreeHostDownloader）
        ["免费间隔 {time}"] = new() { ["zh_TW"] = "免費間隔 {time}", ["ja"] = "無料待機 {time}", ["en"] = "Free wait {time}" },
        ["验证未通过"] = new() { ["zh_TW"] = "驗證未通過", ["ja"] = "認証失敗", ["en"] = "Captcha failed" },
        ["浏览器不可用"] = new() { ["zh_TW"] = "瀏覽器不可用", ["ja"] = "ブラウザ不可", ["en"] = "No browser" },
        ["免费下载失败"] = new() { ["zh_TW"] = "免費下載失敗", ["ja"] = "無料DL失敗", ["en"] = "Free DL failed" },
        ["debrid-link 不可用，且该网盘不支持免费下载（只支持 rapidgator / katfile）"] = new()
        {
            ["zh_TW"] = "debrid-link 不可用，且該網盤不支援免費下載（只支援 rapidgator / katfile）",
            ["ja"] = "debrid-link が使えず、このホストは無料ダウンロード非対応です（rapidgator / katfile のみ対応）",
            ["en"] = "debrid-link is unavailable and this host has no free download support (rapidgator / katfile only)",
        },
        ["该文件只能会员下载（rapidgator 免费账户只能下 500MB 以内的文件）"] = new()
        {
            ["zh_TW"] = "該檔案只能會員下載（rapidgator 免費帳戶只能下 500MB 以內的檔案）",
            ["ja"] = "このファイルはプレミアム限定です（rapidgator の無料枠は 500MB まで）",
            ["en"] = "Premium-only file (rapidgator free downloads are limited to 500 MB)",
        },
        ["网盘免费下载的人机验证多次没有自动通过（可点「全部重新解析」再试）"] = new()
        {
            ["zh_TW"] = "網盤免費下載的人機驗證多次沒有自動通過（可點「全部重新解析」再試）",
            ["ja"] = "無料ダウンロードの認証が何度も自動で通りませんでした（「すべて再解析」で再試行）",
            ["en"] = "The free-download captcha did not pass automatically after several tries (use Reparse all to retry)",
        },
        ["网盘免费下载需要内嵌浏览器（WebView2），这台电脑上打不开"] = new()
        {
            ["zh_TW"] = "網盤免費下載需要內嵌瀏覽器（WebView2），這台電腦上打不開",
            ["ja"] = "無料ダウンロードには内蔵ブラウザ（WebView2）が必要ですが、このPCでは起動できません",
            ["en"] = "Free downloads need the embedded browser (WebView2), which cannot start on this PC",
        },
        ["网盘免费下载页面多次没有走通（可点「全部重新解析」再试）"] = new()
        {
            ["zh_TW"] = "網盤免費下載頁面多次沒有走通（可點「全部重新解析」再試）",
            ["ja"] = "無料ダウンロードのページを何度も進められませんでした（「すべて再解析」で再試行）",
            ["en"] = "The free-download page failed several times (use Reparse all to retry)",
        },
        ["网盘免费下载"] = new() { ["zh_TW"] = "網盤免費下載", ["ja"] = "無料ダウンロード", ["en"] = "Free host download" },
        ["下载间隔 (分钟)"] = new() { ["zh_TW"] = "下載間隔 (分鐘)", ["ja"] = "間隔 (分)", ["en"] = "Interval (min)" },
        ["debrid-link 没配置、Key 失效或会员到期时，rapidgator / katfile 链接改用网盘自己的免费下载（每个网盘按间隔一个一个下）"] = new()
        {
            ["zh_TW"] = "debrid-link 沒設定、Key 失效或會員到期時，rapidgator / katfile 連結改用網盤自己的免費下載（每個網盤按間隔一個一個下）",
            ["ja"] = "debrid-link 未設定・Key 無効・会員期限切れの時、rapidgator / katfile はホストの無料ダウンロードを使います（ホストごとに間隔を空けて1つずつ）",
            ["en"] = "When debrid-link is not set up, its key is invalid or premium has expired, rapidgator / katfile links use the host's own free download (one file per host per interval)",
        },
        ["同一个网盘两次免费下载之间至少隔多久；rapidgator 免费账户为 120 分钟"] = new()
        {
            ["zh_TW"] = "同一個網盤兩次免費下載之間至少隔多久；rapidgator 免費帳戶為 120 分鐘",
            ["ja"] = "同じホストで無料ダウンロードを行う最短間隔。rapidgator の無料枠は 120 分",
            ["en"] = "Minimum gap between two free downloads from the same host; rapidgator free is 120 minutes",
        },
        ["点击查看各网盘流量详情"] = new() { ["zh_TW"] = "點擊查看各網盤流量詳情", ["ja"] = "各ホストの通信量詳細を表示", ["en"] = "Click for per-host data usage" },
        ["debrid-link 流量详情"] = new() { ["zh_TW"] = "debrid-link 流量詳情", ["ja"] = "debrid-link 通信量詳細", ["en"] = "debrid-link data usage" },
        ["总用量 {pct}%"] = new() { ["zh_TW"] = "總用量 {pct}%", ["ja"] = "合計 {pct}%", ["en"] = "Total {pct}%" },
        ["（{reset} 后重置）"] = new() { ["zh_TW"] = "（{reset} 後重置）", ["ja"] = "（{reset} 後にリセット）", ["en"] = " (resets in {reset})" },
        ["各网盘用量："] = new() { ["zh_TW"] = "各網盤用量：", ["ja"] = "ホスト別使用量：", ["en"] = "Per-host usage:" },
        ["流量已用尽的网盘："] = new() { ["zh_TW"] = "流量已用盡的網盤：", ["ja"] = "通信量を使い切ったホスト：", ["en"] = "Hosts out of data:" },
        ["账户总流量（所有网盘）"] = new() { ["zh_TW"] = "帳戶總流量（所有網盤）", ["ja"] = "アカウント全体（全ホスト）", ["en"] = "Account total (all hosts)" },
        ["暂无网盘流量用尽"] = new() { ["zh_TW"] = "暫無網盤流量用盡", ["ja"] = "通信量を使い切ったホストはありません", ["en"] = "No hosts are out of data" },
        ["重新解析"] = new() { ["zh_TW"] = "重新解析", ["ja"] = "再解析", ["en"] = "Re-parse" },
        ["重新搜索"] = new() { ["zh_TW"] = "重新搜尋", ["ja"] = "再検索", ["en"] = "Re-search" },
        ["将删除 {id} 已下载的分卷与文件夹，并重新搜索。是否继续？"] = new()
        {
            ["zh_TW"] = "將刪除 {id} 已下載的分卷與資料夾，並重新搜尋。是否繼續？",
            ["ja"] = "{id} のダウンロード済み分割ファイルとフォルダを削除して再検索します。続行しますか？",
            ["en"] = "This will delete the downloaded parts and folder of {id}, then search again. Continue?"
        },
        ["未知({status})"] = new() { ["zh_TW"] = "未知({status})", ["ja"] = "不明({status})", ["en"] = "Unknown({status})" },
        ["番号 / 文件"] = new() { ["zh_TW"] = "番號 / 檔案", ["ja"] = "番号 / ファイル", ["en"] = "ID / File" },
        ["下载进度"] = new() { ["zh_TW"] = "下載進度", ["ja"] = "進捗", ["en"] = "Progress" },
        ["速度"] = new() { ["zh_TW"] = "速度", ["ja"] = "速度", ["en"] = "Speed" },
        ["状态"] = new() { ["zh_TW"] = "狀態", ["ja"] = "状態", ["en"] = "Status" },
        ["清空下载列表"] = new() { ["zh_TW"] = "清空下載列表", ["ja"] = "ダウンロードリストをクリア", ["en"] = "Clear download list" },
        ["确定要清空整个下载列表吗？等待中的任务也会被删除。"] = new()
        {
            ["zh_TW"] = "確定要清空整個下載列表嗎？等待中的任務也會被刪除。",
            ["ja"] = "ダウンロードリスト全体をクリアしますか？待機中のタスクも削除されます。",
            ["en"] = "Clear the entire download list? Pending tasks will also be removed."
        },

        // ---------- 已下载页 ----------
        ["RJ号"] = new() { ["zh_TW"] = "RJ號", ["ja"] = "RJ番号", ["en"] = "RJ number" },
        ["作品名称"] = new() { ["zh_TW"] = "作品名稱", ["ja"] = "作品名", ["en"] = "Title" },
        ["类型"] = new() { ["zh_TW"] = "類型", ["ja"] = "ジャンル", ["en"] = "Genre" },
        ["下载时间"] = new() { ["zh_TW"] = "下載時間", ["ja"] = "ダウンロード日時", ["en"] = "Download time" },
        ["标记为已品悦"] = new() { ["zh_TW"] = "標記為已品悦", ["ja"] = "鑑賞済みにする", ["en"] = "Mark as enjoyed" },
        ["下载ID"] = new() { ["zh_TW"] = "下載ID", ["ja"] = "ダウンロードID", ["en"] = "Download ID" },
        ["下载完成"] = new() { ["zh_TW"] = "下載完成", ["ja"] = "ダウンロード完了", ["en"] = "Completed" },
        ["待入库"] = new() { ["zh_TW"] = "待入庫", ["ja"] = "ライブラリ未登録", ["en"] = "Not in library" },
        ["标记为下载完成"] = new() { ["zh_TW"] = "標記為下載完成", ["ja"] = "完了にする", ["en"] = "Mark as completed" },
        ["全部"] = new() { ["zh_TW"] = "全部", ["ja"] = "すべて", ["en"] = "All" },
        ["已品悦"] = new() { ["zh_TW"] = "已品悦", ["ja"] = "鑑賞済み", ["en"] = "Enjoyed" },
        ["共 {n} 个作品"] = new() { ["zh_TW"] = "共 {n} 個作品", ["ja"] = "作品 {n} 件", ["en"] = "{n} works total" },

        // ---------- 组合下载（对齐 Web 端 combineDownload / /api/combine）----------
        ["组合下载"] = new() { ["zh_TW"] = "組合下載", ["ja"] = "分卷を組み合わせDL", ["en"] = "Combine download" },
        ["组合中…"] = new() { ["zh_TW"] = "組合中…", ["ja"] = "組み合わせ中…", ["en"] = "Combining…" },
        ["该帖未找到可用网盘链接"] = new()
        {
            ["zh_TW"] = "該帖未找到可用網盤連結",
            ["ja"] = "この投稿に利用可能なアップローダーのリンクがありません",
            ["en"] = "No usable host links found in this post"
        },
        ["所有网盘分卷均失效，无可用下载"] = new()
        {
            ["zh_TW"] = "所有網盤分卷均失效，無可用下載",
            ["ja"] = "すべての分卷が無効で、ダウンロードできません",
            ["en"] = "All volumes are dead on every host — nothing to download"
        },
        ["仅凑齐 {have}/{total} 个分卷（部分分卷在所有网盘均失效），无法组合完整下载"] = new()
        {
            ["zh_TW"] = "僅湊齊 {have}/{total} 個分卷（部分分卷在所有網盤均失效），無法組合完整下載",
            ["ja"] = "分卷 {have}/{total} のみ確保（一部が全ホストで無効）— 完全な組み合わせができません",
            ["en"] = "Only {have}/{total} volumes available (some dead on every host) — cannot assemble a complete download"
        },

        // ---------- 媒体库页 ----------
        ["搜索 RJ号 / 作品名 / 社团"] = new()
        {
            ["zh_TW"] = "搜尋 RJ號 / 作品名 / 社團",
            ["ja"] = "RJ番号 / 作品名 / サークルで検索",
            ["en"] = "Search RJ / title / maker"
        },
        ["搜索 下载ID / 作品名 / 社团"] = new()
        {
            ["zh_TW"] = "搜尋 下載ID / 作品名 / 社團",
            ["ja"] = "ダウンロードID / 作品名 / サークルで検索",
            ["en"] = "Search download ID / title / maker"
        },
        ["作品标签"] = new() { ["zh_TW"] = "作品標籤", ["ja"] = "作品タグ", ["en"] = "Tags" },
        ["媒体库设置"] = new() { ["zh_TW"] = "媒體庫設定", ["ja"] = "ライブラリ設定", ["en"] = "Library settings" },
        ["打开文件夹"] = new() { ["zh_TW"] = "開啟資料夾", ["ja"] = "フォルダを開く", ["en"] = "Open folder" },
        ["移动媒体库"] = new() { ["zh_TW"] = "移動媒體庫", ["ja"] = "ライブラリを移動", ["en"] = "Move library" },
        ["作品详情"] = new() { ["zh_TW"] = "作品詳情", ["ja"] = "作品詳細", ["en"] = "Details" },
        ["作品文件"] = new() { ["zh_TW"] = "作品檔案", ["ja"] = "作品ファイル", ["en"] = "Files" },
        ["跳转作品"] = new() { ["zh_TW"] = "跳轉作品", ["ja"] = "作品へ移動", ["en"] = "Open work" },
        ["删除作品"] = new() { ["zh_TW"] = "刪除作品", ["ja"] = "作品を削除", ["en"] = "Delete work" },
        ["解压密码库"] = new()
        {
            ["zh_TW"] = "解壓密碼庫", ["ja"] = "解凍パスワード一覧", ["en"] = "Archive passwords",
        },
        ["添加一条密码"] = new()
        {
            ["zh_TW"] = "新增一條密碼", ["ja"] = "パスワードを追加", ["en"] = "Add a password",
        },
        ["删除这条密码"] = new()
        {
            ["zh_TW"] = "刪除這條密碼", ["ja"] = "このパスワードを削除", ["en"] = "Remove this password",
        },
        ["作品信息"] = new() { ["zh_TW"] = "作品資訊", ["ja"] = "作品情報", ["en"] = "Work info" },
        ["查看图片"] = new() { ["zh_TW"] = "查看圖片", ["ja"] = "画像を表示", ["en"] = "View image" },
        ["{count} 项"] = new() { ["zh_TW"] = "{count} 項", ["ja"] = "{count} 件", ["en"] = "{count} items" },
        ["上一张"] = new() { ["zh_TW"] = "上一張", ["ja"] = "前へ", ["en"] = "Previous" },
        ["下一张"] = new() { ["zh_TW"] = "下一張", ["ja"] = "次へ", ["en"] = "Next" },
        ["无法显示该图片"] = new() { ["zh_TW"] = "無法顯示該圖片", ["ja"] = "この画像を表示できません", ["en"] = "Cannot display this image" },
        ["播放"] = new() { ["zh_TW"] = "播放", ["ja"] = "再生", ["en"] = "Play" },
        ["暂停"] = new() { ["zh_TW"] = "暫停", ["ja"] = "一時停止", ["en"] = "Pause" },
        ["移动中…"] = new() { ["zh_TW"] = "移動中…", ["ja"] = "移動中…", ["en"] = "Moving…" },
        ["删除中…"] = new() { ["zh_TW"] = "刪除中…", ["ja"] = "削除中…", ["en"] = "Deleting…" },
        ["确定删除《{name}》吗？\n作品文件夹内的全部文件与数据库记录都会被删除，且无法恢复。"] = new()
        {
            ["zh_TW"] = "確定刪除《{name}》嗎？\n作品資料夾內的全部檔案與資料庫記錄都會被刪除，且無法復原。",
            ["ja"] = "《{name}》を削除しますか？\n作品フォルダ内の全ファイルとデータベース記録が削除され、元に戻せません。",
            ["en"] = "Delete “{name}”?\nAll files in the work folder and its database records will be removed permanently."
        },
        ["选择要移动到的媒体库"] = new()
        {
            ["zh_TW"] = "選擇要移動到的媒體庫",
            ["ja"] = "移動先のライブラリを選択",
            ["en"] = "Choose the library to move to"
        },
        ["选择目标文件夹"] = new() { ["zh_TW"] = "選擇目標資料夾", ["ja"] = "移動先フォルダを選択", ["en"] = "Choose target folder" },
        ["没有可移动到的其它媒体库"] = new()
        {
            ["zh_TW"] = "沒有可移動到的其它媒體庫",
            ["ja"] = "移動できる他のライブラリがありません",
            ["en"] = "No other library to move to"
        },
        ["确定将 {id} 移动到媒体库“{lib}”吗？"] = new()
        {
            ["zh_TW"] = "確定將 {id} 移動到媒體庫「{lib}」嗎？",
            ["ja"] = "{id} をライブラリ「{lib}」へ移動しますか？",
            ["en"] = "Move {id} to library \"{lib}\"?"
        },
        ["已移动到新媒体库"] = new() { ["zh_TW"] = "已移動到新媒體庫", ["ja"] = "新しいライブラリへ移動しました", ["en"] = "Moved to the new library" },
        ["作品文件夹不存在，无法移动"] = new()
        {
            ["zh_TW"] = "作品資料夾不存在，無法移動",
            ["ja"] = "作品フォルダが存在しないため移動できません",
            ["en"] = "Work folder does not exist; cannot move"
        },
        ["全年龄"] = new() { ["zh_TW"] = "全年齡", ["ja"] = "全年齢", ["en"] = "All ages" },
        ["社团"] = new() { ["zh_TW"] = "社團", ["ja"] = "サークル", ["en"] = "Maker" },
        ["系列"] = new() { ["zh_TW"] = "系列", ["ja"] = "シリーズ", ["en"] = "Series" },
        ["剧本"] = new() { ["zh_TW"] = "劇本", ["ja"] = "シナリオ", ["en"] = "Scenario" },
        ["插画"] = new() { ["zh_TW"] = "插畫", ["ja"] = "イラスト", ["en"] = "Illustration" },
        ["声优"] = new() { ["zh_TW"] = "聲優", ["ja"] = "声優", ["en"] = "Voice actor" },
        ["販売日"] = new() { ["zh_TW"] = "販售日", ["ja"] = "販売日", ["en"] = "Release date" },
        ["年龄分级"] = new() { ["zh_TW"] = "年齡分級", ["ja"] = "年齢区分", ["en"] = "Age rating" },
        ["作品形式"] = new() { ["zh_TW"] = "作品形式", ["ja"] = "作品形式", ["en"] = "Work type" },
        ["文件容量"] = new() { ["zh_TW"] = "檔案容量", ["ja"] = "ファイル容量", ["en"] = "File size" },
        ["简介"] = new() { ["zh_TW"] = "簡介", ["ja"] = "紹介", ["en"] = "Description" },
        ["未知社团"] = new() { ["zh_TW"] = "未知社團", ["ja"] = "不明なサークル", ["en"] = "Unknown maker" },
        ["{count} 个作品"] = new() { ["zh_TW"] = "{count} 個作品", ["ja"] = "作品 {count} 件", ["en"] = "{count} works" },
        ["{works} 个作品 · {folders} 个文件夹"] = new()
        {
            ["zh_TW"] = "{works} 個作品 · {folders} 個資料夾",
            ["ja"] = "作品 {works} 件 · フォルダ {folders} 個",
            ["en"] = "{works} works · {folders} folders"
        },
        ["个媒体库"] = new() { ["zh_TW"] = "個媒體庫", ["ja"] = "ライブラリ", ["en"] = "libraries" },
        ["作品数 多→少"] = new() { ["zh_TW"] = "作品數 多→少", ["ja"] = "作品数 多→少", ["en"] = "Works ↓" },
        ["作品数 少→多"] = new() { ["zh_TW"] = "作品數 少→多", ["ja"] = "作品数 少→多", ["en"] = "Works ↑" },
        ["社团名 A→Z"] = new() { ["zh_TW"] = "社團名 A→Z", ["ja"] = "サークル名 A→Z", ["en"] = "Maker A→Z" },
        ["发售日 新→旧"] = new() { ["zh_TW"] = "發售日 新→舊", ["ja"] = "販売日 新→旧", ["en"] = "Date ↓" },
        ["发售日 旧→新"] = new() { ["zh_TW"] = "發售日 舊→新", ["ja"] = "販売日 旧→新", ["en"] = "Date ↑" },
        ["RJ号 新→旧"] = new() { ["zh_TW"] = "RJ號 新→舊", ["ja"] = "RJ番号 新→旧", ["en"] = "RJ ↓" },
        ["RJ号 旧→新"] = new() { ["zh_TW"] = "RJ號 舊→新", ["ja"] = "RJ番号 旧→新", ["en"] = "RJ ↑" },
        ["个社团"] = new() { ["zh_TW"] = "個社團", ["ja"] = "サークル", ["en"] = "makers" },
        ["个标签"] = new() { ["zh_TW"] = "個標籤", ["ja"] = "タグ", ["en"] = "tags" },
        ["个形式"] = new() { ["zh_TW"] = "個形式", ["ja"] = "形式", ["en"] = "types" },
        ["共 {total} {unit}，匹配 {shown} 个"] = new()
        {
            ["zh_TW"] = "共 {total} {unit}，相符 {shown} 個",
            ["ja"] = "{unit} {total} 件、一致 {shown} 件",
            ["en"] = "{total} {unit}, {shown} matched"
        },
        ["共 {total} {unit}，{works} 个作品"] = new()
        {
            ["zh_TW"] = "共 {total} {unit}，{works} 個作品",
            ["ja"] = "{unit} {total} 件、作品 {works} 件",
            ["en"] = "{total} {unit}, {works} works"
        },
        ["共 {total} 个作品，匹配 {matched} 个"] = new()
        {
            ["zh_TW"] = "共 {total} 個作品，相符 {matched} 個",
            ["ja"] = "作品 {total} 件、一致 {matched} 件",
            ["en"] = "{total} works, {matched} matched"
        },
        ["共 {total} 个作品"] = new() { ["zh_TW"] = "共 {total} 個作品", ["ja"] = "作品 {total} 件", ["en"] = "{total} works" },
        ["（已加载 {loaded}）"] = new() { ["zh_TW"] = "（已載入 {loaded}）", ["ja"] = "（読み込み済み {loaded}）", ["en"] = " (loaded {loaded})" },

        // ---------- 媒体库管理（内联于系统设置页）----------
        ["新建媒体库"] = new() { ["zh_TW"] = "新增媒體庫", ["ja"] = "ライブラリを新規作成", ["en"] = "New library" },
        ["还没有媒体库，点击\"新建媒体库\"创建。"] = new()
        {
            ["zh_TW"] = "還沒有媒體庫，點擊「新增媒體庫」建立。",
            ["ja"] = "ライブラリがまだありません。「ライブラリを新規作成」をクリックしてください。",
            ["en"] = "No libraries yet. Click \"New library\" to create one."
        },
        ["{n} 个文件夹"] = new() { ["zh_TW"] = "{n} 個資料夾", ["ja"] = "{n} 個のフォルダ", ["en"] = "{n} folders" },
        ["添加文件夹"] = new() { ["zh_TW"] = "新增資料夾", ["ja"] = "フォルダを追加", ["en"] = "Add folder" },
        ["扫描元数据"] = new() { ["zh_TW"] = "掃描中繼資料", ["ja"] = "メタデータをスキャン", ["en"] = "Scan metadata" },
        ["重新扫描元数据"] = new() { ["zh_TW"] = "重新掃描中繼資料", ["ja"] = "メタデータを再スキャン", ["en"] = "Rescan metadata" },
        ["媒体库名称："] = new() { ["zh_TW"] = "媒體庫名稱：", ["ja"] = "ライブラリ名：", ["en"] = "Library name:" },
        ["已存在同名媒体库。"] = new() { ["zh_TW"] = "已存在同名媒體庫。", ["ja"] = "同じ名前のライブラリが既に存在します。", ["en"] = "A library with this name already exists." },
        ["选择媒体库文件夹"] = new() { ["zh_TW"] = "選擇媒體庫資料夾", ["ja"] = "ライブラリフォルダを選択", ["en"] = "Choose library folder" },
        ["该文件夹已在媒体库\"{name}\"中。"] = new()
        {
            ["zh_TW"] = "該資料夾已在媒體庫「{name}」中。",
            ["ja"] = "このフォルダは既にライブラリ「{name}」に含まれています。",
            ["en"] = "This folder is already in library \"{name}\"."
        },
        ["删除媒体库"] = new() { ["zh_TW"] = "刪除媒體庫", ["ja"] = "ライブラリを削除", ["en"] = "Delete library" },
        ["确定删除媒体库\"{name}\"吗？\n不会删除本地文件，已导入的作品记录保留。"] = new()
        {
            ["zh_TW"] = "確定刪除媒體庫「{name}」嗎？\n不會刪除本機檔案，已匯入的作品記錄會保留。",
            ["ja"] = "ライブラリ「{name}」を削除しますか？\nローカルファイルは削除されず、取り込み済みの作品記録は保持されます。",
            ["en"] = "Delete library \"{name}\"?\nLocal files will not be deleted; imported work records are kept."
        },
        ["正在扫描媒体库\"{name}\"…"] = new()
        {
            ["zh_TW"] = "正在掃描媒體庫「{name}」…",
            ["ja"] = "ライブラリ「{name}」をスキャン中…",
            ["en"] = "Scanning library \"{name}\"…"
        },
        ["已导入 {total} 个作品（新增 {added}），正在从 DL API 补全数据…"] = new()
        {
            ["zh_TW"] = "已匯入 {total} 個作品（新增 {added}），正在從 DL API 補全資料…",
            ["ja"] = "{total} 件の作品を取り込みました（新規 {added}）。DL API からデータを補完中…",
            ["en"] = "Imported {total} works ({added} new), filling data from DL API…"
        },
        ["正在抓取 DLsite 作品页元数据与图片…"] = new()
        {
            ["zh_TW"] = "正在抓取 DLsite 作品頁中繼資料與圖片…",
            ["ja"] = "DLsite 作品ページのメタデータと画像を取得中…",
            ["en"] = "Fetching DLsite work page metadata and images…"
        },
        ["扫描完成：导入 {total} 个，API 补全 {filled} 个，作品页补全 {page_filled} 个（失败 {page_missed}）"] = new()
        {
            ["zh_TW"] = "掃描完成：匯入 {total} 個，API 補全 {filled} 個，作品頁補全 {page_filled} 個（失敗 {page_missed}）",
            ["ja"] = "スキャン完了：取り込み {total} 件、API 補完 {filled} 件、作品ページ補完 {page_filled} 件（失敗 {page_missed}）",
            ["en"] = "Scan complete: imported {total}, API filled {filled}, page filled {page_filled} ({page_missed} failed)"
        },
        ["DL API 数据补全中… {i}/{total}"] = new()
        {
            ["zh_TW"] = "DL API 資料補全中… {i}/{total}",
            ["ja"] = "DL API データ補完中… {i}/{total}",
            ["en"] = "Filling data from DL API… {i}/{total}"
        },
        ["作品页抓取中… {i}/{total}（{rj}）"] = new()
        {
            ["zh_TW"] = "作品頁抓取中… {i}/{total}（{rj}）",
            ["ja"] = "作品ページ取得中… {i}/{total}（{rj}）",
            ["en"] = "Fetching work pages… {i}/{total} ({rj})"
        },

        // ---------- 设置页 ----------
        ["下载路径"] = new() { ["zh_TW"] = "下載路徑", ["ja"] = "ダウンロード先", ["en"] = "Download path" },
        ["缓存路径"] = new() { ["zh_TW"] = "快取路徑", ["ja"] = "キャッシュ先", ["en"] = "Cache path" },
        ["代理"] = new() { ["zh_TW"] = "代理", ["ja"] = "プロキシ", ["en"] = "Proxy" },
        ["Debrid-Link 下载中转站"] = new() { ["zh_TW"] = "Debrid-Link 下載中轉站", ["ja"] = "Debrid-Link 中継", ["en"] = "Debrid-Link relay" },
        ["下载选项"] = new() { ["zh_TW"] = "下載選項", ["ja"] = "ダウンロード設定", ["en"] = "Download options" },
        ["自动下载"] = new() { ["zh_TW"] = "自動下載", ["ja"] = "自動ダウンロード", ["en"] = "Auto download" },
        ["自动解压"] = new() { ["zh_TW"] = "自動解壓", ["ja"] = "自動解凍", ["en"] = "Auto extract" },
        ["文件夹命名"] = new() { ["zh_TW"] = "資料夾命名", ["ja"] = "フォルダ命名", ["en"] = "Folder naming" },
        ["单文件线程数"] = new() { ["zh_TW"] = "單檔執行緒數", ["ja"] = "ファイルごとのスレッド数", ["en"] = "Threads per file" },
        ["单个文件分成多少段并发下载（多线程下载），1 为不分段"] = new()
        {
            ["zh_TW"] = "單個檔案分成多少段並發下載（多執行緒下載），1 為不分段",
            ["ja"] = "1 つのファイルを何分割して並列ダウンロードするか（マルチスレッド）。1 で分割なし",
            ["en"] = "How many segments to split each file into for concurrent download; 1 means no split"
        },
        ["单个文件分成多少段并发下载（多线程下载），1 为不分段，最大 16"] = new()
        {
            ["zh_TW"] = "單個檔案分成多少段並發下載（多執行緒下載），1 為不分段，最大 16",
            ["ja"] = "1 つのファイルを何分割して並列ダウンロードするか（マルチスレッド）。1 で分割なし、最大 16",
            ["en"] = "How many segments to split each file into for concurrent download; 1 means no split, max 16"
        },
        ["查询线程数"] = new() { ["zh_TW"] = "查詢執行緒數", ["ja"] = "検索スレッド数", ["en"] = "Query threads" },
        ["最低速度 (KB/s)"] = new() { ["zh_TW"] = "最低速度 (KB/s)", ["ja"] = "最低速度 (KB/s)", ["en"] = "Min speed (KB/s)" },
        ["持续低于该速度 30 秒后自动重试，0 表示不限制"] = new()
        {
            ["zh_TW"] = "持續低於該速度 30 秒後自動重試，0 表示不限制",
            ["ja"] = "この速度を 30 秒下回ると自動で再試行。0 は無制限",
            ["en"] = "Auto-retry after staying below this speed for 30s; 0 means no limit"
        },
        ["速度限制 (KB/s)"] = new() { ["zh_TW"] = "速度限制 (KB/s)", ["ja"] = "速度制限 (KB/s)", ["en"] = "Speed limit (KB/s)" },
        ["下载总速度上限，0 表示不限速"] = new()
        {
            ["zh_TW"] = "下載總速度上限，0 表示不限速",
            ["ja"] = "ダウンロード総速度の上限。0 は無制限",
            ["en"] = "Total download speed cap; 0 means unlimited"
        },
        ["系统"] = new() { ["zh_TW"] = "系統", ["ja"] = "システム", ["en"] = "System" },
        ["日志级别"] = new() { ["zh_TW"] = "日誌等級", ["ja"] = "ログレベル", ["en"] = "Log level" },
        ["解压编码"] = new() { ["zh_TW"] = "解壓編碼", ["ja"] = "解凍エンコード", ["en"] = "Extract encoding" },
        ["API 地址"] = new() { ["zh_TW"] = "API 位址", ["ja"] = "API アドレス", ["en"] = "API address" },
        ["语言"] = new() { ["zh_TW"] = "語言", ["ja"] = "言語", ["en"] = "Language" },
        ["开启"] = new() { ["zh_TW"] = "開啟", ["ja"] = "オン", ["en"] = "On" },
        ["关闭"] = new() { ["zh_TW"] = "關閉", ["ja"] = "オフ", ["en"] = "Off" },
        ["测试成功"] = new() { ["zh_TW"] = "測試成功", ["ja"] = "テスト成功", ["en"] = "Test succeeded" },
        ["测试失败"] = new() { ["zh_TW"] = "測試失敗", ["ja"] = "テスト失敗", ["en"] = "Test failed" },
        ["已连接 debrid-link\n账户: {account}"] = new()
        {
            ["zh_TW"] = "已連接 debrid-link\n帳戶: {account}",
            ["ja"] = "debrid-link に接続しました\nアカウント: {account}",
            ["en"] = "Connected to debrid-link\nAccount: {account}"
        },
        ["API Key 无效或网络不可用"] = new()
        {
            ["zh_TW"] = "API Key 無效或網路不可用",
            ["ja"] = "API Key が無効か、ネットワークが利用できません",
            ["en"] = "Invalid API Key or network unavailable"
        },
        ["选择下载路径"] = new() { ["zh_TW"] = "選擇下載路徑", ["ja"] = "ダウンロード先を選択", ["en"] = "Choose download path" },
        ["选择缓存路径"] = new() { ["zh_TW"] = "選擇快取路徑", ["ja"] = "キャッシュ先を選択", ["en"] = "Choose cache path" },

        // ---------- 图床存储（设置页）----------
        ["图床存储"] = new() { ["zh_TW"] = "圖床儲存", ["ja"] = "画像ホスティング", ["en"] = "Image hosting" },
        ["启用"] = new() { ["zh_TW"] = "啟用", ["ja"] = "有効", ["en"] = "Enabled" },
        ["项目"] = new() { ["zh_TW"] = "專案", ["ja"] = "プロジェクト", ["en"] = "Project" },
        ["服务地址"] = new() { ["zh_TW"] = "服務位址", ["ja"] = "サービスアドレス", ["en"] = "Service URL" },
        ["测试连接"] = new() { ["zh_TW"] = "測試連線", ["ja"] = "接続テスト", ["en"] = "Test connection" },
        ["迁移封面"] = new() { ["zh_TW"] = "遷移封面", ["ja"] = "カバーを移行", ["en"] = "Migrate covers" },
        ["测试中…"] = new() { ["zh_TW"] = "測試中…", ["ja"] = "テスト中…", ["en"] = "Testing…" },
        ["请先启用图床存储"] = new()
        {
            ["zh_TW"] = "請先啟用圖床儲存",
            ["ja"] = "先に画像ホスティングを有効にしてください",
            ["en"] = "Enable image hosting first"
        },
        ["⏳ 迁移中 "] = new() { ["zh_TW"] = "⏳ 遷移中 ", ["ja"] = "⏳ 移行中 ", ["en"] = "⏳ Migrating " },
        ["✓ 已连接（图床现有 {count} 张）"] = new()
        {
            ["zh_TW"] = "✓ 已連線（圖床現有 {count} 張）",
            ["ja"] = "✓ 接続済み（画像ホスト上に {count} 枚）",
            ["en"] = "✓ Connected ({count} images on host)"
        },
    };
}
