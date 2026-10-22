using System.Net;
using System.Net.Sockets;

namespace AirWave.Core.Receiver;

/// <summary>Sockets that accept IPv4 and IPv6 where possible, and plain IPv4 where IPv6 is unavailable.</summary>
internal static class Net
{
    public static TcpListener Listen(int port)
    {
        try
        {
            var l = new TcpListener(IPAddress.IPv6Any, port);
            l.Server.DualMode = true;
            l.Start();
            return l;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.AddressNotAvailable or SocketError.ProtocolNotSupported)
        {
            var l = new TcpListener(IPAddress.Any, port);
            l.Start();
            return l;
        }
    }

    public static UdpClient Udp(int port = 0)
    {
        try
        {
            var s = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) { DualMode = true };
            s.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            s.ReceiveBufferSize = 1 << 20;
            return new UdpClient { Client = s };
        }
        catch (SocketException)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            s.ReceiveBufferSize = 1 << 20;
            return new UdpClient { Client = s };
        }
    }

    /// <summary>Destination usable with a socket from <see cref="Udp"/>.</summary>
    public static IPEndPoint For(Socket s, IPEndPoint ep) =>
        s.AddressFamily == AddressFamily.InterNetworkV6 && ep.AddressFamily == AddressFamily.InterNetwork ? new IPEndPoint(ep.Address.MapToIPv6(), ep.Port) : ep;
}
