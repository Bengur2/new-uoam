using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace NewUOAM.App;

/// <summary>A small dialog for the menu bar (Mapa > Nastavení, Online > Připojit k mapě) that shows
/// a panel MainWindow owns. The panel is moved out of MainWindow's hidden DialogPanelStore while
/// the dialog is open and back when it closes, so its controls - and all MainWindow code using
/// them (settings load/save, connect/disconnect state) - keep working with the dialog closed.</summary>
internal sealed class HostedPanelWindow : Window
{
    public HostedPanelWindow(string title, FrameworkElement panel, Panel store, Window owner)
    {
        Title = title;
        Owner = owner; // stays above the map, which may be always on top
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Width = 340;
        SizeToContent = SizeToContent.Height;
        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x2D, 0x30));

        store.Children.Remove(panel);
        Content = panel;
        Closed += (_, _) =>
        {
            Content = null;
            store.Children.Add(panel);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
    }
}
