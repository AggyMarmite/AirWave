using System.Net;
using System.Net.Sockets;
using System.Text;
using AirWave.Core.Crypto;

namespace AirWave.Core.Streaming;

/// <summary>
/// The speaker's reverse "events" connection. Speakers send remote-control style requests here
/// (e.g. someone pressed play/pause on a HomePod); we decrypt them, answer 200 OK, and surface them.
/// </summary>
internal sealed class EventsChannel : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly ChannelCipher _cipher;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _name;

    /// <summary>Every request the receiver sends, as its decoded plist (e.g. updateInfo during screen mirroring).</summary>
    public event Action<Dictionary<string, object>> Command;

    private EventsChannel(string name, TcpClient tcp, byte[] secret)
    {
        _name = name;
        _tcp = tcp;
        _cipher = ChannelCipher.ForEvents(secret);
        _ = Task.Run(LoopAsync);
    }

    public static async Task<EventsChannel> ConnectAsync(string name, IPEndPoint ep, byte[] secret, CancellationToken ct)
    {
        var tcp = new TcpClient(ep.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(4000);
        await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        return new EventsChannel(name, tcp, secret);
    }

    private async Task LoopAsync()
    {
        var buf = new byte[8192];
        var plain = new List<byte>();
        try
        {
            var s = _tcp.GetStream();
            while (!_cts.IsCancellationRequested)
            {
                int n = await s.ReadAsync(buf, _cts.Token);
                if (n <= 0) break;
                plain.AddRange(_cipher.Decrypt(buf.AsSpan(0, n)));
                while (TryTakeRequest(plain, out var head, out var body))
                {
                    int cseq = 0;
                    foreach (var line in head.Split("\r\n"))
                        if (line.StartsWith("CSeq:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[5..].Trim(), out cseq);
                    if (Log.MinLevel <= LogLevel.Debug) Log.Debug($"{_name}: event {head.Split("\r\n")[0]} {Plist.PlistText.DescribeBody(body, 500)}");
                    TryRaiseCommand(body);
                    var resp = $"RTSP/1.0 200 OK\r\nServer: AirWave/1.0\r\n" + (cseq > 0 ? $"CSeq: {cseq}\r\n" : "") + "\r\n";
                    await s.WriteAsync(_cipher.Encrypt(Encoding.ASCII.GetBytes(resp)), _cts.Token);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or System.Security.Cryptography.CryptographicException)
        {
            Log.Debug($"{_name}: events channel closed ({ex.GetType().Name})");
        }
    }

    private static bool TryTakeRequest(List<byte> data, out string head, out byte[] body)
    {
        head = null; body = null;
        int end = -1;
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') { end = i; break; }
        if (end < 0) return false;
        head = Encoding.ASCII.GetString(data.GetRange(0, end).ToArray());
        int len = 0;
        foreach (var line in head.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out len);
        if (data.Count < end + 4 + len) return false;
        body = data.GetRange(end + 4, len).ToArray();
        data.RemoveRange(0, end + 4 + len);
        return true;
    }

    private void TryRaiseCommand(byte[] body)
    {
        try
        {
            if (body.Length > 8 && Plist.BinaryPlist.Read(body) is Dictionary<string, object> all) Command?.Invoke(all);
        }
        catch { }
        try
        {
            if (body.Length > 8 && Plist.BinaryPlist.Read(body) is Dictionary<string, object> d
                && d.TryGetValue("type", out var t) && t as string == "sendMediaRemoteCommand"
                && d.TryGetValue("value", out var v) && v is string cmd)
            {
                Log.Info($"{_name}: remote command '{cmd}'");
                SpeakerCommands.Raise(_name, cmd);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _tcp.Close(); } catch { }
        _cipher.Dispose();
    }
}

/// <summary>Remote-control commands sent by speakers (e.g. a HomePod tap: "paus", "play", "nitm", "pitm").</summary>
public static class SpeakerCommands
{
    public static event Action<string, string> RemoteCommand; // (speaker name, command)

    internal static void Raise(string speaker, string cmd)
    {
        try { RemoteCommand?.Invoke(speaker, cmd); } catch { }
    }
}
