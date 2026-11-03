using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ServiceMonitor.Core.Hosting;

/// <summary>Daily log files in %ProgramData%\ServiceMonitor\logs, kept for 14 days.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _directory;
    private readonly int _retainDays;
    private readonly Lock _lock = new();
    private string _currentDay = "";

    public FileLoggerProvider(string directory, int retainDays = 14)
    {
        _directory = directory;
        _retainDays = retainDays;
        Directory.CreateDirectory(directory);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = DateTimeOffset.Now;
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(' ').Append(level switch
            {
                LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
                LogLevel.Warning => "WRN", LogLevel.Error => "ERR", _ => "CRT",
            })
            .Append(' ').Append(category.Split('.').Last()).Append(": ").Append(message);
        if (exception is not null) line.AppendLine().Append(exception);
        line.AppendLine();

        lock (_lock)
        {
            var day = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (day != _currentDay)
            {
                _currentDay = day;
                CleanUp();
            }
            try
            {
                File.AppendAllText(Path.Combine(_directory, $"monitor-{day}.log"), line.ToString());
            }
            catch (IOException)
            {
            }
        }
    }

    private void CleanUp()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "monitor-*.log"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-_retainDays)) File.Delete(file);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            provider.Write(logLevel, category, formatter(state, exception), exception);
        }
    }
}
