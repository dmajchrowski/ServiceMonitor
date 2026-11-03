using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMonitor.Core;
using ServiceMonitor.Core.Hosting;
using ServiceMonitor.Core.Security;

namespace ServiceMonitor.Service;

/// <summary>Generic host for the background monitor when Windows starts us as a service.</summary>
public static class MonitorHost
{
    public static int Run(string[] args)
    {
        SecureFolder.Ensure(AppInfo.DataDirectory);
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddWindowsService(options => options.ServiceName = AppInfo.ServiceName);
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new FileLoggerProvider(AppInfo.LogsDirectory));
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddHostedService<MonitorWorker>();

        builder.Build().Run();
        return 0;
    }
}
