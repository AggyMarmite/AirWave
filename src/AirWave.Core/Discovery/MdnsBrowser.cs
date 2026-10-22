using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace AirWave.Core.Discovery;

/// <summary>
/// Finds AirPlay 2 speakers with multicast DNS (Bonjour), with no dependency on Apple's Bonjour
/// service. It listens on 5353 when it can (to hear announcements) and also asks from its own
/// port so speakers answer it directly even when another app owns 5353.
/// </summary>
public sealed class MdnsBrowser : IDisposable
{
    public const string AirPlayService = "_airplay._tcp.local";
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly List<UdpClient> _sockets = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<string, Instance> _instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<IPAddress>> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SpeakerInfo> _speakers = new(StringComparer.OrdinalIgnoreCase);

    public event Action<SpeakerInfo> SpeakerFound;
    public event Action<SpeakerInfo> SpeakerUpdated;
    public event Action<SpeakerInfo> SpeakerLost;

    public IReadOnlyCollection<SpeakerInfo> Speakers => _speakers.Values.ToList();

    private sealed class Instance
    {
        public string Name;
        public string Host;
        public int Port;
        public Dictionary<string, string> Txt;
        public DateTime Expires;
    }

    public void Start()
    {
        // Shared listener on 5353 (announcements + multicast answers)
        try
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.Bind(new IPEndPoint(IPAddress.Any, 5353));
            foreach (var addr in LocalIPv4())
            {
                try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, addr)); }
                catch (SocketException) { }
            }
            _sockets.Add(new UdpClient { Client = s });
        }
        catch (SocketException ex)
        {
            Log.Debug($"mDNS: port 5353 busy ({ex.SocketErrorCode}), relying on direct answers");
        }

        // Our own querying socket on each interface (answers come back unicast to it)
        foreach (var addr in LocalIPv4())
        {
            try
            {
                var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                s.Bind(new IPEndPoint(addr, 0));
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, addr.GetAddressBytes());
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
                _sockets.Add(new UdpClient { Client = s });
            }
            catch (SocketException) { }
        }

        foreach (var u in _sockets) _ = Task.Run(() => ReceiveLoop(u));
        _ = Task.Run(QueryLoop);
    }

    /// <summary>Ask again right now (e.g. when the user opens the speaker list).</summary>
    public void Refresh() => SendQuery(AirPlayService, 12);

    private static IEnumerable<IPAddress> LocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            if (!ni.SupportsMulticast) continue;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
                if (u.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(u.Address))
                    yield return u.Address;
        }
    }

    private async Task QueryLoop()
    {
        int round = 0;
        while (!_cts.IsCancellationRequested)
        {
            SendQuery(AirPlayService, 12);
            foreach (var inst in _instances.Values)
            {
                if (inst.Host == null || inst.Txt == null) SendQuery(inst.Name, 255);
                else if (!_hosts.ContainsKey(inst.Host)) SendQuery(inst.Host, 1);
            }
            Prune();
            round++;
            int delay = round < 5 ? 1500 : round < 10 ? 5000 : 15000;
            try { await Task.Delay(delay, _cts.Token); } catch { break; }
        }
    }

    private void Prune()
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _instances)
        {
            if (kv.Value.Expires < now && _instances.TryRemove(kv.Key, out var inst))
                Lose(inst.Name);
        }
    }

    private void Lose(string instanceName)
    {
        foreach (var kv in _speakers)
        {
            if (kv.Value.Txt.TryGetValue("__instance", out var n) && string.Equals(n, instanceName, StringComparison.OrdinalIgnoreCase)
                && _speakers.TryRemove(kv.Key, out var sp))
            {
                Log.Info($"mDNS: speaker gone: {sp.Name}");
                SpeakerLost?.Invoke(sp);
            }
        }
    }

    private void SendQuery(string name, ushort type)
    {
        var q = BuildQuery(name, type);
        foreach (var u in _sockets)
        {
            try { u.Client.SendTo(q, new IPEndPoint(Group, 5353)); } catch { }
        }
    }

    private static byte[] BuildQuery(string name, ushort type)
    {
        using var ms = new MemoryStream();
        Span<byte> h = stackalloc byte[12];
        h.Clear();
        BinaryPrimitives.WriteUInt16BigEndian(h[4..], 1); // one question
        ms.Write(h);
        WriteName(ms, name);
        Span<byte> t = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(t, type);
        BinaryPrimitives.WriteUInt16BigEndian(t[2..], 0x0001); // class IN
        ms.Write(t);
        return ms.ToArray();
    }

    private static void WriteName(Stream s, string name)
    {
        foreach (var label in SplitLabels(name))
        {
            var b = Encoding.UTF8.GetBytes(label);
            s.WriteByte((byte)b.Length);
            s.Write(b);
        }
        s.WriteByte(0);
    }

    // Instance names can contain dots escaped as "\." — split carefully
    private static IEnumerable<string> SplitLabels(string name)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            if (name[i] == '\\' && i + 1 < name.Length) { sb.Append(name[++i]); continue; }
            if (name[i] == '.') { if (sb.Length > 0) yield return sb.ToString(); sb.Clear(); continue; }
            sb.Append(name[i]);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private async Task ReceiveLoop(UdpClient u)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { Handle(r.Buffer, r.RemoteEndPoint.Address); }
            catch (Exception ex) { Log.Debug($"mDNS: bad packet from {r.RemoteEndPoint}: {ex.Message}"); }
        }
    }

    private void Handle(byte[] p, IPAddress from)
    {
        if (p.Length < 12) return;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
        if ((flags & 0x8000) == 0) return; // a query, not an answer
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
        int records = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(6)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(8)) + BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(10));
        int pos = 12;
        for (int i = 0; i < qd; i++) { ReadName(p, ref pos); pos += 4; }

        var touched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < records && pos + 10 <= p.Length; i++)
        {
            string name = ReadName(p, ref pos);
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(pos + 4));
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 8));
            pos += 10;
            int rd = pos;
            pos += rdlen;
            if (pos > p.Length) break;

            switch (type)
            {
                case 12: // PTR
                    if (!name.Equals(AirPlayService, StringComparison.OrdinalIgnoreCase)) break;
                    int rp = rd;
                    string inst = ReadName(p, ref rp);
                    if (ttl == 0) { _instances.TryRemove(inst, out _); Lose(inst); break; }
                    var instance = _instances.GetOrAdd(inst, n => new Instance { Name = n });
                    instance.Expires = DateTime.UtcNow.AddSeconds(Math.Max(ttl, 120));
                    touched.Add(inst);
                    break;
                case 33: // SRV
                    if (!name.EndsWith(AirPlayService, StringComparison.OrdinalIgnoreCase)) break;
                    var si = _instances.GetOrAdd(name, n => new Instance { Name = n, Expires = DateTime.UtcNow.AddSeconds(120) });
                    si.Port = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(rd + 4));
                    int sp = rd + 6;
                    si.Host = ReadName(p, ref sp);
                    touched.Add(name);
                    break;
                case 16: // TXT
                    if (!name.EndsWith(AirPlayService, StringComparison.OrdinalIgnoreCase)) break;
                    var ti = _instances.GetOrAdd(name, n => new Instance { Name = n, Expires = DateTime.UtcNow.AddSeconds(120) });
                    ti.Txt = ParseTxt(p, rd, rdlen);
                    touched.Add(name);
                    break;
                case 1: // A
                    if (rdlen != 4) break;
                    var a = new IPAddress(p.AsSpan(rd, 4));
                    var list = _hosts.GetOrAdd(name, _ => new List<IPAddress>());
                    lock (list) { if (!list.Contains(a)) list.Insert(0, a); }
                    touched.Add("host:" + name);
                    break;
                case 28: // AAAA
                    if (rdlen != 16) break;
                    var a6 = new IPAddress(p.AsSpan(rd, 16));
                    if (a6.IsIPv6LinkLocal) break; // needs a scope id, keep it simple
                    var l6 = _hosts.GetOrAdd(name, _ => new List<IPAddress>());
                    lock (l6) { if (!l6.Contains(a6)) l6.Add(a6); }
                    touched.Add("host:" + name);
                    break;
            }
        }

        foreach (var inst in _instances.Values)
        {
            if (touched.Contains(inst.Name) || (inst.Host != null && touched.Contains("host:" + inst.Host)))
                Publish(inst, from);
        }
    }

    private void Publish(Instance inst, IPAddress from)
    {
        if (inst.Txt == null || inst.Port == 0) return;
        List<IPAddress> addrs = inst.Host != null && _hosts.TryGetValue(inst.Host, out var l) ? l : null;
        if (addrs == null || addrs.Count == 0)
        {
            // Fall back to the address the answer came from
            addrs = new List<IPAddress> { from };
        }
        string id = inst.Txt.TryGetValue("deviceid", out var did) ? did.ToUpperInvariant() : inst.Name;
        string friendly = InstanceLabel(inst.Name);
        if (friendly.Contains('@')) friendly = friendly[(friendly.IndexOf('@') + 1)..];

        var features = SpeakerInfo.ParseFeatures(inst.Txt.GetValueOrDefault("features") ?? inst.Txt.GetValueOrDefault("ft"));
        bool isNew = !_speakers.TryGetValue(id, out var sp);
        if (isNew)
        {
            sp = new SpeakerInfo { Id = id };
        }
        sp.Name = friendly;
        sp.Model = inst.Txt.GetValueOrDefault("model") ?? inst.Txt.GetValueOrDefault("am");
        sp.Manufacturer = inst.Txt.GetValueOrDefault("manufacturer");
        sp.Port = inst.Port;
        sp.Features = features;
        sp.StatusFlags = SpeakerInfo.ParseFlags(inst.Txt.GetValueOrDefault("flags") ?? inst.Txt.GetValueOrDefault("sf"));
        sp.Txt = new Dictionary<string, string>(inst.Txt, StringComparer.OrdinalIgnoreCase) { ["__instance"] = inst.Name };
        sp.LastSeen = DateTime.UtcNow;
        lock (addrs)
        {
            sp.Addresses.Clear();
            // The address the answer came from is the most trustworthy one: try it first
            if (from != null && from.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && addrs.Contains(from)) sp.Addresses.Add(from);
            foreach (var a in addrs) if (!sp.Addresses.Contains(a)) sp.Addresses.Add(a);
        }

        if (!sp.SupportsAudio) return; // e.g. a Mac's screen mirroring target with audio off
        if (isNew && _speakers.TryAdd(id, sp))
        {
            Log.Info($"mDNS: found {sp} features=0x{features:X} model={sp.Model}");
            SpeakerFound?.Invoke(sp);
        }
        else if (!isNew)
        {
            SpeakerUpdated?.Invoke(sp);
        }
    }

    private static string InstanceLabel(string instanceName)
    {
        int i = instanceName.IndexOf("._airplay", StringComparison.OrdinalIgnoreCase);
        return (i > 0 ? instanceName[..i] : instanceName).Replace("\\.", ".");
    }

    private static Dictionary<string, string> ParseTxt(byte[] p, int start, int len)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = start, end = start + len;
        while (pos < end)
        {
            int l = p[pos++];
            if (pos + l > end) break;
            var s = Encoding.UTF8.GetString(p, pos, l);
            pos += l;
            int eq = s.IndexOf('=');
            if (eq > 0) d[s[..eq]] = s[(eq + 1)..];
            else if (s.Length > 0) d[s] = "";
        }
        return d;
    }

    private static string ReadName(byte[] p, ref int pos)
    {
        var sb = new StringBuilder();
        int jumps = 0;
        int cur = pos;
        bool jumped = false;
        while (cur < p.Length)
        {
            int len = p[cur];
            if (len == 0) { cur++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (cur + 1 >= p.Length || ++jumps > 20) break;
                int ptr = ((len & 0x3F) << 8) | p[cur + 1];
                if (!jumped) pos = cur + 2;
                jumped = true;
                cur = ptr;
                continue;
            }
            cur++;
            if (cur + len > p.Length) break;
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(p, cur, len).Replace(".", "\\."));
            cur += len;
        }
        if (!jumped) pos = cur;
        return sb.ToString();
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var u in _sockets) u.Dispose();
    }
}
