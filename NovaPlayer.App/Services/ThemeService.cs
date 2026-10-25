using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace NovaPlayer.App.Services;

public sealed class ThemeService : IDisposable
{
    private readonly PreferencesService _preferences;
    public ThemeService(PreferencesService preferences)
    {
        _preferences = preferences;
        _preferences.PropertyChanged += Changed;
        SystemEvents.UserPreferenceChanged += SystemChanged;
        Apply();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(PreferencesService.Theme)) Apply(); }
    private void SystemChanged(object sender, UserPreferenceChangedEventArgs e)
    { if (_preferences.Theme == "跟随系统") Application.Current.Dispatcher.BeginInvoke(Apply); }
    private void Apply()
    {
        bool light = _preferences.Theme == "浅色";
        if (_preferences.Theme == "跟随系统")
            light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0) is 1;
        string[] keys = ["AppBackground", "NavBackground", "PanelBackground", "TextPrimary", "TextSecondary", "TextMuted", "LineBrush", "HoverBrush", "SelectionBrush", "InputBrush"];
        string[] values = light
            ? ["#F1F4FA", "#E9EEF7", "#FFFFFF", "#182236", "#53637B", "#687A92", "#DCE3EF", "#E3EBFA", "#DAE7FF", "#E9EEF6"]
            : ["#121722", "#182031", "#1A2232", "#F5F7FB", "#A7B2C6", "#748198", "#2B3548", "#293851", "#223B65", "#161E2D"];
        for (int i = 0; i < keys.Length; i++)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(values[i]));
            brush.Freeze(); Application.Current.Resources[keys[i]] = brush;
        }
    }
    public void Dispose()
    { _preferences.PropertyChanged -= Changed; SystemEvents.UserPreferenceChanged -= SystemChanged; }
}
