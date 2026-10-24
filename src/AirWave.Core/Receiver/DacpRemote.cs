using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace AirWave.Core.Receiver;

/// <summary>
/// Remote control of the device playing to us (play/pause, next, previous), using DACP: the sender advertises
/// "iTunes_Ctrl_&lt;DACP-ID&gt;._dacp._tcp" and accepts "GET /ctrl-int/1/&lt;command&gt;" with the Active-Remote code
/// it gave us. This is what Shairport Sync uses; not every app or device offers it, so it's best effort.
/// </summary>
public sealed class DacpRemote
{
    public string DacpId { get; }
    public string ActiveRemote { get; }
    public IPAddress Address { get; }
    private int _port;
    private DateTime _lastResolve = DateTime.MinValue;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>For tests: the mDNS group/port to ask (default 224.0.0.251:5353).</summary>
    public static IPEndPoint MdnsEndPoint = new(IPAddress.Parse("224.0.0.251"), 5353);

    public DacpRemote(string dacpId, string activeRemote, IPAddress address, int port = 0)
    {
        DacpId = dacpId; ActiveRemote = activeRemote; Address = address; _port = port;
    }

    public bool Resolved => _port > 0;

    public static string Command(RemoteCommand c) => c switch
    {
        RemoteCommand.PlayPause => "playpause",
        RemoteCommand.Next => "nextitem",
        RemoteCommand.Previous => "previtem",
        RemoteCommand.Play => "play",
        RemoteCommand.Pause => "pause",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Sends a command; returns false if the sender couldn't be reached.</summary>
    public async Task<bool> SendAsync(RemoteCommand command, CancellationToken ct = default)
    {
        if (!Resolved && !await ResolveAsync(ct)) return false;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var host = Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString();
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://{host}:{_port}/ctrl-int/1/{Command(command)}");
                req.Headers.Add("Active-Remote", ActiveRemote);
                using var resp = await Http.SendAsync(req, ct);
                Log.Debug($"DACP {Command(command)} -> {(int)resp.StatusCode}");
                return resp.IsSuccessStatusCode;
            }
            catch (Exception ex) when (attempt == 0 && ex is HttpRequestException or TaskCanceledException)
            {
                // The port can change (app restarted): look it up again once
                _port = 0;
                if (!await ResolveAsync(ct)) return false;
            }
            catch (Exception ex)
            {
                Log.Debug($"DACP {Command(command)} failed: {ex.Message}");
                return false;
            }
        }
        return false;
    }

    /// <summary>Finds the port of the sender's DACP service with a one-shot mDNS question.</summary>
    public async Task<bool> ResolveAsync(CancellationToken ct = default)
    {
        if (Resolved) return true;
        if ((DateTime.UtcNow - _lastResolve).TotalSeconds < 2) return false;
        _lastResolve = DateTime.UtcNow;
        string name = $"iTunes_Ctrl_{DacpId}._dacp._tcp.local";
        try
        {
            using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            var q = Query(name);
            for (int i = 0; i < 3 && !Resolved; i++)
            {
                await udp.SendAsync(q, MdnsEndPoint, ct);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(700);
                try
                {
                    while (!Resolved)
                    {
                        var r = await udp.ReceiveAsync(wait.Token);
                        int port = ParseSrvPort(r.Buffer, name);
                        if (port > 0) _port = port;
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
            }
        }
        catch (Exception ex) { Log.Debug($"DACP lookup failed: {ex.Message}"); }
        if (Resolved) Log.Debug($"DACP: remote control for {Address} on port {_port}");
        return Resolved;
    }

    public static byte[] Query(string name)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 }); // id 0, 1 question
        foreach (var label in name.Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
        ms.Write(new byte[] { 0, 33, 0x80, 1 }); // SRV, class IN with "unicast response" bit
        return ms.ToArray();
    }

    /// <summary>Port from an SRV record for <paramref name="name"/> in an mDNS reply, or 0.</summary>
    public static int ParseSrvPort(byte[] p, string name)
    {
        try
        {
            if (p.Length < 12) return 0;
            int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4)), an = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6));
            int ns = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)), ar = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
            int pos = 12;
            for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }
            for (int i = 0; i < an + ns + ar; i++)
            {
                string rn = ReadName(p, ref pos);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
                int rdlen = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
                int rd = pos + 10;
                if (type == 33 && string.Equals(rn, name, StringComparison.OrdinalIgnoreCase))
                    return BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(rd + 4));
                pos = rd + rdlen;
            }
        }
        catch { }
        return 0;
    }

    private static string ReadName(byte[] p, ref int pos)
    {
        var sb = new StringBuilder();
        int jumps = 0, cur = pos;
        bool jumped = false;
        while (true)
        {
            int len = p[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                int off = ((len & 0x3F) << 8) | p[cur + 1];
                if (!jumped) pos = cur + 2;
                jumped = true;
                cur = off;
                if (++jumps > 20) throw new InvalidDataException();
                continue;
            }
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p, cur + 1, len));
            cur += 1 + len;
        }
        if (!jumped) pos = cur;
        return sb.ToString();
    }
}

public enum RemoteCommand { PlayPause, Next, Previous, Play, Pause }
