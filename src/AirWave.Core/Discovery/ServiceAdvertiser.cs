using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace AirWave.Core.Discovery;

/// <summary>A DNS-SD service to publish: e.g. "Kitchen PC" of type "_airplay._tcp".</summary>
public sealed record ServiceRecord(string Instance, string Type, int Port, IReadOnlyList<(string Key, string Value)> Txt);

/// <summary>Publishes services so iPhones and Macs see AirWave in their AirPlay list.</summary>
public interface IServiceAdvertiser : IDisposable
{
    void Publish(IReadOnlyList<ServiceRecord> services);
    void Withdraw();
}

public static class ServiceAdvertiser
{
    /// <summary>Windows' own mDNS service where available (it already owns port 5353), else our responder.</summary>
    public static IServiceAdvertiser Create()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            try { return new WindowsDnsServiceAdvertiser(); }
            catch (Exception ex) { Log.Warn($"Windows service registration unavailable ({ex.Message}); using built-in responder"); }
        }
        return new MdnsResponder();
    }
}

// =====================================================================================================
// Windows: DnsServiceRegister (Windows 10 1809+)
// =====================================================================================================

internal sealed class WindowsDnsServiceAdvertiser : IServiceAdvertiser
{
    private readonly List<Registration> _regs = new();
    private static readonly RegisterCallback Callback = OnRegistered; // keep alive for native code

    private sealed class Registration
    {
        public IntPtr Instance;
        public IntPtr Request; // DNS_SERVICE_REGISTER_REQUEST, unmanaged so it outlives the call
        public string Name;
    }

    public WindowsDnsServiceAdvertiser()
    {
        // Probe the API exists (throws EntryPointNotFound on older Windows)
        Marshal.Prelink(typeof(WindowsDnsServiceAdvertiser).GetMethod(nameof(DnsServiceRegister), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!);
    }

    public void Publish(IReadOnlyList<ServiceRecord> services)
    {
        Withdraw();
        string host = Dns.GetHostName().Split('.')[0] + ".local";
        foreach (var s in services)
        {
            string full = $"{EscapeInstance(s.Instance)}.{s.Type}.local";
            var keys = s.Txt.Select(t => Marshal.StringToHGlobalUni(t.Key)).ToArray();
            var vals = s.Txt.Select(t => Marshal.StringToHGlobalUni(t.Value)).ToArray();
            IntPtr inst;
            unsafe
            {
                fixed (IntPtr* k = keys) fixed (IntPtr* v = vals)
                    inst = DnsServiceConstructInstance(full, host, IntPtr.Zero, IntPtr.Zero, (ushort)s.Port, 0, 0, (uint)keys.Length, (IntPtr)k, (IntPtr)v);
            }
            foreach (var p in keys.Concat(vals)) Marshal.FreeHGlobal(p);
            if (inst == IntPtr.Zero) { Log.Warn($"mDNS: couldn't build service {full}"); continue; }

            var req = new DnsServiceRegisterRequest
            {
                Version = 1,
                InterfaceIndex = 0,
                pServiceInstance = inst,
                pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(Callback),
                pQueryContext = IntPtr.Zero,
                hCredentials = IntPtr.Zero,
                unicastEnabled = 0,
            };
            IntPtr reqPtr = Marshal.AllocHGlobal(Marshal.SizeOf<DnsServiceRegisterRequest>());
            Marshal.StructureToPtr(req, reqPtr, false);
            uint rc = DnsServiceRegister(reqPtr, IntPtr.Zero);
            if (rc != 9506 /* DNS_REQUEST_PENDING */ && rc != 0)
            {
                Log.Warn($"mDNS: registering {full} failed ({rc})");
                DnsServiceFreeInstance(inst);
                Marshal.FreeHGlobal(reqPtr);
                continue;
            }
            _regs.Add(new Registration { Instance = inst, Request = reqPtr, Name = full });
            Log.Info($"mDNS: advertising {full} on port {s.Port}");
        }
    }

    public void Withdraw()
    {
        foreach (var r in _regs)
        {
            try { DnsServiceDeRegister(r.Request, IntPtr.Zero); } catch { }
            // The instance/request are released after Windows confirms (callback), or leak harmlessly on exit.
        }
        _regs.Clear();
    }

    public void Dispose() => Withdraw();

    private static void OnRegistered(uint status, IntPtr context, IntPtr instance)
    {
        if (status != 0) Log.Debug($"mDNS: registration callback status {status}");
    }

    internal static string EscapeInstance(string s) => s.Replace("\\", "\\\\").Replace(".", "\\.");

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void RegisterCallback(uint status, IntPtr context, IntPtr instance);

    [StructLayout(LayoutKind.Sequential)]
    private struct DnsServiceRegisterRequest
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr DnsServiceConstructInstance(string pServiceName, string pHostName, IntPtr pIp4, IntPtr pIp6,
        ushort wPort, ushort wPriority, ushort wWeight, uint dwPropertiesCount, IntPtr keys, IntPtr values);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceRegister(IntPtr pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern uint DnsServiceDeRegister(IntPtr pRequest, IntPtr pCancel);

    [DllImport("dnsapi.dll", ExactSpelling = true)]
    private static extern void DnsServiceFreeInstance(IntPtr pInstance);
}

// =====================================================================================================
// Built-in multicast DNS responder (RFC 6762) — used where the OS doesn't provide one
// =====================================================================================================

public sealed class MdnsResponder : IServiceAdvertiser
{
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");
    private readonly CancellationTokenSource _cts = new();
    private UdpClient _udp;
    private volatile IReadOnlyList<ServiceRecord> _services = Array.Empty<ServiceRecord>();
    private readonly string _host = SanitizeHost(Dns.GetHostName().Split('.')[0]) + "-airwave.local";
    private const int Ttl = 120;

    public void Publish(IReadOnlyList<ServiceRecord> services)
    {
        _services = services;
        if (_udp == null) Start();
        _ = AnnounceAsync();
    }

    public void Withdraw()
    {
        if (_udp == null || _services.Count == 0) return;
        try { Send(BuildResponse(_services, null, goodbye: true), new IPEndPoint(Group, 5353)); } catch { }
        _services = Array.Empty<ServiceRecord>();
    }

    private void Start()
    {
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        s.Bind(new IPEndPoint(IPAddress.Any, 5353));
        foreach (var a in LocalIPv4())
        {
            try { s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, a)); } catch { }
        }
        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        _udp = new UdpClient { Client = s };
        _ = Task.Run(ReceiveLoop);
    }

