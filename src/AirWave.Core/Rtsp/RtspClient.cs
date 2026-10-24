using System.Net;
using System.Net.Sockets;
using System.Text;
using AirWave.Core.Crypto;

namespace AirWave.Core.Rtsp;

public sealed class RtspResponse
{
    public int Status { get; init; }
    public string StatusText { get; init; }
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public bool Ok => Status == 200;
    public override string ToString() => $"{Status} {StatusText}";
}

/// <summary>
/// RTSP/1.0 client for the AirPlay control connection. Requests run one at a time.
/// Once pairing has produced a shared secret the connection switches to encrypted framing.
/// </summary>
public sealed class RtspClient : IDisposable
{
    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ChannelCipher _cipher;
    private readonly List<byte> _plainIn = new();
    private int _cseq = 1;

    public IPEndPoint Remote { get; }
    public IPEndPoint Local { get; }
    public Dictionary<string, string> DefaultHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsEncrypted => _cipher != null;
    public bool Connected => _tcp.Connected;

    private RtspClient(TcpClient tcp, IPEndPoint remote)
    {
        _tcp = tcp;
        _tcp.NoDelay = true;
        _stream = tcp.GetStream();
        Remote = remote;
        Local = (IPEndPoint)tcp.Client.LocalEndPoint;
    }

    public static async Task<RtspClient> ConnectAsync(IPEndPoint ep, int timeoutMs, CancellationToken ct)
    {
        var tcp = new TcpClient(ep.AddressFamily);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            await tcp.ConnectAsync(ep.Address, ep.Port, cts.Token);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
        return new RtspClient(tcp, ep);
    }

    public void EnableEncryption(byte[] sharedSecret)
    {
        _cipher = ChannelCipher.ForControl(sharedSecret);
    }

    public async Task<RtspResponse> SendAsync(string method, string uri, byte[] body = null, string contentType = null,
        IDictionary<string, string> headers = null, int timeoutMs = 10000, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var sb = new StringBuilder();
            sb.Append(method).Append(' ').Append(uri).Append(" RTSP/1.0\r\n");
            sb.Append("CSeq: ").Append(_cseq++).Append("\r\n");
            foreach (var kv in DefaultHeaders) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (headers != null)
                foreach (var kv in headers) sb.Append(kv.Key).Append(": ").Append(kv.Value).Append("\r\n");
            if (contentType != null) sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body?.Length ?? 0).Append("\r\n\r\n");

            var head = Encoding.ASCII.GetBytes(sb.ToString());
            var msg = new byte[head.Length + (body?.Length ?? 0)];
            Buffer.BlockCopy(head, 0, msg, 0, head.Length);
            if (body != null) Buffer.BlockCopy(body, 0, msg, head.Length, body.Length);

            var wire = _cipher != null ? _cipher.Encrypt(msg) : msg;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await _stream.WriteAsync(wire, cts.Token);
            var resp = await ReadResponseAsync(cts.Token);
            if (Log.MinLevel <= LogLevel.Debug)
            {
                string path = uri.StartsWith("rtsp://") ? uri[(uri.IndexOf('/', 7) is int i && i > 0 ? i : 0)..] : uri;
                string sent = body != null && body.Length > 0 && method != "POST" ? " " + Plist.PlistText.DescribeBody(body, 400) : "";
                Log.Debug($"RTSP {Remote.Address} {method} {path}{sent} -> {resp.Status} {Plist.PlistText.DescribeBody(resp.Body, 500)}");
            }
            return resp;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<RtspResponse> ReadResponseAsync(CancellationToken ct)
    {
        var buf = new byte[16384];
        while (true)
        {
            var parsed = TryParse();
            if (parsed != null) return parsed;
            int n = await _stream.ReadAsync(buf, ct);
            if (n <= 0) throw new IOException("Speaker closed the connection");
            if (_cipher != null) _plainIn.AddRange(_cipher.Decrypt(buf.AsSpan(0, n)));
            else _plainIn.AddRange(buf.AsSpan(0, n).ToArray());
        }
    }

    private RtspResponse TryParse()
    {
        // Find end of headers
        int end = -1;
        for (int i = 0; i + 3 < _plainIn.Count; i++)
        {
            if (_plainIn[i] == '\r' && _plainIn[i + 1] == '\n' && _plainIn[i + 2] == '\r' && _plainIn[i + 3] == '\n') { end = i; break; }
        }
        if (end < 0) return null;
        string head = Encoding.ASCII.GetString(_plainIn.GetRange(0, end).ToArray());
        var lines = head.Split("\r\n");
        var first = lines[0].Split(' ', 3);
        int status = first.Length > 1 && int.TryParse(first[1], out var s) ? s : 0;
        var resp = new RtspResponseBuilder { Status = status, StatusText = first.Length > 2 ? first[2] : "" };
        for (int i = 1; i < lines.Length; i++)
        {
            int c = lines[i].IndexOf(':');
            if (c > 0) resp.Headers[lines[i][..c].Trim()] = lines[i][(c + 1)..].Trim();
        }
        int len = resp.Headers.TryGetValue("Content-Length", out var cl) && int.TryParse(cl, out var l) ? l : 0;
        if (_plainIn.Count < end + 4 + len) return null;
        var body = _plainIn.GetRange(end + 4, len).ToArray();
        _plainIn.RemoveRange(0, end + 4 + len);
        var r = new RtspResponse { Status = resp.Status, StatusText = resp.StatusText, Body = body };
        foreach (var kv in resp.Headers) r.Headers[kv.Key] = kv.Value;
        return r;
    }

    private sealed class RtspResponseBuilder
    {
        public int Status;
        public string StatusText;
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        try { _tcp.Close(); } catch { }
        _cipher?.Dispose();
    }
}
