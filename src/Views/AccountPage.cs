using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary.Views;

/// <summary>
/// 账号访问（一级导航「🔑 账号访问」，对齐 Web accounts.js 的 renderAccountsPage）：
/// FANBOX / pixiv / E-Hentai 三个站点各一张分区卡——标题右边是登录状态，其下每个账号一行
/// 「名称（用户号）· 检测 · 退出登录」+ 自动维护结果，卡底是「🖥 在服务器上登录」。
///
/// 账号只能在服务器上登录（<see cref="LoginBrowser"/>：本机开内嵌浏览器，人登录后自动取 cookie），
/// 不提供手填 cookie（用户指定）；桌面端就是那台电脑，直接操作弹出的窗口即可（Web 端是把同一个窗口串流过去）。
/// pixiv / FANBOX 可登多个账号、并行使用（作品搜索的来源下拉每个账号一项），E-Hentai 只留一个。
/// 纯代码构建、不带 XAML：见 CLAUDE.md「XAML 里不要引用本程序集的类型」。
/// </summary>
public class AccountPage : UserControl
{
    private readonly StackPanel _panel = new() { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };

    private readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold };

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));

    public AccountPage()
    {
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        // 顶栏只有标题（Web accounts.js：$('title') = '账号访问'），样式同其它页的页顶横条
        // 标题右端是 ? 使用帮助（Web 顶栏 #helpBtn）
        var head = new DockPanel();
        var help = HelpPanel.Button("13");
        DockPanel.SetDock(help, Dock.Right);
        head.Children.Add(help);
        _title.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_title);
        root.Children.Add(new Border { Style = Res<Style>("ToolbarCard"), Child = head });
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _panel };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        Content = root;
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue)
                Refresh();
        };
        I18n.LanguageChanged += Refresh;
        // 登录浏览器有了结果（不论是在桌面上还是经网页串流登的）：刷新列表并提示
        // 对话框是阻塞式的，挪到事件之后再弹，别卡住 LoginBrowser 的收尾
        LoginBrowser.Finished += (site, ok, message) => Dispatcher.BeginInvoke(new Action(() =>
        {
            Refresh();
            if (ok && IsVisible)
                InAppDialog.Info(this, message, AccountService.Title(site));
        }));
        AccountService.Changed += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsVisible)
                Refresh();
        }));
        Refresh();
    }

    /// <summary>按当前账号重建三张卡（进页、登录、退出登录后各调一次）。</summary>
    public void Refresh()
    {
        _title.Text = I18n.Tr("账号访问");
        _panel.Children.Clear();
        foreach (var site in AccountService.Sites)
            _panel.Children.Add(BuildCard(site));
    }

    private GroupBox BuildCard(string site)
    {
        var title = AccountService.Title(site);
        var multi = AccountService.AllowsMultiple(site);
        var list = AccountService.List(site);
        var body = new StackPanel();
        foreach (var a in list)
            body.Children.Add(BuildRow(a, title));

        // 在本机开浏览器登录，登录成功自动存成账号（结果经 LoginBrowser.Finished 回来）
        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Foreground = Red };
        var login = new Button
        {
            Content = I18n.Tr(multi
                ? list.Count > 0 ? "＋ 在服务器上登录其它账号" : "🖥 在服务器上登录"
                : list.Count > 0 ? "🖥 在服务器上重新登录" : "🖥 在服务器上登录"),
            Style = Res<Style>("AccentButton"),
        };
        login.Click += (_, _) =>
        {
            try
            {
                LoginBrowser.Open(site);
            }
            catch (Exception e)
            {
                result.Text = e.Message;
            }
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        actions.Children.Add(login);
        actions.Children.Add(result);
        body.Children.Add(actions);

        // 标题：站点名 + 登录状态胶囊（对齐 Web 的 .acct-st）
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = title, VerticalAlignment = VerticalAlignment.Center });
        var on = list.Count > 0;
        header.Children.Add(new Border
        {
            Margin = new Thickness(12, 0, 0, 0), Padding = new Thickness(10, 1, 10, 1),
            CornerRadius = new CornerRadius(999), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(on ? Color.FromRgb(0x2F, 0x6B, 0x45) : Color.FromRgb(0x3A, 0x3A, 0x42)),
            Child = new TextBlock
            {
                FontSize = 13, FontWeight = FontWeights.Normal,
                Text = !on ? I18n.Tr("未登录")
                    : multi ? I18n.Format(I18n.Tr("已登录 {n} 个账号"), ("n", list.Count))
                    : I18n.Format(I18n.Tr("已登录（{name}）"), ("name", list[0].Name)),
                Foreground = on ? Green : Res<Brush>("CaptionBrush") ?? Brushes.Gray,
            },
        });
        return new GroupBox { Header = header, Content = body };
    }

    /// <summary>一个账号：名称（用户号）· 检测 · 退出登录，其下一行是自动维护的最近结果（对齐 Web 的 .acct-row）。</summary>
    private Border BuildRow(AccountInfo a, string title)
    {
        var row = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock { MinWidth = 160, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
        name.Inlines.Add(a.Name);
        if (a.Name != a.UserId)
            name.Inlines.Add(new System.Windows.Documents.Run("  " + a.UserId)
            {
                FontWeight = FontWeights.Normal, FontSize = 12.5, Foreground = Res<Brush>("CaptionBrush"),
            });
        line.Children.Add(name);

        var result = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        var check = new Button { Content = I18n.Tr("检测"), Margin = new Thickness(10, 0, 0, 0) };
        check.Click += async (_, _) =>
        {
            check.IsEnabled = false;
            check.Content = I18n.Tr("检测中…");
            (bool Ok, string Message) r;
            try
            {
                r = await AccountService.CheckAsync(a.Site, a.UserId);
            }
            finally
            {
                check.IsEnabled = true;
                check.Content = I18n.Tr("检测");
            }
            result.Text = r.Message;
            result.Foreground = r.Ok ? Green : Red;
        };
        line.Children.Add(check);

        var logout = new Button { Content = I18n.Tr("退出登录"), Margin = new Thickness(10, 0, 0, 0) };
        logout.Click += (_, _) =>
        {
            if (!InAppDialog.Confirm(this,
                    I18n.Format(I18n.Tr("退出 {site} 账号「{name}」？服务器上该账号的浏览器登录记录也会一并删除。"),
                        ("site", title), ("name", a.Name)), I18n.Tr("退出登录")))
                return;
            AccountService.Remove(a.Site, a.UserId);
            Refresh();
        };
        line.Children.Add(logout);
        line.Children.Add(result);
        row.Children.Add(line);

        var keepText = a.Keep is { } keep
            ? I18n.Format(I18n.Tr("自动维护（{at}）：{message}"),
                ("at", keep.At.ToString("MM-dd HH:mm")), ("message", keep.Message))
            : !a.CanKeep ? I18n.Tr("旧版手动填写的登录，无法自动续期，失效后请重新在服务器上登录") : "";
        if (keepText.Length > 0)
            row.Children.Add(new TextBlock
            {
                Text = keepText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), FontSize = 12.5,
                Foreground = a.Keep is { Ok: false } ? Red : Res<Brush>("CaptionBrush") ?? Brushes.Gray,
            });
        return new Border
        {
            Child = row, Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = Res<Brush>("BorderBrush"),
        };
    }

    private static T? Res<T>(string key) where T : class =>
        Application.Current?.TryFindResource(key) as T;
}
