using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using Serilog;

namespace NovaPlayer.App.Services;

public partial class PreferencesService : ObservableObject
{
    private readonly string _path;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private bool _initializing;
    [ObservableProperty] private bool _autoResume = true;
    [ObservableProperty] private double _defaultSpeed = 1;
    [ObservableProperty] private bool _playlistExpanded = true;
    [ObservableProperty] private bool _informationExpanded = true;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private string _theme = "深色";
    public event EventHandler<string>? SaveFailed;
    internal string StoragePath => _path;

    private PreferencesService(string path) { _path = path; }
    public static async Task<PreferencesService> LoadAsync(string path)
    {
        var service = new PreferencesService(path) { _initializing = true };
        try
        {
            if (File.Exists(path))
            {
                await using var input = File.OpenRead(path);
                if (await JsonSerializer.DeserializeAsync<PreferenceData>(input) is not { } data) { service._initializing = false; return service; }
                service.AutoResume = data.AutoResume;
                service.DefaultSpeed = double.IsFinite(data.DefaultSpeed) && data.DefaultSpeed is >= 0.25 and <= 15 ? data.DefaultSpeed : 1;
                service.PlaylistExpanded = data.PlaylistExpanded;
                service.InformationExpanded = data.InformationExpanded;
                service.AlwaysOnTop = data.AlwaysOnTop;
                service.Theme = data.Theme is "浅色" or "跟随系统" ? data.Theme : "深色";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { Log.Warning(ex, "读取界面设置失败，保留原文件并使用默认设置"); }
        service._initializing = false;
        return service;
    }
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_initializing) _ = SaveAsync();
    }
    public async Task SaveAsync()
    {
        var data = new PreferenceData(AutoResume, DefaultSpeed, PlaylistExpanded, InformationExpanded, AlwaysOnTop, Theme);
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(data)).ConfigureAwait(false);
            File.Move(temporary, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warning(ex, "保存界面设置失败"); SaveFailed?.Invoke(this, "设置暂时无法保存，请检查配置目录权限。"); }
        finally { _writeGate.Release(); }
    }
    private sealed record PreferenceData(bool AutoResume, double DefaultSpeed, bool PlaylistExpanded, bool InformationExpanded, bool AlwaysOnTop, string Theme);
}
