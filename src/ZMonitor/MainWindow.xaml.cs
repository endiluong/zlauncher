using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZMonitor;

public partial class MainWindow : Window
{
    readonly Settings _settings = Settings.Load();
    readonly HardwareSensors _sensors = new();
    readonly FrameRateTracker _fps = new();
    readonly int _ownPid = Environment.ProcessId;
    readonly Dictionary<int, string> _names = new();
    readonly GameDetector _games;
    readonly NetworkMonitor _net = new();
    Process? _game;
    Timer? _timer, _fpsTimer;
    int _busy, _fpsBusy;
    int _fpsPeriod = FpsActiveMs;
    bool _idleShown, _sensorsReady, _positioned;
    IntPtr _hwnd;

    // Idle mode: with no game, look for one once a second and leave the FPS row alone.
    const int FpsActiveMs = 250, FpsIdleMs = 1000;

    public MainWindow()
    {
        InitializeComponent();
        _games = new GameDetector(_settings);
        // Launching the widget always shows it (Z Launcher may have hidden it last time).
        if (!_settings.Visible) { _settings.Visible = true; _settings.Save(); }
        ApplySettings();
        ContentRendered += OnFirstRender;
        SizeChanged += (_, _) => KeepOnScreen();
        Closed += (_, _) => Stop();
    }

    // ---------- window / glass ----------

    void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;
        RestorePosition();
        Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var src = HwndSource.FromHwnd(_hwnd);
        src.CompositionTarget.BackgroundColor = Colors.Transparent;
        src.AddHook(WndProc);

        // Tool window: no taskbar button, not in Alt+Tab.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr((ex | Native.WS_EX_TOOLWINDOW) & ~Native.WS_EX_APPWINDOW));
        // No system menu: otherwise DWM paints a caption close button over the extended glass frame.
        long style = Native.GetWindowLongPtr(_hwnd, Native.GWL_STYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_STYLE, new IntPtr(style & ~Native.WS_SYSMENU));

