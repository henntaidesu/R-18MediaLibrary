using System.Windows;
using R18MediaLibrary.Core;
using R18MediaLibrary.Services;

namespace R18MediaLibrary;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Db.EnsureTables();
        I18n.InitLanguage();  // 创建窗口前加载已保存的语言
        // 设置中开启了自动下载时，随程序启动下载线程；否则由下载页"开始下载"按钮启动
        if (AppConfig.AutoDownload)
            DownloadEngine.Start();
        // 设置中开启了外部访问时，随程序启动内嵌 Web 服务
        if (AppConfig.WebEnabled)
            WebServer.StartFromConfig();
        // 后台补齐 DLsite 社团头像（查过的社团不会再发请求）；查完会自己再 Kick 一次图床
        DlsiteMakerIcon.Kick();
        // 开启了图床存储时，后台把新入库作品的封面补传上去（未开启为空操作）
        ImageHostService.Kick();
        // FANBOX 作家监控的轮询线程：到点就去 pawchive 看有没有新投稿，有就自动入队下载
        // （没有监控项时为空转；总开关关闭时只是不做事，手动「立即检查」仍可用）
        FanboxWatchService.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        FanboxWatchService.Stop();
        ImageHostService.Stop();
        WebServer.Stop();
        Db.Checkpoint();
        base.OnExit(e);
    }
}
