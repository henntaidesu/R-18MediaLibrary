using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// 账号访问（一级导航「🔑 账号访问」，对齐 Web accounts.js 的 renderAccountsPage）：
/// FANBOX / pixiv / E-Hentai 三个站点各一张分区卡——标题右边是登录状态「已登录（账号名）/ 未登录」，
/// 其下是 cookie 输入与「保存并检测 / 检测 / 退出登录」；E-Hentai 另有账号密码登录（账号密码存库，进页即回填）。
/// 页面不放说明文字，只有 FANBOX / pixiv 留一行 cookie 的获取方法（用户指定）。
///
/// cookie 输入框只写不读（同 Web：服务端从不把 cookie 回给网页，桌面端保持同一交互），已登录时留空即保持不变。
/// 数据与校验都在 <see cref="AccountService"/>；登录 FANBOX 后作品搜索的来源下拉多出「FANBOX（账号名）」。
/// 原先 E-Hentai 的账号/cookie 与 pixiv 的 PHPSESSID 在系统设置里，2026-10-26 起挪到这一页。
/// 纯代码构建、不带 XAML：见 CLAUDE.md「XAML 里不要引用本程序集的类型」。
/// </summary>
public class AccountPage : UserControl
{
    private readonly StackPanel _panel = new() { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };

    // 各站点的 cookie 字段：(AccountService 认的字段名, 标签)
    private static readonly Dictionary<string, (string Key, string Label)[]> Fields = new()
    {
        [AccountService.Fanbox] = [("sessid", "FANBOXSESSID")],
        [AccountService.Pixiv] = [("php_sessid", "PHPSESSID")],
        [AccountService.Ehentai] = [("member_id", "ipb_member_id"), ("pass_hash", "ipb_pass_hash"), ("igneous", "igneous")],
    };

    // 获取方法：去哪个网站登录、从哪个域的 cookie 里复制（只有 FANBOX / pixiv 显示）
    private static readonly Dictionary<string, (string Url, string Domain)> Where = new()
    {
        [AccountService.Fanbox] = ("https://www.fanbox.cc/", "fanbox.cc"),
        [AccountService.Pixiv] = ("https://www.pixiv.net/", "pixiv.net"),
    };