    private async Task AnnounceAsync()
    {
        for (int i = 0; i < 3 && !_cts.IsCancellationRequested; i++)
        {
            Send(BuildResponse(_services, null, goodbye: false), new IPEndPoint(Group, 5353));
            try { await Task.Delay(1000 << i, _cts.Token); } catch { return; }
        }
    }

    private void Send(byte[] pkt, IPEndPoint to)
    {
        // Send out of every interface so all subnets the PC is on can see us
        foreach (var a in LocalIPv4())
        {
            try
            {
                _udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, a.GetAddressBytes());
                _udp.Client.SendTo(pkt, to);
            }
            catch { }
            if (!to.Address.Equals(Group)) break; // unicast replies only need sending once
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try { HandleQuery(r.Buffer, r.RemoteEndPoint); } catch { }
        }
    }

    private void HandleQuery(byte[] p, IPEndPoint from)
    {
        if (p.Length < 12) return;
        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(2));
        if ((flags & 0x8000) != 0) return; // a response, not a query
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(4));
        int pos = 12;
        var services = _services;
        if (services.Count == 0) return;
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool unicast = false;
        for (int i = 0; i < qd; i++)
        {
            string name = ReadName(p, ref pos);
            ushort qtype = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos));
            ushort qclass = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(pos + 2));
            pos += 4;
            if ((qclass & 0x8000) != 0) unicast = true;
            if (Matches(services, name, qtype)) wanted.Add(name);
        }
        if (wanted.Count == 0) return;
        var resp = BuildResponse(services, wanted, goodbye: false);
        // Legacy unicast (source port != 5353) and QU questions get a direct reply
        if (from.Port != 5353 || unicast) Send(resp, from);
        else Send(resp, new IPEndPoint(Group, 5353));
    }

    private bool Matches(IReadOnlyList<ServiceRecord> services, string name, ushort qtype)
    {
        if (name.Equals("_services._dns-sd._udp.local", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Equals(_host, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var s in services)
        {
            if (name.Equals(s.Type + ".local", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals(InstanceName(s), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string InstanceName(ServiceRecord s) => $"{s.Instance}.{s.Type}.local";

    private byte[] BuildResponse(IReadOnlyList<ServiceRecord> services, HashSet<string> wanted, bool goodbye)
    {
        int ttl = goodbye ? 0 : Ttl;
        var answers = new List<byte[]>();
        foreach (var s in services)
        {
            string type = s.Type + ".local";
            string inst = InstanceName(s);
            bool all = wanted == null || wanted.Contains(type) || wanted.Contains(inst);
            if (!all) continue;
            answers.Add(Rr(type, 12, ttl, false, Name(inst)));                                   // PTR
            var srv = new MemoryStream();
            srv.Write(new byte[] { 0, 0, 0, 0, (byte)(s.Port >> 8), (byte)s.Port });
            srv.Write(Name(_host));
            answers.Add(Rr(inst, 33, ttl, true, srv.ToArray()));                                 // SRV
            var txt = new MemoryStream();
            foreach (var (k, v) in s.Txt)
            {
                var b = Encoding.UTF8.GetBytes($"{k}={v}");
                txt.WriteByte((byte)Math.Min(255, b.Length));
                txt.Write(b, 0, Math.Min(255, b.Length));
            }
            answers.Add(Rr(inst, 16, ttl, true, txt.ToArray()));                                // TXT
        }
        if (wanted != null && wanted.Contains("_services._dns-sd._udp.local"))
            foreach (var t in services.Select(x => x.Type).Distinct())
                answers.Add(Rr("_services._dns-sd._udp.local", 12, ttl, false, Name(t + ".local")));
        foreach (var a in LocalIPv4())
            answers.Add(Rr(_host, 1, ttl, true, a.GetAddressBytes()));                          // A

        var ms = new MemoryStream();
        var h = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(2), 0x8400); // response, authoritative
        BinaryPrimitives.WriteUInt16BigEndian(h.AsSpan(6), (ushort)answers.Count);
        ms.Write(h);
        foreach (var a in answers) ms.Write(a);
        return ms.ToArray();
    }

    private static byte[] Rr(string name, ushort type, int ttl, bool cacheFlush, byte[] rdata)
    {
        var ms = new MemoryStream();
        ms.Write(Name(name));
        var t = new byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(t, type);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(2), (ushort)(cacheFlush ? 0x8001 : 0x0001));
        BinaryPrimitives.WriteInt32BigEndian(t.AsSpan(4), ttl);
        BinaryPrimitives.WriteUInt16BigEndian(t.AsSpan(8), (ushort)rdata.Length);
        ms.Write(t);
        ms.Write(rdata);
        return ms.ToArray();
    }

    /// <summary>Encodes a name; the first label may contain dots (instance names), the rest are split on dots.</summary>
    private static byte[] Name(string name)
    {
        var ms = new MemoryStream();
        // Instance names are "<instance>.<_svc>.<_tcp>.local": split from the right on known parts
        var labels = new List<string>();
        int svc = name.IndexOf("._", StringComparison.Ordinal);
        if (svc > 0 && !name.StartsWith("_"))
        {
            labels.Add(name[..svc]);
            labels.AddRange(name[(svc + 1)..].Split('.'));
        }
        else labels.AddRange(name.Split('.'));
        foreach (var l in labels.Where(x => x.Length > 0))
        {
            var b = Encoding.UTF8.GetBytes(l);
            ms.WriteByte((byte)Math.Min(63, b.Length));
            ms.Write(b, 0, Math.Min(63, b.Length));
        }
        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static string ReadName(byte[] p, ref int pos)
    {
        var labels = new List<string>();
        int end = -1, guard = 0;
        int i = pos;
        while (guard++ < 128)
        {
            int len = p[i];
            if (len == 0) { i++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (end < 0) end = i + 2;
                i = ((len & 0x3f) << 8) | p[i + 1];
                continue;
            }
            labels.Add(Encoding.UTF8.GetString(p, i + 1, len));
            i += 1 + len;
        }
        pos = end >= 0 ? end : i;
        // Join; the first label of an instance name may itself contain dots — fine for comparison
        return string.Join(".", labels);
    }

    private static string SanitizeHost(string h)
    {
        var sb = new StringBuilder();
        foreach (char c in h) sb.Append(char.IsLetterOrDigit(c) || c == '-' ? c : '-');
        return sb.Length > 0 ? sb.ToString() : "airwave";
    }

    internal static IEnumerable<IPAddress> LocalIPv4()
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

    public void Dispose()
    {
        Withdraw();
        _cts.Cancel();
        _udp?.Dispose();
    }
}
