using System.Windows;
using System.Windows.Controls;
using NovaPlayer.App.ViewModels;
using System.ComponentModel;

namespace NovaPlayer.App.Controls;

public partial class VideoThumbnail : UserControl
{
    private CancellationTokenSource? _visible;
    private VideoItem? _observed;
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(VideoItem), typeof(VideoThumbnail), new PropertyMetadata(null, ItemChanged));
    public VideoItem? Item { get => (VideoItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public VideoThumbnail()
    {
        InitializeComponent(); Loaded += (_, _) => Attach();
        Unloaded += (_, _) => { if (_observed is not null) _observed.PropertyChanged -= ItemPropertyChanged; _observed = null; _visible?.Cancel(); _visible?.Dispose(); _visible = null; };
    }
    private static void ItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) { if (d is VideoThumbnail control && control.IsLoaded) control.Attach(); }
    private void Attach()
    {
        if (_observed is not null) _observed.PropertyChanged -= ItemPropertyChanged;
        _observed = Item;
        if (_observed is not null) _observed.PropertyChanged += ItemPropertyChanged;
        Start();
    }
    private void ItemPropertyChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(VideoItem.Thumbnail) && Item?.Thumbnail is null) Start(); }
    private async void Start()
    {
        _visible?.Cancel(); _visible?.Dispose(); _visible = new CancellationTokenSource();
        if (Item?.ThumbnailLoader is not { } load || Item.Thumbnail is not null) return;
        try { await load(Item, _visible.Token); } catch (OperationCanceledException) { }
    }
}
