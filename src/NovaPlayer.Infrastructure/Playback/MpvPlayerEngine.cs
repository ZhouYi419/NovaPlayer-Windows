using System.Globalization;
using System.Runtime.InteropServices;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Core.Playback;
using Serilog;
using System.Collections.Concurrent;

namespace NovaPlayer.Infrastructure.Playback;

public sealed class MpvPlayerEngine : IPlayerEngine
{
    private readonly object _gate = new();
    private readonly Task _eventTask;
    private readonly ILogger _log;
    private nint _handle;
    private volatile bool _stopping;
    private PlayerState _state = new();
    private double _requestedSpeed = 1;
    private long _sessionId;
    private long _requestId;
    private PlaybackOptions _options = new();
    private bool _appliedHardware = true;
    private readonly ConcurrentDictionary<ulong, (long Session, string Label)> _operations = new();

    public PlayerState State => Volatile.Read(ref _state);
    public event EventHandler<PlayerState>? StateChanged;
    /// <summary>后台读取固定白名单中的构建与性能诊断；复制后立即释放 mpv 分配的字符串。</summary>
    public IReadOnlyDictionary<string, string?> ReadDiagnostics()
    {
        string[] names = ["mpv-version", "mpv-configuration", "ffmpeg-version", "libass-version", "frame-drop-count", "decoder-frame-drop-count", "estimated-vf-fps"];
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            var result = new Dictionary<string, string?>();
            foreach (string name in names)
            {
                nint pointer = MpvNative.mpv_get_property_string(_handle, name);
                try { result[name] = pointer == 0 ? null : Marshal.PtrToStringUTF8(pointer); }
                finally { if (pointer != 0) MpvNative.mpv_free(pointer); }
            }
            return result;
        }
    }
    internal nint NativeHandle => _handle;

    public MpvPlayerEngine(ILogger log)
    {
        _log = log;
        try
        {
            _handle = MpvNative.mpv_create();
            if (_handle == 0) throw new InvalidOperationException("无法创建 libmpv 播放会话。");
            // 加载媒体前先创建渲染上下文，并禁用 mpv 自带界面、脚本及用户配置加载。
            Option("config", "no"); Option("terminal", "no"); Option("vo", "libmpv");
            Option("input-default-bindings", "no"); Option("input-vo-keyboard", "no");
            Option("osc", "no"); Option("osd-level", "0"); Option("idle", "yes");
            Option("keep-open", "yes"); Option("pause", "yes"); Option("volume", "70");
            Option("hwdec", "d3d11va,d3d11va-copy,no");
            Option("audio-pitch-correction", "yes"); Option("framedrop", "vo");
            Option("sub-auto", "exact"); Option("sub-auto-exts", "srt,ass,ssa");
            MpvNative.Check(MpvNative.mpv_initialize(_handle));
            string[] doubles = ["time-pos", "duration", "speed", "volume", "container-fps", "sub-scale", "sub-delay"];
            string[] flags = ["pause", "mute", "seeking", "eof-reached", "sub-visibility"];
            foreach (var name in doubles) Observe(name, MpvNative.Double);
            foreach (var name in flags) Observe(name, MpvNative.Flag);
            Observe("width", MpvNative.Int64); Observe("height", MpvNative.Int64);
            Observe("hwdec-current", MpvNative.String);
            Observe("video-format", MpvNative.String);
            Observe("audio-codec-name", MpvNative.String);
            Observe("track-list", MpvNative.Node);
            Observe("sid", MpvNative.Int64); Observe("aid", MpvNative.Int64);
            MpvNative.Check(MpvNative.mpv_request_log_messages(_handle, "warn"));
            _eventTask = Task.Run(EventLoop);
            _log.Information("libmpv initialized: PID {Pid}", Environment.ProcessId);
        }
        catch
        {
            if (_handle != 0) MpvNative.mpv_terminate_destroy(_handle);
            _handle = 0;
            throw;
        }
    }

    private void Option(string name, string value) => MpvNative.Check(MpvNative.mpv_set_option_string(_handle, name, value));
    private void Observe(string name, int format) => MpvNative.Check(MpvNative.mpv_observe_property(_handle, 0, name, format));

    public void Open(string localFile)
    {
        var path = Path.GetFullPath(localFile);
        if (!File.Exists(path)) throw new FileNotFoundException("视频文件不存在，请重新选择文件。", path);
        Update(s => new PlayerState
        {
            SessionId = Interlocked.Increment(ref _sessionId),
            FilePath = path,
            IsLoading = true,
            Speed = s.Speed,
            Volume = s.Volume,
            UserMuted = s.UserMuted,
            EffectiveMuted = s.EffectiveMuted,
            SubtitleSize = s.SubtitleSize,
            SubtitlesVisible = s.SubtitlesVisible
        });
        ApplyHardwareBeforeOpen();
        SetSubtitleDelay(0);
        SetAspectRatio("原始比例");
        Command("loadfile", path, "replace");
        SetPaused(false);
    }

    public void SetPaused(bool paused) => SetFlag("pause", paused);
    public void Stop() { Command("stop"); Update(s => new PlayerState { Volume = s.Volume, Speed = s.Speed, UserMuted = s.UserMuted, EffectiveMuted = s.EffectiveMuted }); }
    public void SetSubtitlesVisible(bool visible) => SetFlag("sub-visibility", visible, "字幕显示设置");
    public void LoadSubtitle(string localFile)
    {
        var path = Path.GetFullPath(localFile);
        if (!File.Exists(path)) throw new FileNotFoundException("字幕文件不存在。", path);
        if (!new[] { ".srt", ".ass", ".ssa" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("请选择 SRT、ASS 或 SSA 字幕文件。");
        var existing = State.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Subtitle && string.Equals(t.ExternalPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) { SelectSubtitle(existing.Id); return; }
        Update(s => s with { OperationWarning = null });
        CommandWithRequest(Issue("加载外挂字幕"), "sub-add", path, "select");
        SetSubtitlesVisible(true);
    }
    public void SelectSubtitle(long id)
    {
        ValidateTrack(id, MediaTrackKind.Subtitle);
        SetString("sid", id.ToString(CultureInfo.InvariantCulture), "切换字幕");
        SetSubtitlesVisible(true);
    }
    public void SelectAudio(long id)
    {
        ValidateTrack(id, MediaTrackKind.Audio);
        SetString("aid", id.ToString(CultureInfo.InvariantCulture), "切换音轨");
    }
    private void ValidateTrack(long id, MediaTrackKind kind)
    {
        if (!State.IsLoaded || !State.Tracks.Any(t => t.Id == id && t.Kind == kind)) throw new ArgumentException("所选轨道已不可用，请重新打开菜单选择。");
        Update(s => s with { OperationWarning = null });
    }
    public void SetSubtitleSize(double size)
    {
        if (!double.IsFinite(size) || size is < 20 or > 90) throw new ArgumentOutOfRangeException(nameof(size), "字幕字号应为 20～90。");
        // 以默认字号 55 为基准缩放文本字幕；ASS/SSA 保留原排版和特效。
        SetDouble("sub-scale", size / 55, "调整字幕缩放");
    }
    public void SetSubtitleDelay(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds is < -600 or > 600) throw new ArgumentOutOfRangeException(nameof(seconds), "字幕偏移应为 -600～600 秒。");
        SetDouble("sub-delay", seconds, "调整字幕偏移");
    }
    public void SetAspectRatio(string ratio)
    {
        string value = ratio switch { "原始比例" => "-1", "16:9" => "16:9", "4:3" => "4:3", "1:1" => "1:1", "21:9" => "21:9", _ => throw new ArgumentException("无效的画面比例。") };
        SetString("video-aspect-override", value, "调整画面比例");
        Update(s => s with { AspectRatio = ratio });
    }
    public void Configure(PlaybackOptions options)
    {
        var previous = _options;
        _options = options;
        if (previous.AutoLoadSubtitles != options.AutoLoadSubtitles) SetString("sub-auto", options.AutoLoadSubtitles ? "exact" : "no", "自动加载字幕设置");
        if (previous.PitchCorrection != options.PitchCorrection) SetFlag("audio-pitch-correction", options.PitchCorrection, "音调修正设置");
        // 硬件解码在下次打开前应用，避免重建当前解码器改变暂停位置。
        if (previous.AutoMuteHighSpeed != options.AutoMuteHighSpeed) SetFlag("mute", State.UserMuted || options.AutoMuteHighSpeed && State.Speed > 8);
    }
    private unsafe void ApplyHardwareBeforeOpen()
    {
        bool hardware = _options.HardwareDecoding;
        if (_appliedHardware == hardware) return;
        nint value = Marshal.StringToCoTaskMemUTF8(hardware ? "d3d11va,d3d11va-copy,no" : "no");
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                MpvNative.Check(MpvNative.mpv_set_property(_handle, "hwdec", MpvNative.String, &value));
                _appliedHardware = hardware;
            }
        }
        catch
        {
            Update(s => s with { IsLoading = false, Error = "解码配置应用失败，请恢复自动硬件解码后重试。" });
            throw;
        }
        finally { Marshal.FreeCoTaskMem(value); }
    }
    public void Seek(double seconds)
    {
        var state = State;
        double target = PlaybackPolicy.ClampPosition(seconds, state.Duration);
        string extension = Path.GetExtension(state.FilePath)?.ToLowerInvariant() ?? "";
        // 传输流/节目流缺少可靠索引时，解复用器可能落到目标之后的关键帧；提前读取再精确解码到目标。
        SetDouble("hr-seek-demuxer-offset", extension is ".ts" or ".m2ts" or ".mpg" or ".mpeg" ? 10 : 0);
        Command("seek", target.ToString("R", CultureInfo.InvariantCulture), "absolute+exact");
    }

    public void SetSpeed(double speed)
    {
        PlaybackPolicy.ValidateSpeed(speed);
        lock (_gate)
        {
            _requestedSpeed = speed;
            SetDouble("speed", speed);
            SetFlag("mute", State.UserMuted || _options.AutoMuteHighSpeed && speed > 8);
        }
    }

    public void SetVolume(double volume)
    {
        if (!double.IsFinite(volume)) throw new ArgumentOutOfRangeException(nameof(volume));
        SetDouble("volume", Math.Clamp(volume, 0, 100));
    }

    public void SetMuted(bool muted)
    {
        Update(s => s with { UserMuted = muted });
        lock (_gate) SetFlag("mute", muted || _options.AutoMuteHighSpeed && _requestedSpeed > 8);
    }

    private ulong Issue(string label)
    {
        ulong id = (ulong)Interlocked.Increment(ref _requestId);
        _operations[id] = (State.SessionId, label);
        return id;
    }
    private void CheckRequest(ulong request, int result)
    {
        if (result >= 0) return;
        _operations.TryRemove(request, out _);
        MpvNative.Check(result);
    }
    private unsafe void SetString(string name, string value, string label)
    {
        nint text = Marshal.StringToCoTaskMemUTF8(value);
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                ulong request = Issue(label);
                CheckRequest(request, MpvNative.mpv_set_property_async(_handle, request, name, MpvNative.String, &text));
            }
        }
        finally { Marshal.FreeCoTaskMem(text); }
    }
    private unsafe void SetDouble(string name, double value, string? label = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ulong request = label is null ? 0 : Issue(label);
            CheckRequest(request, MpvNative.mpv_set_property_async(_handle, request, name, MpvNative.Double, &value));
        }
    }

    private unsafe void SetFlag(string name, bool value, string? label = null)
    {
        int flag = value ? 1 : 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            ulong request = label is null ? 0 : Issue(label);
            CheckRequest(request, MpvNative.mpv_set_property_async(_handle, request, name, MpvNative.Flag, &flag));
        }
    }

    private void Command(params string[] args) => CommandWithRequest(0, args);
    private void CommandWithRequest(ulong request, params string[] args)
    {
        // 使用参数数组传递命令，避免中文、空格路径及命令行特殊字符引发转义或注入问题。
        var strings = new nint[args.Length + 1];
        nint argv = 0;
        try
        {
            for (int i = 0; i < args.Length; i++) strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            argv = Marshal.AllocHGlobal(strings.Length * nint.Size);
            Marshal.Copy(strings, 0, argv, strings.Length);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_stopping, this);
                CheckRequest(request, MpvNative.mpv_command_async(_handle, request, argv));
            }
        }
        finally
        {
            foreach (var value in strings) if (value != 0) Marshal.FreeCoTaskMem(value);
            if (argv != 0) Marshal.FreeHGlobal(argv);
        }
    }

    private void EventLoop()
    {
        try
        {
            while (!_stopping)
            {
                var ev = Marshal.PtrToStructure<MpvNative.Event>(MpvNative.mpv_wait_event(_handle, 0.1));
                if (_stopping) break;
                switch (ev.Id)
                {
                    case 0: break;
                    case 1: return;
                    case 2:
                        // 原生日志结构依次包含来源、级别名称、正文及级别数值，此处读取正文指针。
                        if (ev.Data != 0)
                        {
                            string? message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(ev.Data, 2 * nint.Size))?.Trim();
                            if (!string.IsNullOrWhiteSpace(message)) _log.Warning("mpv: {Message}", message);
                            if (message?.Contains("desynchronisation", StringComparison.OrdinalIgnoreCase) == true)
                                Update(s => s with { Warning = "高倍速下设备可能无法及时解码，允许丢帧并优先推进播放进度。" });
                        }
                        break;
                    case 4 or 5:
                        if (_operations.TryRemove(ev.UserData, out var operation))
                        {
                            if (ev.Error < 0 && operation.Session == State.SessionId)
                            {
                                _log.Warning("{Operation}失败：{Error}，会话 {Session} 继续播放", operation.Label, ev.Error, operation.Session);
                                Update(s => s with { OperationWarning = $"{operation.Label}失败：{Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(ev.Error))}；视频继续播放。" });
                            }
                        }
                        else if (ev.Error < 0 && ev.UserData == 0)
                            Update(s => s with { Error = $"播放操作失败：{Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(ev.Error))}", IsLoading = false });
                        break;
                    case 6: Update(s => s with { IsLoading = true, Error = null }); break;
                    case 7:
                        var end = Marshal.PtrToStructure<MpvNative.EndFile>(ev.Data);
                        if (end.Reason == 4)
                            Update(s => s with
                            {
                                IsLoaded = false,
                                IsLoading = false,
                                Error = $"无法播放：{Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(end.Error))}"
                            });
                        else Update(s => s with { IsLoading = false });
                        break;
                    case 8: Update(s => s with { IsLoading = false, IsLoaded = true, Error = null }); break;
                    case 22: ReadProperty(ev.Data); break;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Playback event loop failed");
            Update(s => s with { Error = "播放事件处理失败，请重新启动播放器。", IsLoading = false });
        }
    }

    private void ReadProperty(nint data)
    {
        var p = Marshal.PtrToStructure<MpvNative.Property>(data);
        var name = Marshal.PtrToStringUTF8(p.Name);
        if (name == "track-list")
        {
            var tracks = p.Data == 0 ? Array.Empty<MediaTrack>() : MpvTrackReader.Read(p.Data);
            Update(s => s.Tracks.SequenceEqual(tracks) ? s : s with { Tracks = tracks });
            return;
        }
        if (name is "sid" or "aid")
        {
            long? id = p.Data != 0 && p.Format == MpvNative.Int64 ? Marshal.ReadInt64(p.Data) : null;
            Update(s => name == "sid" ? s with { SubtitleId = id } : s with { AudioId = id });
            return;
        }
        if (p.Data == 0) return;
        double number = p.Format == MpvNative.Double ? Marshal.PtrToStructure<double>(p.Data) : 0;
        bool flag = p.Format == MpvNative.Flag && Marshal.ReadInt32(p.Data) != 0;
        Update(s => name switch
        {
            "time-pos" => s with { Position = Math.Max(0, number) },
            "duration" => s with { Duration = Math.Max(0, number) },
            "speed" => s with { Speed = number },
            "volume" => s with { Volume = number },
            "container-fps" => s with { FramesPerSecond = number },
            "sub-scale" => s with { SubtitleSize = number * 55 },
            "sub-delay" => s with { SubtitleDelay = number },
            "pause" => s with { IsPaused = flag },
            "mute" => s with { EffectiveMuted = flag },
            "sub-visibility" => s with { SubtitlesVisible = flag },
            "seeking" => s with { IsSeeking = flag },
            "eof-reached" => s with { IsEnded = flag },
            "width" => s with { Width = (int)Marshal.ReadInt64(p.Data) },
            "height" => s with { Height = (int)Marshal.ReadInt64(p.Data) },
            "hwdec-current" => s with { Decoder = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p.Data)) is { Length: > 0 } decoder && decoder != "no" ? decoder : "软件解码" },
            "video-format" => s with { VideoCodec = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p.Data)) ?? "未知" },
            "audio-codec-name" => s with { AudioCodec = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p.Data)) ?? "未知" },
            _ => s
        });
    }

    private void Update(Func<PlayerState, PlayerState> update)
    {
        PlayerState next;
        lock (_gate)
        {
            next = update(_state);
            if (next == _state) return;
            Volatile.Write(ref _state, next);
        }
        StateChanged?.Invoke(this, next);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopping) return;
            _stopping = true;
            MpvNative.mpv_wakeup(_handle);
        }
        // 销毁播放内核前，视频宿主必须已经释放渲染上下文。
        _eventTask.GetAwaiter().GetResult();
        lock (_gate)
        {
            MpvNative.mpv_terminate_destroy(_handle);
            _handle = 0;
        }
        _log.Information("libmpv session disposed");
    }
}
