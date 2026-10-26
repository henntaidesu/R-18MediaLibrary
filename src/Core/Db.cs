using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace R18MediaLibrary.Core;

/// <summary>
/// SQLite 数据库访问层（对应 Python 版 datebase_execution.py）。
/// 沿用项目根目录的 R-18MediaLibrary.db：conf / download_list / works / work_genres 表，老数据无缝继承。
/// 每次操作独立连接（Sqlite 连接池），WAL 模式下 UI 刷新 + 下载线程 + 元数据补全并发安全。
/// </summary>
public static class Db
{
    private const string DbFileName = "R-18MediaLibrary.db";
    // 历次改名前的库文件名（新 → 旧）：仅用于一次性迁移旧数据，之后一律按 DbFileName 访问。
    private static readonly string[] LegacyDbFileNames = ["DLsiteMedia.db", "DASD.db"];

    private static readonly string DbPath = LocateDb();
    private static readonly string ConnString =
        new SqliteConnectionStringBuilder { DataSource = DbPath, DefaultTimeout = 30 }.ToString();

    private static bool _initialized;
    private static readonly object InitLock = new();

    /// <summary>
    /// 定位 R-18MediaLibrary.db：优先当前工作目录，其次从 exe 目录向上逐级查找（开发期命中仓库根），
    /// 每处查找前都会先尝试把同目录下的旧版库文件（DLsiteMedia.db / DASD.db）迁移为新文件名，都没有时在 exe 目录新建。
    /// </summary>
    private static string LocateDb()
    {
        var cwdDir = Environment.CurrentDirectory;
        MigrateLegacyDb(cwdDir);
        var cwd = Path.Combine(cwdDir, DbFileName);
        if (File.Exists(cwd))
            return cwd;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            MigrateLegacyDb(dir.FullName);
            var candidate = Path.Combine(dir.FullName, DbFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return Path.Combine(AppContext.BaseDirectory, DbFileName);
    }

    /// <summary>
    /// 旧版数据库改名迁移：目录下存在老版库文件（按 <see cref="LegacyDbFileNames"/> 由新到旧取第一个命中的）
    /// 但新文件名尚不存在时原地改名（.db 本体 + -wal/-shm 一并改名，不丢 WAL 里尚未 checkpoint 的数据），
    /// 一次性完成。迁移前不能有任何连接打开过旧库，故只能在 LocateDb 算出路径之前做纯文件操作；
    /// 失败（如文件被占用）时静默保留旧文件名，下次启动重试。
    /// </summary>
    private static void MigrateLegacyDb(string dir)
    {
        var newPath = Path.Combine(dir, DbFileName);
        if (File.Exists(newPath))
            return;
        foreach (var legacyName in LegacyDbFileNames)
        {
            var legacyPath = Path.Combine(dir, legacyName);
            if (!File.Exists(legacyPath))
                continue;
            try
            {
                File.Move(legacyPath, newPath);
                foreach (var suffix in new[] { "-wal", "-shm" })
                {
                    var legacySide = legacyPath + suffix;
                    if (File.Exists(legacySide))
                        File.Move(legacySide, newPath + suffix);
                }
                Console.WriteLine($"[DB] 已将旧版数据库 {legacyPath} 迁移为 {newPath}");
            }
            catch (IOException e)
            {
                Console.WriteLine($"[DB] 旧版数据库迁移失败，保留旧文件名待下次重试: {e.Message}");
            }
            return;
        }
    }

    public static string DatabasePath => DbPath;

    public static SqliteConnection Open()
    {
        EnsureTables();
        var conn = new SqliteConnection(ConnString);
        conn.Open();
        // WAL 是数据库级持久属性、synchronous 由 EnsureTables 建库时设一次即可，
        // 不再每次开连接重复执行 PRAGMA（每查询固定开销）。
        return conn;
    }

    /// <summary>建表（不存在时）并为旧版 works 表补加缺失列，进程内只执行一次。</summary>
    public static void EnsureTables()
    {
        if (_initialized) return;
        lock (InitLock)
        {
            if (_initialized) return;
            using var conn = new SqliteConnection(ConnString);
            conn.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    PRAGMA journal_mode=WAL;
                    PRAGMA synchronous=NORMAL;
                    CREATE TABLE IF NOT EXISTS "conf" (
                        "section" TEXT NOT NULL,
                        "key" TEXT NOT NULL,
                        "value" TEXT,
                        PRIMARY KEY ("section", "key")
                    );
                    CREATE TABLE IF NOT EXISTS "download_list" (
                        "UUID" text,
                        "work_id" text,
                        "url" TEXT NOT NULL,
                        "status" text,
                        "long" text,
                        "delete" text,
                        PRIMARY KEY ("url")
                    );
                    CREATE TABLE IF NOT EXISTS "works" (
                        "work_id" text,
                        "work_name" TEXT,
                        "maker_id" text,
                        "maker_name" TEXT,
                        "work_type" text,
                        "intro_s" TEXT,
                        "age_category" text,
                        "is_ana" text,
                        "state" text,
                        "library" text,
                        "sell_date" text,
                        "series" text,
                        "scenario" text,
                        "illust" text,
                        "voice_actor" text,
                        "genre" text,
                        "file_size" text,
                        "cover" text,
                        "down_time" text,
                        "meta_scanned" text,
                        "folder" text,
                        "target" text,
                        "target_lib" text,
                        PRIMARY KEY ("work_id")
                    );
                    CREATE TABLE IF NOT EXISTS "work_genres" (
                        "work_id" text NOT NULL,
                        "genre" text NOT NULL,
                        PRIMARY KEY ("work_id", "genre")
                    );
                    CREATE TABLE IF NOT EXISTS "dislikes" (
                        "work_id" text NOT NULL,
                        "time" text,
                        PRIMARY KEY ("work_id")
                    );
                    CREATE TABLE IF NOT EXISTS "as_scan_cache" (
                        "work_id" text NOT NULL,
                        "count" integer,
                        "time" text,
                        PRIMARY KEY ("work_id")
                    );
                    -- 图床映射：本地图片 -> 图床上的那一份。external_key 同时是图床侧的幂等键
                    -- （同 key 重复上传直接复用旧记录），一个作品封面至多一行，故"迁移两次"在此被挡住。
                    -- src_* 是本地文件指纹（快路径：指纹一致就跳过，不读盘）；sha256 是内容指纹
                    -- （慢路径：指纹变了但内容没变——移库改了路径、重扫改了 mtime——照样不重传）。
                    -- 图床不可用/未接管时各处回退读本地原图，故本表可随时清空，靠图床的 lookup 重建。
                    CREATE TABLE IF NOT EXISTS "image_host" (
                        "external_key" text NOT NULL,
                        "work_id" text,
                        "kind" text,
                        "stored_name" text,
                        "path" text,
                        "src_path" text,
                        "src_size" text,
                        "src_mtime" text,
                        "sha256" text,
                        "up_time" text,
                        PRIMARY KEY ("external_key")
                    );

                    -- 社团显示名映射：只改界面上的显示，works.maker_name 这个真名不动。
                    -- 分组、筛选、入库路径全部仍按真名走，本表丢了也只是回到显示真名。
                    -- DLsite 社团头像（来自 ci-en）的查询结果缓存。
                    -- icon_url 为空串表示"查过了，该社团没有 ci-en 账号/头像"——与"还没查过"
                    -- （表里没有这一行）区分开，否则每次都要为没头像的社团重新发一次请求。
                    CREATE TABLE IF NOT EXISTS "maker_icon" (
                        "maker_id" text NOT NULL,
                        "icon_url" text,
                        "up_time" text,
                        PRIMARY KEY ("maker_id")
                    );

                    CREATE TABLE IF NOT EXISTS "maker_alias" (
                        "maker_name" text NOT NULL,
                        "alias" text NOT NULL,
                        "up_time" text,
                        PRIMARY KEY ("maker_name")
                    );

                    -- 社团主页（FANBOX 官方主页 / pawchive 主页），社团卡上显示成两个外链。按真名记，同 maker_alias。
                    -- fanbox 作家下载时自动记上；两个地址都是空串的行表示"用户清掉了"，自动补齐不再填回去。
                    CREATE TABLE IF NOT EXISTS "maker_home" (
                        "maker_name" text NOT NULL,
                        "fanbox_url" text,
                        "pawchive_url" text,
                        "up_time" text,
                        PRIMARY KEY ("maker_name")
                    );

                    -- FANBOX 作家监控：按作家轮询 pawchive，发现新投稿就自动入队下载。
                    -- last_published 是"已处理到哪一篇"的水位线：站上投稿的 published 是 ISO 字符串
                    -- （2026-08-29T23:53:10），按字典序比较即按时间比较，比它新的才算新作品。
                    -- 空串表示"连现有作品也要下"，第一轮会把该作家站上的全部投稿入队，水位线随即推到最新。
                    -- target_lib / target_folder 是添加监控时选定的下载目标，自动下载据此入库。
                    CREATE TABLE IF NOT EXISTS "fanbox_watch" (
                        "artist_id" text NOT NULL,
                        "service" text,
                        "artist_name" text,
                        "interval_min" integer,
                        "enabled" text,
                        "last_check" text,
                        "last_published" text,
                        "target_lib" text,
                        "target_folder" text,
                        "last_result" text,
                        "downloaded" integer,
                        "add_time" text,
                        PRIMARY KEY ("artist_id")
                    );

                    -- 外部访问（Web 端）的登录用户，由门户单点登录首次登录时建出来（Services/PortalSso）。
                    -- 认人只认 portal_sub（门户的用户 id）：用户名在门户里能改，跟着它走会把改过名的人
                    -- 认成另一个人。用户名只在首次绑定时用来匹配同名的已有用户。
                    -- 媒体库数据不分用户，这张表只管「谁登进来了」。
                    CREATE TABLE IF NOT EXISTS "web_users" (
                        "id" integer PRIMARY KEY AUTOINCREMENT,
                        "username" text NOT NULL UNIQUE COLLATE NOCASE,
                        "display_name" text,
                        "portal_sub" text UNIQUE,
                        "is_admin" text,
                        "created_at" text,
                        "last_login" text
                    );
                    """;
                cmd.ExecuteNonQuery();
            }

            // 旧版 works 表缺列时补加
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"works\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    columns.Add(reader.GetString(1));
            }
            string[] required =
            [
                "state", "library", "sell_date", "series", "scenario", "illust",
                "voice_actor", "genre", "file_size", "cover", "meta_scanned", "folder",
                "target", "target_lib", "read_flag", "favorite",
                // 数据源区分："anime-sharing"(默认/旧数据) / "asmr" / "fanbox" / "ehentai" / "pixiv"；
                // asmr_id 存 asmr.one 的数字作品 id，eh_token 存 E-Hentai 画廊令牌（回访画廊页两者缺一不可）
                "source", "asmr_id", "eh_token"
            ];
            foreach (var col in required)
            {
                if (!columns.Contains(col))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE \"works\" ADD COLUMN \"{col}\" text";
                    cmd.ExecuteNonQuery();
                }
            }

            // 旧版 download_list 缺 error 列时补加（记录解析失败原因）
            var dlColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"download_list\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    dlColumns.Add(reader.GetString(1));
            }
            if (!dlColumns.Contains("error"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE \"download_list\" ADD COLUMN \"error\" text";
                cmd.ExecuteNonQuery();
            }
            // download_list 补加来源与作品内子目录列：
            // source="asmr" 时 url 即直链（跳过 debrid 解析）；sub_path 为该文件在作品内的相对子目录（保留 asmr 目录树）
            // account：pixiv / FANBOX 多账号时，该条是从哪个账号入队的（用户号；SiteAccounts），下载时用它的 cookie
            foreach (var dlCol in new[] { "source", "sub_path", "account" })
            {
                if (!dlColumns.Contains(dlCol))
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"ALTER TABLE \"download_list\" ADD COLUMN \"{dlCol}\" text";
                    cmd.ExecuteNonQuery();
                }
            }

            // image_host 补 sha256 列：该表在加入内容指纹之前就可能已被建出来（开发期跑过一次），
            // 那种库里 INSERT 会因缺列直接失败，故照 works/download_list 的做法补一手。
            var ihColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(\"image_host\")";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                    ihColumns.Add(reader.GetString(1));
            }
            if (ihColumns.Count > 0 && !ihColumns.Contains("sha256"))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "ALTER TABLE \"image_host\" ADD COLUMN \"sha256\" text";
                cmd.ExecuteNonQuery();
            }

            // 为高频过滤/排序列建二级索引（列必已由上方建表或补列保证存在），
            // 避免媒体库/标签/形式/已下载页的全表扫描。
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    CREATE INDEX IF NOT EXISTS "idx_works_library" ON "works" ("library");
                    CREATE INDEX IF NOT EXISTS "idx_works_state" ON "works" ("state");
                    CREATE INDEX IF NOT EXISTS "idx_works_maker_name" ON "works" ("maker_name");
                    CREATE INDEX IF NOT EXISTS "idx_works_work_type" ON "works" ("work_type");
                    CREATE INDEX IF NOT EXISTS "idx_works_down_time" ON "works" ("down_time");
                    CREATE INDEX IF NOT EXISTS "idx_work_genres_genre" ON "work_genres" ("genre");
                    CREATE INDEX IF NOT EXISTS "idx_download_list_work_id" ON "download_list" ("work_id");
                    CREATE INDEX IF NOT EXISTS "idx_download_list_status" ON "download_list" ("status");
                    CREATE INDEX IF NOT EXISTS "idx_image_host_work" ON "image_host" ("work_id");
                    """;
                cmd.ExecuteNonQuery();
            }

            MigrateFanboxTables(conn);
            _initialized = true;
        }
    }

