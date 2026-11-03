using System.Collections.Concurrent;
using System.Threading.Channels;
using ServiceMonitor.Core.Configuration;
using ServiceMonitor.Core.Monitoring;
using ServiceMonitor.Core.OAuth;

namespace ServiceMonitor.Core.Notifications;

/// <summary>
/// Background delivery queue. Every notification is fanned out to all enabled channels; each channel
/// retries on its own (network blips, provider throttling) so one broken channel never blocks the others.
/// </summary>
public sealed class NotificationDispatcher : INotificationSink, IAsyncDisposable
{
    private static readonly TimeSpan[] RetryDelays = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10)];

    private readonly ChannelSender _sender;
    private readonly ActivityLog _activity;
    private readonly Func<MonitorConfig> _config;
    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>();
    private readonly ConcurrentDictionary<string, ChannelStatusInfo> _channelStatus = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _attentionNoticeSent = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _concurrency = new(4);
    private readonly Task _pump;
    private int _inFlight;

    private sealed record Work(Notification Notification, string ChannelId, int Attempt);

    public NotificationDispatcher(ChannelSender sender, ActivityLog activity, Func<MonitorConfig> config)
    {
        _sender = sender;
        _activity = activity;
        _config = config;
        _pump = Task.Run(PumpAsync);
    }

    public void Enqueue(Notification notification)
    {
        var channels = _config().Channels.Where(c => c.Enabled).ToList();
        if (channels.Count == 0)
        {
            _activity.Add(ActivityLevel.Warning, $"No notification channels are set up, so nobody was told: {notification.Title}",
                notification.ServiceName, EventIds.NotificationFailed);
            return;
        }
        foreach (var channel in channels)
        {
            Interlocked.Increment(ref _inFlight);
            _queue.Writer.TryWrite(new Work(notification, channel.Id, 1));
        }
    }

    public IReadOnlyList<ChannelStatusInfo> ChannelStatus(MonitorConfig config) =>
        config.Channels.Select(c => _channelStatus.TryGetValue(c.Id, out var s)
            ? new ChannelStatusInfo { Id = c.Id, Name = c.Name, LastSuccess = s.LastSuccess, LastFailure = s.LastFailure, LastError = s.LastError }
            : new ChannelStatusInfo { Id = c.Id, Name = c.Name }).ToList();

    /// <summary>Waits until everything queued so far has been delivered or has failed (used when the monitor stops).</summary>
    public async Task FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _inFlight) > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
    }

    private async Task PumpAsync()
    {
        try
        {
            await foreach (var work in _queue.Reader.ReadAllAsync(_stopping.Token))
            {
                await _concurrency.WaitAsync(_stopping.Token);
                _ = Task.Run(async () =>
                {
                    try { await DeliverAsync(work); }
                    finally { _concurrency.Release(); }
                });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task DeliverAsync(Work work)
    {
        var config = _config();
        var channel = config.Channels.FirstOrDefault(c => c.Id == work.ChannelId && c.Enabled);
        if (channel is null)
        {
            Interlocked.Decrement(ref _inFlight);
            return; // channel deleted or disabled meanwhile
        }

        var status = _channelStatus.GetOrAdd(channel.Id, _ => new ChannelStatusInfo { Id = channel.Id });
        status.Name = channel.Name;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await _sender.SendAsync(channel, config, work.Notification, timeout.Token);
            status.LastSuccess = DateTimeOffset.Now;
            status.LastError = "";
            _activity.Add(ActivityLevel.Info, $"{channel.Name}: {result} – \"{work.Notification.Title}\"", work.Notification.ServiceName);
            Interlocked.Decrement(ref _inFlight);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_stopping.IsCancellationRequested)
        {
            status.LastFailure = DateTimeOffset.Now;
            status.LastError = ex.Message;
            var retryable = ex is DeliveryException { Retryable: true } or OperationCanceledException;
            if (retryable && work.Attempt <= RetryDelays.Length)
            {
                var delay = RetryDelays[work.Attempt - 1];
                _activity.Add(ActivityLevel.Warning, $"{channel.Name}: delivery failed ({ex.Message}). Retrying in {TimeText.Duration(delay)}.", work.Notification.ServiceName);
                _ = Task.Delay(delay, _stopping.Token).ContinueWith(t =>
                {
                    if (t.IsCanceled) Interlocked.Decrement(ref _inFlight);
                    else _queue.Writer.TryWrite(work with { Attempt = work.Attempt + 1 });
                }, TaskScheduler.Default);
                return;
            }

            Interlocked.Decrement(ref _inFlight);
            _activity.Add(ActivityLevel.Error, $"{channel.Name}: could not deliver \"{work.Notification.Title}\": {ex.Message}",
                work.Notification.ServiceName, ex is ReauthorizationRequiredException ? EventIds.ChannelNeedsAttention : EventIds.NotificationFailed);
            if (ex is ReauthorizationRequiredException) ReportChannelNeedsAttention(channel, ex.Message);
        }
        catch (OperationCanceledException)
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    /// <summary>Tells people through the other channels that one channel is broken (at most once a day per channel).</summary>
    public void ReportChannelNeedsAttention(ChannelConfig broken, string problem)
    {
        var now = DateTimeOffset.Now;
        if (_attentionNoticeSent.TryGetValue(broken.Id, out var last) && now - last < TimeSpan.FromHours(24)) return;
        _attentionNoticeSent[broken.Id] = now;

        var config = _config();
        var others = config.Channels.Where(c => c.Enabled && c.Id != broken.Id).ToList();
        if (others.Count == 0) return;
        var n = new Notification
        {
            Kind = NotificationKind.Warning,
            Severity = Severity.Warning,
            Title = $"Notification channel '{broken.Name}' needs attention",
            Summary = $"{problem} Until this is fixed, alerts will not reach this channel.",
            SiteName = config.SiteName,
        };
        foreach (var channel in others)
        {
            Interlocked.Increment(ref _inFlight);
            _queue.Writer.TryWrite(new Work(n, channel.Id, 1));
        }
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync();
        try { await _pump; } catch (OperationCanceledException) { }
        _stopping.Dispose();
    }
}
