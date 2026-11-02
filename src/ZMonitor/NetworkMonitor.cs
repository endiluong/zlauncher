using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;

namespace ZMonitor;

/// <summary>
/// Download / upload totals from the adapters that carry the default route (so WSL / Hyper-V virtual
/// adapters are not double counted), and per-app traffic from the Microsoft-Windows-Kernel-Network
/// ETW provider. The per-app session only runs while the network card is expanded.
/// </summary>
sealed unsafe class NetworkMonitor : IDisposable
{
    static readonly Guid KernelNetworkGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");
    // TCP send/recv IPv4 (10/11) and IPv6 (26/27); UDP send/recv IPv4 (42/43) and IPv6 (58/59).
    static readonly ushort[] TrafficEvents = [10, 11, 26, 27, 42, 43, 58, 59];

    public readonly record struct AppTraffic(string Name, double Down, double Up);

    NetworkInterface[] _adapters = [];
    long _adaptersRefreshed = long.MinValue / 2;
    long _lastRx = -1, _lastTx = -1, _lastTotalsTs;

    readonly object _lock = new();
    Dictionary<int, (long Down, long Up)> _perPid = new();
    long _perPidSince = Stopwatch.GetTimestamp();
    EtwSession? _session;
    readonly Dictionary<int, (string Exe, string Display)> _names = new();

    /// <summary>Bytes per second since the previous call.</summary>
    public (double Down, double Up) ReadTotals()
    {
        if (Environment.TickCount64 - _adaptersRefreshed > 30_000) RefreshAdapters();
        long rx = 0, tx = 0;
        foreach (var ni in _adapters)
        {
            try { var s = ni.GetIPStatistics(); rx += s.BytesReceived; tx += s.BytesSent; }
            catch { _adaptersRefreshed = long.MinValue / 2; }
        }
        long now = Stopwatch.GetTimestamp();
        double secs = (now - _lastTotalsTs) / (double)Stopwatch.Frequency;
        (double, double) rate = _lastRx < 0 || secs <= 0 ? (0, 0)
            : (Math.Max(0, rx - _lastRx) / secs, Math.Max(0, tx - _lastTx) / secs);
        _lastRx = rx; _lastTx = tx; _lastTotalsTs = now;
        return rate;
    }

    void RefreshAdapters()
    {
        _adaptersRefreshed = Environment.TickCount64;
        try
        {
            _adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                            && n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)))
                .ToArray();
        }
        catch { _adapters = []; }
        _lastRx = -1; // adapter set may have changed: restart the delta
    }

    // ---------- per-app ----------

    public bool PerAppRunning => _session != null;

    readonly object _sessionLock = new();

    public void StartPerApp()
    {
        lock (_sessionLock)
        {
            if (_session != null) return;
            var s = new EtwSession("ZMonitor-Network", Handle);
            if (!s.Start()) { App.Log("Network capture: " + s.LastError); s.Dispose(); return; }
            s.Enable(KernelNetworkGuid, 5, 0x30 /* IPv4 | IPv6 */, TrafficEvents);
            if (!s.Consume()) { App.Log("Network capture: " + s.LastError); s.Dispose(); return; }
            lock (_lock) { _perPid = new(); _perPidSince = Stopwatch.GetTimestamp(); _window.Clear(); }
            _session = s;
        }
    }

    public void StopPerApp()
    {
        lock (_sessionLock)
        {
            _session?.Dispose();
            _session = null;
        }
    }

    void Handle(EtwSession.EventRecord* r)
    {
        // Payload: PID (u32), size (u32), daddr, saddr, ... (addresses are 4 bytes for IPv4, 16 for IPv6).
        byte* d = (byte*)r->UserData;
        ushort id = r->Id;
        bool v6 = id is 26 or 27 or 58 or 59;
        if (d == null || r->UserDataLength < (v6 ? 40 : 16)) return;
        if (v6 ? IsLoopback6(d + 8) || IsLoopback6(d + 24) : d[8] == 127 || d[12] == 127) return;

        int pid = (int)*(uint*)d;
        long size = *(uint*)(d + 4);
        bool send = id is 10 or 26 or 42 or 58;
        lock (_lock)
        {
            var t = _perPid.GetValueOrDefault(pid);
            _perPid[pid] = send ? (t.Down, t.Up + size) : (t.Down + size, t.Up);
        }
    }

    static bool IsLoopback6(byte* a)
    {
        for (int i = 0; i < 15; i++) if (a[i] != 0) return false;
        return a[15] == 1;
    }

    // ETW delivers network events in bursts, so rates are averaged over the last few intervals.
    readonly Queue<(double Secs, Dictionary<int, (long Down, long Up)> Bytes)> _window = new();
    const int WindowIntervals = 3;

    /// <summary>Top apps by traffic over the last ~3 s, in bytes per second, grouped by exe.</summary>
    public List<AppTraffic> TakeTopApps(int count)
    {
        long now = Stopwatch.GetTimestamp();
        List<KeyValuePair<int, (long Down, long Up)>> bytes;
        double secs;
        lock (_lock)
        {
            _window.Enqueue(((now - _perPidSince) / (double)Stopwatch.Frequency, _perPid));
            _perPid = new();
            _perPidSince = now;
            while (_window.Count > WindowIntervals) _window.Dequeue();
            secs = Math.Max(0.1, _window.Sum(w => w.Secs));
            bytes = _window.SelectMany(w => w.Bytes).ToList();
        }

        return bytes
            .Select(kv => (Name: Name(kv.Key), kv.Value.Down, kv.Value.Up))
            .GroupBy(x => x.Name.Exe, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AppTraffic(g.First().Name.Display, g.Sum(x => x.Down) / secs, g.Sum(x => x.Up) / secs))
            .Where(a => a.Down + a.Up > 0)
            .OrderByDescending(a => a.Down + a.Up)
            .Take(count)
            .ToList();
    }

    (string Exe, string Display) Name(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        if (pid is 0 or 4) n = ("System", "System");
        else
        {
            try
            {
                var p = Process.GetProcessById(pid);
                string desc = "";
                try { desc = p.MainModule?.FileVersionInfo.FileDescription ?? ""; } catch { }
                n = (p.ProcessName, desc.Length > 0 ? desc : p.ProcessName);
            }
            catch { n = ("pid " + pid, "pid " + pid); }
        }
        if (_names.Count > 500) _names.Clear();
        return _names[pid] = n;
    }

    public void Dispose() => StopPerApp();
}
