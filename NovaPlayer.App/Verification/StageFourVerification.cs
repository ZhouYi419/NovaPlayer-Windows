using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using NovaPlayer.App.Controls;
using NovaPlayer.App.Services;
using NovaPlayer.Core.Playback;
using NovaPlayer.Infrastructure.Playback;
using NovaPlayer.Infrastructure.Rendering;

namespace NovaPlayer.App.Verification;

/// <summary>真实多轨媒体、GPU 字幕像素、快捷键分发与同会话窗口切换的集成验收。</summary>
internal static class StageFourVerification
{
    internal static async Task<int> RunAsync(MainWindow window, MpvPlayerEngine engine, MpvGlRenderer renderer,
        PlaybackOverlay overlay, string mode, string file, string reportPath)
    {
        var vm = window.ViewModel;
        string folder = Path.GetDirectoryName(reportPath)!;
        string fixtures = Path.GetDirectoryName(file)!;
        var checks = new List<object>();
        bool passed = false; string? failure = null;
        int subtitlePixelChanges = 0;
        void Check(string name, bool ok) { checks.Add(new { name, passed = ok }); if (!ok) throw new InvalidOperationException(name); }
        async Task Pause()
        {
            engine.SetPaused(true);
            await Until(() => engine.State.IsPaused && vm.State.IsPaused && vm.State.SessionId == engine.State.SessionId && vm.State.IsLoaded);
        }
        async Task Seek(double position)
        {
            vm.SeekTo(position);
            await Until(() => Math.Abs(engine.State.Position - position) < .3 && !engine.State.IsSeeking);
            await Task.Delay(150);
        }
        async Task Capture(string name) { overlay.ShowControls(); await Task.Delay(80); await ShellCapture.SaveAsync(window, overlay, renderer, Path.Combine(folder, name + ".png")); }
        static int Changed(RenderedFrame a, RenderedFrame b)
        {
            if (a.Width != b.Width || a.Height != b.Height) throw new InvalidOperationException("像素比较要求相同渲染尺寸。");
            int count = 0;
            for (int i = 0; i < a.Pixels.Length; i += 4)
                if (Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > 30) count++;
            return count;
        }
        try
        {
            if (mode.EndsWith("-reopen", StringComparison.Ordinal))
            {
                Check("独立进程恢复自定义快捷键", vm.Shortcuts.Resolve(Key.G, ModifierKeys.None, false) == ShortcutAction.TogglePause && vm.Shortcuts.Resolve(Key.Space, ModifierKeys.None, false) is null);
                Check("独立进程恢复字幕与播放设置", vm.Preferences.SubtitleSize == 64 && vm.Preferences.SeekInterval == 15 && vm.Preferences.ControlsHideSeconds == 1);
                Check("独立进程恢复主窗口尺寸与小窗位置", Math.Abs(window.Width - 1180) < 2 && Math.Abs(window.Height - 740) < 2 && vm.Preferences.MiniGeometry is { Width: >= 280 });
                await vm.PlayItemAsync(vm.Playlist.Single(v => v.Path == file), true);
                await Until(() => engine.State.IsLoaded && engine.State.Tracks.Count >= 5 && Math.Abs(engine.State.SubtitleSize - 64) < .01);
                await Pause();
                Check("重启后多轨媒体仍正常播放", engine.State.Error is null && vm.SubtitleTracks.Count >= 2 && vm.AudioTracks.Count == 2);
            }
            else
            {
                await vm.ImportFilesAsync([file]);
                await Until(() => engine.State.IsLoaded && engine.State.Tracks.Count >= 6 && vm.State.IsLoaded);
                await Pause(); await Seek(10);
                Check("读取真实内嵌字幕与同名外挂字幕", vm.SubtitleTracks.Count == 3 && vm.SubtitleTracks.Count(t => t.IsExternal) == 1);
                Check("读取真实音轨名称与语言", vm.AudioTracks.Count == 2 && vm.AudioTracks.Any(t => t.Language == "eng") && vm.AudioTracks.Any(t => t.Title == "原始音轨"));
                var chinese = vm.SubtitleTracks.First(t => !t.IsExternal && t.Language == "zho");
                vm.SelectSubtitleCommand.Execute(chinese);
                await Until(() => engine.State.SubtitleId == chinese.Id);
                engine.SetSubtitlesVisible(false); await Until(() => !engine.State.SubtitlesVisible); await Task.Delay(150);
                var without = await renderer.CaptureFrameAsync();
                engine.SetSubtitlesVisible(true); await Until(() => engine.State.SubtitlesVisible); await Task.Delay(200);
                var with = await renderer.CaptureFrameAsync(); subtitlePixelChanges = Changed(without, with);
                Check("开关字幕改变真实GPU像素", subtitlePixelChanges > 100);
                long session = engine.State.SessionId;
                var subtitleMenu = overlay.BuildSubtitleMenu((Button)overlay.FindName("SubtitleButton"));
                var english = subtitleMenu.Items.OfType<MenuItem>().Single(m => m.CommandParameter is MediaTrack { Language: "eng" });
                english.Command.Execute(english.CommandParameter);
                var selectedEnglish = (MediaTrack)english.CommandParameter;
                await Until(() => engine.State.SubtitleId == selectedEnglish.Id);
                Check("字幕菜单选择不同语言不重开视频", engine.State.SessionId == session && Math.Abs(engine.State.Position - 10) < .4);
                var audioMenu = overlay.BuildMoreMenu((Button)overlay.FindName("MoreButton")).Items.OfType<MenuItem>().Single(m => Equals(m.Header, "音轨选择"));
                var secondAudio = audioMenu.Items.OfType<MenuItem>().Last(); secondAudio.Command.Execute(secondAudio.CommandParameter);
                await Until(() => engine.State.AudioId == ((MediaTrack)secondAudio.CommandParameter).Id);
                Check("真实音轨切换保持会话与暂停位置", engine.State.SessionId == session && engine.State.IsPaused && Math.Abs(engine.State.Position - 10) < .4);
                vm.SelectAudioCommand.Execute(new MediaTrack(long.MaxValue, MediaTrackKind.Audio, null, null, null, false, null));
                Check("失效轨道选择被拦截且保持原音轨", engine.State.Error is null && engine.State.AudioId == ((MediaTrack)secondAudio.CommandParameter).Id);
                var fontBefore = await renderer.CaptureFrameAsync();
                vm.Preferences.SubtitleSize = 72;
                vm.AdjustSubtitleDelay(1.25);
                await Until(() => Math.Abs(engine.State.SubtitleSize - 72) < .01 && Math.Abs(engine.State.SubtitleDelay - 1.25) < .001);
                Check("字幕字号与偏移真实生效", engine.State.SessionId == session && engine.State.Error is null);
                await Task.Delay(180); var fontAfter = await renderer.CaptureFrameAsync();
                Check("字号调整改变实际字幕像素", Changed(fontBefore, fontAfter) > 100);
                vm.AdjustSubtitleDelay(20); await Until(() => Math.Abs(engine.State.SubtitleDelay - 20) < .001); await Task.Delay(180);
                Check("字幕偏移改变实际字幕时间轴", Changed(fontAfter, await renderer.CaptureFrameAsync()) > 100);
                vm.AdjustSubtitleDelay(0);
                await Until(() => Math.Abs(engine.State.SubtitleDelay) < .001); await Task.Delay(180);
                var aspectBefore = await renderer.CaptureFrameAsync();
                vm.SetAspectRatioCommand.Execute("4:3"); await Task.Delay(200);
                Check("画面比例调整实际画面且保持会话", engine.State.AspectRatio == "4:3" && engine.State.SessionId == session && Changed(aspectBefore, await renderer.CaptureFrameAsync()) > 1000);
                vm.SetAspectRatioCommand.Execute("原始比例");
                foreach (string extension in new[] { "srt", "ass", "ssa" })
                {
                    string subtitle = Path.Combine(fixtures, "外挂字幕." + extension);
                    vm.AddSubtitle(subtitle);
                    await Until(() => vm.SubtitleTracks.Any(t => string.Equals(t.ExternalPath, subtitle, StringComparison.OrdinalIgnoreCase) && t.Id == engine.State.SubtitleId));
                    Check($"外挂{extension.ToUpperInvariant()}加载并选中", engine.State.SessionId == session && engine.State.Error is null);
                }
                int count = vm.SubtitleTracks.Count;
                vm.AddSubtitle(Path.Combine(fixtures, "外挂字幕.ssa")); await Task.Delay(150);
                Check("重复加载外挂字幕复用已有轨道", vm.SubtitleTracks.Count == count);
                vm.AddSubtitle(Path.Combine(fixtures, "损坏字幕.srt"));
                await Until(() => engine.State.OperationWarning is not null);
                Check("损坏字幕仅提示不终止视频", engine.State.IsLoaded && engine.State.Error is null && engine.State.SessionId == session);
                vm.AddSubtitle(Path.Combine(fixtures, "外挂字幕.ass"));
                await Until(() => engine.State.OperationWarning is null);
                await Capture("stage4-subtitles");
                string plain = Path.Combine(fixtures, "无字幕.mp4");
                await vm.ImportFilesAsync([plain]);
                await vm.PlayItemAsync(vm.Playlist.Single(v => v.Path == plain), true);
                await Until(() => engine.State.IsLoaded && engine.State.FilePath == plain && engine.State.Tracks.Count == 2 && vm.State.FilePath == plain);
                await Pause();
                Check("无字幕与单音轨菜单不显示无效操作", vm.SubtitleSummary == "未检测到字幕" &&
                    !overlay.BuildMoreMenu((Button)overlay.FindName("MoreButton")).Items.OfType<MenuItem>().Any(m => Equals(m.Header, "音轨选择")));
                vm.Preferences.AutoLoadSubtitles = false; await Task.Delay(180);
                await vm.PlayItemAsync(vm.Playlist.Single(v => v.Path == file), true);
                await Until(() => engine.State.IsLoaded && engine.State.FilePath == file && vm.SubtitleTracks.Count == 2); await Pause();
                Check("关闭自动加载仅保留内嵌字幕", vm.SubtitleTracks.All(t => !t.IsExternal));
                vm.Preferences.AutoLoadSubtitles = true;
                vm.SelectSubtitleCommand.Execute(vm.SubtitleTracks.First());
                engine.SetSpeed(1.75); engine.SetVolume(37); vm.AdjustSubtitleDelay(.5);
                await Until(() => engine.State.Speed == 1.75 && engine.State.Volume == 37 && engine.State.SubtitleDelay == .5);
                await Seek(15);
                long decoderSession = engine.State.SessionId;
                vm.Preferences.HardwareDecoding = false;
                await Task.Delay(180);
                Check("切换解码设置不扰动当前帧", engine.State.SessionId == decoderSession && Math.Abs(engine.State.Position - 15) < .3);
                await vm.PlayItemAsync(vm.Playlist.Single(v => v.Path == file), true);
                await Until(() => engine.State.Decoder == "软件解码");
                await Pause(); await Seek(15);
                Check("软件解码设置在下次打开生效", engine.State.IsLoaded && engine.State.Error is null);
                vm.Preferences.HardwareDecoding = true;
                await vm.PlayItemAsync(vm.Playlist.Single(v => v.Path == file), true);
                await Until(() => engine.State.IsLoaded && engine.State.Decoder.Contains("d3d11va", StringComparison.Ordinal));
                await Pause(); await Seek(15);
                vm.Preferences.PitchCorrection = false; vm.Preferences.PitchCorrection = true;
                vm.Preferences.AutoMuteHighSpeed = false; engine.SetSpeed(15);
                await Until(() => engine.State.Speed == 15 && !engine.State.EffectiveMuted);
                vm.Preferences.AutoMuteHighSpeed = true;
                await Until(() => engine.State.EffectiveMuted);
                engine.SetSpeed(1.75); await Until(() => engine.State.Speed == 1.75 && !engine.State.EffectiveMuted);
                Check("高倍速自动静音开关立即生效并恢复音量", engine.State.Volume == 37 && engine.State.Error is null);
                var before = engine.State; var original = WindowPlacement.Capture(window);
                window.ToggleMiniPlayer(); await Task.Delay(250);
                Check("画中画默认尺寸与置顶", vm.IsMiniPlayer && window.Topmost && Math.Abs(window.Width - 400) < 2 && Math.Abs(window.Height - 225) < 2);
                var resize = (Thumb)overlay.FindName("MiniResizeCorner");
                resize.RaiseEvent(new DragDeltaEventArgs(80, 45) { RoutedEvent = Thumb.DragDeltaEvent }); await Task.Delay(120);
                Check("浮层缩放手柄驱动真实小窗尺寸", Math.Abs(window.Width - 480) < 2 && Math.Abs(window.Height - 270) < 2);
                resize.RaiseEvent(new DragDeltaEventArgs(-80, -45) { RoutedEvent = Thumb.DragDeltaEvent }); await Task.Delay(120);
                Check("画中画保持字幕音轨与完整播放状态", engine.State.SessionId == before.SessionId && engine.State.Speed == before.Speed && engine.State.Volume == before.Volume && engine.State.SubtitleId == before.SubtitleId && engine.State.AudioId == before.AudioId && engine.State.SubtitleDelay == before.SubtitleDelay && Math.Abs(engine.State.Position - before.Position) < .3);
                Check("画中画复用同一GPU渲染器且控制可命中", ReferenceEquals(window.VideoHost.Renderer, renderer) && overlay.VerifyOverlayHitTest());
                await Capture("stage4-mini");
                window.Width = 280; window.Height = 158; await Task.Delay(200);
                var miniControls = (FrameworkElement)overlay.FindName("MiniControls");
                Rect controlsBounds = miniControls.TransformToAncestor((FrameworkElement)overlay.Content).TransformBounds(new Rect(miniControls.RenderSize));
                Check("最小画中画控制栏不裁切", controlsBounds.Left >= -1 && controlsBounds.Right <= overlay.ActualWidth + 1 && controlsBounds.Bottom <= overlay.ActualHeight + 1);
                await Capture("stage4-mini-small");
                window.Close(); await Task.Delay(180);
                Check("关闭画中画返回主窗口并恢复布局", window.IsVisible && !vm.IsMiniPlayer && Math.Abs(window.Width - original.Width) < 2 && engine.State.SessionId == before.SessionId);
                for (int i = 0; i < 6; i++) { window.ToggleMiniPlayer(); await Task.Delay(60); window.ToggleMiniPlayer(); await Task.Delay(60); }
                Check("多次画中画切换不重建会话或渲染器", engine.State.SessionId == before.SessionId && ReferenceEquals(window.VideoHost.Renderer, renderer) && Math.Abs(engine.State.Position - 15) < .3);
                vm.Preferences.ControlsHideSeconds = 1;
                window.ToggleFullscreen(); overlay.ShowControls();
                var menu = overlay.BuildSubtitleMenu((Button)overlay.FindName("SubtitleButton")); overlay.OpenContext(menu);
                await Task.Delay(1400);
                Check("全屏打开菜单时控制栏保持显示", overlay.ControlsVisible);
                overlay.CloseMenu(); await Until(() => !overlay.ControlsVisible);
                Check("全屏按配置隐藏控制栏", !overlay.ControlsVisible);
                overlay.ShowControls(); Check("活动恢复控制栏与命中", overlay.ControlsVisible && overlay.VerifyOverlayHitTest());
                window.ToggleMiniPlayer(); await Task.Delay(120);
                Check("全屏进入画中画保持会话", vm.IsMiniPlayer && !vm.IsFullscreen && engine.State.SessionId == before.SessionId);
                window.ToggleFullscreen(); await Task.Delay(120);
                Check("画中画进入全屏保持会话", vm.IsFullscreen && !vm.IsMiniPlayer && engine.State.SessionId == before.SessionId);
                window.ToggleFullscreen();
                Check("快捷键冲突拒绝且提示原操作", !vm.Shortcuts.Assign(ShortcutAction.TogglePause, new("F")) && vm.Shortcuts.Message!.Contains("切换全屏"));
                Check("自定义快捷键立即替换原键", vm.Shortcuts.Assign(ShortcutAction.TogglePause, new("G")) && !window.TryHandleShortcut(Key.Space, ModifierKeys.None, false));
                Check("输入框编辑不触发播放命令", !window.TryHandleShortcut(Key.G, ModifierKeys.None, false, new TextBox()) && !window.TryHandleShortcut(Key.G, ModifierKeys.None, false, new ComboBox()));
                Check("重复按键不反复切换暂停状态", !window.TryHandleShortcut(Key.G, ModifierKeys.None, true));
                Check("预设特殊键匹配WPF枚举别名", vm.Shortcuts.Resolve(Key.Oem4, ModifierKeys.None, false) == ShortcutAction.Slower &&
                    vm.Shortcuts.Resolve(Key.Oem6, ModifierKeys.None, false) == ShortcutAction.Faster && vm.Shortcuts.Resolve(Key.Oem5, ModifierKeys.None, false) == ShortcutAction.NormalSpeed);
                window.TryHandleShortcut(Key.G, ModifierKeys.None, false); await Until(() => !engine.State.IsPaused);
                window.TryHandleShortcut(Key.G, ModifierKeys.None, false); await Pause();
                Check("自定义快捷键实际控制内核", engine.State.IsPaused);
                await vm.Shortcuts.SaveAsync();
                var reloaded = await ShortcutService.LoadAsync(vm.Shortcuts.StoragePath);
                Check("自定义快捷键可重新读取", reloaded.Resolve(Key.G, ModifierKeys.None, false) == ShortcutAction.TogglePause);
                string damagedPath = Path.Combine(folder, "invalid-shortcuts.json");
                await File.WriteAllTextAsync(damagedPath, "此配置不是JSON。");
                var damaged = await ShortcutService.LoadAsync(damagedPath); await damaged.SaveAsync();
                Check("损坏快捷键配置不会被退出保存覆盖", await File.ReadAllTextAsync(damagedPath) == "此配置不是JSON。");
                damaged.Assign(ShortcutAction.TogglePause, new("G")); await damaged.SaveAsync();
                Check("修改损坏配置前保留原件备份", Directory.GetFiles(folder, "invalid-shortcuts.json.invalid-*.bak").Length == 1);
                vm.Shortcuts.Reset();
                Check("恢复全部默认快捷键", vm.Shortcuts.Resolve(Key.Space, ModifierKeys.None, false) == ShortcutAction.TogglePause && vm.Shortcuts.Resolve(Key.G, ModifierKeys.None, false) is null);
                vm.Shortcuts.Assign(ShortcutAction.TogglePause, new("G"));
                vm.ShowShortcutsCommand.Execute(null); await Task.Delay(200); await Capture("stage4-shortcuts");
                vm.Preferences.SubtitleSize = 64; vm.Preferences.SeekInterval = 15;
                await Seek(15); window.TryHandleShortcut(Key.Right, ModifierKeys.None, false);
                await Until(() => Math.Abs(engine.State.Position - 30) < .3 && !engine.State.IsSeeking);
                Check("自定义快进间隔参与实际快捷键跳转", Math.Abs(engine.State.Position - 30) < .3);
                vm.NavigateCommand.Execute("首页"); window.WindowState = WindowState.Normal; window.Width = 1180; window.Height = 740; window.Left = 40; window.Top = 60;
            }
            Check("验收结束播放内核与存储正常", engine.State.Error is null && engine.State.OperationWarning is null && vm.PersistenceWarning is null);
            passed = true;
        }
        catch (Exception ex) { failure = ex.ToString(); }
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
        {
            passed,
            failure,
            checks,
            subtitlePixelChanges,
            state = engine.State,
            vm.Notice,
            vm.PersistenceWarning,
            renderer.Frames,
            window = new { window.Width, window.Height, window.Left, window.Top, window.WindowState, vm.IsMiniPlayer }
        }, new JsonSerializerOptions { WriteIndented = true }));
        return passed ? 0 : 1;
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.Elapsed.TotalSeconds > 15) throw new TimeoutException("等待第四阶段真实播放状态超时。"); await Task.Delay(30); }
    }
}
