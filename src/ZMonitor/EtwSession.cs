using System.Runtime.InteropServices;

namespace ZMonitor;

/// <summary>Minimal real-time ETW session: start, enable providers (with an event-ID filter), consume on a background thread.</summary>
sealed unsafe class EtwSession : IDisposable
{
    public delegate void EventHandler(EventRecord* record);

    readonly string _name;
    readonly EventHandler _handler;
    GCHandle _self;
    IntPtr _props, _logFile, _loggerName;
    ulong _session, _trace = ulong.MaxValue;
    Thread? _thread;

    public string? LastError { get; private set; }

    public EtwSession(string name, EventHandler handler)
    {
        _name = name;
        _handler = handler;
    }

    public bool Start()
    {
        _props = AllocProperties();
        ControlTraceW(0, _name, _props, EVENT_TRACE_CONTROL_STOP); // leftover from a crash
        new Span<byte>((void*)_props, PropertiesSize + 1024).Clear();
        InitProperties(_props);
        int err = StartTraceW(out _session, _name, _props);
        if (err != 0) { LastError = $"StartTrace failed ({err})"; return false; }
        return true;
    }

    /// <summary>Starts consuming. Call after the providers are enabled.</summary>
    public bool Consume()
    {
        _self = GCHandle.Alloc(this);
        _loggerName = Marshal.StringToHGlobalUni(_name);
        _logFile = Marshal.AllocHGlobal(LogFileSize);
        new Span<byte>((void*)_logFile, LogFileSize).Clear();
        var lf = (byte*)_logFile;
        *(IntPtr*)(lf + 8) = _loggerName;                                                 // LoggerName
        *(uint*)(lf + 28) = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD | PROCESS_TRACE_MODE_RAW_TIMESTAMP;
        *(IntPtr*)(lf + 424) = (IntPtr)(delegate* unmanaged<EventRecord*, void>)&OnEvent; // EventRecordCallback
        *(IntPtr*)(lf + 440) = GCHandle.ToIntPtr(_self);                                  // Context -> EventRecord.UserContext

        _trace = OpenTraceW(_logFile);
        if (_trace == ulong.MaxValue) { LastError = $"OpenTrace failed ({Marshal.GetLastWin32Error()})"; return false; }

        _thread = new Thread(() => { var h = _trace; ProcessTrace(&h, 1, IntPtr.Zero, IntPtr.Zero); })
        { IsBackground = true, Name = "ETW " + _name, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        return true;
    }

    public void Enable(Guid provider, byte level, ulong keywords, params ushort[] eventIds)
    {
        // EVENT_FILTER_EVENT_ID { BOOLEAN FilterIn; UCHAR Reserved; USHORT Count; USHORT Events[] }
        int filterSize = 4 + 2 * eventIds.Length;
        byte* filter = stackalloc byte[filterSize];
        filter[0] = 1; filter[1] = 0;
        *(ushort*)(filter + 2) = (ushort)eventIds.Length;
        for (int i = 0; i < eventIds.Length; i++) *(ushort*)(filter + 4 + 2 * i) = eventIds[i];

        // EVENT_FILTER_DESCRIPTOR { ULONGLONG Ptr; ULONG Size; ULONG Type }
        byte* desc = stackalloc byte[16];
        *(ulong*)desc = (ulong)filter;
        *(uint*)(desc + 8) = (uint)filterSize;
        *(uint*)(desc + 12) = 0x80000200; // EVENT_FILTER_TYPE_EVENT_ID

        // ENABLE_TRACE_PARAMETERS (v2): Version, EnableProperty, ControlFlags, SourceId, EnableFilterDesc, FilterDescCount
        byte* p = stackalloc byte[48];
        new Span<byte>(p, 48).Clear();
        *(uint*)p = 2;
        *(IntPtr*)(p + 32) = (IntPtr)desc;
        *(uint*)(p + 40) = 1;

        int err = EnableTraceEx2(_session, &provider, 1 /* ENABLE */, level, keywords, 0, 0, (IntPtr)p);
        if (err != 0) // filtering unsupported: fall back to unfiltered
            err = EnableTraceEx2(_session, &provider, 1, level, keywords, 0, 0, IntPtr.Zero);
        if (err != 0) LastError = $"EnableTraceEx2 {provider} failed ({err})";
    }

    [UnmanagedCallersOnly]
    static void OnEvent(EventRecord* r)
    {
        try
        {
            if (r->UserContext != IntPtr.Zero && GCHandle.FromIntPtr(r->UserContext).Target is EtwSession s) s._handler(r);
        }
        catch { /* never throw into ETW */ }
    }

    public void Dispose()
    {
        if (_props != IntPtr.Zero) ControlTraceW(_session, null, _props, EVENT_TRACE_CONTROL_STOP);
        if (_trace != ulong.MaxValue) CloseTrace(_trace);
        _thread?.Join(1000);
        if (_self.IsAllocated) _self.Free();
        if (_props != IntPtr.Zero) Marshal.FreeHGlobal(_props);
        if (_logFile != IntPtr.Zero) Marshal.FreeHGlobal(_logFile);
        if (_loggerName != IntPtr.Zero) Marshal.FreeHGlobal(_loggerName);
        _props = _logFile = _loggerName = IntPtr.Zero;
        _trace = ulong.MaxValue;
    }

    static IntPtr AllocProperties()
    {
        var p = Marshal.AllocHGlobal(PropertiesSize + 1024);
        new Span<byte>((void*)p, PropertiesSize + 1024).Clear();
        InitProperties(p);
        return p;
    }

    static void InitProperties(IntPtr p)
    {
        var b = (byte*)p;
        *(uint*)(b + 0) = (uint)(PropertiesSize + 1024); // Wnode.BufferSize
        *(uint*)(b + 40) = 1;                            // Wnode.ClientContext = QPC timestamps
        *(uint*)(b + 44) = 0x20000;                      // WNODE_FLAG_TRACED_GUID
        *(uint*)(b + 48) = 64;                           // BufferSize (KB)
        *(uint*)(b + 52) = 4;                            // MinimumBuffers
        *(uint*)(b + 56) = 32;                           // MaximumBuffers
        *(uint*)(b + 64) = EVENT_TRACE_REAL_TIME_MODE | EVENT_TRACE_USE_MS_FLUSH_TIMER;
        *(uint*)(b + 68) = 200;                          // FlushTimer (ms)
        *(uint*)(b + 116) = PropertiesSize;              // LoggerNameOffset
    }

    [StructLayout(LayoutKind.Explicit, Size = 112)]
    public struct EventRecord
    {
        [FieldOffset(4)] public ushort Flags;
        [FieldOffset(12)] public uint ProcessId;
        [FieldOffset(16)] public long TimeStamp;
        [FieldOffset(24)] public Guid ProviderId;
        [FieldOffset(40)] public ushort Id;
        [FieldOffset(86)] public ushort UserDataLength;
        [FieldOffset(96)] public IntPtr UserData;
        [FieldOffset(104)] public IntPtr UserContext;
    }

    const int PropertiesSize = 120;
    const int LogFileSize = 448;
    const uint EVENT_TRACE_CONTROL_STOP = 1;
    const uint EVENT_TRACE_REAL_TIME_MODE = 0x100;
    const uint EVENT_TRACE_USE_MS_FLUSH_TIMER = 0x10;
    const uint PROCESS_TRACE_MODE_REAL_TIME = 0x100;
    const uint PROCESS_TRACE_MODE_RAW_TIMESTAMP = 0x1000;
    const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint control);

    [DllImport("advapi32.dll")]
    static extern int EnableTraceEx2(ulong handle, Guid* provider, uint control, byte level,
        ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    static extern int ProcessTrace(ulong* handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    static extern int CloseTrace(ulong handle);
}
