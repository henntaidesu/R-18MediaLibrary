# R-18MediaLibrary · R18 媒体库

**图文系统展示：** [简体中文](系统展示.md) · [日本語](系统展示.ja.md) · [English](系统展示.en.md)

Windows 上的本地媒体库与下载管理工具，使用 WPF、C# 和 .NET 10。支持作品导入、元数据补全、多维分类、音视频播放，以及浏览器远程访问。

**[完整使用手册](docs/使用手册.md)** · [日文说明（历史版本）](README.ja.md)

![媒体库 Web 首页](docs/assets/manual/home.png)

## 功能

| 功能 | 当前实现 |
| --- | --- |
| 媒体库 | 多库、多数据源；作品／社团视图切换；扫描导入、元数据与封面补全；库改名和路径更新 |
| 浏览与整理 | 本地搜索、排序、标签、社团、形式、收藏、已读、作品移动与删除 |
| 播放与看图 | 文件树／缩略图，大图翻页，音视频队列、睡眠定时；桌面 LibVLC 视频与 NAudio 音频 |
| DLsite 来源 | 编号／社团／目录查询，Anime-sharing 帖子解析、网盘检测、组合分卷下载 |
| asmr.one | 桌面 SOU 直链下载、文件类型过滤；无对应作品时回退论坛来源 |
| FANBOX | pawchive 归档作家检索；登录后增加官方来源；单篇与批量下载 |
| E-Hentai／pixiv | 检索、详情预览、单件／批量下载，完成后统一入库 |
| 下载管理 | 进度、断点续传、暂停、解析重试、限速、用量、自动解压与历史 |
| 作家监控 | 定时检查 pawchive 作家新投稿，支持独立间隔、暂停和立即检查 |
| 账号访问 | FANBOX、pixiv、E-Hentai 登录信息配置、检测与清除 |
| Web 与串流 | HTTP 服务、访问密码、可选门户 SSO；游戏在服务端 Windows 运行并向网页串流 |
| 封面图床 | 可选自建图床、连接检测和已有封面迁移 |

图片翻译已有桌面设置和模型依赖管理，**尚未接入实际翻译执行与查看入口**，不列作完整可用功能。详情见[手册第 17 节](docs/使用手册.md#17-图片翻译的当前实现范围)。

## 快速开始

### 从源码运行

需要 Windows 和 .NET 10 SDK，在仓库根目录执行：

```powershell
dotnet build src/R-18MediaLibrary.csproj
dotnet run --project src/R-18MediaLibrary.csproj
```

也可双击 `start.bat`，以 Release 配置运行。

### 发布

运行 `publish.bat`，生成 `publish/R-18MediaLibrary.exe`。目标电脑需要 **.NET 10 Desktop Runtime**，保留 exe 旁的 `libvlc/win-x64/` 数据目录。

脚本会清理旧 `publish` 文件夹，请勿把个人文件、数据库或媒体库放入其中。

### 首次使用

1. 在系统设置确认下载缓存路径及网络配置。
2. 新建媒体库，添加运行程序的电脑上的数据源目录。
3. 导入已有作品，等待扫描和元数据补全。
4. 按来源配置账号或 Debrid-Link，在作品搜索查询并选择目标库下载。
5. 到下载管理确认进度，完成后在媒体库查看和播放。

本地扫描检查数据源下的一级子目录，名称需包含 `RJ` 加至少六位数字，或 `FB`／`EH`／`PX` 加数字。搜索支持的编号与本地扫描范围并不完全相同。

## 来源与前置条件

| 来源 | 前置条件 | 下载方式 |
| --- | --- | --- |
| Anime-sharing | 有效 Debrid-Link Key、可用网盘额度与链接 | 论坛网盘 → 中转解析 → 缓存／解压 → 媒体库 |
| asmr.one | 桌面配置账号、SOU 优先源及文件类型 | 直链，保留多级目录 |
| FANBOX 归档 | pawchive 来源可访问 | 投稿及受支持附件直链，目录 `FB<投稿ID>` |
| FANBOX 官方 | 有效登录，账号拥有对应内容权限 | 官方投稿，目录同上 |
| E-Hentai／ExHentai | 按站点及内容要求配置登录 | 画廊图片，目录 `EH<画廊ID>` |
| pixiv | 按内容要求配置登录，未登录可能过滤 R-18 结果 | 作品图片，目录 `PX<作品ID>` |

Debrid-Link 只负责相关网盘解析，不能代替来源账号权限。FANBOX 作家监控目前使用归档来源。

## 桌面端与 Web 端

在桌面“系统设置 → 外部访问”配置开关、端口和密码，浏览器打开 `http://<主机地址>:<端口>/`，默认端口 8080。网页和桌面共用数据库、文件、任务与监控。

Web 的目录、下载位置以及“本机运行”均指向**服务端电脑**。程序关闭后 Web、下载和监控停止。网页播放兼容性由浏览器决定，桌面使用本地播放引擎。

asmr.one 直链入口、SOU 优先源、E-Hentai 画质／站点、pixiv 分级／匹配／画质、外部访问及 SSO 参数在桌面配置；当前 Web 搜索没有 asmr.one 专用入口。

门户代理需配套 API 和 WebSocket 转发。当前部分请求使用根路径 `/api/...`，子路径下首页能打开不代表全部功能可用。部署与串流限制见[使用手册](docs/使用手册.md)。

## 配置与数据

配置保存在 SQLite `R-18MediaLibrary.db` 的 `conf` 表，由 [AppConfig.cs](src/Core/AppConfig.cs) 管理。普通设置通常在失焦或选择时保存，账号页使用“保存并检测”。

| 数据 | 位置／作用 |
| --- | --- |
| 数据库 | 优先工作目录，再从 exe 目录向上查找；不存在时在 exe 目录创建 |
| 媒体库 | 用户配置的数据源目录，保存作品及 `DataSource` 元数据等 |
| 下载缓存 | 系统设置中的缓存路径 |
| 回退封面 | 工作目录 `images/` |
| 日志 | 工作目录 `log/` |
| 来源缓存 | `cache/`，如 pawchive 作家索引 |

旧 `DLsiteMedia.db` 或 `DASD.db` 在没有新名库时会尝试迁移。备份前正常退出并确认实际数据库位置；数据库含账号信息，不应公开提交。

“删除媒体库”保留文件和已导入记录；“删除作品”会删除实际目录及相关记录，不可恢复。扫描也会清理缺失作品记录，网络盘或移动盘不可用时先恢复挂载。

## 开发导航

```text
src/
├─ Core/                 SQLite、配置、日志、网络与语言
├─ Views/                WPF 页面与对话框
├─ Controls/             文件与播放等界面组件
├─ Services/             来源、下载、解压、媒体库、Web 与串流
│  └─ Translate/         翻译模型依赖管理等基础代码
└─ Web/                  内嵌 Web 前端
docs/
├─ 使用手册.md           分功能步骤、限制与排查
└─ assets/manual/        Edge 实际界面截图
```

主要依赖包括 Microsoft.Data.Sqlite、HtmlAgilityPack、SharpCompress、LibVLCSharp、VideoLAN.LibVLC.Windows、NAudio 和 CommunityToolkit.Mvvm，版本以[工程文件](src/R-18MediaLibrary.csproj)为准。程序集名为 `R-18MediaLibrary`，C# 根命名空间为 `R18MediaLibrary`。

手册按 2026-10-26 源码与 Web 界面核对，附功能到源码的对应表。下载、删除、移动、登录及串流未在文档编写中实际执行测试。

仅管理自己有权访问的内容，并遵守来源服务条款。
