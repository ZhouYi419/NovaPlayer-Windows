using System.Windows;
using System.Windows.Controls;
using NovaPlayer.App.ViewModels;
namespace NovaPlayer.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
    private void HistoryMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: VideoItem item } button || DataContext is not PlayerViewModel vm) return;
        var menu = new ContextMenu { PlacementTarget = button };
        menu.Items.Add(new MenuItem { Header = "从头播放", Command = vm.PlayFromStartCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "打开文件所在位置", Command = vm.RevealCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "重新定位文件", Command = vm.RequestRelocateCommand, CommandParameter = item });
        menu.Items.Add(new MenuItem { Header = "删除播放记录", Command = vm.DeleteHistoryCommand, CommandParameter = item });
        menu.IsOpen = true;
    }
}
