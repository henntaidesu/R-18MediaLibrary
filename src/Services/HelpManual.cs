using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using R18MediaLibrary.Core;

namespace R18MediaLibrary.Services;

/// <summary>
/// 程序内「使用帮助」：把 docs/使用手册*.md 按「## N. 标题」拆成章节，供两端的 ? 按钮按页面打开对应章节。
///
/// 手册只有 docs/ 下那一份（中 / 日 / 英三个文件），作为嵌入资源打进程序（csproj 的 Help.* 资源），
/// 不在程序里另存一份拆好的副本——改手册即改帮助，两边不会走样。
/// 拆分时丢掉第一章之前的文档头（语言切换、核对日期、目录）与最后的附录（源码核对表），那些只对读文档的人有意义。
///
/// Markdown 只解析手册实际用到的子集（标题、段落、有序/无序列表、表格、代码块、整行图片，
/// 行内的粗体、行内代码、链接），解析成 <see cref="Block"/> 模型：
/// Web 端由 <see cref="RenderHtml"/> 转成 HTML（/api/help），桌面端 HelpPanel 转成 FlowDocument，两端共用这一个解析器。
/// 正文里的「第 N 节」「section N」「第 N 節」会解析成章节跳转（<see cref="Inline.Chapter"/>）。
/// </summary>
internal static class HelpManual
{
    /// <summary>帮助可选的语言（手册只有这三种；繁中界面读简中手册）。</summary>
    public static readonly (string Code, string Name)[] Languages = [("zh", "中文"), ("ja", "日本語"), ("en", "English")];

    /// <summary>界面语言 → 手册语言。</summary>
    public static string LangFor(string uiLang) => uiLang switch
    {
        "ja" => "ja",
        "en" => "en",
        _ => "zh",
    };

    public static string NormalizeLang(string? lang) =>
        Languages.Any(l => l.Code == lang) ? lang! : LangFor(AppConfig.Language);

    // ---------- 模型 ----------

    public sealed record Chapter(string No, string Title, List<Block> Blocks);

    public abstract record Block;

    /// <summary>章内小标题；Id 是标题开头的编号（如 "11.5"），供 ? 按钮直达小节，没有编号则为空串。</summary>
    public sealed record Heading(int Level, string Id, List<Inline> Text) : Block;

    public sealed record Para(List<Inline> Text) : Block;

    public sealed record ListBlock(bool Ordered, List<List<Inline>> Items) : Block;

    public sealed record Table(List<List<Inline>> Header, List<List<List<Inline>>> Rows) : Block;

    public sealed record Code(string Text) : Block;

    /// <summary>整行图片；Name 只留文件名（docs/assets/manual/ 下的截图），由 <see cref="Image"/> 取字节。</summary>
    public sealed record Picture(string Alt, string Name) : Block;

    /// <summary>行内片段：普通文字 / 粗体 / 行内代码 / 外链（Href）/ 章节跳转（Chapter）。</summary>
    public sealed record Inline(string Text, bool Bold = false, bool IsCode = false, string? Href = null, string? Chapter = null);

    // ---------- 读取 ----------

    private static readonly ConcurrentDictionary<string, List<Chapter>> Cache = new();

    /// <summary>取某语言的全部章节（进程级缓存；资源缺失时返回空表）。</summary>
    public static List<Chapter> Chapters(string lang) =>
        Cache.GetOrAdd(NormalizeLang(lang), l => Parse(ReadResource($"Help.manual.{l}.md") is { } b
            ? Encoding.UTF8.GetString(b)
            : ""));

    private static readonly Regex ImageName = new(@"^[A-Za-z0-9_-]+\.png$", RegexOptions.Compiled);

    /// <summary>取手册截图的字节；文件名只放行「字母数字.png」，免得借此读到任意嵌入资源。</summary>
    public static byte[]? Image(string name) =>
        ImageName.IsMatch(name) ? ReadResource("Help.img." + name) : null;

