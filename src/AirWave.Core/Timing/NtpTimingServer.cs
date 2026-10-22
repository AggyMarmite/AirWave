using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace AirWave.Core.Timing;

/// <summary>
/// Answers the AirPlay "NTP" timing requests (0x80 0xd2) that speakers using NTP timing send us,
/// so they can lock their clock to <see cref="MonoClock"/>.
/// </summary>
public sealed class NtpTimingServer : IDisposable
{
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    public int Port { get; }

    public NtpTimingServer()
    {
        _udp = UdpHelpers.CreateDualMode(0);
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint).Port;
        _ = Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        var res = new byte[32];
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }

            long recvNs = MonoClock.NowNs;
            var req = r.Buffer;
            if (req.Length != 32 || req[0] != 0x80 || req[1] != 0xd2) continue;

            Array.Clear(res);
            res[0] = 0x80;
            res[1] = 0xd3;
            res[2] = req[2];
            Buffer.BlockCopy(req, 24, res, 8, 8); // client's transmit time becomes our origin time
            BinaryPrimitives.WriteUInt64BigEndian(res.AsSpan(16), MonoClock.ToNtp(recvNs));
            BinaryPrimitives.WriteUInt64BigEndian(res.AsSpan(24), MonoClock.ToNtp(MonoClock.NowNs));
            try { await _udp.SendAsync(res, r.RemoteEndPoint); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}

internal static class UdpHelpers
{
    /// <summary>An IPv6 socket that also accepts IPv4 (mapped), bound to the given port (0 = any).</summary>
    public static UdpClient CreateDualMode(int port)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return new UdpClient { Client = s };
        }
        catch (SocketException)
        {
            return new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
    }

    public static IPEndPoint Normalize(IPEndPoint ep) =>
        ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    /// <summary>Destination usable with a socket from <see cref="CreateDualMode"/>.</summary>
    public static IPEndPoint ForSocket(Socket s, IPEndPoint ep) =>
        s.AddressFamily == AddressFamily.InterNetworkV6 && ep.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;
}
