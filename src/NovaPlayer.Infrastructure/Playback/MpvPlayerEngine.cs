using System.Globalization;
using System.Runtime.InteropServices;
using NovaPlayer.Core.Interfaces;
using NovaPlayer.Core.Playback;
using Serilog;

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

    public PlayerState State => Volatile.Read(ref _state);
    public event EventHandler<PlayerState>? StateChanged;
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
            MpvNative.Check(MpvNative.mpv_initialize(_handle));
            string[] doubles = ["time-pos", "duration", "speed", "volume"];
            string[] flags = ["pause", "mute", "seeking", "eof-reached"];
            foreach (var name in doubles) Observe(name, MpvNative.Double);
            foreach (var name in flags) Observe(name, MpvNative.Flag);
            Observe("width", MpvNative.Int64); Observe("height", MpvNative.Int64);
            Observe("hwdec-current", MpvNative.String);
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
        Update(s => new PlayerState { FilePath = path, IsLoading = true, Speed = s.Speed,
            Volume = s.Volume, UserMuted = s.UserMuted, EffectiveMuted = s.EffectiveMuted });
        Command("loadfile", path, "replace");
        SetPaused(false);
    }

    public void SetPaused(bool paused) => SetFlag("pause", paused);
    public void Seek(double seconds) => Command("seek",
        PlaybackPolicy.ClampPosition(seconds, State.Duration).ToString("R", CultureInfo.InvariantCulture), "absolute+exact");

    public void SetSpeed(double speed)
    {
        PlaybackPolicy.ValidateSpeed(speed);
        lock (_gate)
        {
            _requestedSpeed = speed;
            SetDouble("speed", speed);
            SetFlag("mute", PlaybackPolicy.EffectiveMute(State.UserMuted, speed));
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
        lock (_gate) SetFlag("mute", PlaybackPolicy.EffectiveMute(muted, _requestedSpeed));
    }

    private unsafe void SetDouble(string name, double value)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            MpvNative.Check(MpvNative.mpv_set_property_async(_handle, 0, name, MpvNative.Double, &value));
        }
    }

    private unsafe void SetFlag(string name, bool value)
    {
        int flag = value ? 1 : 0;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            MpvNative.Check(MpvNative.mpv_set_property_async(_handle, 0, name, MpvNative.Flag, &flag));
        }
    }

    private void Command(params string[] args)
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
                MpvNative.Check(MpvNative.mpv_command_async(_handle, 0, argv));
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
                    case 4 or 5 when ev.Error < 0:
                        Update(s => s with { Error = $"播放操作失败：{Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(ev.Error))}", IsLoading = false });
                        break;
                    case 6: Update(s => s with { IsLoading = true, Error = null }); break;
                    case 7:
                        var end = Marshal.PtrToStructure<MpvNative.EndFile>(ev.Data);
                        if (end.Reason == 4)
                            Update(s => s with { IsLoaded = false, IsLoading = false,
                                Error = $"无法播放：{Marshal.PtrToStringUTF8(MpvNative.mpv_error_string(end.Error))}" });
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
        if (p.Data == 0) return;
        var name = Marshal.PtrToStringUTF8(p.Name);
        double number = p.Format == MpvNative.Double ? Marshal.PtrToStructure<double>(p.Data) : 0;
        bool flag = p.Format == MpvNative.Flag && Marshal.ReadInt32(p.Data) != 0;
        Update(s => name switch
        {
            "time-pos" => s with { Position = Math.Max(0, number) },
            "duration" => s with { Duration = Math.Max(0, number) },
            "speed" => s with { Speed = number },
            "volume" => s with { Volume = number },
            "pause" => s with { IsPaused = flag },
            "mute" => s with { EffectiveMuted = flag },
            "seeking" => s with { IsSeeking = flag },
            "eof-reached" => s with { IsEnded = flag },
            "width" => s with { Width = (int)Marshal.ReadInt64(p.Data) },
            "height" => s with { Height = (int)Marshal.ReadInt64(p.Data) },
            "hwdec-current" => s with { Decoder = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(p.Data)) is { Length: > 0 } decoder ? decoder : "软件解码" },
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
