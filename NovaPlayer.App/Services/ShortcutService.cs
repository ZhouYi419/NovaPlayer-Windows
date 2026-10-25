using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using NovaPlayer.Core.Playback;
using Serilog;

namespace NovaPlayer.App.Services;

public sealed partial class ShortcutItem(ShortcutDefinition definition) : ObservableObject
{
    public ShortcutDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    [ObservableProperty] private string _display = definition.Default.Display;
}

public sealed partial class ShortcutService : ObservableObject
{
    private readonly string _path;
    private readonly ShortcutMap _map = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _preserveInvalid, _edited;
    [ObservableProperty] private string? _message;
    public ObservableCollection<ShortcutItem> Items { get; } = new(ShortcutMap.Definitions.Select(d => new ShortcutItem(d)));
    public string Summary => string.Join("    ", Items.Take(6).Select(i => $"{i.Display}  {i.Name}"));
    private ShortcutService(string path) { _path = path; }
    internal string StoragePath => _path;
    public static async Task<ShortcutService> LoadAsync(string path)
    {
        var service = new ShortcutService(path);
        try
        {
            if (File.Exists(path))
            {
                var entries = JsonSerializer.Deserialize<Dictionary<ShortcutAction, ShortcutGesture?>>(await File.ReadAllTextAsync(path));
                if (entries is null || entries.Values.Any(g => g is not null && !IsKeyValid(g.Key)) ||
                    !service._map.TryReplace(entries.ToDictionary(e => e.Key, e => e.Value is null ? null : Normalize(e.Value)), out _))
                { service._preserveInvalid = true; service.Message = "快捷键配置无效，已使用默认值；原配置文件已保留。"; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Log.Warning(ex, "读取快捷键失败"); service._preserveInvalid = true; service.Message = "无法读取快捷键，已使用默认配置。"; }
        service.Refresh();
        return service;
    }
    private static bool IsKeyValid(string value) => Enum.TryParse<Key>(value, out var key) && Enum.IsDefined(key);
    private static ShortcutGesture Normalize(ShortcutGesture gesture) => gesture with { Key = KeyName(Enum.Parse<Key>(gesture.Key)) };
    private static string KeyName(Key key) => key switch
    {
        Key.OemOpenBrackets => "OemOpenBrackets", Key.OemCloseBrackets => "OemCloseBrackets",
        Key.OemPipe => "OemPipe", Key.Return => "Return", _ => key.ToString()
    };
    public bool Assign(ShortcutAction action, ShortcutGesture? gesture)
    {
        if (gesture is not null && !IsKeyValid(gesture.Key)) { Message = "无法识别此按键。"; return false; }
        if (gesture is not null) gesture = Normalize(gesture);
        if (!_map.TryAssign(action, gesture, out var error)) { Message = error; return false; }
        _edited = true; Message = "快捷键已更新并立即生效。"; Refresh(); _ = SaveAsync(); return true;
    }
    public void Reset() { _edited = true; _map.Reset(); Refresh(); Message = "已恢复全部默认快捷键。"; _ = SaveAsync(); }
    public ShortcutAction? Resolve(Key key, ModifierKeys modifiers, bool repeated)
    {
        if ((modifiers & ModifierKeys.Windows) != 0) return null;
        return _map.Resolve(new(KeyName(key), (ShortcutModifiers)(int)modifiers), repeated);
    }
    private void Refresh()
    {
        foreach (var item in Items) item.Display = _map.Bindings[item.Definition.Action]?.Display ?? "未绑定";
        OnPropertyChanged(nameof(Summary));
    }
    public async Task SaveAsync()
    {
        if (_preserveInvalid && !_edited) return;
        string json = JsonSerializer.Serialize(_map.Bindings, new JsonSerializerOptions { WriteIndented = true });
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (_preserveInvalid && File.Exists(_path))
            {
                File.Copy(_path, _path + $".invalid-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak");
                _preserveInvalid = false;
            }
            await File.WriteAllTextAsync(_path + ".tmp", json).ConfigureAwait(false);
            File.Move(_path + ".tmp", _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning(ex, "保存快捷键失败"); await System.Windows.Application.Current.Dispatcher.InvokeAsync(() => Message = "快捷键暂时无法保存，请检查目录权限。"); }
        finally { _writeGate.Release(); }
    }
}
