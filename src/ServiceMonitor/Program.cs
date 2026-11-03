using System.Runtime.InteropServices;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Hosting;
using ServiceMonitor.Service;

namespace ServiceMonitor;

/// <summary>
/// One executable, three roles:
///   ServiceMonitor.exe                 → configuration app (WPF)
///   ServiceMonitor.exe --service       → background monitor (started by Windows as the ServiceMonitor service)
///   ServiceMonitor.exe --install | --uninstall [--remove-data] | --status → for deployment scripts
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Has(args, "--service")) return MonitorHost.Run(args);
        if (Has(args, "--install") || Has(args, "--uninstall") || Has(args, "--status")) return RunCommandLine(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static int RunCommandLine(string[] args)
    {
        AttachConsole(-1); // print to the console that launched us, if any
        try
        {
            if (Has(args, "--install"))
            {
                MonitorServiceInstaller.InstallOrUpdate(new SyncProgress(Console.WriteLine));
                Console.WriteLine($"{AppInfo.ProductName} installed and running.");
            }
            else if (Has(args, "--uninstall"))
            {
                MonitorServiceInstaller.Uninstall(Has(args, "--remove-data"), new SyncProgress(Console.WriteLine));
                Console.WriteLine($"{AppInfo.ProductName} removed.");
            }
            else
            {
                Console.WriteLine($"{MonitorServiceInstaller.InstalledServiceName}: {MonitorServiceInstaller.GetState()}");
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static bool Has(string[] args, string flag) => args.Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase)
                                                                      || a.Equals("/" + flag.TrimStart('-'), StringComparison.OrdinalIgnoreCase));

    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
}