        int on = 1, round = Native.DWMWCP_ROUND, border = 0x00403C3A; // COLORREF 0x00BBGGRR
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, 4);
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_BORDER_COLOR, ref border, 4);
        ApplyBackdrop();

        // Z Launcher runs without admin; let its command message through UIPI to this elevated window.
        Native.ChangeWindowMessageFilterEx(_hwnd, CommandMessage, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);
    }

    // ---------- Z Launcher commands ----------

    /// <summary>Posted by Z Launcher. wParam: 1 = reload settings.json, 2 = quit, 3 = move to next monitor.</summary>
    static readonly int CommandMessage = Native.RegisterWindowMessage("ZMonitor.Command");

    void HandleCommand(int command)
    {
        switch (command)
        {
            case 1: ReloadSettings(); break;
            case 2: Close(); break;
            case 3: OnNextMonitorClick(this, new RoutedEventArgs()); break;
        }
    }

    void ReloadSettings()
    {
        _settings.ReloadFrom(Settings.Load());
        ApplySettings();
        ApplyBackdrop();
    }

    /// <summary>Applies everything Z Launcher can change: visibility, sections, window options.</summary>
    void ApplySettings()
    {
        Topmost = MenuTopmost.IsChecked = _settings.Topmost;
        MenuLocked.IsChecked = _settings.Locked;
        Opacity = Math.Clamp(_settings.Opacity, 0.3, 1);

        FpsCard.Visibility = Vis(_settings.ShowFps);
        CpuCard.Visibility = Vis(_settings.ShowCpu);
        GpuCard.Visibility = Vis(_settings.ShowGpu);
        RamCard.Visibility = Vis(_settings.ShowRam);
        NetCard.Visibility = Vis(_settings.ShowNet);

        ApplySection("Fps", _settings.FpsExpanded);
        ApplySection("Cpu", _settings.CpuExpanded);
        ApplySection("Gpu", _settings.GpuExpanded);
        ApplySection("Ram", _settings.RamExpanded);
        ApplySection("Net", _settings.NetworkExpanded);
        SetVisible(_settings.Visible);
    }

    /// <summary>Hidden = window gone and all polling paused (costs ~nothing); shown = resume.</summary>
    void SetVisible(bool visible)
    {
        if (visible == IsVisible) return;
        if (visible)
        {
            Show();
            _idleShown = false;
            _fpsPeriod = FpsActiveMs;
            _fpsTimer?.Change(0, FpsActiveMs);
            _timer?.Change(0, 1000);
        }
        else
        {
            _timer?.Change(Timeout.Infinite, Timeout.Infinite);
            _fpsTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            Hide();
        }
        SetNetworkExpanded(_settings.NetworkExpanded);
    }

    void OnHideClick(object s, RoutedEventArgs e)
    {
        _settings.Visible = false;
        _settings.Save();
        SetVisible(false);
    }

    void ApplyBackdrop()
    {
        // Windows 11 acrylic (DWM system backdrop) behind the whole client area.
        var m = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(_hwnd, ref m);
        int backdrop = Native.DWMSBT_TRANSIENTWINDOW;
        Native.DwmSetWindowAttribute(_hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);

        try { Root.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_settings.Tint)); }
        catch (FormatException) { }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Keep the acrylic in its "active" look when focus moves to the game; otherwise DWM swaps it for a flat fill.
        if (msg == Native.WM_NCACTIVATE && wParam == IntPtr.Zero)
        {
            handled = true;
            return Native.DefWindowProc(hwnd, msg, new IntPtr(1), lParam);
        }
        if (msg == CommandMessage)
        {
            handled = true;
            HandleCommand(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_settings.Locked || e.ButtonState != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SavePosition();
    }

    void RestorePosition()
    {
        var monitors = Native.GetMonitors();
        if (monitors.Count == 0) return;
        Native.GetWindowRect(_hwnd, out var r);
        int w = r.Right - r.Left, h = r.Bottom - r.Top;

        var mon = _settings.HasPosition ? monitors.FirstOrDefault(m => m.Device == _settings.Monitor) : null;
        int x, y;
        if (mon != null)
        {
            x = mon.Bounds.Left + _settings.OffsetX;
            y = mon.Bounds.Top + _settings.OffsetY;
        }
        else
        {
            // First run (or saved monitor gone): top-right of a secondary monitor if there is one.
            mon = monitors.FirstOrDefault(m => !m.Primary) ?? monitors[0];
            x = mon.Work.Right - w - 24;
            y = mon.Work.Top + 24;
        }
        x = Math.Clamp(x, mon.Bounds.Left, Math.Max(mon.Bounds.Left, mon.Bounds.Right - w));
        y = Math.Clamp(y, mon.Bounds.Top, Math.Max(mon.Bounds.Top, mon.Bounds.Bottom - h));
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SavePosition();
        _positioned = true;
        KeepOnScreen();
    }

    /// <summary>
    /// The saved position is the anchor. If the widget grows (network card expanded) past the bottom of the
    /// monitor, slide it up just enough; when it shrinks again it returns to the anchor.
    /// </summary>
    void KeepOnScreen()
    {
        if (!_positioned) return;
        var mon = Native.GetMonitors().FirstOrDefault(m => m.Device == _settings.Monitor);
        if (mon == null || !Native.GetWindowRect(_hwnd, out var r)) return;
        int h = r.Bottom - r.Top;
        int x = mon.Bounds.Left + _settings.OffsetX;
        int y = Math.Max(mon.Work.Top, Math.Min(mon.Bounds.Top + _settings.OffsetY, mon.Work.Bottom - h));
        if (x != r.Left || y != r.Top)
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    void SavePosition()
    {
        if (!Native.GetWindowRect(_hwnd, out var r)) return;
        var mon = Native.MonitorOf(_hwnd);
        if (mon == null) return;
        _settings.Monitor = mon.Device;
        _settings.OffsetX = r.Left - mon.Bounds.Left;
        _settings.OffsetY = r.Top - mon.Bounds.Top;
        _settings.HasPosition = true;
        _settings.Save();
    }

    void OnTopmostClick(object s, RoutedEventArgs e) { Topmost = _settings.Topmost = MenuTopmost.IsChecked; _settings.Save(); }
    void OnLockClick(object s, RoutedEventArgs e) { _settings.Locked = MenuLocked.IsChecked; _settings.Save(); }
    void OnExitClick(object s, RoutedEventArgs e) => Close();

    // ---------- collapsible sections ----------

    static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
    static string Chevron(bool expanded) => expanded ? "" : ""; // ChevronDown / ChevronRight

    void OnSectionToggle(object sender, RoutedEventArgs e)
    {
        string key = (string)((FrameworkElement)sender).Tag;
        bool open = key switch
        {
            "Fps" => _settings.FpsExpanded = !_settings.FpsExpanded,
            "Cpu" => _settings.CpuExpanded = !_settings.CpuExpanded,
            "Gpu" => _settings.GpuExpanded = !_settings.GpuExpanded,
            "Ram" => _settings.RamExpanded = !_settings.RamExpanded,
            _ => _settings.NetworkExpanded = !_settings.NetworkExpanded,
        };
        _settings.Save();
        ApplySection(key, open);
    }

    void ApplySection(string key, bool open)
    {
        switch (key)
        {
            case "Fps": // collapsed: only the FPS number
                FpsLowPanel.Visibility = FpsDetail.Visibility = Vis(open);
                FpsChevron.Text = Chevron(open);
                break;
            case "Cpu": // collapsed: usage + temp in the header
                CpuBody.Visibility = CpuName.Visibility = Vis(open);
                CpuSummary.Visibility = Vis(!open);
                CpuChevron.Text = Chevron(open);
                break;
            case "Gpu":
                GpuBody.Visibility = GpuName.Visibility = Vis(open);
                GpuSummary.Visibility = Vis(!open);
                GpuChevron.Text = Chevron(open);
                break;
            case "Ram": // collapsed: used / total in the header
                RamBody.Visibility = RamPctHeader.Visibility = Vis(open);
                RamSummary.Visibility = Vis(!open);
                RamChevron.Text = Chevron(open);
                break;
            default:
                SetNetworkExpanded(open);
                break;
        }
    }

    void SetNetworkExpanded(bool expanded)
    {
        NetBody.Visibility = Vis(expanded);
        NetChevron.Text = Chevron(expanded);
        if (expanded) { NetApps.ItemsSource = null; NetAppsEmpty.Text = "Measuring..."; NetAppsEmpty.Visibility = Visibility.Visible; }
        // Per-app capture only runs while the card is open and on screen.
        bool capture = expanded && _settings.Visible && _settings.ShowNet;
        Task.Run(() => { if (capture) _net.StartPerApp(); else _net.StopPerApp(); });
    }

    void OnNextMonitorClick(object s, RoutedEventArgs e)
    {
        var monitors = Native.GetMonitors();
        var cur = Native.MonitorOf(_hwnd);
        int i = monitors.FindIndex(m => m.Device == cur?.Device);
        var next = monitors[(i + 1) % monitors.Count];
        _settings.Monitor = next.Device;
        _settings.HasPosition = false;
        Native.GetWindowRect(_hwnd, out var r);
        Native.SetWindowPos(_hwnd, IntPtr.Zero, next.Work.Right - (r.Right - r.Left) - 24, next.Work.Top + 24, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        SavePosition();
    }

    // ---------- data ----------

    void Start()
    {
        Task.Run(() =>
        {
            if (!_fps.Start()) App.Log("FPS capture: " + _fps.LastError);
            _fpsTimer = new Timer(_ => FpsTick(), null, 0, FpsActiveMs);
            try { _sensors.Open(); _sensorsReady = true; } catch (Exception ex) { App.Log("Sensors: " + ex); }
            _timer = new Timer(_ => SensorTick(), null, 0, 1000);
        });
    }

    void Stop()
    {
        _timer?.Dispose();
        _fpsTimer?.Dispose();
        _fps.Dispose();
        _net.Dispose();
        if (_sensorsReady) _sensors.Dispose();
    }

    void SensorTick()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            var snap = _sensorsReady ? _sensors.Read() : new Snapshot();
            var (down, up) = _net.ReadTotals();
            var apps = _net.PerAppRunning ? _net.TakeTopApps(3) : null;
            Dispatcher.BeginInvoke(() => { Render(snap); RenderNetwork(down, up, apps); });
        }
        catch (Exception ex) { App.Log("Sensor tick: " + ex.Message); }
        finally { Volatile.Write(ref _busy, 0); }
    }

    void FpsTick()
    {
        if (Interlocked.Exchange(ref _fpsBusy, 1) == 1) return;
        try
        {
            // Only games: pick up the foreground app when it is a game, then stay on it
            // (also while alt-tabbed) until it exits.
            var hwnd = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(hwnd, out uint fg);
            if (fg != 0 && fg != _ownPid && fg != _game?.Id && _games.IsGame((int)fg, hwnd))
            {
                try { _game?.Dispose(); _game = Process.GetProcessById((int)fg); } catch { _game = null; }
            }
            if (_game != null && HasExited(_game)) { _game.Dispose(); _game = null; }

            bool idle = _game == null;
            int period = idle ? FpsIdleMs : FpsActiveMs;
            if (period != _fpsPeriod) { _fpsPeriod = period; _fpsTimer?.Change(period, period); }
            if (idle && _idleShown) return; // "No game running" is already on screen: nothing to redraw
            _idleShown = idle;

            var fps = _game != null ? _fps.Read(_game.Id) : null;
            string name = _game != null ? ProcessName(_game.Id) : "";
            Dispatcher.BeginInvoke(() => RenderFps(fps, name));
        }
        catch (Exception ex) { App.Log("FPS tick: " + ex.Message); }
        finally { Volatile.Write(ref _fpsBusy, 0); }
    }

    static bool HasExited(Process p)
    {
        try { return p.HasExited; } catch { return true; }
    }

    string ProcessName(int pid)
    {
        if (_names.TryGetValue(pid, out var n)) return n;
        try
        {
            var p = Process.GetProcessById(pid);
            n = p.MainModule?.FileVersionInfo.FileDescription is { Length: > 0 } d ? d : p.ProcessName;
        }
        catch { n = "pid " + pid; }
        if (_names.Count > 200) _names.Clear();
        return _names[pid] = n;
    }

    static string F(double? v, string fmt = "0") => v.HasValue && !double.IsNaN(v.Value) ? v.Value.ToString(fmt) : "--";

    /// <summary>Bytes/s as ("12.3", " MB/s").</summary>
    static (string Value, string Unit) Rate(double bps) =>
        bps >= 1e6 ? ((bps / 1e6).ToString(bps >= 1e8 ? "0" : "0.0"), " MB/s")
        : bps >= 1e3 ? ((bps / 1e3).ToString("0"), " KB/s")
        : (bps.ToString("0"), " B/s");

    static string RateText(double bps) { var (v, u) = Rate(bps); return v + u; }

    sealed record NetAppRow(string Name, string Down, string Up);

    void RenderNetwork(double down, double up, List<NetworkMonitor.AppTraffic>? apps)
    {
        NetDownSummary.Text = RateText(down);
        NetUpSummary.Text = RateText(up);
        NetDownGraph.Push(down);
        NetUpGraph.Push(up);
        if (NetBody.Visibility != Visibility.Visible) return;

        (NetDown.Text, NetDownUnit.Text) = Rate(down);
        (NetUp.Text, NetUpUnit.Text) = Rate(up);
        if (apps == null) return;
        NetApps.ItemsSource = apps.Select(a => new NetAppRow(a.Name, RateText(a.Down), RateText(a.Up))).ToList();
        NetAppsEmpty.Text = "No traffic";
        NetAppsEmpty.Visibility = apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void RenderFps(FrameRateTracker.FpsReading? fps, string game)
    {
        AppName.Text = string.IsNullOrEmpty(game) ? "No game running" : game;
        if (fps is not { } f)
        {
            FpsValue.Text = LowValue.Text = FrameTime.Text = FrameMax.Text = "--";
            FrameGraph.SetValues(Array.Empty<double>());
            return;
        }
        FpsValue.Text = f.Fps.ToString("0");
        LowValue.Text = f.Low1Pct.ToString("0");
        FrameTime.Text = f.FrameTimeMs.ToString("0.0");
        FrameMax.Text = f.FrameTimes.Length > 0 ? f.FrameTimes.Max().ToString("0.0") : "--";
        // Scale to at least 2x the average frame time so normal jitter stays flat and real spikes stand out.
        FrameGraph.MinimumMax = Math.Max(8, f.FrameTimeMs * 2);
        FrameGraph.SetValues(f.FrameTimes);
    }

    void Render(Snapshot s)
    {
        CpuName.Text = s.CpuName;
        CpuLoad.Text = F(s.CpuLoad);
        CpuPower.Text = F(s.CpuPower);
        CpuTemp.Text = F(s.CpuTemp);
        CpuClock.Text = F(s.CpuClockGHz, "0.00");
        if (s.CpuClockGHz is { } cc) CpuGraph.Push(cc);

        GpuName.Text = s.GpuName;
        GpuLoad.Text = F(s.GpuLoad);
        GpuPower.Text = F(s.GpuPower);
        GpuTemp.Text = F(s.GpuTemp);
        GpuClock.Text = F(s.GpuClockGHz, "0.00");
        if (s.GpuClockGHz is { } gc) GpuGraph.Push(gc);
        if (s.VramUsedMb is { } used && s.VramTotalMb is { } total && total > 0)
        {
            VramText.Text = $"{used / 1024:0.0} / {total / 1024:0}";
            VramBar.ScaleX = Math.Clamp(used / total, 0, 1);
        }
        else VramText.Text = s.VramUsedMb is { } u ? $"{u / 1024:0.0}" : "--";

        RamUsed.Text = s.RamTotalGb > 0 ? s.RamUsedGb.ToString("0.0") : "--";
        RamTotal.Text = $" / {s.RamTotalGb:0} GB";
        RamPct.Text = s.RamTotalGb > 0 ? s.RamLoad.ToString("0") : "--";
        RamBar.ScaleX = Math.Clamp(s.RamLoad / 100.0, 0, 1);

        // Collapsed-header summaries.
        CpuLoadS.Text = CpuLoad.Text;
        CpuTempS.Text = CpuTemp.Text;
        GpuLoadS.Text = GpuLoad.Text;
        GpuTempS.Text = GpuTemp.Text;
        RamUsedS.Text = RamUsed.Text;
        RamTotalS.Text = RamTotal.Text;
    }
}
