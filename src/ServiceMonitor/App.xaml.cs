using System.Windows;
using System.Windows.Threading;

namespace ServiceMonitor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        if (ThemeMode != ThemeMode.System) ThemeMode = ThemeMode.System; // Mica backdrop + dark title bar
        base.OnStartup(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, Core.AppInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
