using System.Diagnostics;

namespace ZMonitor;

/// <summary>
/// Measures per-process present rate from a real-time ETW session on the same providers
/// PresentMon uses (DXGI, D3D9, DxgKrnl). Frame time = interval between successive presents.
/// </summary>
sealed unsafe class FrameRateTracker : IDisposable
{
    const string SessionName = "ZMonitor-FrameTimes";

    static readonly Guid DxgiGuid = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    static readonly Guid D3D9Guid = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    static readonly Guid DxgKrnlGuid = new("802EC45A-1E99-4B83-9920-87C98277BA9D");

    // Present_Start events (Microsoft-Windows-DXGI 42 / 55 MPO, Microsoft-Windows-D3D9 1)
    // and DxgKrnl Present_Info (184), which also covers Vulkan / OpenGL presents.
    const ushort DxgiPresentStart = 42, DxgiPresentMpoStart = 55, D3D9PresentStart = 1, DxgKrnlPresentInfo = 184;

    // Source priority: an API-level stream wins over the kernel stream for the same process.
    enum Source : byte { Dxgi = 0, D3D9 = 1, DxgKrnl = 2 }

    readonly record struct StreamKey(int Pid, Source Source, ulong SwapChain);

    sealed class Stream { public readonly Queue<long> Times = new(); }


    readonly Dictionary<StreamKey, Stream> _streams = new();
    readonly object _lock = new();
    readonly long _keepTicks = Stopwatch.Frequency * 12;
    EtwSession? _session;

    public Dictionary<(Guid, ushort, int), int>? ProbeCounts; // diagnostics only
    public string? LastError => _session?.LastError;

    public bool Start()
    {
        _session = new EtwSession("ZMonitor-FrameTimes", Handle);
        if (!_session.Start()) return false;
        // Event-ID filters keep ETW from delivering anything but presents (DxgKrnl's Present keyword is very chatty).
        _session.Enable(DxgiGuid, 4, 0x2, DxgiPresentStart, DxgiPresentMpoStart); // Events
        _session.Enable(D3D9Guid, 4, 0x2, D3D9PresentStart);                      // Events
        _session.Enable(DxgKrnlGuid, 4, 0x8000000, DxgKrnlPresentInfo);           // Present
        return _session.Consume();
    }
    void Handle(EtwSession.EventRecord* r)
    {
        Guid provider = r->ProviderId;
        ushort id = r->Id;
        Source source;
        ulong swapChain = 0;

        if (provider == DxgiGuid && (id == DxgiPresentStart || id == DxgiPresentMpoStart))
        {
            source = Source.Dxgi;
            if (r->UserData != IntPtr.Zero && r->UserDataLength >= 8)
                swapChain = (r->Flags & 0x20) != 0 ? *(uint*)r->UserData : *(ulong*)r->UserData;
        }
        else if (provider == D3D9Guid && id == D3D9PresentStart) source = Source.D3D9;
        else if (provider == DxgKrnlGuid && id == DxgKrnlPresentInfo) source = Source.DxgKrnl;
        else
        {
            if (ProbeCounts != null) lock (_lock) { var k = (provider, id, (int)r->ProcessId); ProbeCounts[k] = ProbeCounts.GetValueOrDefault(k) + 1; }
            return;
        }

        if (ProbeCounts != null) lock (_lock) { var k = (provider, id, (int)r->ProcessId); ProbeCounts[k] = ProbeCounts.GetValueOrDefault(k) + 1; }

        var key = new StreamKey((int)r->ProcessId, source, swapChain);
        long ts = r->TimeStamp;
        lock (_lock)
        {
            if (!_streams.TryGetValue(key, out var s)) _streams[key] = s = new Stream();
            s.Times.Enqueue(ts);
            long cutoff = ts - _keepTicks;
            while (s.Times.Count > 0 && s.Times.Peek() < cutoff) s.Times.Dequeue();
        }
    }

    /// <param name="FrameTimes">Per-frame times (ms) in present order, most recent last, for the frame-time graph.</param>
    public readonly record struct FpsReading(double Fps, double Low1Pct, double FrameTimeMs, double[] FrameTimes);

    /// <summary>FPS over the last second and the 1% low over the last ~10 s for a process.</summary>
    public FpsReading? Read(int pid)
    {
        long now = Stopwatch.GetTimestamp();
        double freq = Stopwatch.Frequency;
        long[] times;
        lock (_lock)
        {
            // Prune dead streams.
            foreach (var k in _streams.Where(kv => kv.Value.Times.Count == 0 || now - kv.Value.Times.Last() > _keepTicks).Select(kv => kv.Key).ToList())
                _streams.Remove(k);

            Stream? best = null;
            Source bestSource = Source.DxgKrnl;
            int bestCount = 0;
            foreach (var (k, s) in _streams)
            {
                if (k.Pid != pid) continue;
                int recent = s.Times.Count(t => now - t < freq * 2);
                if (recent < 2) continue;
                if (best == null || k.Source < bestSource || (k.Source == bestSource && recent > bestCount))
                { best = s; bestSource = k.Source; bestCount = recent; }
            }
            if (best == null) return null;
            times = best.Times.ToArray();
        }

        // Events arrive up to ~200 ms late (flush timer), so anchor the window at the newest present.
        long last = times[^1];
        if ((now - last) / freq > 2.0) return null;

        long oneSec = (long)freq;
        int first = times.Length - 1;
        while (first > 0 && last - times[first - 1] <= oneSec) first--;
        double fps = first < times.Length - 1 ? (times.Length - 1 - first) * freq / (last - times[first]) : 0;

        var frameMs = new double[times.Length - 1];
        for (int i = 1; i < times.Length; i++) frameMs[i - 1] = (times[i] - times[i - 1]) * 1000.0 / freq;
        if (frameMs.Length == 0) return null;
        var lastFrames = frameMs[Math.Max(0, frameMs.Length - 300)..];
        Array.Sort(frameMs);
        int n = Math.Max(1, frameMs.Length / 100);
        double slowAvg = 0;
        for (int i = frameMs.Length - n; i < frameMs.Length; i++) slowAvg += frameMs[i];
        slowAvg /= n;
        double low = slowAvg > 0 ? 1000.0 / slowAvg : 0;
        return new FpsReading(fps, Math.Min(low, fps), fps > 0 ? 1000.0 / fps : 0, lastFrames);
    }


    public void Dispose() => _session?.Dispose();
}