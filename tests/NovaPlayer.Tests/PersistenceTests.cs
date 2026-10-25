using Microsoft.Data.Sqlite;
using NovaPlayer.Core.Library;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.Database;
using NovaPlayer.Infrastructure.Thumbnails;
using Xunit;

namespace NovaPlayer.Tests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "NovaPlayer-tests-" + Guid.NewGuid().ToString("N"));
    private string Database => Path.Combine(_directory, "player.db");
    public Task InitializeAsync() { Directory.CreateDirectory(_directory); return Task.CompletedTask; }
    public Task DisposeAsync() { Directory.Delete(_directory, true); return Task.CompletedTask; }
    private VideoRecord Sample(string name = "中文 视频 'A'.mp4") => new() { FilePath = Path.Combine(_directory, name), FileSize = 400, ModifiedUtcTicks = 123456, Duration = 120, Position = 25, Speed = 1.75, HasSpeed = true, LastPlayedUtc = DateTime.UtcNow, HistoryVisible = true };

    [Fact]
    public async Task ReopenRestoresProgressSpeedAndOrderedQueue()
    {
        var first = Sample(); var second = Sample("second.mp4");
        await using (var store = new SqlitePlaybackStore(Database))
        {
            await store.InitializeAsync(); await store.RegisterAsync([first, second]);
            await store.SavePlaylistAsync([second.FilePath, first.FilePath], first.FilePath);
        }
        await using var reopened = new SqlitePlaybackStore(Database); await reopened.InitializeAsync();
        var snapshot = await reopened.ReadAsync();
        Assert.Equal(new[] { second.FilePath, first.FilePath }, snapshot.Playlist);
        var restored = snapshot.Videos.Single(v => v.FilePath == first.FilePath);
        Assert.Equal(25, restored.Position); Assert.Equal(1.75, restored.Speed); Assert.True(restored.HasSpeed);
        Assert.Equal(first.FilePath, snapshot.CurrentPath);
    }
    [Fact]
    public async Task RepeatedUpsertsAndCaseVariantsDoNotDuplicateHistory()
    {
        await using var store = new SqlitePlaybackStore(Database); await store.InitializeAsync();
        var record = Sample(); await store.RegisterAsync([record]);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => store.SaveVideoAsync(record with { Position = i, FilePath = i % 2 == 0 ? record.FilePath : record.FilePath.ToUpperInvariant() })));
        var snapshot = await store.ReadAsync(); Assert.Single(snapshot.Videos); Assert.Equal(19, snapshot.Videos[0].Position);
    }
    [Fact]
    public async Task FingerprintChangeInvalidatesOldResumeSpeedAndThumbnail()
    {
        await using var store = new SqlitePlaybackStore(Database); await store.InitializeAsync();
        var old = Sample() with { ThumbnailPath = "old.jpg", Completed = true }; await store.RegisterAsync([old]);
        var current = new VideoRecord { FilePath = old.FilePath, FileSize = 999, ModifiedUtcTicks = 7654321 };
        var updated = Assert.Single(await store.RegisterAsync([current]));
        Assert.Equal(0, updated.Position); Assert.False(updated.HasSpeed); Assert.Null(updated.ThumbnailPath); Assert.False(updated.Completed);
    }
    [Fact]
    public async Task ClearingHistoryPreservesQueueAndOriginalFile()
    {
        var record = Sample(); await File.WriteAllTextAsync(record.FilePath, "测试原文件");
        await using var store = new SqlitePlaybackStore(Database); await store.InitializeAsync();
        await store.RegisterAsync([record]); await store.SavePlaylistAsync([record.FilePath], record.FilePath);
        await store.SetHistoryVisibilityAsync(null, false);
        var snapshot = await store.ReadAsync(); Assert.Single(snapshot.Playlist); Assert.False(snapshot.Videos[0].HistoryVisible);
        Assert.Equal("测试原文件", await File.ReadAllTextAsync(record.FilePath));
    }
    [Fact]
    public async Task CorruptDatabaseIsArchivedBeforeNewDatabaseIsCreated()
    {
        byte[] damaged = [9, 4, 6, 7, 2, 9, 8, 3]; await File.WriteAllBytesAsync(Database, damaged);
        await using var store = new SqlitePlaybackStore(Database); await store.InitializeAsync();
        Assert.NotNull(store.StartupWarning);
        string backup = Assert.Single(Directory.GetFiles(_directory, "player.db.corrupt-*"));
        Assert.Equal(damaged, await File.ReadAllBytesAsync(backup)); Assert.Empty((await store.ReadAsync()).Videos);
    }
    [Fact]
    public async Task VersionOneMigratesWithoutLosingPlaybackData()
    {
        var record = Sample();
        await using (var initial = new SqlitePlaybackStore(Database)) { await initial.InitializeAsync(); await initial.RegisterAsync([record]); }
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE videos DROP COLUMN history_visible; ALTER TABLE videos DROP COLUMN has_speed; PRAGMA user_version=1"; command.ExecuteNonQuery();
        }
        await using var migrated = new SqlitePlaybackStore(Database); await migrated.InitializeAsync();
        var restored = Assert.Single((await migrated.ReadAsync()).Videos); Assert.Equal(25, restored.Position); Assert.True(restored.HasSpeed);
    }
    [Fact]
    public async Task FutureSchemaIsNeverOverwritten()
    {
        using (var db = new SqliteConnection($"Data Source={Database};Pooling=False"))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version=999; CREATE TABLE preserve_me(value TEXT)"; command.ExecuteNonQuery(); }
        await using var store = new SqlitePlaybackStore(Database); await Assert.ThrowsAsync<InvalidOperationException>(store.InitializeAsync);
        using var check = new SqliteConnection($"Data Source={Database};Pooling=False"); check.Open(); using var query = check.CreateCommand();
        query.CommandText = "SELECT name FROM sqlite_master WHERE name='preserve_me'"; Assert.Equal("preserve_me", query.ExecuteScalar());
    }
    [Fact]
    public void CacheFingerprintChangesWithFileVersion()
    {
        var record = Sample(); Assert.NotEqual(ThumbnailService.CacheKey(record), ThumbnailService.CacheKey(record with { ModifiedUtcTicks = 999 }));
        Assert.Equal(ThumbnailService.CacheKey(record), ThumbnailService.CacheKey(record with { FilePath = record.FilePath.ToUpperInvariant() }));
    }
    [Fact]
    public async Task LockedWriteFailsWithoutPoisoningWorkerAndCanRecover()
    {
        await using var store = new SqlitePlaybackStore(Database); await store.InitializeAsync();
        var record = Sample(); await store.RegisterAsync([record]);
        using (var connection = new SqliteConnection($"Data Source={Database};Pooling=False"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction();
            var saving = store.SaveVideoAsync(record with { Position = 40 });
            Assert.False(saving.IsCompleted);
            await Assert.ThrowsAsync<SqliteException>(() => saving);
        }
        Assert.Equal(25, (await store.ReadAsync()).Videos[0].Position);
        await store.SaveVideoAsync(record with { Position = 40 });
        Assert.Equal(40, (await store.ReadAsync()).Videos[0].Position);
    }
    [Theory]
    [InlineData(PlaybackMode.Sequential, 1, 3, new[] { 2 })]
    [InlineData(PlaybackMode.SingleRepeat, 1, 3, new[] { 1 })]
    [InlineData(PlaybackMode.ListRepeat, 1, 3, new[] { 2, 0, 1 })]
    [InlineData(PlaybackMode.StopAfterCurrent, 1, 3, new int[0])]
    public void PlaybackModesChooseCorrectFiniteCandidates(PlaybackMode mode, int current, int count, int[] expected) =>
        Assert.Equal(expected, PlaylistPolicy.NextCandidates(current, count, mode));
}
