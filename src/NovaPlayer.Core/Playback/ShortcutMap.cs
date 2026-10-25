namespace NovaPlayer.Core.Playback;

[Flags]
public enum ShortcutModifiers { None = 0, Alt = 1, Control = 2, Shift = 4 }
public enum ShortcutAction
{
    TogglePause, Backward, Forward, VolumeUp, VolumeDown, Fullscreen, ExitView,
    ToggleMute, Slower, Faster, NormalSpeed, OpenFile, OpenFolder, Next, Previous, MiniPlayer, ToggleSubtitles
}

public sealed record ShortcutGesture(string Key, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    public string Display => string.Join(" + ", new[]
    {
        Modifiers.HasFlag(ShortcutModifiers.Control) ? "Ctrl" : null,
        Modifiers.HasFlag(ShortcutModifiers.Alt) ? "Alt" : null,
        Modifiers.HasFlag(ShortcutModifiers.Shift) ? "Shift" : null,
        Key switch { "Left" => "←", "Right" => "→", "Up" => "↑", "Down" => "↓", "Escape" => "Esc", "Return" => "Enter", "OemOpenBrackets" => "[", "OemCloseBrackets" => "]", "OemPipe" => "\\", _ => Key }
    }.Where(v => v is not null));
    public bool IsUsable => !string.IsNullOrWhiteSpace(Key) && !new[] { "None", "System", "ImeProcessed", "DeadCharProcessed", "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftShift", "RightShift", "LWin", "RWin" }.Contains(Key) &&
        (Modifiers & ~(ShortcutModifiers.Alt | ShortcutModifiers.Control | ShortcutModifiers.Shift)) == 0 &&
        !(Modifiers.HasFlag(ShortcutModifiers.Alt) && Key is "F4" or "Tab") &&
        !(Modifiers.HasFlag(ShortcutModifiers.Control | ShortcutModifiers.Alt) && Key == "Delete");
}

public sealed record ShortcutDefinition(ShortcutAction Action, string Name, ShortcutGesture Default, bool Repeatable = false);

/// <summary>统一校验与分发映射；冲突时保留原绑定，批量读取时先校验全部配置再替换。</summary>
public sealed class ShortcutMap
{
    public static IReadOnlyList<ShortcutDefinition> Definitions { get; } = Array.AsReadOnly(new[]
    {
        new ShortcutDefinition(ShortcutAction.TogglePause, "播放 / 暂停", new("Space")),
        new ShortcutDefinition(ShortcutAction.Backward, "快退", new("Left"), true),
        new ShortcutDefinition(ShortcutAction.Forward, "快进", new("Right"), true),
        new ShortcutDefinition(ShortcutAction.VolumeUp, "增加音量", new("Up"), true),
        new ShortcutDefinition(ShortcutAction.VolumeDown, "降低音量", new("Down"), true),
        new ShortcutDefinition(ShortcutAction.Fullscreen, "切换全屏", new("F")),
        new ShortcutDefinition(ShortcutAction.ExitView, "退出全屏 / 小窗 / 菜单", new("Escape")),
        new ShortcutDefinition(ShortcutAction.ToggleMute, "静音", new("M")),
        new ShortcutDefinition(ShortcutAction.Slower, "降低 0.25 倍速", new("OemOpenBrackets"), true),
        new ShortcutDefinition(ShortcutAction.Faster, "增加 0.25 倍速", new("OemCloseBrackets"), true),
        new ShortcutDefinition(ShortcutAction.NormalSpeed, "恢复 1.0 倍速", new("OemPipe")),
        new ShortcutDefinition(ShortcutAction.OpenFile, "打开视频", new("O", ShortcutModifiers.Control)),
        new ShortcutDefinition(ShortcutAction.OpenFolder, "打开文件夹", new("O", ShortcutModifiers.Control | ShortcutModifiers.Shift)),
        new ShortcutDefinition(ShortcutAction.Next, "下一条视频", new("Right", ShortcutModifiers.Control)),
        new ShortcutDefinition(ShortcutAction.Previous, "上一条视频", new("Left", ShortcutModifiers.Control)),
        new ShortcutDefinition(ShortcutAction.MiniPlayer, "切换画中画", new("P", ShortcutModifiers.Control)),
        new ShortcutDefinition(ShortcutAction.ToggleSubtitles, "显示 / 隐藏字幕", new("S", ShortcutModifiers.Control))
    });
    private Dictionary<ShortcutAction, ShortcutGesture?> _bindings = Definitions.ToDictionary(d => d.Action, d => (ShortcutGesture?)d.Default);
    public IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> Bindings => _bindings;
    public bool TryAssign(ShortcutAction action, ShortcutGesture? gesture, out string? error)
    {
        var candidate = new Dictionary<ShortcutAction, ShortcutGesture?>(_bindings) { [action] = gesture };
        return TryReplace(candidate, out error);
    }
    public bool TryReplace(IReadOnlyDictionary<ShortcutAction, ShortcutGesture?> entries, out string? error)
    {
        error = null;
        var candidate = Definitions.ToDictionary(d => d.Action, d => entries.TryGetValue(d.Action, out var gesture) ? gesture : d.Default);
        foreach (var entry in entries)
            if (!Enum.IsDefined(entry.Key)) { error = "配置包含未知操作。"; return false; }
        foreach (var entry in candidate)
        {
            if (entry.Value is null) continue;
            if (!entry.Value.IsUsable) { error = "此按键组合不可用，请选择普通按键或搭配 Ctrl / Alt / Shift。"; return false; }
            var conflict = candidate.FirstOrDefault(other => other.Key != entry.Key && other.Value == entry.Value);
            if (conflict.Value is not null)
            {
                error = $"{entry.Value.Display} 已用于“{Definitions.Single(d => d.Action == conflict.Key).Name}”，请先修改或清除此绑定。";
                return false;
            }
        }
        _bindings = candidate;
        return true;
    }
    public ShortcutAction? Resolve(ShortcutGesture gesture, bool repeated = false)
    {
        foreach (var definition in Definitions)
            if (_bindings[definition.Action] == gesture && (!repeated || definition.Repeatable)) return definition.Action;
        return null;
    }
    public void Reset() => _bindings = Definitions.ToDictionary(d => d.Action, d => (ShortcutGesture?)d.Default);
}
