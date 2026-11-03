using System.Net;
using System.Net.Sockets;

namespace ServiceMonitor.Core.Notifications;

/// <summary>
/// Opens a TCP connection by trying all of a server's addresses, a quarter of a second apart, and keeping whichever
/// answers first ("Happy Eyeballs", RFC 8305). Windows tries them one at a time, so a PC name that resolves to an
/// IPv6 link-local address the mail server doesn't listen on held up every send by the 21 s connect timeout.
/// </summary>
public static class TcpConnector
{
    /// <summary>How long one address gets before the next one is tried alongside it.</summary>
    public static TimeSpan AttemptDelay { get; } = TimeSpan.FromMilliseconds(250);

    public static async Task<Socket> ConnectAsync(string host, int port, CancellationToken cancellationToken)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, cancellationToken);
        if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
        return await ConnectAsync(Interleave(addresses), port, cancellationToken);
    }

    /// <summary>Alternates IPv6 and IPv4, keeping Windows' preferred family first, so one unreachable family can't hold up the other.</summary>
    internal static IReadOnlyList<IPAddress> Interleave(IReadOnlyList<IPAddress> addresses)
    {
        var first = addresses.Where(a => a.AddressFamily == addresses[0].AddressFamily).ToList();
        var other = addresses.Where(a => a.AddressFamily != addresses[0].AddressFamily).ToList();
        var result = new List<IPAddress>(addresses.Count);
        for (var i = 0; i < Math.Max(first.Count, other.Count); i++)
        {
            if (i < first.Count) result.Add(first[i]);
            if (i < other.Count) result.Add(other[i]);
        }
        return result;
    }

    internal static async Task<Socket> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pending = new List<Task<Socket>>();
        Exception? lastError = null;
        var next = 0;
        try
        {
            while (next < addresses.Count || pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (next < addresses.Count) pending.Add(AttemptAsync(addresses[next++], port, stop.Token));

                // Wait for an attempt to finish or, while addresses are left, for the moment to start the next one.
                var finished = next < addresses.Count
                    ? await Task.WhenAny([.. pending, Task.Delay(AttemptDelay, stop.Token)])
                    : await Task.WhenAny(pending);
                if (finished is not Task<Socket> attempt) continue;

                pending.Remove(attempt);
                try
                {
                    return await attempt;
                }
                catch (SocketException ex)
                {
                    lastError = ex;
                }
            }
            throw lastError ?? new SocketException((int)SocketError.HostNotFound);
        }
        finally
        {
            stop.Cancel();
            // Close any attempt that connected after the winner, and observe the failures so they aren't reported as unobserved.
            foreach (var loser in pending)
                _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); else _ = t.Exception; }, TaskScheduler.Default);
        }
    }

    private static async Task<Socket> AttemptAsync(IPAddress address, int port, CancellationToken cancellationToken)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, cancellationToken);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
