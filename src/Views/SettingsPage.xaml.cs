using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;
using R18MediaLibrary.Services.Translate;
using Microsoft.Win32;

namespace R18MediaLibrary.Views;

/// <summary>设置页（对应 Python 版 setting_UI.py）：输入框失焦即保存，下拉框选择即保存。</summary>
public partial class SettingsPage : UserControl
{
    private bool _loading;  // 回填控件时不触发保存
    private readonly List<string> _unzipPwds = new();  // 解压密码库：一条一张小卡片
    private readonly MediaLibSettingDialog _mediaLibSection = new();  // 内联的媒体库管理区块（常驻，扫描随其存活）

    public SettingsPage()
    {
        InitializeComponent();
        BuildCombos();
        HelpSlot.Content = HelpPanel.Button("14");
        RetranslateUi();
        ReadConf();
        // 媒体库管理内联进设置页（对齐 Web 设置页），构建一次；扫描状态在页面切换间保持
        MediaLibHost.Content = _mediaLibSection.BuildSection(this);
        // FANBOX 作家监控同样内联进设置页（对齐 Web），「打开主页」转交宿主跨分区跳转
        I18n.LanguageChanged += RetranslateUi;
        // 模型依赖下载在后台线程推进度，切回调度线程再动控件
        TranslateDeps.Changed += OnTranslateDepsChanged;
    }

    private void OnTranslateDepsChanged() =>
        Dispatcher.BeginInvoke(new Action(UpdateTranslateStatus));

    private void BuildCombos()
    {
        _loading = true;
        ProxyStatusCombo.Items.Add(I18n.Tr("开启"));
        ProxyStatusCombo.Items.Add(I18n.Tr("关闭"));
        foreach (var type in new[] { "http", "https", "Socks5" })
            ProxyTypeCombo.Items.Add(type);
        AutoDownloadCombo.Items.Add(I18n.Tr("开启"));
        AutoDownloadCombo.Items.Add(I18n.Tr("关闭"));
        AutoUnzipCombo.Items.Add(I18n.Tr("开启"));
        AutoUnzipCombo.Items.Add(I18n.Tr("关闭"));
        WebEnableCombo.Items.Add(I18n.Tr("开启"));
        WebEnableCombo.Items.Add(I18n.Tr("关闭"));
        ImageHostEnableCombo.Items.Add(I18n.Tr("开启"));
        ImageHostEnableCombo.Items.Add(I18n.Tr("关闭"));
        SsoEnableCombo.Items.Add(I18n.Tr("开启"));
        SsoEnableCombo.Items.Add(I18n.Tr("关闭"));
        TransEnableCombo.Items.Add(I18n.Tr("开启"));
        TransEnableCombo.Items.Add(I18n.Tr("关闭"));
        // 档位来自依赖清单（用户可换清单），故按 id 记住顺序而不是写死索引语义
        foreach (var tier in TranslateModels.Tiers)
        {
            _tierIds.Add(tier.Id);
            TransTierCombo.Items.Add(tier.Note.Length > 0 ? $"{tier.Name} · {tier.Note}" : tier.Name);
        }
        TransDeviceCombo.Items.Add("CPU");
        TransDeviceCombo.Items.Add("GPU");
        // 关闭按钮行为：索引 0=每次询问 / 1=最小化到托盘 / 2=退出程序
        CloseActionCombo.Items.Add(I18n.Tr("每次询问"));
        CloseActionCombo.Items.Add(I18n.Tr("最小化到托盘"));
        CloseActionCombo.Items.Add(I18n.Tr("退出程序"));
        foreach (var level in new[] { "info", "error", "debug" })
            LogLevelCombo.Items.Add(level);
        foreach (var (_, name) in I18n.Languages)
            LanguageCombo.Items.Add(name);
        foreach (var site in new[] { "Original", "Mirror-1", "Mirror-2", "Mirror-3" })
            AsmrMirrorCombo.Items.Add(site);
        foreach (var host in EhHosts)
            EhHostCombo.Items.Add(host);
        BuildSearchSourceRows();
        BuildFileTypeChecks();
        _loading = false;
    }

    // 作品类型 → 来源下拉框（动态生成，按 WorkTypes.Known）
    private readonly System.Collections.Generic.Dictionary<string, ComboBox> _sourceCombos = new();

    // 图片翻译档位下拉框的索引 → 清单里的档位 id（档位由清单决定，不能按索引写死语义）
    private readonly System.Collections.Generic.List<string> _tierIds = [];

