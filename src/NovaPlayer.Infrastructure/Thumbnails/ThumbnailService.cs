using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NovaPlayer.Core.Library;
using Serilog;

namespace NovaPlayer.Infrastructure.Thumbnails;

/// <summary>按文件指纹复用封面，限制后台提帧并发；仅在界面需要封面时发起生成。</summary>
public sealed class ThumbnailService : IAsyncDisposable
{
    private readonly string _ffmpeg;
    private readonly string _cache;
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<ThumbnailResult?>>> _jobs = new();
    private long _generated, _hits;
    public long GeneratedCount => Interlocked.Read(ref _generated);
    public long CacheHitCount => Interlocked.Read(ref _hits);
    public ThumbnailService(string ffmpeg, string cache) { _ffmpeg = Path.GetFullPath(ffmpeg); _cache = Path.GetFullPath(cache); }
    public static string CacheKey(VideoRecord video) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{VideoRecord.Key(video.FilePath)}|{video.FileSize}|{video.ModifiedUtcTicks}")));
    public async Task<ThumbnailResult?> GetAsync(VideoRecord video, CancellationToken cancellation = default)
    {
        string key = CacheKey(video);
        // 即使并发槽立即可用，缓存检查与进程启动也只在线程池执行。
        var job = _jobs.GetOrAdd(key, _ => new Lazy<Task<ThumbnailResult?>>(
            () => Task.Run(() => GenerateAsync(video, key, cancellation), _stop.Token), LazyThreadSafetyMode.ExecutionAndPublication));
        try { return await job.Value.WaitAsync(cancellation).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !_stop.IsCancellationRequested)
        {
            _jobs.TryRemove(new KeyValuePair<string, Lazy<Task<ThumbnailResult?>>>(key, job));
            return await GetAsync(video, cancellation).ConfigureAwait(false);
        }
        finally { if (job.IsValueCreated && job.Value.IsCompleted) _jobs.TryRemove(new KeyValuePair<string, Lazy<Task<ThumbnailResult?>>>(key, job)); }
    }
    private async Task<ThumbnailResult?> GenerateAsync(VideoRecord video, string key, CancellationToken request)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, request);
        var token = cancellation.Token;
        await _slots.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(_cache);
            string path = Path.Combine(_cache, key + ".jpg");
            string metadata = Path.Combine(_cache, key + ".json");
            if (ValidJpeg(path))
            {
                Interlocked.Increment(ref _hits);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
                double duration = video.Duration;
                try { if (File.Exists(metadata)) duration = JsonSerializer.Deserialize<double>(await File.ReadAllTextAsync(metadata, token).ConfigureAwait(false)); }
                catch (JsonException) { }
                return new(path, duration);
            }
            if (!File.Exists(_ffmpeg) || !File.Exists(video.FilePath)) return null;
            double[] samples = video.Duration > 0
                ? [Math.Min(video.Duration * 0.1, 30), Math.Min(video.Duration * 0.5, 120), 0]
                : [1, 5, 0];
            foreach (double seconds in samples.Distinct())
            {
                token.ThrowIfCancellationRequested();
                temporary = Path.Combine(_cache, key + $".{Guid.NewGuid():N}.tmp.jpg");
                var start = new ProcessStartInfo(_ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
                string filter = "thumbnail=30,format=yuv420p,signalstats,metadata=mode=select:key=lavfi.signalstats.YAVG:value=24:function=greater,scale=320:180:force_original_aspect_ratio=decrease";
                foreach (string arg in new[] { "-hide_banner", "-nostdin", "-threads", "1", "-filter_threads", "1", "-ss", seconds.ToString("R", CultureInfo.InvariantCulture), "-i", video.FilePath,
                    "-an", "-sn", "-t", "4", "-vf", filter, "-frames:v", "1", "-q:v", "3", "-y", temporary }) start.ArgumentList.Add(arg);
                using var process = new Process { StartInfo = start };
                process.Start();
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(12));
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    if (!process.HasExited) process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                    await Task.WhenAll(stderr, stdout).ConfigureAwait(false);
                    if (token.IsCancellationRequested) throw;
                    if (File.Exists(temporary)) File.Delete(temporary);
                    temporary = null;
                    continue;
                }
                string diagnostic = await stderr.ConfigureAwait(false); await stdout.ConfigureAwait(false);
                if (process.ExitCode == 0 && ValidJpeg(temporary))
                {
                    var match = Regex.Match(diagnostic, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);
                    double duration = match.Success ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600 +
                        double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60 + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : video.Duration;
                    // 生成过程中原文件发生变化时，丢弃旧任务结果，避免封面与视频不对应。
                    var info = new FileInfo(video.FilePath);
                    if (!info.Exists || !video.MatchesFile(info.Length, info.LastWriteTimeUtc.Ticks)) return null;
                    File.Move(temporary, path, true); temporary = null;
                    await File.WriteAllTextAsync(metadata, JsonSerializer.Serialize(duration), token).ConfigureAwait(false);
                    Interlocked.Increment(ref _generated);
                    return new(path, duration);
                }
                if (File.Exists(temporary)) File.Delete(temporary);
                temporary = null;
            }
            return null;
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Log.Warning(ex, "视频封面生成失败，使用默认图标"); return null; }
        finally
        {
            if (temporary is not null) { try { File.Delete(temporary); } catch (IOException) { } }
            _slots.Release();
        }
    }
    private static bool ValidJpeg(string path)
    {
        if (!File.Exists(path)) return false;
        using var file = File.OpenRead(path);
        if (file.Length < 128 || file.ReadByte() != 0xFF || file.ReadByte() != 0xD8) return false;
        file.Seek(-2, SeekOrigin.End); return file.ReadByte() == 0xFF && file.ReadByte() == 0xD9;
    }
    public Task CleanAsync() => Task.Run(() =>
    {
        try
        {
            if (!Directory.Exists(_cache)) return;
            // 仅清理由本服务命名且已过期的临时帧，兼容上次进程异常终止留下的文件。
            foreach (var temporary in new DirectoryInfo(_cache).EnumerateFiles("*.tmp.jpg")
                .Where(f => Regex.IsMatch(f.Name, @"^[A-F0-9]{64}\.[a-f0-9]{32}\.tmp\.jpg$") && f.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1))) temporary.Delete();
            var images = new DirectoryInfo(_cache).EnumerateFiles("*.jpg").Where(f => Regex.IsMatch(f.Name, @"^[A-F0-9]{64}\.jpg$"))
                .OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long bytes = 0; int kept = 0;
            foreach (var image in images)
            {
                bytes += image.Length;
                if (kept++ < 2048 && bytes <= 256L * 1024 * 1024 && image.LastWriteTimeUtc > DateTime.UtcNow.AddDays(-30)) continue;
                image.Delete(); string metadata = Path.ChangeExtension(image.FullName, ".json"); if (File.Exists(metadata)) File.Delete(metadata);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warning(ex, "封面缓存清理失败"); }
    });
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        var tasks = _jobs.Values.Where(j => j.IsValueCreated).Select(j => j.Value).ToArray();
        try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch (OperationCanceledException) { }
    }
}
