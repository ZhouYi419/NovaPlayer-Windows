using System.Globalization;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Core.Library;
using NovaPlayer.Core.Playback;
using Serilog;

namespace NovaPlayer.Infrastructure.Database;

/// <summary>所有 SQLite 操作由串行后台队列执行，不在界面线程调用同步数据库接口。</summary>
public sealed class SqlitePlaybackStore : IPlaybackStore
{
    private const int SchemaVersion = 2;
    private readonly Channel<Action> _queue = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private bool _initialized;
    public string DatabasePath { get; }
    public string? StartupWarning { get; private set; }
    public SqlitePlaybackStore(string path)
    {
        DatabasePath = Path.GetFullPath(path);
        _worker = Task.Run(async () =>
        {
            await foreach (var work in _queue.Reader.ReadAllAsync().ConfigureAwait(false)) work();
        });
    }
    private Task<T> Enqueue<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(() =>
        {
            try { completion.TrySetResult(action()); }
            catch (Exception ex) { Log.Error(ex, "本地播放数据库操作失败"); completion.TrySetException(ex); }
        })) completion.TrySetException(new ObjectDisposedException(nameof(SqlitePlaybackStore)));
        return completion.Task;
    }
    private Task<T> Execute<T>(Func<SqliteConnection, T> action) => Enqueue(() =>
    {
        if (!_initialized) throw new InvalidOperationException("播放数据库尚不可用。");
        using var db = Open();
        return action(db);
    });
    private SqliteConnection Open(bool readOnly = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 2,
            ForeignKeys = true
        }.ToString());
        try { db.Open(); return db; } catch { db.Dispose(); throw; }
    }
    public Task InitializeAsync() => Enqueue(() =>
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        try { InitializeDatabase(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 11 or 26)
        {
            var salvage = new List<VideoRecord>();
            try { using var damaged = Open(true); ReadVideos(damaged, salvage); }
            catch (Exception recoveryError) { Log.Warning(recoveryError, "损坏数据库只能恢复部分或无法恢复记录"); }
            string backup = DatabasePath + $".corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            // 原文件与日志文件一并保留；归档失败时停止恢复，绝不覆盖仍在使用的数据。
            foreach (string suffix in new[] { "", "-wal", "-shm" })
                if (File.Exists(DatabasePath + suffix)) File.Move(DatabasePath + suffix, backup + suffix);
            InitializeDatabase();
            using (var db = Open())
            using (var tx = db.BeginTransaction())
            {
                foreach (var video in salvage) Upsert(db, tx, video);
                tx.Commit();
            }
            StartupWarning = $"数据库损坏，原文件已保留至 {backup}；已恢复 {salvage.Count} 条可读取记录。";
            Log.Warning("{Recovery}", StartupWarning);
        }
        _initialized = true;
        return true;
    });
    private void InitializeDatabase()
    {
        using var db = Open();
        using (var check = db.CreateCommand())
        {
            check.CommandText = "PRAGMA quick_check";
            string? result = check.ExecuteScalar() as string;
            if (result != "ok") throw new SqliteException("数据库完整性检查未通过。", 11);
        }
        using var versionCommand = db.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version";
        int version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException("数据库版本高于当前应用版本，请使用较新的 Nova Player。");
        using (var pragmas = db.CreateCommand())
        { pragmas.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;"; pragmas.ExecuteNonQuery(); }
        using var tx = db.BeginTransaction();
        if (version < 1)
        {
            ExecuteSql(db, tx, """
                CREATE TABLE videos (
                    id INTEGER PRIMARY KEY, file_key TEXT NOT NULL UNIQUE, file_path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    file_name TEXT NOT NULL, file_size INTEGER NOT NULL, file_modified_at INTEGER NOT NULL,
                    duration REAL NOT NULL DEFAULT 0, last_position REAL NOT NULL DEFAULT 0,
                    playback_speed REAL NOT NULL DEFAULT 1, last_played_at TEXT NULL,
                    is_completed INTEGER NOT NULL DEFAULT 0, thumbnail_path TEXT NULL, created_at TEXT NOT NULL);
                CREATE TABLE playlist_items (
                    id INTEGER PRIMARY KEY, video_id INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
                    sort_order INTEGER NOT NULL UNIQUE, is_current INTEGER NOT NULL DEFAULT 0,
                    UNIQUE(video_id));
                CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE INDEX ix_videos_last_played ON videos(last_played_at DESC);
                PRAGMA user_version=1;
                """);
        }
        if (version < 2)
            ExecuteSql(db, tx, """
                ALTER TABLE videos ADD COLUMN has_speed INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE videos ADD COLUMN history_visible INTEGER NOT NULL DEFAULT 1;
                UPDATE videos SET has_speed=1 WHERE last_played_at IS NOT NULL;
                PRAGMA user_version=2;
                """);
        tx.Commit();
    }
    public Task<LibrarySnapshot> ReadAsync() => Execute(db =>
    {
        var videos = new List<VideoRecord>(); ReadVideos(db, videos);
        var playlist = new List<string>(); string? current = null;
        using var query = db.CreateCommand();
        query.CommandText = "SELECT v.file_path,p.is_current FROM playlist_items p JOIN videos v ON v.id=p.video_id ORDER BY p.sort_order";
        using var reader = query.ExecuteReader();
        while (reader.Read()) { string path = reader.GetString(0); playlist.Add(path); if (reader.GetBoolean(1)) current = path; }
        return new LibrarySnapshot(videos, playlist, current);
    });
    private static void ReadVideos(SqliteConnection db, List<VideoRecord> videos)
    {
        using var query = db.CreateCommand();
        query.CommandText = "SELECT file_path,file_size,file_modified_at,duration,last_position,playback_speed,last_played_at,is_completed,thumbnail_path,created_at,has_speed,history_visible FROM videos";
        using var r = query.ExecuteReader();
        while (r.Read()) videos.Add(new VideoRecord
        {
            FilePath = r.GetString(0),
            FileSize = r.GetInt64(1),
            ModifiedUtcTicks = r.GetInt64(2),
            Duration = r.GetDouble(3),
            Position = r.GetDouble(4),
            Speed = r.GetDouble(5),
            LastPlayedUtc = r.IsDBNull(6) ? null : DateTime.Parse(r.GetString(6), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            Completed = r.GetBoolean(7),
            ThumbnailPath = r.IsDBNull(8) ? null : r.GetString(8),
            CreatedUtc = DateTime.Parse(r.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            HasSpeed = r.GetBoolean(10),
            HistoryVisible = r.GetBoolean(11)
        });
    }
    public Task<IReadOnlyList<VideoRecord>> RegisterAsync(IReadOnlyList<VideoRecord> videos) => Execute<IReadOnlyList<VideoRecord>>(db =>
    {
        var existing = new List<VideoRecord>(); ReadVideos(db, existing);
        var byKey = existing.ToDictionary(v => VideoRecord.Key(v.FilePath));
        var result = new List<VideoRecord>();
        using var tx = db.BeginTransaction();
        foreach (var candidate in videos)
        {
            var record = byKey.TryGetValue(VideoRecord.Key(candidate.FilePath), out var old) && old.MatchesFile(candidate.FileSize, candidate.ModifiedUtcTicks) ? old : candidate;
            Upsert(db, tx, record); result.Add(record);
        }
        tx.Commit(); return result;
    });
    public Task SaveVideoAsync(VideoRecord video) => Execute(db => { Upsert(db, null, video); return true; });
    private static void Upsert(SqliteConnection db, SqliteTransaction? tx, VideoRecord video)
    {
        PlaybackPolicy.ValidateSpeed(video.Speed);
        if (!double.IsFinite(video.Position) || !double.IsFinite(video.Duration) || video.Position < 0 || video.Duration < 0) throw new ArgumentException("视频进度数据无效。");
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.CommandText = """
            INSERT INTO videos(file_key,file_path,file_name,file_size,file_modified_at,duration,last_position,playback_speed,last_played_at,is_completed,thumbnail_path,created_at,has_speed,history_visible)
            VALUES($key,$path,$name,$size,$modified,$duration,$position,$speed,$played,$completed,$thumbnail,$created,$hasSpeed,$visible)
            ON CONFLICT(file_key) DO UPDATE SET file_path=excluded.file_path,file_name=excluded.file_name,file_size=excluded.file_size,
            file_modified_at=excluded.file_modified_at,duration=excluded.duration,last_position=excluded.last_position,
            playback_speed=excluded.playback_speed,last_played_at=excluded.last_played_at,is_completed=excluded.is_completed,
            thumbnail_path=CASE WHEN videos.file_size=excluded.file_size AND videos.file_modified_at=excluded.file_modified_at
                THEN COALESCE(excluded.thumbnail_path,videos.thumbnail_path) ELSE excluded.thumbnail_path END,
            has_speed=excluded.has_speed,history_visible=excluded.history_visible;
            """;
        Add(command, "$key", VideoRecord.Key(video.FilePath)); Add(command, "$path", Path.GetFullPath(video.FilePath));
        Add(command, "$name", video.FileName); Add(command, "$size", video.FileSize); Add(command, "$modified", video.ModifiedUtcTicks);
        Add(command, "$duration", video.Duration); Add(command, "$position", video.Position); Add(command, "$speed", video.Speed);
        Add(command, "$played", video.LastPlayedUtc?.ToString("O", CultureInfo.InvariantCulture)); Add(command, "$completed", video.Completed);
        Add(command, "$thumbnail", video.ThumbnailPath); Add(command, "$created", video.CreatedUtc.ToString("O", CultureInfo.InvariantCulture));
        Add(command, "$hasSpeed", video.HasSpeed); Add(command, "$visible", video.HistoryVisible);
        command.ExecuteNonQuery();
    }
    public Task SavePlaylistAsync(IReadOnlyList<string> paths, string? currentPath) => Execute(db =>
    {
        using var tx = db.BeginTransaction(); ExecuteSql(db, tx, "DELETE FROM playlist_items");
        int index = 0;
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO playlist_items(video_id,sort_order,is_current) SELECT id,$order,$current FROM videos WHERE file_key=$key";
            Add(command, "$order", index++); Add(command, "$current", string.Equals(path, currentPath, StringComparison.OrdinalIgnoreCase)); Add(command, "$key", VideoRecord.Key(path)); command.ExecuteNonQuery();
        }
        tx.Commit(); return true;
    });
    public Task SetThumbnailAsync(VideoRecord video, ThumbnailResult thumbnail) => Execute(db =>
    {
        using var command = db.CreateCommand();
        command.CommandText = "UPDATE videos SET thumbnail_path=$thumbnail,duration=CASE WHEN duration=0 THEN $duration ELSE duration END WHERE file_key=$key AND file_size=$size AND file_modified_at=$modified";
        Add(command, "$thumbnail", thumbnail.Path); Add(command, "$duration", thumbnail.Duration); Add(command, "$key", VideoRecord.Key(video.FilePath));
        Add(command, "$size", video.FileSize); Add(command, "$modified", video.ModifiedUtcTicks); command.ExecuteNonQuery(); return true;
    });
    public Task SetHistoryVisibilityAsync(string? filePath, bool visible) => Execute(db =>
    {
        using var command = db.CreateCommand(); command.CommandText = "UPDATE videos SET history_visible=$visible WHERE ($key IS NULL OR file_key=$key)";
        Add(command, "$visible", visible); Add(command, "$key", filePath is null ? null : VideoRecord.Key(filePath)); command.ExecuteNonQuery(); return true;
    });
    public Task<string?> ReadSettingAsync(string key) => Execute(db =>
    { using var command = db.CreateCommand(); command.CommandText = "SELECT value FROM settings WHERE key=$key"; Add(command, "$key", key); return command.ExecuteScalar() as string; });
    public Task SaveSettingAsync(string key, string value) => Execute(db =>
    { using var command = db.CreateCommand(); command.CommandText = "INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value"; Add(command, "$key", key); Add(command, "$value", value); command.ExecuteNonQuery(); return true; });
    private static void Add(SqliteCommand command, string key, object? value) => command.Parameters.AddWithValue(key, value ?? DBNull.Value);
    private static void ExecuteSql(SqliteConnection db, SqliteTransaction tx, string text)
    { using var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = text; command.ExecuteNonQuery(); }
    public Task FlushAsync() => Enqueue(() => true);
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _worker.ConfigureAwait(false); }
}