    public AccountPage()
    {
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _panel };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                Refresh();
        };
        I18n.LanguageChanged += Refresh;
        Refresh();
    }

    /// <summary>按当前登录状态重建三张卡（进页、保存、退出登录后各调一次）。</summary>
    public void Refresh()
    {
        AppConfig.Reload();
        _panel.Children.Clear();
        _statusOf.Clear();
        foreach (var site in AccountService.Sites)
            _panel.Children.Add(BuildCard(AccountService.Info(site)));
    }

    private GroupBox BuildCard(AccountInfo a)
    {
        var body = new StackPanel();

        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        void Say(bool ok, string message)
        {
            result.Text = message;
            result.Foreground = new SolidColorBrush(ok ? Color.FromRgb(0x4A, 0xDE, 0x80) : Color.FromRgb(0xF8, 0x71, 0x71));
        }

        // E-Hentai：账号密码登录（成功即写好下面三个 cookie）。账号密码都存库，进页即回填
        if (a.Site == AccountService.Ehentai)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            row.Children.Add(Label(I18n.Tr("账号")));
            var user = new TextBox { Width = 180, Text = AppConfig.EhentaiUsername, VerticalContentAlignment = VerticalAlignment.Center };
            var passLabel = Label(I18n.Tr("密码"));
            passLabel.MinWidth = 0;
            passLabel.Margin = new Thickness(24, 0, 12, 0);
            var pass = new PasswordBox { Width = 200, Password = AppConfig.EhentaiPassword,
                VerticalContentAlignment = VerticalAlignment.Center };
            var login = new Button { Content = I18n.Tr("登录并获取 Cookie"), Margin = new Thickness(8, 0, 0, 0),
                Style = Res<Style>("AccentButton") };
            login.Click += async (_, _) =>
            {
                if (user.Text.Trim().Length == 0 || pass.Password.Length == 0)
                {
                    Say(false, I18n.Tr("请先填写账号与密码"));
                    return;
                }
                login.IsEnabled = false;
                login.Content = I18n.Tr("登录中…");
                EhentaiApi.EhLoginResult r;
                try
                {
                    r = await AccountService.LoginEhentaiAsync(user.Text.Trim(), pass.Password);
                }
                finally
                {
                    login.IsEnabled = true;
                    login.Content = I18n.Tr("登录并获取 Cookie");
                }
                if (!r.Ok)
                {
                    Say(false, r.Message);
                    return;
                }
                InAppDialog.Info(this, r.Message, I18n.Tr("登录成功"));
                Refresh();
            };
            row.Children.Add(user);
            row.Children.Add(passLabel);
            row.Children.Add(pass);
            row.Children.Add(login);
            body.Children.Add(row);
        }

        // cookie 输入：只写不读，留空即保持原值
        var inputs = new Dictionary<string, PasswordBox>();
        foreach (var (key, label) in Fields[a.Site])
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var name = Label(label);
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var box = new PasswordBox { VerticalContentAlignment = VerticalAlignment.Center };
            row.Children.Add(box);
            inputs[key] = box;
            body.Children.Add(row);
        }
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(132, 6, 0, 0) };
        var save = new Button { Content = I18n.Tr("保存并检测"), Style = Res<Style>("AccentButton") };
        save.Click += async (_, _) =>
        {
            var values = new Dictionary<string, string>();
            foreach (var (key, box) in inputs)
                if (box.Password.Trim().Length > 0)
                    values[key] = box.Password.Trim();
            if (values.Count == 0)
            {
                Say(false, I18n.Tr("没有填写任何内容"));
                return;
            }
            save.IsEnabled = false;
            save.Content = I18n.Tr("检测中…");
            (bool Ok, string Message) r;
            try
            {
                AccountService.SaveCookies(a.Site, values);
                r = await AccountService.CheckAsync(a.Site);
            }
            finally
            {
                save.IsEnabled = true;
                save.Content = I18n.Tr("保存并检测");
            }
            Say(r.Ok, r.Message);
            if (r.Ok)
            {
                InAppDialog.Info(this, r.Message, I18n.Tr("登录有效"));
                Refresh();
            }
        };
        actions.Children.Add(save);

        var check = new Button { Content = I18n.Tr("检测"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = a.LoggedIn };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            check.Content = I18n.Tr("检测中…");
            (bool Ok, string Message) r;
            try
            {
                r = await AccountService.CheckAsync(a.Site);
            }
            finally
            {
                check.IsEnabled = true;
                check.Content = I18n.Tr("检测");
            }
            Say(r.Ok, r.Message);
            // 名字可能刚查到：标题行跟着刷新
            if (r.Ok)
                SetStatus(StatusOf(check), AccountService.Info(a.Site));
        };
        actions.Children.Add(check);

        var logout = new Button { Content = I18n.Tr("退出登录"), Margin = new Thickness(8, 0, 0, 0), IsEnabled = a.LoggedIn };
        logout.Click += (_, _) =>
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("清除 {site} 的登录信息？"), ("site", a.Title)), I18n.Tr("退出登录")))
                return;
            AccountService.Logout(a.Site);
            Refresh();
        };
        actions.Children.Add(logout);
        actions.Children.Add(result);
        body.Children.Add(actions);

        // 获取方法：浏览器登录 → F12 → Cookie（只有 FANBOX / pixiv 有）
        if (Where.TryGetValue(a.Site, out var where))
        {
            var (url, domain) = where;
            var tip = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0),
                Style = Res<Style>("CaptionText") };
            tip.Inlines.Add(new Run(I18n.Tr("获取方法：在浏览器登录 ")));
            var link = new Hyperlink(new Run(url))
            {
                Foreground = Res<Brush>("AccentLightBrush") ?? Brushes.DodgerBlue, TextDecorations = null,
            };
            link.Click += (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            tip.Inlines.Add(link);
            tip.Inlines.Add(new Run(I18n.Format(
                I18n.Tr(" 后按 F12 →「应用 / Application」→ Cookie → {domain}，复制对应名称的「值」粘到上面。"),
                ("domain", domain))));
            body.Children.Add(tip);
        }

        // 标题：站点名 + 登录状态胶囊（对齐 Web 的 .acct-st）
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = a.Title, VerticalAlignment = VerticalAlignment.Center });
        var status = new Border
        {
            Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 1, 10, 1),
            CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1),
            Child = new TextBlock { FontSize = 12.5, FontWeight = FontWeights.Normal },
        };
        SetStatus(status, a);
        header.Children.Add(status);
        var group = new GroupBox { Header = header, Content = body };
        _statusOf[check] = status;
        return group;
    }

    // 「检测」按钮 → 它所在卡片的状态胶囊（检测到新名字时就地改，不必整页重建、冲掉结果文字）
    private readonly Dictionary<Button, Border> _statusOf = [];

    private Border StatusOf(Button check) => _statusOf[check];

    private static void SetStatus(Border pill, AccountInfo a)
    {
        var on = a.LoggedIn;
        pill.BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x2F, 0x6B, 0x45) : Color.FromRgb(0x3A, 0x3A, 0x42));
        if (pill.Child is TextBlock text)
        {
            text.Text = on ? I18n.Format(I18n.Tr("已登录（{name}）"), ("name", a.Account)) : I18n.Tr("未登录");
            text.Foreground = on
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                : Res<Brush>("CaptionBrush") ?? Brushes.Gray;
        }
    }

    /// <summary>表单标签：120px 定宽在控件左侧（同设置页的 FieldLabel）。</summary>
    private static TextBlock Label(string text) => new()
    {
        Text = text, MinWidth = 120, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    private static T? Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
