using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ServiceMonitor.Views;

public partial class OverviewView : UserControl
{
    public OverviewView() => InitializeComponent();

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = PauseButton.ContextMenu!;
        menu.PlacementTarget = PauseButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }
}
