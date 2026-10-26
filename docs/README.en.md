# R-18MediaLibrary · R18 Media Library

[简体中文](../README.md) · [日本語](README.ja.md) · **English**

**Visual showcase:** [简体中文](系统展示.md) · [日本語](系统展示.ja.md) · [English](系统展示.en.md)

A local media library and download manager for Windows, built with WPF, C# and .NET 10. It imports works, fills in metadata, organizes them along several dimensions, plays audio and video, and can be accessed remotely from a browser.

**[Full user manual](使用手册.en.md)**

![Media library web home page](assets/manual/home.png)

## Features

| Feature | Current implementation |
| --- | --- |
| Media library | Multiple libraries and data sources; switch between work and circle views; scan-based import, metadata and cover backfill; library rename and path updates |
| Browsing and organizing | Local search, sorting, tags, circles, work types, favorites, read status, moving and deleting works |
| Playback and viewing | File tree / thumbnails, full-size image paging, audio and video queues, sleep timer; LibVLC video and NAudio audio on the desktop |
| DLsite source | Lookup by work ID / circle / catalog, Anime-sharing thread parsing, file-host checks, combined multi-part downloads |
| asmr.one | Direct-link downloads of SOU works on the desktop, file-type filter; falls back to the forum source when the work is missing |
| FANBOX | Creator search via the pawchive archive; official source added after login; single and batch downloads |
| E-Hentai / pixiv | Search, detail preview, single / batch downloads, imported into the library on completion |
| Download manager | Progress, resume, pause, re-resolve, speed limit, usage, automatic extraction and history |
| Creator monitoring | Periodically checks pawchive creators for new posts, with per-creator interval, pause and check-now |
| Account access | Configure, check and clear logins for FANBOX, pixiv and E-Hentai |
| Web and streaming | HTTP server, access password, optional portal SSO; games run on the server's Windows and stream to the web page |
| Cover image host | Optional self-hosted image host, connection check and migration of existing covers |

Image translation has desktop settings and model-dependency management, but **actual translation and a viewer are not wired up yet**, so it is not listed as a working feature. See [manual section 17](使用手册.en.md#17-current-image-translation-scope).

## Quick start

### Run from source

Requires Windows and the .NET 10 SDK. From the repository root:

```powershell
dotnet build src/R-18MediaLibrary.csproj
dotnet run --project src/R-18MediaLibrary.csproj
```

You can also double-click `start.bat` to run the Release configuration.

### Publish

Run `publish.bat` to produce `publish/R-18MediaLibrary.exe`. The target machine needs the **.NET 10 Desktop Runtime**, and the `libvlc/win-x64/` data folder must stay next to the exe.

The script wipes the old `publish` folder, so don't keep personal files, the database or a media library in it.

### First use

1. In system settings, confirm the download cache path and network settings.
2. Create a media library and add data-source folders on the computer running the program.
3. Import existing works and wait for scanning and metadata backfill.
4. Configure accounts or Debrid-Link per source, then search in Work Search and pick a target library to download into.
5. Check progress in the download manager; once finished, browse and play in the media library.

Local scanning looks at the first-level subfolders of each data source; a folder name must contain `RJ` plus at least six digits, or `FB` / `EH` / `PX` plus digits. The IDs accepted by search and the ones recognized by local scanning are not exactly the same.

## Sources and prerequisites

| Source | Prerequisites | Download method |
| --- | --- | --- |
| Anime-sharing | Valid Debrid-Link key, available file-host quota and links | Forum file host → relay resolve → cache / extract → media library |
| asmr.one | Account, SOU preferred source and file types configured on the desktop | Direct links, folder hierarchy preserved |
| FANBOX archive | pawchive must be reachable | Direct links to posts and supported attachments, folder `FB<post ID>` |
| FANBOX official | Valid login whose account has access to the content | Official posts, same folder layout |
| E-Hentai / ExHentai | Login configured as the site and content require | Gallery images, folder `EH<gallery ID>` |
| pixiv | Login configured as the content requires; without login R-18 results may be filtered out | Artwork images, folder `PX<artwork ID>` |

Debrid-Link only resolves supported file hosts; it does not replace account permissions at the source. FANBOX creator monitoring currently uses the archive source only.

## Desktop and web

Under desktop "System settings → External access", set the switch, port and password, then open `http://<host address>:<port>/` in a browser (default port 8080). The web and desktop share the database, files, tasks and monitoring.

Folders, download locations and "Run on this PC" in the web UI all refer to the **server computer**. Closing the program stops the web server, downloads and monitoring. Playback compatibility on the web depends on the browser; the desktop uses its local playback engines.

The asmr.one direct-link entry, SOU preferred source, E-Hentai quality / site, pixiv rating / match mode / quality, external access and SSO parameters are configured on the desktop; the web search currently has no dedicated asmr.one entry.

A portal reverse proxy needs matching API and WebSocket forwarding. Some requests currently use the root path `/api/...`, so a home page that loads under a sub-path does not mean every feature works. See the [user manual](使用手册.en.md) for deployment and streaming limitations.

## Configuration and data

Settings live in the `conf` table of the SQLite database `R-18MediaLibrary.db`, managed by [AppConfig.cs](../src/Core/AppConfig.cs). Ordinary settings usually save on blur or selection; the account page uses "Save and check".

| Data | Location / purpose |
| --- | --- |
| Database | Working directory first, then searched upward from the exe folder; created in the exe folder if not found |
| Media library | User-configured data-source folders holding works, `DataSource` metadata and so on |
| Download cache | Cache path in system settings |
| Fallback covers | `images/` in the working directory |
| Logs | `log/` in the working directory |
| Source cache | `cache/`, e.g. the pawchive creator index |

An old `DLsiteMedia.db` or `DASD.db` is migrated when no database with the new name exists. Before backing up, exit the program normally and confirm where the database actually is; it contains account data and must not be committed publicly.

"Delete media library" keeps the files and imported records; "Delete work" removes the actual folder and its records and cannot be undone. Scanning also removes records of missing works, so restore network or removable drives before scanning if they are unavailable.

## Development guide

```text
src/
├─ Core/                 SQLite, config, logging, networking, languages
├─ Views/                WPF pages and dialogs
├─ Controls/             File and playback UI components
├─ Services/             Sources, downloads, extraction, media library, web, streaming
│  └─ Translate/         Translation model dependency management and groundwork
└─ Web/                  Embedded web front end
docs/
├─ 使用手册.md           Step-by-step usage, limits, troubleshooting (.ja / .en available)
├─ 系统展示.md           Illustrated feature showcase (.ja / .en available)
├─ README.ja.md / README.en.md
└─ assets/manual/        Screenshots of the real UI taken in Edge
```

Main dependencies are Microsoft.Data.Sqlite, HtmlAgilityPack, SharpCompress, LibVLCSharp, VideoLAN.LibVLC.Windows, NAudio and CommunityToolkit.Mvvm; see the [project file](../src/R-18MediaLibrary.csproj) for versions. The assembly name is `R-18MediaLibrary`; the C# root namespace is `R18MediaLibrary`.

The manual was checked against the source and web UI as of 2026-10-26 and includes a feature-to-source reference table. Downloading, deleting, moving, logging in and streaming were not actually exercised while writing the docs.

Only manage content you are entitled to access, and follow each source's terms of service.
