using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NovaPlayer.App.ViewModels;

namespace NovaPlayer.App.Controls;

/// <summary>通过主窗口拥有的透明分层窗口覆盖原生视频子窗口，解决 WPF 控件无法直接覆盖视频的问题。</summary>
public partial class PlaybackOverlay : Window
{
    private readonly MainWindow _main;
    private readonly PlayerViewModel _viewModel;
    private readonly FrameworkElement _target;
    private readonly DispatcherTimer _timer;
    private DateTime _lastActivity = DateTime.UtcNow;
    private bool _dragging, _ownerMinimized;
    private Rect _lastBounds;
    public PlaybackOverlay(MainWindow main, PlayerViewModel viewModel, FrameworkElement target)
    {
        _main = main; _viewModel = viewModel; _target = target;
        InitializeComponent(); DataContext = viewModel;
        _target.LayoutUpdated += Target_LayoutUpdated;
        _main.LocationChanged += Owner_Changed; _main.StateChanged += Owner_Changed;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, HideIdleControls, Dispatcher);
        Closed += (_, _) =>
        {
            SpeedPopup.IsOpen = false; _timer.Stop();
            _target.LayoutUpdated -= Target_LayoutUpdated;
            _main.LocationChanged -= Owner_Changed; _main.StateChanged -= Owner_Changed;
        };
    }
    private void Target_LayoutUpdated(object? sender, EventArgs e) => SyncBounds();
    private void Owner_Changed(object? sender, EventArgs e) => SyncBounds();
    public void SyncBounds()
    {
        if (!IsLoaded || PresentationSource.FromVisual(_target) is null) return;
        if (_main.WindowState == WindowState.Minimized) { _ownerMinimized = true; Hide(); return; }
        if (_ownerMinimized) { _ownerMinimized = false; Show(); }
        var origin = _target.PointToScreen(new Point());
        var end = _target.PointToScreen(new Point(_target.ActualWidth, _target.ActualHeight));
        var bounds = new Rect(origin, end);
        if (_lastBounds == bounds) return;
        _lastBounds = bounds;
        SetWindowPos(new WindowInteropHelper(this).Handle, 0, (int)Math.Round(origin.X), (int)Math.Round(origin.Y),
            Math.Max(1, (int)Math.Round(bounds.Width)), Math.Max(1, (int)Math.Round(bounds.Height)), 0x0010 | 0x0004);
    }
    public void ShowControls()
    {
        _lastActivity = DateTime.UtcNow;
        TopChrome.Opacity = BottomChrome.Opacity = 1;
        BottomChrome.IsHitTestVisible = true; Cursor = Cursors.Arrow;
    }
    private void HideIdleControls(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullscreen) { ShowControls(); return; }
        if (!_viewModel.State.IsLoaded || SpeedPopup.IsOpen || _dragging || BottomChrome.IsMouseOver) return;
        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromSeconds(3)) return;
        TopChrome.Opacity = BottomChrome.Opacity = 0;
        BottomChrome.IsHitTestVisible = false; Cursor = Cursors.None;
    }
    private void Video_MouseMove(object sender, MouseEventArgs e) => ShowControls();
    private void Video_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement element && IsControl(element)) return;
        _main.ToggleFullscreen();
    }
    private static bool IsControl(DependencyObject element)
    {
        for (var current = element; current != null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
            if (current is ButtonBase or Slider or TextBox) return true;
        return false;
    }
    private void Overlay_KeyDown(object sender, KeyEventArgs e) => _main.HandleKey(e);
    private void Files_Drop(object sender, DragEventArgs e) => _main.HandleDrop(e);
    private void Seek_Start(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        System.Windows.Data.BindingOperations.ClearBinding(SeekSlider, RangeBase.ValueProperty);
    }
    private void Seek_End(object sender, MouseButtonEventArgs e) => CommitSeek();
    private void Seek_LostCapture(object sender, MouseEventArgs e) => CommitSeek();
    private void CommitSeek()
    {
        if (!_dragging) return;
        _dragging = false; _viewModel.SeekTo(SeekSlider.Value);
        SeekSlider.SetBinding(RangeBase.ValueProperty, new System.Windows.Data.Binding("State.Position") { Mode = System.Windows.Data.BindingMode.OneWay });
    }
    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IsLoaded && sender is Slider slider && (slider.IsMouseCaptureWithin || slider.IsKeyboardFocusWithin))
            _viewModel.ChangeVolume(e.NewValue);
    }
    private void Speed_Click(object sender, RoutedEventArgs e)
    {
        CustomSpeed.Text = _viewModel.State.Speed.ToString("0.##", CultureInfo.CurrentCulture);
        SpeedPopup.IsOpen = !SpeedPopup.IsOpen;
    }
    private void PresetSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: double speed }) _viewModel.ChangeSpeed(speed);
        SpeedPopup.IsOpen = false;
    }
    private void CustomSpeed_Click(object sender, RoutedEventArgs e)
    {
        if (double.TryParse(CustomSpeed.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double speed) &&
            double.IsFinite(speed) && speed is >= 0.25 and <= 15)
        { _viewModel.ChangeSpeed(speed); SpeedPopup.IsOpen = false; }
        else _viewModel.Notice = "请输入 0.25～15.0 之间的有效倍速。";
    }
    private void CloseSpeed_Click(object sender, RoutedEventArgs e) => SpeedPopup.IsOpen = false;
    public bool CloseMenu() { bool wasOpen = SpeedPopup.IsOpen; SpeedPopup.IsOpen = false; return wasOpen; }
    internal bool VerifyOverlayHitTest()
    {
        var point = SpeedButton.PointToScreen(new Point(SpeedButton.ActualWidth / 2, SpeedButton.ActualHeight / 2));
        var hwnd = new WindowInteropHelper(this).Handle;
        return IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == new WindowInteropHelper(_main).Handle &&
            WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)) == hwnd;
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
}
