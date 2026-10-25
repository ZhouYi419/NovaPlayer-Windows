using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using NovaPlayer.App.ViewModels;
using NovaPlayer.Core.Playback;
using System.ComponentModel;

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
    private long _menuSession;
    public PlaybackOverlay(MainWindow main, PlayerViewModel viewModel, FrameworkElement target)
    {
        _main = main; _viewModel = viewModel; _target = target;
        InitializeComponent(); DataContext = viewModel;
        _menuSession = viewModel.State.SessionId;
        viewModel.PropertyChanged += PlaybackChanged;
        SizeChanged += (_, _) => UpdatePresentation();
        _target.LayoutUpdated += Target_LayoutUpdated;
        _main.LocationChanged += Owner_Changed; _main.StateChanged += Owner_Changed;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, HideIdleControls, Dispatcher);
        Closed += (_, _) =>
        {
            SpeedPopup.IsOpen = false; _timer.Stop();
            _target.LayoutUpdated -= Target_LayoutUpdated;
            _main.LocationChanged -= Owner_Changed; _main.StateChanged -= Owner_Changed;
            _viewModel.PropertyChanged -= PlaybackChanged;
        };
    }
    private void PlaybackChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PlayerViewModel.State) || _menuSession == _viewModel.State.SessionId) return;
        // 切片时关闭旧菜单，避免新视频复用相同轨道编号后执行旧菜单中的选择。
        _menuSession = _viewModel.State.SessionId;
        CloseMenu(); ShowControls();
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
        TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = true; Cursor = Cursors.Arrow;
    }
    private void HideIdleControls(object? sender, EventArgs e)
    {
        if (!_viewModel.IsFullscreen && !_viewModel.IsMiniPlayer) { ShowControls(); return; }
        if (!_viewModel.State.IsLoaded || SpeedPopup.IsOpen || _contextMenu?.IsOpen == true || _dragging || BottomChrome.IsMouseOver) return;
        if (DateTime.UtcNow - _lastActivity < TimeSpan.FromSeconds(_viewModel.Preferences.ControlsHideSeconds)) return;
        TopChrome.Opacity = BottomChrome.Opacity = 0;
        TopChrome.IsHitTestVisible = BottomChrome.IsHitTestVisible = false; Cursor = Cursors.None;
    }
    private void Video_MouseMove(object sender, MouseEventArgs e) => ShowControls();
    private void Video_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.IsMiniPlayer && e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed && e.OriginalSource is DependencyObject element && !IsControl(element))
        {
            try { _main.DragMove(); } catch (InvalidOperationException) { }
        }
    }
    private void UpdatePresentation()
    {
        bool mini = _viewModel.IsMiniPlayer;
        TopTools.Visibility = NormalControls.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        MetadataLabel.Visibility = mini ? Visibility.Collapsed : Visibility.Visible;
        PlayerStatus.Visibility = mini || _viewModel.HasResumePrompt ? Visibility.Collapsed : Visibility.Visible;
        ResumeBanner.Visibility = !mini && _viewModel.HasResumePrompt ? Visibility.Visible : Visibility.Collapsed;
        TopChrome.Height = mini ? 52 : 110;
        BottomChrome.Height = mini ? 92 : 152;
        OverlayTitle.FontSize = mini ? 12 : 17;
        TopContent.Margin = mini ? new Thickness(10, 8, 8, 0) : new Thickness(20, 18, 18, 0);
        BottomContent.Margin = mini ? new Thickness(10, 0, 10, 6) : new Thickness(20, 0, 20, 16);
        TimeLabel.Width = mini ? 106 : 152;
        TimeLabel.FontSize = mini ? 10 : 12;
        double miniScale = ActualWidth < 340 ? .85 : 1;
        if (MiniControls.LayoutTransform is not System.Windows.Media.ScaleTransform miniTransform || miniTransform.ScaleX != miniScale)
            MiniControls.LayoutTransform = new System.Windows.Media.ScaleTransform(miniScale, miniScale);
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
            if (current is ButtonBase or Slider or TextBox or Thumb) return true;
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
        SpeedPopup.PlacementTarget = sender as UIElement ?? SpeedButton;
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
    internal ContextMenu BuildMoreMenu(Button target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        menu.Items.Add(new MenuItem { Header = "打开文件所在位置", Command = _viewModel.RevealCommand });
        menu.Items.Add(new MenuItem { Header = "视频信息", Command = _viewModel.ShowInformationCommand });
        menu.Items.Add(new MenuItem { Header = "从头播放", Command = _viewModel.RestartCommand });
        var aspect = new MenuItem { Header = "画面比例" };
        foreach (string ratio in _viewModel.AspectRatios)
            aspect.Items.Add(new MenuItem { Header = ratio, IsCheckable = true, IsChecked = _viewModel.State.AspectRatio == ratio, Command = _viewModel.SetAspectRatioCommand, CommandParameter = ratio });
        menu.Items.Add(aspect);
        var audio = _viewModel.AudioTracks;
        if (audio.Count > 1)
        {
            var tracks = new MenuItem { Header = "音轨选择" };
            foreach (var track in audio)
                tracks.Items.Add(new MenuItem { Header = track.DisplayName, IsCheckable = true, IsChecked = track.Id == _viewModel.State.AudioId, Command = _viewModel.SelectAudioCommand, CommandParameter = track });
            menu.Items.Add(tracks);
        }
        else menu.Items.Add(new MenuItem { Header = audio.Count == 0 ? "未检测到音轨" : audio[0].DisplayName, IsEnabled = false });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "从播放列表移除", Command = _viewModel.RemoveVideoCommand, CommandParameter = _viewModel.CurrentVideo });
        return menu;
    }
    private void More_Click(object sender, RoutedEventArgs e) => OpenContext(BuildMoreMenu((Button)sender));
    internal void OpenContext(ContextMenu menu) { CloseMenu(); ShowControls(); _contextMenu = menu; menu.IsOpen = true; }
    internal bool ControlsVisible => TopChrome.Opacity > 0;
    internal ContextMenu BuildSubtitleMenu(Button target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        var tracks = _viewModel.SubtitleTracks;
        menu.Items.Add(new MenuItem { Header = "显示字幕", IsCheckable = true, IsEnabled = tracks.Count > 0, IsChecked = _viewModel.State.SubtitlesVisible && tracks.Count > 0, Command = _viewModel.ToggleSubtitlesCommand });
        if (tracks.Count == 0) menu.Items.Add(new MenuItem { Header = "未检测到字幕", IsEnabled = false });
        foreach (var track in tracks)
            menu.Items.Add(new MenuItem { Header = track.DisplayName, IsCheckable = true, IsChecked = _viewModel.State.SubtitleId == track.Id, Command = _viewModel.SelectSubtitleCommand, CommandParameter = track });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "加载外挂字幕…", Command = _viewModel.LoadSubtitleCommand });
        var size = new MenuItem { Header = $"字幕字号：{_viewModel.State.SubtitleSize:0}" };
        foreach (double value in new[] { 20d, 28, 36, 44, 55, 64, 72, 90 })
        {
            var choice = new MenuItem { Header = value.ToString("0"), IsCheckable = true, IsChecked = Math.Abs(_viewModel.State.SubtitleSize - value) < .1 };
            choice.Click += (_, _) => _viewModel.Preferences.SubtitleSize = value; size.Items.Add(choice);
        }
        menu.Items.Add(size);
        var delay = new MenuItem { Header = $"字幕偏移：{_viewModel.State.SubtitleDelay:+0.0;-0.0;0.0} 秒" };
        void AddDelay(string label, Func<double> value)
        {
            var choice = new MenuItem { Header = label };
            choice.Click += (_, _) => _viewModel.AdjustSubtitleDelay(value()); delay.Items.Add(choice);
        }
        delay.Items.Add(new MenuItem { Header = "正值延后，负值提前", IsEnabled = false });
        AddDelay("提前 0.1 秒", () => _viewModel.State.SubtitleDelay - .1);
        AddDelay("延后 0.1 秒", () => _viewModel.State.SubtitleDelay + .1);
        AddDelay("重置偏移", () => 0);
        menu.Items.Add(delay);
        return menu;
    }
    private void Subtitle_Click(object sender, RoutedEventArgs e) => OpenContext(BuildSubtitleMenu((Button)sender));
    private void CloseMini_Click(object sender, RoutedEventArgs e) => _main.ToggleMiniPlayer();
    private void MiniResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (sender is Thumb { Tag: string edges }) _main.ResizeMiniPlayer(edges, e.HorizontalChange, e.VerticalChange);
        e.Handled = true; ShowControls();
    }
    internal bool VerifyOverlayHitTest()
    {
        var button = _viewModel.IsMiniPlayer ? MiniSpeedButton : SpeedButton;
        var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
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
