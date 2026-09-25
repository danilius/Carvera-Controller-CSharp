using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Carvera.Core.Connection;

public sealed record DiscoveredMachine(string Name, string Address, int Port, bool Busy)
{
    public string Endpoint => Port == ConnectionOptions.DefaultTcpPort ? Address : $"{Address}:{Port}";
}

/// <summary>Listens for the UDP broadcasts (port 3333) that Carvera machines send on the local network.</summary>
public static class MachineDetector
{
    public const int BroadcastPort = 3333;

    /// <summary>Parses a "name,ip,port,busy" announcement.</summary>
    public static DiscoveredMachine? Parse(string payload)
    {
        var fields = payload.Trim().Split(',');
        if (fields.Length < 4 || !int.TryParse(fields[2], out var port)) return null;
        return new DiscoveredMachine(fields[0], fields[1], port, fields[3].Trim() == "1");
    }

    public static async Task<IReadOnlyList<DiscoveredMachine>> DiscoverAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, DiscoveredMachine>(StringComparer.Ordinal);
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, BroadcastPort));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        try
        {
            while (!timeout.IsCancellationRequested)
            {
                var result = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                var machine = Parse(Encoding.UTF8.GetString(result.Buffer));
                if (machine is not null) found.TryAdd(machine.Name, machine);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        return found.Values.ToList();
    }
}
