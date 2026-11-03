using System.Net.Sockets;

namespace ServiceMonitor.Core.Windows;

public sealed record ProbeResult(bool Success, string? Error = null);

public interface IPortProbe
{
    Task<ProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Checks that something accepts TCP connections on a port, which a hung service usually stops doing.</summary>
public sealed class TcpPortProbe : IPortProbe
{
    public async Task<ProbeResult> ProbeAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(host.Contains(':') ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token);
            return new ProbeResult(true);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ProbeResult(false, $"No answer on {host}:{port} within {timeout.TotalSeconds:0} s");
        }
        catch (SocketException ex)
        {
            return new ProbeResult(false, $"{host}:{port} – {ex.Message}");
        }
    }
}