    private void BuildSearchSourceRows()
    {
        foreach (var (code, name) in WorkTypes.Known)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 28, 8),
            };
            row.Children.Add(new TextBlock
            {
                Text = $"{name}（{code}）",
                MinWidth = 100,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var combo = new ComboBox { Width = 150, Tag = code };
            // 目前仅 SOU 可走 asmr.one；其余类型仅 anime-sharing（下拉禁用以示固定）
            if (WorkTypes.SupportsAsmr(code))
            {
                combo.Items.Add("ASMR.ONE");
                combo.Items.Add("anime-sharing");
            }
            else
            {
                combo.Items.Add("anime-sharing");
                combo.IsEnabled = false;
            }
            combo.SelectionChanged += SourceCombo_SelectionChanged;
            _sourceCombos[code] = combo;
            row.Children.Add(combo);
            SearchSourcePanel.Children.Add(row);
        }
    }

    // asmr 文件类型勾选框（动态生成，避免 10 个命名控件）
    private readonly System.Collections.Generic.Dictionary<string, CheckBox> _fileTypeChecks = new();

    private void BuildFileTypeChecks()
    {
        foreach (var ext in AppConfig.AsmrFileTypes)
        {
            var check = new CheckBox
            {
                Content = ext,
                Margin = new Thickness(0, 0, 14, 6),
                MinWidth = 60,
                Tag = ext,
            };
            check.Click += FileTypeCheck_Click;
            _fileTypeChecks[ext] = check;
            FileTypePanel.Children.Add(check);
        }
    }

    private void FileTypeCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: string ext })
            return;
        AppConfig.Write("asmr_filetype", ext, (sender as CheckBox)!.IsChecked == true ? "True" : "False");
    }

    private void RetranslateUi()
    {
        _loading = true;
        // 各分区标题右端带 ?，打开使用手册对应章节（同 Web settings.js 的 helpHeading）
        PageTitle.Text = I18n.Tr("系统设置");
        DownloadGroup.Header = HelpPanel.Header(I18n.Tr("下载"), "11.3");
        ProxyGroup.Header = HelpPanel.Header(I18n.Tr("代理"), "14.1");
        DebridGroup.Header = HelpPanel.Header(I18n.Tr("Debrid-Link 下载中转站"), "7.2");
        SearchSourceGroup.Header = HelpPanel.Header(I18n.Tr("作品类型优先搜索"), "8");
        AsmrGroup.Header = HelpPanel.Header("ASMR.ONE", "8");
        AsmrUserLabel.Text = I18n.Tr("账号");
        AsmrPassLabel.Text = I18n.Tr("密码");
        AsmrMirrorLabel.Text = I18n.Tr("镜像站");
        AsmrFileTypeLabel.Text = I18n.Tr("下载文件类型");
        AsmrTestButton.Content = I18n.Tr("登录测试");
        EhentaiGroup.Header = HelpPanel.Header("E-Hentai", "10.1");
        EhHostLabel.Text = I18n.Tr("站点");
        EhOriginalLabel.Text = I18n.Tr("图片画质");
        EhTestButton.Content = I18n.Tr("连接测试");
        EhHint.Text = I18n.Tr("登录（访问里站、下载原图）请到左侧「账号访问」页。");
        BuildEhOriginalCombo();
        PixivGroup.Header = HelpPanel.Header("pixiv", "10.2");
        PxOriginalLabel.Text = I18n.Tr("图片画质");
        PxModeLabel.Text = I18n.Tr("搜索分级");
        PxMatchLabel.Text = I18n.Tr("匹配方式");
        PxTestButton.Content = I18n.Tr("连接测试");
        PxHint.Text = I18n.Tr(
            "未登录也能搜索，但站点会把 R-18 作品从结果里滤掉。要看 R-18，请到左侧「账号访问」页登录 pixiv。");
        BuildPixivCombos();
        SystemGroup.Header = HelpPanel.Header(I18n.Tr("系统"), "14.1");
        MediaLibGroup.Header = HelpPanel.Header(I18n.Tr("媒体库"), "3");
        PathLabel.Text = I18n.Tr("缓存路径");
        PathChooseButton.Content = I18n.Tr("保存");
        AutoDownloadLabel.Text = I18n.Tr("自动下载");
        AutoUnzipLabel.Text = I18n.Tr("自动解压");
        ProxyStatusLabel.Text = I18n.Tr("代理");
        DebridTestButton.Content = I18n.Tr("测试");
        DownProcLabel.Text = I18n.Tr("单文件线程数");
        DownProcLabel.ToolTip = I18n.Tr("单个文件分成多少段并发下载（多线程下载），1 为不分段");
        DownProcBox.ToolTip = I18n.Tr("单个文件分成多少段并发下载（多线程下载），1 为不分段，最大 6");
        MinSpeedLabel.Text = I18n.Tr("最低速度 (KB/s)");
        MinSpeedBox.ToolTip = I18n.Tr("持续低于该速度 30 秒后自动重试，0 表示不限制");
        SpeedLimitLabel.Text = I18n.Tr("速度限制 (KB/s)");
        SpeedLimitBox.ToolTip = I18n.Tr("下载总速度上限，0 表示不限速");
        UnzipPwdLabel.Text = I18n.Tr("解压密码库");
        RebuildUnzipPwdRows();
        LanguageLabel.Text = I18n.Tr("语言");
        LogLevelLabel.Text = I18n.Tr("日志级别");
        EncodingLabel.Text = I18n.Tr("解压编码");
        CloseActionLabel.Text = I18n.Tr("关闭按钮");
        ImageHostGroup.Header = HelpPanel.Header(I18n.Tr("图床存储"), "14.2");
        ImageHostEnableLabel.Text = I18n.Tr("启用");
        ImageHostProjectLabel.Text = I18n.Tr("项目");
        ImageHostUrlLabel.Text = I18n.Tr("服务地址");
        ImageHostTokenLabel.Text = "API Token";
        ImageHostTestButton.Content = I18n.Tr("测试连接");
        ImageHostMigrateButton.Content = I18n.Tr("迁移封面");
        TranslateGroup.Header = HelpPanel.Header(I18n.Tr("图片翻译"), "17");
        TransEnableLabel.Text = I18n.Tr("启用");
        TransTierLabel.Text = I18n.Tr("模型档位");
        TransDeviceLabel.Text = I18n.Tr("推理设备");
        TransPathLabel.Text = I18n.Tr("模型目录");
        TransFontLabel.Text = I18n.Tr("嵌字字体");
        TransPathChooseButton.Content = I18n.Tr("浏览");
        TransDownloadButton.Content = I18n.Tr("下载依赖");
        TransCancelButton.Content = I18n.Tr("取消下载");
        TransRemoveButton.Content = I18n.Tr("删除依赖");
        TransFontBox.ToolTip = I18n.Tr("留空使用系统默认中文字体");
        WebGroup.Header = HelpPanel.Header(I18n.Tr("外部访问"), "15.1");
        WebEnableLabel.Text = I18n.Tr("外部访问");
        WebPortLabel.Text = I18n.Tr("端口");
        WebPasswordLabel.Text = I18n.Tr("访问密码");
        WebPasswordBox.ToolTip = I18n.Tr("留空则不需要密码；手机/电脑浏览器访问时输入此密码");
        // 下拉框选项文案（保持索引语义不变，仅更新显示文本）
        ProxyStatusCombo.Items[0] = I18n.Tr("开启");
        ProxyStatusCombo.Items[1] = I18n.Tr("关闭");
        AutoDownloadCombo.Items[0] = I18n.Tr("开启");
        AutoDownloadCombo.Items[1] = I18n.Tr("关闭");
        AutoUnzipCombo.Items[0] = I18n.Tr("开启");
        AutoUnzipCombo.Items[1] = I18n.Tr("关闭");
        WebEnableCombo.Items[0] = I18n.Tr("开启");
        WebEnableCombo.Items[1] = I18n.Tr("关闭");
        ImageHostEnableCombo.Items[0] = I18n.Tr("开启");
        ImageHostEnableCombo.Items[1] = I18n.Tr("关闭");
        SsoEnableCombo.Items[0] = I18n.Tr("开启");
        SsoEnableCombo.Items[1] = I18n.Tr("关闭");
        SsoGroup.Header = HelpPanel.Header(I18n.Tr("门户单点登录"), "15.2");
        SsoEnableLabel.Text = I18n.Tr("启用");
        SsoPortalUrlLabel.Text = I18n.Tr("门户地址");
        SsoInternalUrlLabel.Text = I18n.Tr("内网地址");
        SsoUsersLabel.Text = I18n.Tr("用户");
        SsoPortalUrlBox.ToolTip = I18n.Tr("浏览器访问门户的地址，须与门户 conf.json 的 idp.issuer 一致");
        SsoInternalUrlBox.ToolTip = I18n.Tr("本机访问门户的地址（换 token 用），留空则同门户地址");
        TransEnableCombo.Items[0] = I18n.Tr("开启");
        TransEnableCombo.Items[1] = I18n.Tr("关闭");
        CloseActionCombo.Items[0] = I18n.Tr("每次询问");
        CloseActionCombo.Items[1] = I18n.Tr("最小化到托盘");
        CloseActionCombo.Items[2] = I18n.Tr("退出程序");
        _loading = false;
        UpdateWebStatus();
        UpdateTranslateStatus();
    }

    private long _cfgVersionSeen = -1;

    private void Page_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!(bool)e.NewValue)
            return;
        // 切换到本页时若配置版本变过才重读数据库（未变则用进程缓存，避免每次进页都打库）
        if (AppConfig.Version != _cfgVersionSeen)
        {
            AppConfig.Reload();
            _cfgVersionSeen = AppConfig.Version;
        }
        ReadConf();
        _mediaLibSection.RefreshLibs();   // 拾取外部对媒体库的改动
    }

    /// <summary>用户的"下载"文件夹。</summary>
    private static string DefaultDownPath() =>
        Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));

    private void ReadConf()
    {
        _loading = true;
        var path = AppConfig.DownloadPath.Replace(@"\\", @"\");
        // 校验下载路径：不存在时回退到用户的下载文件夹并保存
        if (path.Length == 0 || !Directory.Exists(path))
        {
            path = DefaultDownPath();
            AppConfig.Write("downpath", "downpath", path);
        }
        PathBox.Text = path;

        var (enabled, host, port, proxyType) = AppConfig.ReadProxySetting();
        ProxyHostBox.Text = host;
        ProxyPortBox.Text = port;
        ProxyStatusCombo.SelectedIndex = enabled == "True" ? 0 : 1;
        ProxyTypeCombo.SelectedIndex = proxyType switch
        {
            "http" => 0,
            "https" => 1,
            _ => 2,
        };

        DebridKeyBox.Text = AppConfig.DebridApiKey;

        foreach (var (code, combo) in _sourceCombos)
            combo.SelectedIndex = WorkTypes.SupportsAsmr(code) && AppConfig.SouSearchSource != "anime-sharing"
                ? 0   // SOU 默认 ASMR.ONE
                : (WorkTypes.SupportsAsmr(code) ? 1 : 0);  // SOU 选了 anime-sharing → 索引1；其余仅 anime-sharing → 索引0

        AsmrUserBox.Text = AppConfig.AsmrUsername;
        AsmrPassBox.Text = AppConfig.AsmrPassword;
        var ehHost = Array.IndexOf(EhHosts, AppConfig.EhentaiHost);
        EhHostCombo.SelectedIndex = ehHost >= 0 ? ehHost : 0;
        EhOriginalCombo.SelectedIndex = AppConfig.EhentaiOriginal ? 0 : 1;
        PxOriginalCombo.SelectedIndex = AppConfig.PixivOriginal ? 0 : 1;
        PxModeCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PixivModes, AppConfig.PixivSearchMode));
        PxMatchCombo.SelectedIndex = Math.Max(0, Array.IndexOf(PixivMatches, AppConfig.PixivTagMatch));
        AsmrMirrorCombo.SelectedIndex = AppConfig.AsmrMirrorSite switch
        {
            "Mirror-1" => 1,
            "Mirror-2" => 2,
            "Mirror-3" => 3,
            _ => 0,
        };
        foreach (var (ext, check) in _fileTypeChecks)
            check.IsChecked = AppConfig.AsmrFileTypeEnabled(ext);

        AutoDownloadCombo.SelectedIndex = AppConfig.AutoDownload ? 0 : 1;
        AutoUnzipCombo.SelectedIndex = AppConfig.AutoUnzip ? 0 : 1;
        DownProcBox.SelectedIndex = AppConfig.DownloadProcesses - 1;
        MinSpeedBox.Text = AppConfig.MinSpeedKb.ToString();
        SpeedLimitBox.Text = AppConfig.SpeedLimitKb.ToString();
        _unzipPwds.Clear();
        _unzipPwds.AddRange(AppConfig.UnzipPasswords);
        RebuildUnzipPwdRows();
        LogLevelCombo.SelectedIndex = AppConfig.Read("loglevel", "level") switch
        {
            "error" => 1,
            "debug" => 2,
            _ => 0,
        };
        EncodingBox.Text = AppConfig.SysEncoding;
        CloseActionCombo.SelectedIndex = AppConfig.CloseAction switch
        {
            "tray" => 1,
            "exit" => 2,
            _ => 0,
        };
        var codes = I18n.Languages.Select(l => l.Code).ToList();
        var langIndex = codes.IndexOf(I18n.CurrentLanguage);
        LanguageCombo.SelectedIndex = langIndex >= 0 ? langIndex : 0;

        ImageHostEnableCombo.SelectedIndex = AppConfig.ImageHostEnabled ? 0 : 1;
        ImageHostUrlBox.Text = AppConfig.ImageHostBaseUrl;
        ImageHostProjectBox.Text = AppConfig.ImageHostProject;
        ImageHostTokenBox.Text = AppConfig.ImageHostToken;

        TransEnableCombo.SelectedIndex = AppConfig.TranslateEnabledSetting ? 0 : 1;
        var tierIndex = _tierIds.IndexOf(AppConfig.TranslateModelTier);
        TransTierCombo.SelectedIndex = tierIndex >= 0 ? tierIndex : 0;
        TransDeviceCombo.SelectedIndex = AppConfig.TranslateDevice == "gpu" ? 1 : 0;
        TransPathBox.Text = TranslateModels.Root;
        TransFontBox.Text = AppConfig.TranslateFont;

        WebEnableCombo.SelectedIndex = AppConfig.WebEnabled ? 0 : 1;
        WebPortBox.Text = AppConfig.WebPort.ToString();
        WebPasswordBox.Text = AppConfig.WebPassword;

        SsoEnableCombo.SelectedIndex = AppConfig.SsoEnabled ? 0 : 1;
        SsoPortalUrlBox.Text = AppConfig.SsoPortalUrl;
        SsoInternalUrlBox.Text = AppConfig.SsoInternalUrl;
        SsoClientIdBox.Text = AppConfig.SsoClientId;
        SsoSecretBox.Text = AppConfig.SsoClientSecret;
        _loading = false;
        UpdateWebStatus();
        UpdateSsoStatus();
        UpdateImageHostStatus();
        UpdateTranslateStatus();
    }

    // ---------- 保存 ----------

    private void PathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        // 手动输入下载路径：输入的是有效目录时才保存
        var path = PathBox.Text.Trim();
        if (path.Length > 0 && Directory.Exists(path))
            AppConfig.Write("downpath", "downpath", Path.GetFullPath(path));
    }

    private void PathChooseButton_Click(object sender, RoutedEventArgs e)
    {
        var current = PathBox.Text;
        var dialog = new OpenFolderDialog
        {
            Title = I18n.Tr("选择缓存路径"),
            InitialDirectory = Directory.Exists(current) ? current : DefaultDownPath(),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        var path = Path.GetFullPath(dialog.FolderName);
        PathBox.Text = path;
        AppConfig.Write("downpath", "downpath", path);
    }

    private void Proxy_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("proxy", "host", ProxyHostBox.Text);
        AppConfig.Write("proxy", "port", ProxyPortBox.Text);
    }

    private void ProxyStatusCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("proxy", "openproxy", ProxyStatusCombo.SelectedIndex == 0 ? "True" : "False");
    }

    private void ProxyTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("proxy", "type", new[] { "http", "https", "Socks5" }[ProxyTypeCombo.SelectedIndex]);
    }

    private void DebridKeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("debrid", "api_key", DebridKeyBox.Text.Trim());
    }

    private async void DebridTestButton_Click(object sender, RoutedEventArgs e)
    {
        string? account = null;
        try
        {
            using var client = new DebridLinkClient(DebridKeyBox.Text.Trim());
            var info = await client.AccountInfosAsync();
            if (info is { } v)
            {
                account = DlsiteApi.JStr(v, "email");
                if (account.Length == 0)
                    account = DlsiteApi.JStr(v, "username");
            }
        }
        catch (Exception)
        {
            account = null;
        }
        if (account != null)
            InAppDialog.Info(this,
                I18n.Format(I18n.Tr("已连接 debrid-link\n账户: {account}"), ("account", account)),
                I18n.Tr("测试成功"));
        else
            InAppDialog.Warn(this, I18n.Tr("API Key 无效或网络不可用"), I18n.Tr("测试失败"));
    }

    // ---------- 作品类型优先搜索 ----------

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { Tag: string code } combo || combo.SelectedIndex < 0)
            return;
        // 仅 SOU 有两个选项（索引0=ASMR.ONE / 1=anime-sharing）；其余类型固定 anime-sharing
        var source = WorkTypes.SupportsAsmr(code) && combo.SelectedIndex == 0 ? "asmr" : "anime-sharing";
        AppConfig.Write("search", $"{code.ToLowerInvariant()}_source", source);
    }

    // ---------- ASMR.ONE ----------

    private void AsmrUserBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("asmr", "username", AsmrUserBox.Text.Trim());
        AppConfig.Write("asmr", "token", "");  // 改账号后清空旧 token，下次按新账号登录
    }

    private void AsmrPassBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("asmr", "password", AsmrPassBox.Text.Trim());
        AppConfig.Write("asmr", "token", "");
    }

    private void AsmrMirrorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AsmrMirrorCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("asmr", "mirror_site",
            new[] { "Original", "Mirror-1", "Mirror-2", "Mirror-3" }[AsmrMirrorCombo.SelectedIndex]);
    }

    private async void AsmrTestButton_Click(object sender, RoutedEventArgs e)
    {
        // 先保存当前输入，再用其登录
        AppConfig.Write("asmr", "username", AsmrUserBox.Text.Trim());
        AppConfig.Write("asmr", "password", AsmrPassBox.Text.Trim());
        AppConfig.Write("asmr", "token", "");
        var (ok, error) = await AsmrApi.LoginAsync();
        if (ok)
            InAppDialog.Info(this, I18n.Tr("登录成功，token 已保存"), I18n.Tr("测试成功"));
        else
            InAppDialog.Warn(this,
                I18n.Format(I18n.Tr("登录失败：{err}"), ("err", error ?? "")), I18n.Tr("测试失败"));
    }

    // ---------- E-Hentai ----------

    /// <summary>可选站点：表站可匿名浏览，里站必须带登录 cookie。</summary>
    private static readonly string[] EhHosts = ["e-hentai.org", "exhentai.org"];

    /// <summary>画质下拉的选项随语言重建（下标 0=原图 / 1=站点显示图）。</summary>
    private void BuildEhOriginalCombo()
    {
        var index = EhOriginalCombo.SelectedIndex;
        var loading = _loading;
        _loading = true;
        EhOriginalCombo.Items.Clear();
        EhOriginalCombo.Items.Add(I18n.Tr("原图"));
        EhOriginalCombo.Items.Add(I18n.Tr("站点显示图（省额度）"));
        EhOriginalCombo.SelectedIndex = index >= 0 ? index : (AppConfig.EhentaiOriginal ? 0 : 1);
        _loading = loading;
    }

    private void EhHostCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EhHostCombo.SelectedIndex < 0)
            return;
        AppConfig.EhentaiHost = EhHosts[EhHostCombo.SelectedIndex];
        EhentaiApi.Invalidate();   // 换站/换 cookie 后要按新配置重建客户端
    }

    private void EhOriginalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EhOriginalCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("ehentai", "original", EhOriginalCombo.SelectedIndex == 0 ? "True" : "False");
    }

    private async void EhTestButton_Click(object sender, RoutedEventArgs e)
    {
        // 登录 cookie 在「账号访问」页填写，这里只测当前配置能不能连上
        EhTestButton.IsEnabled = false;
        try
        {
            var (ok, message) = await EhentaiApi.TestAsync();
            if (ok)
                InAppDialog.Info(this, message, I18n.Tr("测试成功"));
            else
                InAppDialog.Warn(this, message, I18n.Tr("测试失败"));
        }
        finally
        {
            EhTestButton.IsEnabled = true;
        }
    }

    // ---------- pixiv ----------

    /// <summary>搜索分级下拉的索引 → 站点的 mode 参数。</summary>
    private static readonly string[] PixivModes = ["all", "safe", "r18"];

    /// <summary>匹配方式下拉的索引 → 站点的 s_mode 参数。</summary>
    private static readonly string[] PixivMatches = ["s_tag", "s_tag_full", "s_tc"];

    /// <summary>三个下拉的选项文案随语言变，故每次重译都重建（同 BuildEhOriginalCombo）。</summary>
    private void BuildPixivCombos()
    {
        var original = PxOriginalCombo.SelectedIndex;
        var mode = PxModeCombo.SelectedIndex;
        var match = PxMatchCombo.SelectedIndex;
        _loading = true;

        PxOriginalCombo.Items.Clear();
        PxOriginalCombo.Items.Add(I18n.Tr("原图"));
        PxOriginalCombo.Items.Add(I18n.Tr("站点缩放图（1200px）"));
        PxOriginalCombo.SelectedIndex = original >= 0 ? original : (AppConfig.PixivOriginal ? 0 : 1);

        PxModeCombo.Items.Clear();
        PxModeCombo.Items.Add(I18n.Tr("全部"));
        PxModeCombo.Items.Add(I18n.Tr("全年龄"));
        PxModeCombo.Items.Add(I18n.Tr("仅 R-18"));
        PxModeCombo.SelectedIndex =
            mode >= 0 ? mode : Math.Max(0, Array.IndexOf(PixivModes, AppConfig.PixivSearchMode));

        PxMatchCombo.Items.Clear();
        PxMatchCombo.Items.Add(I18n.Tr("标签部分一致"));
        PxMatchCombo.Items.Add(I18n.Tr("标签完全一致"));
        PxMatchCombo.Items.Add(I18n.Tr("标题与说明文"));
        PxMatchCombo.SelectedIndex =
            match >= 0 ? match : Math.Max(0, Array.IndexOf(PixivMatches, AppConfig.PixivTagMatch));

        _loading = false;
    }

    private void PxOriginalCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PxOriginalCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("pixiv", "original", PxOriginalCombo.SelectedIndex == 0 ? "True" : "False");
    }

    private void PxModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PxModeCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("pixiv", "mode", PixivModes[PxModeCombo.SelectedIndex]);
    }

    private void PxMatchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PxMatchCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("pixiv", "s_mode", PixivMatches[PxMatchCombo.SelectedIndex]);
    }

    private async void PxTestButton_Click(object sender, RoutedEventArgs e)
    {
        // 登录 cookie 在「账号访问」页填写，这里只测当前配置能不能连上
        PxTestButton.IsEnabled = false;
        try
        {
            var (ok, message) = await PixivApi.TestAsync();
            if (ok)
                InAppDialog.Info(this, message, I18n.Tr("测试成功"));
            else
                InAppDialog.Warn(this, message, I18n.Tr("测试失败"));
        }
        finally
        {
            PxTestButton.IsEnabled = true;
        }
    }

    private void AutoDownloadCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("down_list", "auto_download", AutoDownloadCombo.SelectedIndex == 0 ? "True" : "False");
    }

    private void AutoUnzipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("down_list", "auto_unzip", AutoUnzipCombo.SelectedIndex == 0 ? "True" : "False");
    }

    private void DownProcBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DownProcBox.SelectedIndex < 0)
            return;
        AppConfig.Write("down_list", "download_processes", (DownProcBox.SelectedIndex + 1).ToString());
    }

    private void MinSpeedBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var value = MinSpeedBox.Text.Trim();
        AppConfig.Write("down_list", "min_speed", int.TryParse(value, out _) ? value : "0");
    }

    private void SpeedLimitBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        var value = SpeedLimitBox.Text.Trim();
        AppConfig.Write("down_list", "speed_limit", int.TryParse(value, out _) ? value : "0");
    }

    /// <summary>
    /// 解压密码库：一个输入框一个密码，末尾「＋」新增一条、每行「✕」删除（对齐 Web 设置页）。
    /// 库里仍存老格式（一行一个），故 AppConfig.UnzipPasswords 那边不用改。
    /// </summary>
    /// <summary>
    /// 解压密码库（对齐 Web settings.js 的 .pwlist）：一个密码一张小卡片，宽度随内容、排满换行；
    /// 悬停卡片时右上角出红色 ✕，删除要二次确认；末尾圆形虚线「＋」弹框新增一条。
    /// </summary>
    private void RebuildUnzipPwdRows()
    {
        UnzipPwdList.Children.Clear();
        var surface2 = (Brush)FindResource("ButtonBrush");
        var line = (Brush)FindResource("BorderBrush");
        var accent = (Brush)FindResource("AccentLightBrush");
        for (var i = 0; i < _unzipPwds.Count; i++)
        {
            var index = i;   // 卡片随增删整体重建，故这里捕获的下标始终对得上
            var value = _unzipPwds[index];
            // .pwchip：surface2 底 + line 描边 + 圆角 8，内距 6/12，14px；悬停描边转 accent
            var chip = new Border
            {
                Background = surface2, BorderBrush = line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 6, 12, 6),
                Child = new TextBlock { Text = value, FontSize = 14 },
            };
            // .pwdel：18px 红色圆钮，骑在卡片右上角，悬停卡片才出现
            var del = new Button
            {
                Content = "✕", Width = 18, Height = 18, FontSize = 10, Cursor = Cursors.Hand,
                ToolTip = I18n.Tr("删除这条密码"),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -8, -8, 0), Visibility = Visibility.Hidden,
                Template = RoundButtonTemplate(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), null),
                Foreground = Brushes.White,
            };
            del.Click += (_, _) =>
            {
                if (!InAppDialog.Confirm(this,
                        I18n.Format(I18n.Tr("删除密码「{pwd}」？"), ("pwd", value)), I18n.Tr("删除")))
                    return;
                _unzipPwds.RemoveAt(index);
                SaveUnzipPwds();
                RebuildUnzipPwdRows();
            };
            var cell = new Grid { Margin = new Thickness(0, 0, 10, 10) };
            cell.Children.Add(chip);
            cell.Children.Add(del);
            cell.MouseEnter += (_, _) => { chip.BorderBrush = accent; del.Visibility = Visibility.Visible; };
            cell.MouseLeave += (_, _) => { chip.BorderBrush = line; del.Visibility = Visibility.Hidden; };
            UnzipPwdList.Children.Add(cell);
        }

        // .pwadd：32px 圆形虚线「＋」，悬停描边与文字转 accent
        var add = new Button
        {
            Content = "＋", Width = 32, Height = 32, FontSize = 16, Cursor = Cursors.Hand,
            Foreground = (Brush)FindResource("CaptionBrush"),
            ToolTip = I18n.Tr("添加一条密码"), Margin = new Thickness(0, 0, 0, 10),
            VerticalAlignment = VerticalAlignment.Center,
            Template = RoundButtonTemplate(surface2, line, dashed: true),
        };
        add.MouseEnter += (_, _) => add.Foreground = accent;
        add.MouseLeave += (_, _) => add.Foreground = (Brush)FindResource("CaptionBrush");
        add.Click += (_, _) =>
        {
            var v = (InAppDialog.Prompt(this, I18n.Tr("输入解压密码"), I18n.Tr("添加密码")) ?? "").Trim();
            if (v.Length == 0)
                return;
            if (_unzipPwds.Contains(v))
            {
                InAppDialog.Info(this, I18n.Tr("这个密码已经在密码库里了"), I18n.Tr("添加密码"));
                return;
            }
            _unzipPwds.Add(v);
            SaveUnzipPwds();
            RebuildUnzipPwdRows();
        };
        UnzipPwdList.Children.Add(add);
    }

    /// <summary>圆形按钮模板（密码库的 ✕ / ＋）：圆底 + 可选描边（虚线），内容居中。</summary>
    private static ControlTemplate RoundButtonTemplate(Brush background, Brush? border, bool dashed = false)
    {
        var template = new ControlTemplate(typeof(Button));
        var grid = new FrameworkElementFactory(typeof(Grid));
        var circle = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        circle.SetValue(System.Windows.Shapes.Shape.FillProperty, background);
        if (border != null)
        {
            circle.SetValue(System.Windows.Shapes.Shape.StrokeProperty, border);
            circle.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);
            if (dashed)
                circle.SetValue(System.Windows.Shapes.Shape.StrokeDashArrayProperty, new DoubleCollection { 3, 2 });
        }
        grid.AppendChild(circle);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        grid.AppendChild(content);
        template.VisualTree = grid;
        return template;
    }

    /// <summary>写回密码库：去掉空行与首尾空白，顺序即解压时的尝试顺序。</summary>
    private void SaveUnzipPwds()
    {
        if (_loading)
            return;
        AppConfig.Write("unzip", "passwords",
            string.Join("\n", _unzipPwds.Select(p => p.Trim()).Where(p => p.Length > 0)));
    }

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedIndex < 0)
            return;
        I18n.ApplyLanguage(I18n.Languages[LanguageCombo.SelectedIndex].Code);
    }

    private void LogLevelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LogLevelCombo.SelectedIndex < 0)
            return;
        AppConfig.Write("loglevel", "level",
            new[] { "info", "error", "debug" }[LogLevelCombo.SelectedIndex]);
    }

    private void EncodingBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("encoding", "encoding", EncodingBox.Text.Trim());
    }

    private void CloseActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CloseActionCombo.SelectedIndex < 0)
            return;
        // 索引 0=每次询问("") / 1=最小化到托盘("tray") / 2=退出程序("exit")
        AppConfig.CloseAction = new[] { "", "tray", "exit" }[CloseActionCombo.SelectedIndex];
    }

    // ---------- 外部访问 ----------

    private void WebEnableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("web_server", "enabled", WebEnableCombo.SelectedIndex == 0 ? "True" : "False");
        ApplyWebServer();
    }

    private void WebPortBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        // 端口校验：非法值回退 8080
        var port = int.TryParse(WebPortBox.Text.Trim(), out var v) && v is >= 1 and <= 65535 ? v : 8080;
        WebPortBox.Text = port.ToString();
        AppConfig.Write("web_server", "port", port.ToString());
        ApplyWebServer();
    }

    private void WebPasswordBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("web_server", "password", WebPasswordBox.Text.Trim());
        ApplyWebServer();
    }

    /// <summary>按当前配置启动/停止内嵌 Web 服务（开启时改端口/密码会重启），并刷新状态文案。</summary>
    private void ApplyWebServer()
    {
        if (AppConfig.WebEnabled)
            WebServer.StartFromConfig();
        else
            WebServer.Stop();
        UpdateWebStatus();
    }

    // ---------- 门户单点登录 ----------

    private void SsoEnableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("sso", "enabled", SsoEnableCombo.SelectedIndex == 0 ? "True" : "False");
        UpdateSsoStatus();
        UpdateWebStatus();
    }

    /// <summary>四个文本框共用：Tag 即 sso 段的键名。失焦即存（同其它分区，无保存按钮）。</summary>
    private void SsoBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not TextBox box || box.Tag is not string key)
            return;
        AppConfig.Write("sso", key, box.Text.Trim());
        UpdateSsoStatus();
        UpdateWebStatus();
    }

    private void UpdateSsoStatus()
    {
        if (SsoStatusLabel == null)
            return;
        if (!AppConfig.SsoEnabled)
            SsoStatusLabel.Text = I18n.Tr("单点登录已关闭：Web 端只认访问密码");
        else if (!AppConfig.SsoConfigured)
            SsoStatusLabel.Text = I18n.Tr("未生效：门户地址 / Client ID / Client Secret 需填齐");
        else
        {
            // 门户那边要登记的回调地址：局域网直连一个；经门户代理打开的话再加代理底下那个
            var callback = $"http://{WebServer.LocalIPv4()}:{AppConfig.WebPort}/api/sso/callback";
            SsoStatusLabel.Text = I18n.Format(
                I18n.Tr("已生效 · 请在门户 conf.json 的 idp.clients 里登记 redirect_uris：{url}（经门户代理访问时，另加 <门户地址>/api/proxy/<卡片ID>/api/sso/callback）"),
                ("url", callback));
        }
        RefreshSsoUsers();
    }

    /// <summary>用户列表：门户单点登录首次登录时自动建出来的本地用户。删掉即让其会话失效，下次登录会重新建。</summary>
    private void RefreshSsoUsers()
    {
        SsoUsersPanel.Children.Clear();
        var users = PortalSso.ListUsers();
        if (users.Count == 0)
        {
            SsoUsersPanel.Children.Add(new TextBlock
            {
                Text = I18n.Tr("还没有用户：从门户首次登录时自动创建"),
                Foreground = (System.Windows.Media.Brush)FindResource("CaptionBrush"),
            });
            return;
        }
        foreach (var u in users)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var del = new Button { Content = I18n.Tr("删除"), Margin = new Thickness(8, 0, 0, 0) };
            var id = u.Id;
            var name = u.Username;
            del.Click += (_, _) =>
            {
                PortalSso.DeleteUser(id);
                Logger.Info($"门户单点登录：已删除本地用户 {name}");
                RefreshSsoUsers();
            };
            DockPanel.SetDock(del, Dock.Right);
            row.Children.Add(del);
            var text = u.DisplayName == u.Username ? u.Username : $"{u.DisplayName}（{u.Username}）";
            if (u.IsAdmin)
                text += " · " + I18n.Tr("门户管理员");
            text += " · " + I18n.Tr("最近登录") + " " + u.LastLogin;
            row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
            SsoUsersPanel.Children.Add(row);
        }
    }

    // ---------- 图床存储 ----------

    private void ImageHostEnableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("image_host", "enabled", ImageHostEnableCombo.SelectedIndex == 0 ? "True" : "False");
        ApplyImageHost();
    }

    private void ImageHostUrlBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("image_host", "base_url", ImageHostUrlBox.Text.Trim());
        ImageHostUrlBox.Text = AppConfig.ImageHostBaseUrl;   // 回显补全协议后的地址
        ApplyImageHost();
    }

    private void ImageHostProjectBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("image_host", "project", ImageHostProjectBox.Text.Trim());
        ApplyImageHost();
    }

    private void ImageHostTokenBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.Write("image_host", "token", ImageHostTokenBox.Text.Trim());
        ApplyImageHost();
    }

    /// <summary>图床配置变更后：丢弃内存映射并补传缺的封面（未启用时 Kick 为空操作）。</summary>
    private void ApplyImageHost()
    {
        ImageHostService.Invalidate();
        ImageHostService.Kick();
        UpdateImageHostStatus();
    }

    private async void ImageHostTestButton_Click(object sender, RoutedEventArgs e)
    {
        ImageHostStatusLabel.Text = I18n.Tr("测试中…");
        // 用输入框里的现值：失焦即存，但点按钮那一下的值可能还没写进库
        var (ok, info, error) = await ImageHostClient.PingAsync(
            ImageHostUrlBox.Text.Trim(), ImageHostProjectBox.Text.Trim(), ImageHostTokenBox.Text.Trim());
        ImageHostStatusLabel.Text = ok
            ? I18n.Format(I18n.Tr("✓ 已连接（图床现有 {count} 张）"), ("count", (info?.ImageCount ?? 0).ToString()))
            : $"✗ {error}";
    }

    private void ImageHostMigrateButton_Click(object sender, RoutedEventArgs e)
    {
        if (!AppConfig.ImageHostEnabled)
        {
            ImageHostStatusLabel.Text = I18n.Tr("请先启用图床存储");
            return;
        }
        ImageHostService.Kick();
        StartImageHostPoll();
    }

    /// <summary>同步进行中每 1.5s 刷新状态文本（同 Web 设置页的轮询节奏），结束即停。</summary>
    private void StartImageHostPoll()
    {
        _imageHostTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };
        _imageHostTimer.Tick -= ImageHostTimer_Tick;
        _imageHostTimer.Tick += ImageHostTimer_Tick;
        _imageHostTimer.Start();
        UpdateImageHostStatus();
    }

    private System.Windows.Threading.DispatcherTimer? _imageHostTimer;

    private void ImageHostTimer_Tick(object? sender, EventArgs e)
    {
        UpdateImageHostStatus();
        if (!ImageHostService.State().Running)
            _imageHostTimer?.Stop();
    }

    private void UpdateImageHostStatus()
    {
        if (ImageHostStatusLabel == null)
            return;
        var (running, status) = ImageHostService.State();
        if (running)
        {
            ImageHostStatusLabel.Text = I18n.Tr("⏳ 迁移中 ") + status;
            // 外部（Web 端 / 启动时）触发的迁移也能在本页看到进度。
            // 必须判 IsEnabled：StartImageHostPoll 回头又会调本方法，不判就是无限递归。
            if (_imageHostTimer is not { IsEnabled: true })
                StartImageHostPoll();
            return;
        }
        // 常驻提示（已迁移/待迁移）取服务端同一句文案，与 Web 设置页保持一致
        ImageHostStatusLabel.Text = status.Length > 0 ? "✓ " + status : ImageHostService.IdleSummary();
    }

    // ---------- 图片翻译 ----------

    private void TransEnableCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var wantOn = TransEnableCombo.SelectedIndex == 0;
        // 依赖没下齐就不让开：开了也不会生效（TranslateService.Enabled 两个条件都要），
        // 与其让用户以为开了，不如在这里挡住并说清楚原因
        if (wantOn && !TranslateService.DepsReady)
        {
            _loading = true;
            TransEnableCombo.SelectedIndex = 1;
            _loading = false;
            TransStatusLabel.Text = I18n.Tr("请先下载模型依赖，下齐后才能启用");
            return;
        }
        AppConfig.TranslateEnabledSetting = wantOn;
        UpdateTranslateStatus();
    }

    private void TransTierCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        var index = TransTierCombo.SelectedIndex;
        if (index < 0 || index >= _tierIds.Count)
            return;
        AppConfig.TranslateModelTier = _tierIds[index];
        // 换档位后新档位的依赖多半没下齐，此时先前的"已启用"就成了空头支票——直接关掉
        if (AppConfig.TranslateEnabledSetting && !TranslateService.DepsReady)
        {
            AppConfig.TranslateEnabledSetting = false;
            _loading = true;
            TransEnableCombo.SelectedIndex = 1;
            _loading = false;
        }
        UpdateTranslateStatus();
    }

    private void TransDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.TranslateDevice = TransDeviceCombo.SelectedIndex == 1 ? "gpu" : "cpu";
    }

    private void TransPathBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        ApplyTranslatePath(TransPathBox.Text.Trim());
    }

    private void TransPathChooseButton_Click(object sender, RoutedEventArgs e)
    {
        var current = TransPathBox.Text;
        var dialog = new OpenFolderDialog
        {
            Title = I18n.Tr("选择模型目录"),
            InitialDirectory = Directory.Exists(current) ? current : TranslateModels.Root,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;
        ApplyTranslatePath(Path.GetFullPath(dialog.FolderName));
    }

    /// <summary>换模型目录：清单与安装记录都跟着目录走，必须整体失效重读。</summary>
    private void ApplyTranslatePath(string path)
    {
        if (TranslateDeps.Running)
        {
            TransStatusLabel.Text = I18n.Tr("下载进行中，无法更改模型目录");
            TransPathBox.Text = TranslateModels.Root;
            return;
        }
        AppConfig.TranslateModelPath = path;
        TranslateModels.Invalidate();
        TransPathBox.Text = TranslateModels.Root;
        // 新目录里多半没有模型，同样不能让"已启用"悬空
        if (AppConfig.TranslateEnabledSetting && !TranslateService.DepsReady)
        {
            AppConfig.TranslateEnabledSetting = false;
            _loading = true;
            TransEnableCombo.SelectedIndex = 1;
            _loading = false;
        }
        UpdateTranslateStatus();
    }

    private void TransFontBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        AppConfig.TranslateFont = TransFontBox.Text.Trim();
    }

    private async void TransDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (TranslateDeps.Running)
            return;
        var tier = TranslateModels.Tier(AppConfig.TranslateModelTier);
        var status = TranslateModels.Status(tier.Id);
        if (status.Ready)
        {
            TransStatusLabel.Text = I18n.Tr("依赖已齐备，可以启用图片翻译");
            return;
        }
        var need = Math.Max(0, status.TotalBytes - status.HaveBytes);
        var message = I18n.Format(
            I18n.Tr("将下载「{tier}」的模型依赖，约 {size}，存放在：\n{path}\n\n下载可随时取消并支持断点续传。是否继续？"),
            ("tier", tier.Name), ("size", TranslateModels.FormatSize(need)), ("path", TranslateModels.Root));
        if (!InAppDialog.Confirm(this, message, I18n.Tr("下载依赖")))
            return;

        UpdateTranslateStatus();
        await TranslateDeps.StartAsync(tier.Id);
        UpdateTranslateStatus();
    }

    private void TransCancelButton_Click(object sender, RoutedEventArgs e) => TranslateDeps.Cancel();

    private void TransRemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (TranslateDeps.Running)
        {
            TransStatusLabel.Text = I18n.Tr("请先取消下载");
            return;
        }
        var tier = TranslateModels.Tier(AppConfig.TranslateModelTier);
        var status = TranslateModels.Status(tier.Id);
        if (status.HaveBytes <= 0)
        {
            TransStatusLabel.Text = I18n.Tr("没有已下载的模型文件");
            return;
        }
        var message = I18n.Format(
            I18n.Tr("将删除「{tier}」的模型文件，释放约 {size}。图片翻译会随之停用。是否继续？"),
            ("tier", tier.Name), ("size", TranslateModels.FormatSize(status.HaveBytes)));
        if (!InAppDialog.Confirm(this, message, I18n.Tr("删除依赖")))
            return;

        var freed = TranslateModels.Remove(tier.Id);
        AppConfig.TranslateEnabledSetting = false;
        _loading = true;
        TransEnableCombo.SelectedIndex = 1;
        _loading = false;
        UpdateTranslateStatus();
        TransStatusLabel.Text = I18n.Format(
            I18n.Tr("已删除模型文件，释放 {size}"), ("size", TranslateModels.FormatSize(freed)));
    }

    /// <summary>刷新图片翻译区块的可用性、进度条与状态文字。</summary>
    private void UpdateTranslateStatus()
    {
        if (TransStatusLabel == null)
            return;

        var running = TranslateDeps.Running;
        var ready = TranslateService.DepsReady;

        // 依赖没齐就不给开；下载中把会改动目标的控件全部锁住
        TransEnableCombo.IsEnabled = ready && !running;
        TransTierCombo.IsEnabled = !running;
        TransPathBox.IsEnabled = !running;
        TransPathChooseButton.IsEnabled = !running;
        TransDownloadButton.IsEnabled = !running && !ready;
        TransCancelButton.IsEnabled = running;
        TransRemoveButton.IsEnabled = !running;

        var progress = TranslateDeps.Progress;
        if (running && progress.TotalTotal > 0)
        {
            TransProgressBar.Visibility = Visibility.Visible;
            TransProgressBar.Value = Math.Clamp(progress.TotalHave * 100.0 / progress.TotalTotal, 0, 100);
        }
        else
        {
            TransProgressBar.Visibility = Visibility.Collapsed;
        }

        if (running)
        {
            var speed = progress.SpeedBps > 0
                ? $" · {TranslateModels.FormatSize((long)progress.SpeedBps)}/s"
                : "";
            TransStatusLabel.Text =
                $"⏳ {progress.Message}　{TranslateModels.FormatSize(progress.TotalHave)} / " +
                $"{TranslateModels.FormatSize(progress.TotalTotal)}{speed}";
            return;
        }

        // 下载刚失败/刚取消时优先显示那句话，否则显示常驻状态
        if (progress.Message.Length > 0 && progress.Failed)
        {
            TransStatusLabel.Text = "✗ " + progress.Message;
            return;
        }
        TransStatusLabel.Text = (ready ? "✓ " : "") + TranslateService.StatusText();
    }

    private void UpdateWebStatus()
    {
        if (WebStatusLabel == null)
            return;
        if (!AppConfig.WebEnabled)
        {
            WebStatusLabel.Text = I18n.Tr("外部访问已关闭");
            return;
        }
        if (WebServer.IsRunning)
        {
            var url = $"http://{WebServer.LocalIPv4()}:{WebServer.RunningPort}";
            var text = I18n.Format(I18n.Tr("已开启 · 在手机/电脑浏览器打开 {url}"), ("url", url));
            if (AppConfig.WebPassword.Length > 0)
                text += I18n.Tr("（需输入访问密码）");
            if (PortalSso.Active)
                text += I18n.Tr("（可用门户单点登录）");
            WebStatusLabel.Text = text;
        }
        else
        {
            WebStatusLabel.Text = I18n.Tr("启动失败：端口可能被占用，请更换端口");
        }
    }
}
