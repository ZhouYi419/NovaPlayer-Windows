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
    private bool _dragging;
    private ContextMenu? _contextMenu;
    private Rect _lastBounds;
    public PlaybackOverlay(MainWindow main, PlayerViewModel viewModel, FrameworkElement target)
    {
        _main = main; _viewModel = viewModel; _target = target;
        InitializeComponent(); DataContext = viewModel;
        SizeChanged += (_, _) => UpdatePresentation();
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
        if (new WindowInteropHelper(this).Handle == 0) return;
        if (!_main.ShouldShowPlayerOverlay || _main.WindowState == WindowState.Minimized)
        { CloseMenu(); if (IsVisible) Hide(); return; }
        if (PresentationSource.FromVisual(_target) is null || _target.ActualWidth < 1 || _target.ActualHeight < 1) return;
        if (!IsVisible) Show();
        UpdatePresentation();
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
        if (!_viewModel.IsFullscreen && !_viewModel.IsMiniPlayer) { ShowControls(); return; }
        if (!_viewModel.State.IsLoaded || SpeedPopup.IsOpen || _contextMenu?.IsOpen == true || _dragging || BottomChrome.IsMouseOver) return;
        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromSeconds(3)) return;
        TopChrome.Opacity = BottomChrome.Opacity = 0;
        BottomChrome.IsHitTestVisible = false; Cursor = Cursors.None;
    }
    private void Video_MouseMove(object sender, MouseEventArgs e) => ShowControls();
    private void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsMiniPlayer && e.ClickCount == 1 && e.OriginalSource is DependencyObject element && !IsControl(element)) _main.DragMove();
    }
    private void UpdatePresentation()
    {
        bool mini = _viewModel.IsMiniPlayer;
        TopTools.Visibility = NormalControls.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MetadataLabel.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        PlayerStatus.Visibility = mini || _viewModel.HasResumePrompt ? Visibility.Collapsed : Visibility.Visible;
        VolumeSlider.Visibility = ActualWidth < 640 ? Visibility.Collapsed : Visibility.Visible;
        double scale = ActualHeight < 330 ? 0.75 : 1;
        // 只在缩放比例变化时更新变换，避免在布局通知中持续触发重新测量。
        if (EmptyContent.LayoutTransform is not System.Windows.Media.ScaleTransform current || current.ScaleX != scale)
            EmptyContent.LayoutTransform = new System.Windows.Media.ScaleTransform(scale, scale);
    }
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
    private void Seek_Hover(object sender, MouseEventArgs e)
    {
        if (SeekSlider.ActualWidth > 0) SeekSlider.ToolTip = PlayerViewModel.FormatTime(Math.Clamp(e.GetPosition(SeekSlider).X / SeekSlider.ActualWidth, 0, 1) * _viewModel.State.Duration);
    }
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
    public bool CloseMenu()
    {
        bool wasOpen = SpeedPopup.IsOpen || _contextMenu?.IsOpen == true;
        SpeedPopup.IsOpen = false;
        if (_contextMenu is not null) _contextMenu.IsOpen = false;
        return wasOpen;
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = MoreButton };
        menu.Items.Add(new MenuItem { Header = "打开文件所在位置", Command = _viewModel.RevealCommand });
        menu.Items.Add(new MenuItem { Header = "视频信息", Command = _viewModel.ShowInformationCommand });
        menu.Items.Add(new MenuItem { Header = "从头播放", Command = _viewModel.RestartCommand });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "从播放列表移除", Command = _viewModel.RemoveVideoCommand, CommandParameter = _viewModel.CurrentVideo });
        _contextMenu = menu; menu.IsOpen = true;
    }
    private void Subtitle_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = SubtitleButton };
        menu.Items.Add(new MenuItem { Header = "显示字幕", IsCheckable = true, IsChecked = _viewModel.State.SubtitlesVisible, Command = _viewModel.ToggleSubtitlesCommand });
        menu.Items.Add(new MenuItem { Header = "加载外挂字幕…", Command = _viewModel.LoadSubtitleCommand });
        _contextMenu = menu; menu.IsOpen = true;
    }
    private void CloseMini_Click(object sender, RoutedEventArgs e) => _main.ToggleMiniPlayer();
    internal bool VerifyOverlayHitTest()
    {
        var point = SpeedButton.PointToScreen(new Point(SpeedButton.ActualWidth / 2, SpeedButton.ActualHeight / 2));
        var hwnd = new WindowInteropHelper(this).Handle;
        return IsWindowVisible(hwnd) && GetWindow(hwnd, 4) == new WindowInteropHelper(_main).Handle &&
            WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)) == hwnd;
    }
    internal object HitTestDiagnostics()
    {
        var point = SpeedButton.PointToScreen(new Point(SpeedButton.ActualWidth / 2, SpeedButton.ActualHeight / 2));
        var hwnd = new WindowInteropHelper(this).Handle;
        return new
        {
            IsVisible,
            nativeVisible = IsWindowVisible(hwnd),
            hwnd = hwnd.ToInt64(),
            owner = GetWindow(hwnd, 4).ToInt64(),
            expectedOwner = new WindowInteropHelper(_main).Handle.ToInt64(),
            hit = WindowFromPoint(new NativePoint((int)point.X, (int)point.Y)).ToInt64(),
            point,
            state = _main.WindowState.ToString()
        };
    }
    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
}
