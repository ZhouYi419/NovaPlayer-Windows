using System.ComponentModel;
using System.IO;
using System.Windows.Media.Imaging;
using NovaPlayer.Core.Library;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.FileSystem;
using Serilog;

namespace NovaPlayer.App.ViewModels;

public partial class PlayerViewModel
{
    private bool _restoringLibrary;
    public string DatabasePath => _store.DatabasePath;
    internal long GeneratedThumbnails => _thumbnails.GeneratedCount;
    internal long ThumbnailCacheHits => _thumbnails.CacheHitCount;
    internal Task<LibrarySnapshot> ReadStoredAsync() => _store.ReadAsync();
    partial void OnPersistenceWarningChanged(string? value) => OnPropertyChanged(nameof(Status));
    partial void OnResumePromptChanged(string? value) => OnPropertyChanged(nameof(HasResumePrompt));

    public async Task InitializeLibraryAsync()
    {
        _restoringLibrary = true;
        try
        {
            var snapshot = await _store.ReadAsync();
            foreach (var record in snapshot.Videos)
            {
                var item = Materialize(record);
                if (item.HistoryVisible && item.LastPlayed is not null) History.Add(item);
            }
            foreach (string path in snapshot.Playlist)
                if (_library.TryGetValue(path, out var item)) Playlist.Add(item);
            if (snapshot.CurrentPath is not null && _library.TryGetValue(snapshot.CurrentPath, out var current)) CurrentVideo = current;
            var mode = await _store.ReadSettingAsync("playback.mode");
            if (Enum.TryParse<PlaybackMode>(mode, out var parsed) && Enum.IsDefined(parsed)) Preferences.Mode = parsed;
            OnPropertyChanged(nameof(PlaybackModeIndex));
            if (_store.StartupWarning is { } warning) PersistenceWarning = warning;
        }
        catch (Exception ex) { PersistenceFailed(ex); }
        finally { _restoringLibrary = false; NotifyCollections(); RefreshHistory(); }
    }

