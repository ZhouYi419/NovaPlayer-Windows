using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NovaPlayer.App.Controls;
using NovaPlayer.App.Services;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

/// <summary>以独立进程共享专用测试档案，验证真实内核与持久化服务的完整调用链。</summary>
internal static class StageThreeVerification
{
    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer,
        PlaybackOverlay overlay, string mode, string file, string reportPath)
    {
        var vm = window.ViewModel;
        var checks = new List<object>();
        string folder = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        string second = Path.Combine(Path.GetDirectoryName(file)!, "b 第二段.mp4");
        string missing = Path.Combine(Path.GetDirectoryName(file)!, "c 缺失视频.mp4");
        string? failure = null;
        bool passed = false;
        void Check(string name, bool ok)
        {
            checks.Add(new { name, passed = ok });
            if (!ok) throw new InvalidOperationException(name);
        }
        async Task Pause()
        {
            engine.SetPaused(true);
            await Until(() => engine.State.IsPaused && vm.State.IsPaused && vm.State.SessionId == engine.State.SessionId && vm.State.IsLoaded);
        }
        async Task Seek(double seconds)
        {
            await Until(() => vm.State.IsLoaded && vm.State.SessionId == engine.State.SessionId && vm.State.FilePath == engine.State.FilePath);
            vm.SeekTo(seconds);
            await Until(() => Math.Abs(engine.State.Position - seconds) < .4 && !engine.State.IsSeeking);
            await Task.Delay(150);
        }
        async Task Play(VideoItem item, bool head = false)
        {
            await vm.PlayItemAsync(item, head);
            await Until(() => engine.State.IsLoaded && engine.State.FilePath == item.Path && engine.State.Duration > 0 &&
                !engine.State.IsSeeking && !vm.Notice.StartsWith("正在恢复至", StringComparison.Ordinal));
        }
        async Task End()
        {
            engine.SetSpeed(15);
            await Seek(engine.State.Duration - .4);
            engine.SetPaused(false);
        }
        try
        {
            if (mode == "--verify-library-seed")
            {
                Check("首次启动数据库为空", vm.Playlist.Count == 0 && vm.History.Count == 0);
                await vm.ImportFilesAsync([file, second, missing]);
                await Until(() => engine.State.IsLoaded && engine.State.Duration > 0);
                var a = vm.Playlist[0]; var b = vm.Playlist[1];
                await Pause(); engine.SetSpeed(2.25); await Seek(20);
                await vm.SaveCurrentAsync();
                engine.SetPaused(false); await Until(() => !engine.State.IsPaused);
                await Task.Delay(6100);
                var timed = (await vm.ReadStoredAsync()).Videos.Single(v => v.FilePath == file);
                Check("五秒定时器后台保存真实播放进度", timed.Position > 22 && timed.Speed == 2.25);
                await Pause(); await Seek(32); await vm.SaveCurrentAsync();
                await Play(b); await Pause(); engine.SetSpeed(3.5); await Seek(12); await vm.SaveCurrentAsync();
                await Play(a);
                await Until(() => engine.State.Position >= 31.8 && engine.State.Speed == 2.25);
                await Pause(); await Seek(32); await vm.SaveCurrentAsync();
                Check("切换视频分别恢复位置与倍速", b.Position >= 11.8 && b.Speed == 3.5 && a.Speed == 2.25);
                vm.Reorder(b, a);
                Check("排序同步当前视频索引", vm.PlaylistIndex == "2 / 3");
                foreach (var item in vm.Playlist) await item.ThumbnailLoader!(item, CancellationToken.None);
                Check("真实提帧写入缓存并读取未播放视频时长", vm.Playlist.All(v => v.Thumbnail is not null && File.Exists(v.ThumbnailPath) && v.Duration > 89));
                var snapshot = await vm.ReadStoredAsync();
                Check("SQLite保存缩略图与观看记录", snapshot.Videos.All(v => v.ThumbnailPath is not null) && vm.History.Count == 2);
                vm.Preferences.Mode = PlaybackMode.Sequential;
                await ShellCapture.SaveAsync(window, overlay, renderer, Path.Combine(folder, "stage3-home.png"));
            }
            else if (mode == "--verify-library-resume")
            {
                var a = vm.Playlist.Single(v => v.Path == file); var b = vm.Playlist.Single(v => v.Path == second);
                Check("独立进程恢复队列顺序与选中项", vm.Playlist.Select(v => v.Path).SequenceEqual(new[] { second, file, missing }) && vm.CurrentVideo == a && vm.PlaylistIndex == "2 / 3");
                Check("独立进程恢复位置倍速及历史", Math.Abs(a.Position - 32) < .5 && a.Speed == 2.25 && Math.Abs(b.Position - 12) < .5 && b.Speed == 3.5 && vm.History.Count == 2);
                string? cached = a.ThumbnailPath;
                await Play(a); await Until(() => engine.State.Position >= 31.8 && !engine.State.IsSeeking && vm.HasResumePrompt && engine.State.Speed == 2.25); await Pause();
                Check("重启后真实内核自动续播并提示", vm.HasResumePrompt && engine.State.Position >= 31.8);
                await a.ThumbnailLoader!(a, CancellationToken.None);
                Check("重启后命中同一磁盘封面缓存", a.ThumbnailPath == cached && vm.ThumbnailCacheHits > 0);
                vm.Preferences.AutoResume = false;
                await Play(b); await Pause();
                Check("关闭续播从头播放且独立倍速保留", engine.State.Position < 3 && engine.State.Speed == 3.5);
                await Seek(10); await vm.SaveCurrentAsync();
                vm.Preferences.ResumeMode = "询问后续播"; vm.Confirm = _ => false;
                await Play(a); await Pause();
                Check("询问续播选择拒绝从头播放", engine.State.Position < 3);
                await Seek(20); await vm.SaveCurrentAsync();
                vm.Confirm = _ => true;
                await Play(b); await Until(() => engine.State.Position >= 9.8 && !engine.State.IsSeeking && vm.HasResumePrompt); await Pause();
                Check("询问续播选择同意恢复位置", engine.State.Position >= 9.8);
                vm.Preferences.RememberSpeed = false;
                await Play(a); await Until(() => engine.State.Position >= 19.8 && !engine.State.IsSeeking && vm.HasResumePrompt); await Pause();
                Check("关闭独立倍速记忆使用全局默认", engine.State.Speed == vm.Preferences.DefaultSpeed);
                vm.Preferences.RememberSpeed = true; vm.Preferences.ResumeMode = "自动续播";
                File.SetLastWriteTimeUtc(second, File.GetLastWriteTimeUtc(second).AddSeconds(2));
                string? oldThumbnail = b.ThumbnailPath;
                await Play(b); await Pause();
                Check("文件指纹变化重置旧位置及倍速", engine.State.Position < 3 && engine.State.Speed == 1);
                await b.ThumbnailLoader!(b, CancellationToken.None);
                Check("文件变化重新生成封面", b.ThumbnailPath is not null && b.ThumbnailPath != oldThumbnail && File.Exists(b.ThumbnailPath));
                vm.DeleteHistoryCommand.Execute(b);
                var snapshot = await vm.ReadStoredAsync();
                Check("删除历史持久化且保留队列和视频", !snapshot.Videos.Single(v => v.FilePath == second).HistoryVisible && vm.Playlist.Count == 3 && File.Exists(second));
                vm.NavigateCommand.Execute("播放历史"); await Task.Delay(150);
                await ShellCapture.SaveAsync(window, overlay, renderer, Path.Combine(folder, "stage3-history.png"));
            }
            else
            {
                var a = vm.Playlist.Single(v => v.Path == file); var b = vm.Playlist.Single(v => v.Path == second);
                Check("已删除历史跨进程保持隐藏", !vm.History.Contains(b) && vm.Playlist.Contains(b));
                File.Move(missing, missing + ".missing");
                vm.Preferences.Mode = PlaybackMode.Sequential;
                await Play(b, true); await Pause(); await End();
                await Until(() => engine.State.IsLoaded && engine.State.FilePath == file); await Pause();
                Check("顺序播放自动切换下一条并保存完成状态", (await vm.ReadStoredAsync()).Videos.Single(v => v.FilePath == second).Completed);
                await End(); await Until(() => engine.State.IsEnded); await Task.Delay(500);
                Check("顺序模式跳过失效文件并在末尾停止", engine.State.FilePath == file && engine.State.IsEnded && vm.Playlist.Single(v => v.Path == missing).IsMissing);
                vm.Preferences.Mode = PlaybackMode.ListRepeat;
                await Play(a, true); await Pause(); await End();
                await Until(() => engine.State.IsLoaded && engine.State.FilePath == second); await Pause();
                Check("列表循环跳过失效文件回到首条", vm.CurrentVideo == b && vm.PlaylistIndex == "1 / 3");
                vm.Preferences.Mode = PlaybackMode.SingleRepeat;
                long session = engine.State.SessionId;
                await End(); await Until(() => engine.State.IsLoaded && engine.State.SessionId != session); await Pause();
                Check("单曲循环创建新会话并从头播放", engine.State.FilePath == second && engine.State.Position < 5);
                vm.Preferences.Mode = PlaybackMode.StopAfterCurrent;
                session = engine.State.SessionId;
                await End(); await Until(() => engine.State.IsEnded); await Task.Delay(400);
                Check("关闭连播播完保持当前视频", engine.State.SessionId == session && engine.State.FilePath == second);
                string broken = Path.Combine(Path.GetDirectoryName(file)!, "d 损坏视频.mp4");
                await File.WriteAllTextAsync(broken, "此文件用于验证无法解码时跳过，不是真实视频。");
                await vm.ImportFilesAsync([broken]);
                var bad = vm.Playlist.Single(v => v.Path == broken); vm.Reorder(bad, a);
                vm.Preferences.Mode = PlaybackMode.Sequential;
                await Play(b, true); await Pause(); await End();
                await Until(() => engine.State.IsLoaded && engine.State.FilePath == file); await Pause();
                Check("自动连播跳过无法解码的媒体", vm.CurrentVideo == a && engine.State.Error is null);
                await Until(() => !vm.Notice.StartsWith("正在恢复至", StringComparison.Ordinal));
                engine.SetSpeed(1); await Seek(10); await vm.SaveCurrentAsync();
                var locked = await Task.Run(() =>
                {
                    var connection = new SqliteConnection($"Data Source={vm.DatabasePath};Pooling=False");
                    connection.Open(); return (connection, transaction: connection.BeginTransaction());
                });
                try
                {
                    double position = engine.State.Position;
                    engine.SetPaused(false);
                    var saving = vm.SaveCurrentAsync();
                    await Task.Delay(600);
                    Check("数据库被锁时保存异步且真实播放继续", !saving.IsCompleted && engine.State.Position > position + .2);
                    await saving;
                    Check("数据库写入失败提供非阻塞提示", vm.PersistenceWarning is not null && engine.State.IsLoaded && engine.State.Error is null);
                }
                finally { locked.transaction.Dispose(); locked.connection.Dispose(); }
                await vm.SaveCurrentAsync();
                Check("解除锁后保存自动恢复", vm.PersistenceWarning is null && (await vm.ReadStoredAsync()).Videos.Single(v => v.FilePath == file).Position > 10);
                await Pause();
                vm.Confirm = _ => true; vm.ClearHistoryCommand.Execute(null);
                var cleared = await vm.ReadStoredAsync();
                Check("清空历史只清记录且队列和原文件保留", cleared.Videos.All(v => !v.HistoryVisible) && vm.History.Count == 0 && vm.Playlist.Count == 4 && File.Exists(file));
                vm.NavigateCommand.Execute("设置"); await Task.Delay(150);
                await ShellCapture.SaveAsync(window, overlay, renderer, Path.Combine(folder, "stage3-settings.png"));
            }
            Check("持久化过程没有后台错误", vm.PersistenceWarning is null);
            passed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            passed,
            failure,
            checks,
            state = engine.State,
            vm.Notice,
            vm.PersistenceWarning,
            vm.GeneratedThumbnails,
            vm.ThumbnailCacheHits,
            vm.DatabasePath,
            renderer.Frames
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }

    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("等待第三阶段真实播放状态超时。");
            await Task.Delay(30);
        }
    }
}
