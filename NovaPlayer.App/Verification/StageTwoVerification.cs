using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using NovaPlayer.App.Controls;
using NovaPlayer.App.Services;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;
using NovaPlayer.Infrastructure.FileSystem;

namespace NovaPlayer.App.Verification;

internal static class StageTwoVerification
{
    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer, PlaybackOverlay overlay, string file, string reportPath)
    {
        var checks = new List<object>();
        var screenshots = new List<string>();
        var vm = window.ViewModel;
        string folder = Path.GetDirectoryName(Path.GetFullPath(reportPath))!;
        Directory.CreateDirectory(folder);
        bool passed = false;
        string? failure = null;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        async Task Capture(string name)
        {
            string path = Path.Combine(folder, name + ".png");
            await ShellCapture.SaveAsync(window, overlay, renderer, path); screenshots.Add(path);
        }
        bool Visible(string name) => ((FrameworkElement)window.FindName(name)).IsVisible;
        try
        {
            await Task.Delay(200);
            Check("空状态不展示伪造记录", vm.ShowEmpty && vm.History.Count == 0 && vm.Playlist.Count == 0);
            await Capture("stage2-empty");
            string second = Path.Combine(folder, "第二段视频 队列验证.mp4");
            File.Copy(file, second, true);
            await vm.ImportFilesAsync([file, second]);
            await Until(() => engine.State.IsLoaded && vm.CurrentVideo?.Duration > 0 && engine.State.Position > 0.2);
            Check("真实导入队列与索引同步", vm.Playlist.Count == 2 && vm.PlaylistIndex == "1 / 2");
            await vm.ImportFilesAsync([file]);
            Check("重复导入不产生重复条目", vm.Playlist.Count == 2);
            string importFolder = Path.Combine(folder, "文件夹导入验证");
            Directory.CreateDirectory(Path.Combine(importFolder, "子目录"));
            string nested = Path.Combine(importFolder, "子目录", "嵌套视频.mp4");
            File.Copy(file, nested, true);
            await File.WriteAllTextAsync(Path.Combine(importFolder, "忽略的文档.txt"), "此文件不应被加入播放列表。");
            var import = await MediaImportService.ImportAsync([importFolder, nested], CancellationToken.None);
            Check("文件夹导入去重并忽略非视频", import.Videos.Count == 1 && import.Skipped == 1);
            engine.SetPaused(true); await Until(() => engine.State.IsPaused);
            vm.SeekTo(20); await Until(() => Math.Abs(engine.State.Position - 20) < 0.3 && !engine.State.IsSeeking);
            await Task.Delay(150);
            Check("底部记录与内核进度一致", Math.Abs(vm.CurrentVideo!.Position - engine.State.Position) < 0.3);
            Check("浮层可接收原生点击", overlay.VerifyOverlayHitTest());
            await Capture("stage2-home");
            string originalPath = engine.State.FilePath!;
            vm.NavigateCommand.Execute("播放历史"); await Task.Delay(160);
            Check("历史导航隐藏浮层且会话保持", !overlay.IsVisible && engine.State.FilePath == originalPath && ReferenceEquals(window.VideoHost.Renderer, renderer));
            await Capture("stage2-history");
            vm.NavigateCommand.Execute("设置"); await Task.Delay(160);
            Check("设置导航复用同一渲染器", !overlay.IsVisible && ReferenceEquals(window.VideoHost.Renderer, renderer));
            await Capture("stage2-settings");
            vm.Preferences.AutoResume = false;
            Check("快捷设置与完整设置共享状态", !vm.Preferences.AutoResume);
            vm.Preferences.AutoResume = true;
            vm.NavigateCommand.Execute("首页"); await Task.Delay(160);
            Check("返回首页恢复视频浮层", overlay.VerifyOverlayHitTest() && engine.State.Position >= 19.7);
            vm.SearchText = "第二段视频";
            Check("搜索仅返回已导入的匹配视频", vm.SearchResults.Count == 1 && vm.SearchResults[0].Path == second);
            vm.SearchText = "";
            vm.Preferences.DefaultSpeed = 2;
            vm.NextCommand.Execute(null);
            await Until(() => engine.State.IsLoaded && engine.State.FilePath == second && engine.State.Speed == 2);
            Check("下一条与默认倍速生效", vm.PlaylistIndex == "2 / 2" && vm.CurrentVideo!.Path == second);
            engine.SetPaused(true); await Until(() => engine.State.IsPaused);
            vm.Preferences.DefaultSpeed = 1;
            await Task.Delay(100);
            Check("默认倍速不覆盖当前倍速", engine.State.Speed == 2);
            vm.PreviousCommand.Execute(null);
            await Until(() => engine.State.IsLoaded && engine.State.FilePath == originalPath && engine.State.Position >= 19.7 && engine.State.Speed == 1);
            engine.SetPaused(true); await Until(() => engine.State.IsPaused);
            Check("会话内恢复位置与独立倍速", engine.State.Position >= 19.7 && vm.PlaylistIndex == "1 / 2");
            vm.Reorder(vm.Playlist[0], vm.Playlist[1]);
            Check("拖动排序同步当前索引", vm.PlaylistIndex == "2 / 2" && vm.Playlist[1].IsCurrent);
            vm.Reorder(vm.Playlist[1], vm.Playlist[0]);
            vm.Preferences.PlaylistExpanded = false; await Task.Delay(100);
            Check("收起播放列表扩展视频区域", !Visible("PlaylistPanel"));
            vm.Preferences.PlaylistExpanded = true;
            foreach (double width in new[] { 1440d, 1200d, 1199d, 1049d, 960d })
            {
                window.Width = width; window.Height = width == 960 ? 620 : 900; await Task.Delay(180);
                Check($"{width} 像素自适应与浮层命中", Visible("PlaylistPanel") == (width >= 1200) && Visible("HomeLabel") == (width >= 1050) && overlay.VerifyOverlayHitTest());
                if (width == 960) await Capture("stage2-compact");
            }
            window.Width = 1440; window.Height = 900; await Task.Delay(150);
            vm.Preferences.Theme = "浅色"; await Task.Delay(120);
            Check("浅色主题更新真实资源", ((SolidColorBrush)Application.Current.Resources["AppBackground"]).Color == (Color)ColorConverter.ConvertFromString("#F1F4FA"));
            await Capture("stage2-light");
            vm.Preferences.Theme = "深色";
            vm.Preferences.InformationExpanded = false; await Task.Delay(100);
            Check("信息区域可折叠", !Visible("InformationArea"));
            vm.Preferences.InformationExpanded = true;
            window.ToggleFullscreen(); await Task.Delay(250);
            Check("完整主界面全屏不遮挡控制栏", vm.IsFullscreen && !Visible("Sidebar") && overlay.VerifyOverlayHitTest());
            window.ToggleFullscreen(); await Task.Delay(150);
            window.ToggleMiniPlayer(); await Task.Delay(250);
            Check("画中画复用会话与渲染器", vm.IsMiniPlayer && window.Topmost && engine.State.FilePath == originalPath && ReferenceEquals(window.VideoHost.Renderer, renderer));
            await Capture("stage2-mini");
            window.ToggleMiniPlayer(); await Task.Delay(150);
            Check("返回主窗口恢复布局", !vm.IsMiniPlayer && window.Width >= 960 && Visible("Sidebar"));
            vm.Confirm = _ => true;
            vm.ClearHistoryCommand.Execute(null); await Task.Delay(150);
            Check("清空记录保留文件与队列", vm.History.Count == 0 && vm.Playlist.Count == 2 && File.Exists(file) && File.Exists(second));
            await vm.Preferences.SaveAsync();
            var reloaded = await PreferencesService.LoadAsync(vm.Preferences.StoragePath);
            Check("偏好设置写入并可重新读取", reloaded.DefaultSpeed == 1 && reloaded.AutoResume && reloaded.Theme == "深色" && reloaded.PlaylistExpanded && reloaded.InformationExpanded);
            passed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        finally
        {
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
            {
                passed,
                failure,
                checks,
                screenshots,
                renderer.Gpu,
                renderer.Frames,
                state = engine.State,
                vm.Notice,
                vm.IsReady,
                queueCount = vm.Playlist.Count
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return passed ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("等待主界面或真实播放状态超时。"); await Task.Delay(30); }
    }
}