    private VideoItem Materialize(VideoRecord record)
    {
        if (!_library.TryGetValue(record.FilePath, out var item))
        {
            item = new VideoItem(record.FilePath, record.FileSize) { ThumbnailLoader = RequestThumbnailAsync };
            item.Apply(record); _library.Add(record.FilePath, item);
        }
        else if (!item.ToRecord().MatchesFile(record.FileSize, record.ModifiedUtcTicks))
        {
            item.Apply(record); History.Remove(item);
        }
        return item;
    }
    private async Task<IReadOnlyList<VideoRecord>> RegisterImportedAsync(IReadOnlyList<ImportedVideo> videos)
    {
        var defaults = videos.Select(v => new VideoRecord { FilePath = v.Path, FileSize = v.Size, ModifiedUtcTicks = v.ModifiedAt.Ticks }).ToArray();
        try { return await _store.RegisterAsync(defaults); }
        catch (Exception ex) { PersistenceFailed(ex); return defaults; }
    }
    private async Task PersistAsync(Func<Task> operation)
    {
        try { await operation(); if (PersistenceWarning?.StartsWith("播放记录暂时无法保存", StringComparison.Ordinal) == true) PersistenceWarning = _store.StartupWarning; }
        catch (Exception ex) { PersistenceFailed(ex); }
    }
    private void PersistenceFailed(Exception ex)
    {
        Log.Error(ex, "播放记录持久化失败，播放会话继续运行");
        if (!_disposed) PersistenceWarning = "播放记录暂时无法保存，请检查磁盘或数据库权限；当前播放不受影响。";
    }
    private Task PersistPlaylistAsync()
    {
        if (_restoringLibrary || _disposed) return Task.CompletedTask;
        var paths = Playlist.Select(v => v.Path).ToArray();
        var current = CurrentVideo?.Path;
        return PersistAsync(() => _store.SavePlaylistAsync(paths, current));
    }
    private void PreferenceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.PreferencesService.Mode))
        {
            OnPropertyChanged(nameof(PlaybackModeIndex));
            if (!_restoringLibrary) _ = PersistAsync(() => _store.SaveSettingAsync("playback.mode", Preferences.Mode.ToString()));
        }
    }
    private async void SaveTimerTick(object? sender, EventArgs e)
    {
        if (_disposed || _shuttingDown) return;
        await SaveCurrentAsync();
    }
    public Task SaveCurrentAsync()
    {
        var state = _engine.State;
        if (_pendingPosition is not null || !state.IsLoaded || state.FilePath is null || !_library.TryGetValue(state.FilePath, out var item)) return Task.CompletedTask;
        item.Duration = state.Duration;
        item.Position = state.IsEnded && state.Duration > 0 ? state.Duration : state.Position;
        item.Speed = state.Speed; item.HasSpeed = true;
        item.Completed = state.IsEnded || state.Duration > 0 && item.Position / state.Duration >= 0.95;
        if (item.HistoryVisible) item.LastPlayed = DateTime.UtcNow;
        // 不足五秒只保留观看记录与倍速，不把短暂打开的位置作为有效续播点。
        var record = item.ToRecord() with { Position = item.Position >= 5 ? item.Position : 0 };
        return PersistAsync(() => _store.SaveVideoAsync(record));
    }
    private void ObservePlayback(PlayerState value)
    {
        var previous = _lastSnapshot; _lastSnapshot = value;
        if (_shuttingDown || value.FilePath is null || !_library.TryGetValue(value.FilePath, out var item)) return;
        if (CurrentVideo != item) CurrentVideo = item;
        if (value.IsLoaded)
        {
            item.Duration = value.Duration;
            if (_pendingPosition is { } target && value.Duration > 0)
            {
                if (!_resumeIssued)
                { _resumeIssued = true; _engine.Seek(Math.Min(target, value.Duration)); return; }
                if (value.Position < target - 1 || value.IsSeeking) return;
                _pendingPosition = null; _resumeIssued = false; ShowResumePrompt(target);
            }
            item.Position = value.Position; item.Speed = value.Speed; item.HasSpeed = true;
            item.Completed = value.IsEnded || value.Duration > 0 && value.Position / value.Duration >= 0.95;
            if (item.HistoryVisible && !History.Contains(item)) { History.Insert(0, item); RefreshHistory(); }
            if (previous.SessionId != value.SessionId || !previous.IsLoaded || previous.IsPaused != value.IsPaused ||
                previous.IsSeeking && !value.IsSeeking || previous.Speed != value.Speed)
                _ = SaveCurrentAsync();
        }
        if (value.IsEnded && _handledEndSession != value.SessionId)
        {
            _handledEndSession = value.SessionId; _failedAutomaticPaths.Clear();
            _ = AdvanceAfterEndAsync(value.SessionId, item, false);
        }
        else if (value.Error is not null && value.SessionId == _automaticSession && _handledEndSession != value.SessionId)
        {
            _handledEndSession = value.SessionId;
            _ = AdvanceAfterEndAsync(value.SessionId, item, true);
        }
    }
    private async void ShowResumePrompt(double position)
    {
        string text = $"已从 {FormatTime(position)} 继续播放";
        ResumePrompt = text; Notice = text;
        try { await Task.Delay(TimeSpan.FromSeconds(6), _lifetime.Token); if (ResumePrompt == text) ResumePrompt = null; }
        catch (OperationCanceledException) { }
    }
    public async Task PlayItemAsync(VideoItem? item, bool fromStart = false, bool automatic = false)
    {
        if (item is null || !IsReady || _shuttingDown) return;
        long request = ++_playRequest;
        try
        {
            await _transitionGate.WaitAsync(_lifetime.Token);
            try
            {
                if (request != _playRequest || _shuttingDown) return;
                await SaveCurrentAsync();
                var file = await Task.Run(() =>
                {
                    var info = new FileInfo(item.Path);
                    return info.Exists ? new ImportedVideo(info.FullName, info.Length, info.LastWriteTimeUtc) : null;
                }, _lifetime.Token);
                if (request != _playRequest || _shuttingDown) return;
                if (file is null)
                {
                    item.IsMissing = true; Notice = "视频文件不存在，请重新定位。";
                    if (!automatic) LocateRequested?.Invoke(this, item);
                    return;
                }
                item.IsMissing = false;
                if (!item.ToRecord().MatchesFile(file.Size, file.ModifiedAt.Ticks))
                {
                    var refreshed = await RegisterImportedAsync([file]); item.Apply(refreshed[0]);
                    History.Remove(item); Notice = "视频文件已变化，已重置旧进度与封面。";
                }
                if (request != _playRequest || _shuttingDown) return;
                bool same = string.Equals(_engine.State.FilePath, item.Path, StringComparison.OrdinalIgnoreCase) && _engine.State.IsLoaded;
                if (same && !fromStart && !_engine.State.IsEnded)
                {
                    item.HistoryVisible = true; item.LastPlayed = DateTime.UtcNow;
                    if (!History.Contains(item)) History.Add(item);
                    CurrentPage = "首页"; SearchText = ""; _engine.SetPaused(false); RefreshHistory(); await SaveCurrentAsync(); return;
                }
                double resume = fromStart ? 0 : ResumePolicy.Position(item.Position, item.Duration, Preferences.AutoResume && !item.Completed);
                if (resume > 0 && Preferences.ResumeMode == "询问后续播" && Confirm?.Invoke($"是否从 {FormatTime(resume)} 继续播放？") != true) resume = 0;
                _pendingPosition = resume > 0 ? resume : null; _resumeIssued = false; ResumePrompt = null;
                item.HistoryVisible = true; item.LastPlayed = DateTime.UtcNow;
                CurrentVideo = item; CurrentPage = "首页"; SearchText = "";
                if (!automatic) _failedAutomaticPaths.Clear();
                _engine.SetSpeed(Preferences.RememberSpeed && item.HasSpeed ? item.Speed : Preferences.DefaultSpeed);
                _engine.Open(item.Path);
                _automaticSession = automatic ? _engine.State.SessionId : 0;
                RefreshHistory(); await PersistPlaylistAsync();
                Notice = resume > 0 ? $"正在恢复至 {FormatTime(resume)}…" : "视频已打开";
            }
            finally { _transitionGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "打开视频失败"); if (!_disposed) Notice = ex.Message; }
    }
    private async Task AdvanceAfterEndAsync(long session, VideoItem ended, bool failed)
    {
        long request = _playRequest;
        await SaveCurrentAsync();
        if (_shuttingDown || _engine.State.SessionId != session || _playRequest != request) return;
        if (failed) _failedAutomaticPaths.Add(ended.Path);
        foreach (int index in PlaylistPolicy.NextCandidates(Playlist.IndexOf(ended), Playlist.Count, Preferences.Mode))
        {
            var next = Playlist[index];
            if (_failedAutomaticPaths.Contains(next.Path)) continue;
            bool exists = await Task.Run(() => File.Exists(next.Path));
            // 等待磁盘期间若用户已手动选片，旧会话的自动连播必须让出控制权。
            if (_shuttingDown || _engine.State.SessionId != session || _playRequest != request) return;
            next.IsMissing = !exists;
            if (!exists) { _failedAutomaticPaths.Add(next.Path); continue; }
            _failedAutomaticPaths.Add(next.Path);
            await PlayItemAsync(next, Preferences.Mode == PlaybackMode.SingleRepeat || next.Completed, true);
            return;
        }
        if (failed) Notice = "队列中没有其他可播放的视频。";
    }
    private async Task RelocateItemAsync(VideoItem old, string replacement)
    {
        var imported = await MediaImportService.ImportAsync([replacement], _lifetime.Token);
        if (imported.Videos.Count == 0) { Notice = "未找到支持的视频文件。"; return; }
        var records = await RegisterImportedAsync(imported.Videos);
        var next = Materialize(records[0]);
        if (old.ToRecord().MatchesFile(next.Size, next.ModifiedUtcTicks))
        {
            next.Position = old.Position; next.Duration = old.Duration; next.Speed = old.Speed;
            next.HasSpeed = old.HasSpeed; next.Completed = old.Completed;
        }
        int index = Playlist.IndexOf(old);
        if (index >= 0) { Playlist.Remove(old); if (!Playlist.Contains(next)) Playlist.Insert(index, next); }
        else if (!Playlist.Contains(next)) Playlist.Add(next);
        History.Remove(old); old.HistoryVisible = false;
        await PersistAsync(() => _store.SetHistoryVisibilityAsync(old.Path, false));
        NotifyCollections(); await PlayItemAsync(next);
    }
    private async Task RequestThumbnailAsync(VideoItem item, CancellationToken token)
    {
        var record = item.ToRecord();
        try
        {
            item.IsMissing = !await Task.Run(() => File.Exists(item.Path), token);
            if (item.IsMissing) return;
            var result = await _thumbnails.GetAsync(record, token);
            if (result is null || _shuttingDown || token.IsCancellationRequested || !item.ToRecord().MatchesFile(record.FileSize, record.ModifiedUtcTicks)) return;
            var image = await Task.Run(() =>
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(result.Path); bitmap.DecodePixelWidth = 320; bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            }, token);
            if (_shuttingDown || token.IsCancellationRequested) return;
            item.Thumbnail = image; item.ThumbnailPath = result.Path;
            if (item.Duration <= 0 && result.Duration > 0) item.Duration = result.Duration;
            await PersistAsync(() => _store.SetThumbnailAsync(record, result));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "加载视频封面失败"); }
    }
    public async Task ShutdownAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true; _saveTimer.Stop(); ++_playRequest; _lifetime.Cancel();
        await _importGate.WaitAsync();
        _importGate.Release();
        await _transitionGate.WaitAsync();
        try
        {
            await SaveCurrentAsync(); await PersistPlaylistAsync();
            await Preferences.SaveAsync();
            await _thumbnails.DisposeAsync();
            await PersistAsync(_store.FlushAsync);
        }
        finally { _transitionGate.Release(); }
        await _store.DisposeAsync(); Dispose();
    }
}