    /// <summary>
    /// 一次性迁移：早期版本把 fanbox 作品存在独立的 fanbox_artists / fanbox_posts 两张表里，
    /// 现已改为和 DLsite 作品一样存进 works（用 works.source = 'fanbox' 区分）。
    /// 这里把老数据搬进 works（作品号 fb_xxx 统一改成 FBxxx）后删掉旧表；没有旧表时什么都不做。
    /// </summary>
    private static void MigrateFanboxTables(SqliteConnection conn)
    {
        using (var check = conn.CreateCommand())
        {
            check.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'fanbox_posts'";
            if (Convert.ToInt64(check.ExecuteScalar() ?? 0L) == 0)
                return;
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT OR REPLACE INTO "works"
                    ("work_id", "work_name", "maker_id", "maker_name", "work_type", "intro_s",
                     "genre", "sell_date", "state", "library", "folder", "target", "target_lib",
                     "cover", "down_time", "read_flag", "favorite", "source", "meta_scanned")
                SELECT 'FB' || "post_id", "title", "artist_id", "artist_name", 'FANBOX', "content",
                       "tags",
                       CASE WHEN length("published") >= 10
                            THEN substr("published", 1, 4) || '年' || substr("published", 6, 2) || '月'
                                 || substr("published", 9, 2) || '日'
                            ELSE "published" END,
                       "state", "library", "folder", "target", "target_lib",
                       "cover", "down_time", "read_flag", "favorite", 'fanbox', '1'
                FROM "fanbox_posts";
                -- 下载队列与标签索引里的旧作品号一并改写
                UPDATE "download_list" SET "work_id" = 'FB' || substr("work_id", 4)
                    WHERE "work_id" LIKE 'fb\_%' ESCAPE '\';
                UPDATE OR REPLACE "work_genres" SET "work_id" = 'FB' || substr("work_id", 4)
                    WHERE "work_id" LIKE 'fb\_%' ESCAPE '\';
                DROP VIEW IF EXISTS "works_all";
                DROP TABLE IF EXISTS "fanbox_posts";
                DROP TABLE IF EXISTS "fanbox_artists";
                """;
            cmd.ExecuteNonQuery();
        }
        Console.WriteLine("[DASD] 已把 fanbox 独立表的数据迁入 works 表并删除旧表");
    }

    /// <summary>查询：返回行列表，每行为 object?[]（列序与 SQL 一致），失败时返回 null 并记日志。</summary>
    public static List<object?[]>? Select(string sql, params (string Name, object? Value)[] args)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            using var reader = cmd.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++)
                    row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }
            return rows;
        }
        catch (SqliteException e)
        {
            Logger.Error($"数据库查询失败: {e.Message}");
            Logger.Error(sql);
            return null;
        }
    }

    /// <summary>写操作（INSERT/UPDATE/DELETE），返回是否成功。</summary>
    public static bool Execute(string sql, params (string Name, object? Value)[] args)
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            foreach (var (name, value) in args)
                cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
            cmd.ExecuteNonQuery();
            return true;
        }
        catch (SqliteException e)
        {
            Logger.Error($"数据库操作失败: {e.Message}");
            Logger.Error(sql);
            return false;
        }
    }

    /// <summary>查询单值（第一行第一列），无结果返回 null。</summary>
    public static object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        var rows = Select(sql, args);
        return rows is { Count: > 0 } ? rows[0][0] : null;
    }

    /// <summary>
    /// 手动执行 WAL checkpoint，把 -wal 中已提交但尚未写回主库文件的数据落盘到 R-18MediaLibrary.db 并截断 -wal。
    /// WAL 模式下应用内读写始终能看到最新数据（无需此调用才能读到），但外部工具/备份脚本若只复制主库文件
    /// 而不带上 -wal/-shm，会漏掉尚未 checkpoint 的数据——退出前调用一次收尾，尽量让主库文件保持最新。
    /// </summary>
    public static void Checkpoint()
    {
        try
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException e)
        {
            Logger.Error($"WAL checkpoint 失败: {e.Message}");
        }
    }
}
