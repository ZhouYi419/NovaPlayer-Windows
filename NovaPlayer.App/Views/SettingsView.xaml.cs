using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NovaPlayer.App.Services;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Core.Playback;

namespace NovaPlayer.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
    public void ShowShortcuts()
    {
        SettingsScroll.UpdateLayout();
        if (SettingsScroll.Content is UIElement content)
            SettingsScroll.ScrollToVerticalOffset(Math.Max(0, ShortcutsHeading.TranslatePoint(new Point(), content).Y - 12));
    }
    private void CaptureShortcut_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        if (e.IsRepeat || sender is not TextBox { DataContext: ShortcutItem item } || DataContext is not PlayerViewModel vm) return;
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift) return;
        vm.Shortcuts.Assign(item.Definition.Action, new(key.ToString(), (ShortcutModifiers)(int)Keyboard.Modifiers));
    }
    private void ClearShortcut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ShortcutItem item } && DataContext is PlayerViewModel vm) vm.Shortcuts.Assign(item.Definition.Action, null);
    }
    private void ResetShortcuts_Click(object sender, RoutedEventArgs e) { if (DataContext is PlayerViewModel vm) vm.Shortcuts.Reset(); }
}