    private static byte[]? ReadResource(string logicalSuffix)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + logicalSuffix, StringComparison.OrdinalIgnoreCase));
        if (name == null)
            return null;
        using var stream = asm.GetManifestResourceStream(name);
        if (stream == null)
            return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ---------- 解析 ----------

    private static readonly Regex ChapterHead = new(@"^## (\d+)\.\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex SubHead = new(@"^(#{3,6})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex HeadNo = new(@"^(\d+(?:\.\d+)+)\s", RegexOptions.Compiled);
    private static readonly Regex PictureLine = new(@"^!\[([^\]]*)\]\(([^)]+)\)\s*$", RegexOptions.Compiled);
    private static readonly Regex OrderedItem = new(@"^\d+\.\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    // 正文里的章节引用：中文「第 3 节」、日文「第 3 節」、英文「section 3」
    private static readonly Regex XRef = new(@"第\s*(\d+)\s*[节節]|[Ss]ection (\d+)", RegexOptions.Compiled);

    internal static List<Chapter> Parse(string text)
    {
        var chapters = new List<Chapter>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        string? no = null, title = null;
        var body = new List<string>();
        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                if (no != null)
                    chapters.Add(new Chapter(no, title!, ParseBlocks(body)));
                body.Clear();
                var m = ChapterHead.Match(line);
                if (!m.Success)
                {
                    // 第一章之前的「## 目录」跳过；章节之后的不编号二级标题就是附录，到此为止
                    if (chapters.Count > 0)
                        return chapters;
                    no = null;
                    continue;
                }
                no = m.Groups[1].Value;
                title = m.Groups[2].Value;
                continue;
            }
            if (no != null)
                body.Add(line);
        }
        if (no != null)
            chapters.Add(new Chapter(no, title!, ParseBlocks(body)));
        return chapters;
    }

    private static List<Block> ParseBlocks(List<string> lines)
    {
        var blocks = new List<Block>();
        var para = new List<string>();

        void FlushPara()
        {
            if (para.Count == 0)
                return;
            blocks.Add(new Para(ParseInline(string.Join(" ", para))));
            para.Clear();
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].TrimEnd();
            if (line.Length == 0 || line.StartsWith("<!--", StringComparison.Ordinal))
            {
                FlushPara();
                continue;
            }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushPara();
                var code = new List<string>();
                for (i++; i < lines.Count && !lines[i].TrimEnd().StartsWith("```", StringComparison.Ordinal); i++)
                    code.Add(lines[i].TrimEnd());
                blocks.Add(new Code(string.Join("\n", code)));
                continue;
            }
            var sub = SubHead.Match(line);
            if (sub.Success)
            {
                FlushPara();
                var text = sub.Groups[2].Value;
                var id = HeadNo.Match(text + " ");
                blocks.Add(new Heading(sub.Groups[1].Value.Length, id.Success ? id.Groups[1].Value : "", ParseInline(text)));
                continue;
            }
            var pic = PictureLine.Match(line);
            if (pic.Success)
            {
                FlushPara();
                blocks.Add(new Picture(pic.Groups[1].Value, Path.GetFileName(pic.Groups[2].Value)));
                continue;
            }
            if (line.StartsWith('|'))
            {
                FlushPara();
                var rows = new List<List<List<Inline>>>();
                for (; i < lines.Count && lines[i].TrimStart().StartsWith('|'); i++)
                {
                    var cells = SplitRow(lines[i]);
                    // 表头下面那行 | --- | --- | 分隔线不算数据
                    if (cells.All(c => c.Length > 0 && c.Trim('-', ':', ' ').Length == 0))
                        continue;
                    rows.Add(cells.Select(ParseInline).ToList());
                }
                i--;
                if (rows.Count > 0)
                    blocks.Add(new Table(rows[0], rows.Skip(1).ToList()));
                continue;
            }
            var ordered = OrderedItem.IsMatch(line);
            if (ordered || line.StartsWith("- ", StringComparison.Ordinal))
            {
                FlushPara();
                var items = new List<List<Inline>>();
                for (; i < lines.Count; i++)
                {
                    var l = lines[i].TrimEnd();
                    var m = OrderedItem.Match(l);
                    if (ordered && m.Success)
                        items.Add(ParseInline(m.Groups[1].Value));
                    else if (!ordered && l.StartsWith("- ", StringComparison.Ordinal))
                        items.Add(ParseInline(l[2..]));
                    else
                        break;
                }
                i--;
                blocks.Add(new ListBlock(ordered, items));
                continue;
            }
            para.Add(line.Trim());
        }
        FlushPara();
        return blocks;
    }

    private static List<string> SplitRow(string line)
    {
        var s = line.Trim();
        if (s.StartsWith('|'))
            s = s[1..];
        if (s.EndsWith('|'))
            s = s[..^1];
        return s.Split('|').Select(c => c.Trim()).ToList();
    }

    private static List<Inline> ParseInline(string s) => ParseInline(s, false);

    private static List<Inline> ParseInline(string s, bool bold)
    {
        var list = new List<Inline>();
        var sb = new StringBuilder();

        void Flush()
        {
            if (sb.Length == 0)
                return;
            AddText(list, sb.ToString(), bold);
            sb.Clear();
        }

        for (var i = 0; i < s.Length;)
        {
            if (s[i] == '`')
            {
                var j = s.IndexOf('`', i + 1);
                if (j > i)
                {
                    Flush();
                    list.Add(new Inline(s[(i + 1)..j], bold, IsCode: true));
                    i = j + 1;
                    continue;
                }
            }
            if (!bold && string.CompareOrdinal(s, i, "**", 0, 2) == 0)
            {
                var j = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (j > i)
                {
                    Flush();
                    list.AddRange(ParseInline(s[(i + 2)..j], true));
                    i = j + 2;
                    continue;
                }
            }
            if (s[i] == '[')
            {
                var m = Link.Match(s, i);
                if (m.Success && m.Index == i)
                {
                    Flush();
                    list.Add(new Inline(m.Groups[1].Value, bold, Href: m.Groups[2].Value));
                    i += m.Length;
                    continue;
                }
            }
            sb.Append(s[i]);
            i++;
        }
        Flush();
        return list;
    }

    /// <summary>普通文字里认出章节引用，切成「文字 + 跳转 + 文字」。</summary>
    private static void AddText(List<Inline> list, string text, bool bold)
    {
        var last = 0;
        foreach (Match m in XRef.Matches(text))
        {
            if (m.Index > last)
                list.Add(new Inline(text[last..m.Index], bold));
            var no = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            list.Add(new Inline(m.Value, bold, Chapter: no));
            last = m.Index + m.Length;
        }
        if (last < text.Length)
            list.Add(new Inline(text[last..], bold));
    }

    // ---------- Web 端渲染 ----------

    /// <summary>
    /// 章节正文 → HTML（Web 端 /api/help）。文字一律转义；小节标题带 data-sec 供前端滚动定位，
    /// 章节跳转带 data-ch，截图走 /api/help/img。
    /// </summary>
    public static string RenderHtml(Chapter chapter)
    {
        var sb = new StringBuilder();
        foreach (var block in chapter.Blocks)
        {
            switch (block)
            {
                case Heading h:
                    var tag = h.Level <= 3 ? "h3" : "h4";
                    sb.Append('<').Append(tag);
                    if (h.Id.Length > 0)
                        sb.Append(" data-sec=\"").Append(Enc(h.Id)).Append('"');
                    sb.Append('>');
                    AppendInlines(sb, h.Text);
                    sb.Append("</").Append(tag).Append('>');
                    break;
                case Para p:
                    sb.Append("<p>");
                    AppendInlines(sb, p.Text);
                    sb.Append("</p>");
                    break;
                case ListBlock l:
                    sb.Append(l.Ordered ? "<ol>" : "<ul>");
                    foreach (var item in l.Items)
                    {
                        sb.Append("<li>");
                        AppendInlines(sb, item);
                        sb.Append("</li>");
                    }
                    sb.Append(l.Ordered ? "</ol>" : "</ul>");
                    break;
                case Table t:
                    sb.Append("<div class=\"htable\"><table><thead><tr>");
                    foreach (var cell in t.Header)
                    {
                        sb.Append("<th>");
                        AppendInlines(sb, cell);
                        sb.Append("</th>");
                    }
                    sb.Append("</tr></thead><tbody>");
                    foreach (var row in t.Rows)
                    {
                        sb.Append("<tr>");
                        foreach (var cell in row)
                        {
                            sb.Append("<td>");
                            AppendInlines(sb, cell);
                            sb.Append("</td>");
                        }
                        sb.Append("</tr>");
                    }
                    sb.Append("</tbody></table></div>");
                    break;
                case Code c:
                    sb.Append("<pre>").Append(Enc(c.Text)).Append("</pre>");
                    break;
                case Picture pic:
                    sb.Append("<img loading=\"lazy\" alt=\"").Append(Enc(pic.Alt))
                      .Append("\" src=\"/api/help/img?name=").Append(Uri.EscapeDataString(pic.Name)).Append("\">");
                    break;
            }
        }
        return sb.ToString();
    }

    private static void AppendInlines(StringBuilder sb, List<Inline> inlines)
    {
        foreach (var i in inlines)
        {
            if (i.Bold)
                sb.Append("<b>");
            if (i.IsCode)
                sb.Append("<code>").Append(Enc(i.Text)).Append("</code>");
            else if (i.Chapter != null)
                sb.Append("<a class=\"hxref\" data-ch=\"").Append(Enc(i.Chapter)).Append("\">").Append(Enc(i.Text)).Append("</a>");
            else if (i.Href != null && IsWebLink(i.Href))
                sb.Append("<a href=\"").Append(Enc(i.Href)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
                  .Append(Enc(i.Text)).Append("</a>");
            else
                sb.Append(Enc(i.Text));
            if (i.Bold)
                sb.Append("</b>");
        }
    }

    /// <summary>只把 http/https 链接做成可点的（手册里指向仓库文件的相对链接在程序里没有意义）。</summary>
    public static bool IsWebLink(string href) =>
        href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        href.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Enc(string s) => WebUtility.HtmlEncode(s);
}
