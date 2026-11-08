using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ZLauncher;

public partial class MainWindow : Window
{
    readonly LauncherSettings _settings = LauncherSettings.Load();
    readonly ObservableCollection<AppEntry> _apps = new();
    readonly ICollectionView _view;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _opacityTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    readonly DispatcherTimer _zboardTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    int _tab;
    bool _refreshing, _autostartBusy, _zboardBusy, _zboardPolling;
    IntPtr _hwnd;
    ZBoardSettingsWindow? _zboardSettings;

    const int HotkeyId = 0x5A4C;

    /// <summary>Shows the pill and the Apps side. Set to false to make Z Launcher a widgets-only panel.</summary>
    const bool AppsEnabled = true;

    public MainWindow()
    {
        InitializeComponent();
        if (_settings.Left is { } l && _settings.Top is { } t && IsOnScreen(l, t))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = l; Top = t;
        }

        AppList.ItemsSource = _apps;
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = o => Search.Text.Length == 0 || ((AppEntry)o).Name.Contains(Search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);
        foreach (var app in _settings.Apps) _apps.Add(AppEntry.FromFile(app.Path, app.Name));
        UpdateAppsStatus();

        _statusTimer.Tick += (_, _) => RefreshWidget();
        _zboardTimer.Tick += async (_, _) => await RefreshZBoard();
        _opacityTimer.Tick += (_, _) =>
        {
            _opacityTimer.Stop();
            double v = Math.Round(OpacitySlider.Value, 2);
            ZMonitorControl.Update(s => s["Opacity"] = v);
        };

        if (!AppsEnabled)
        {
            Pill.Visibility = Visibility.Collapsed;
            Viewport.Margin = new Thickness(0, 18, 0, 0);
            Height = 580;
        }
        Loaded += (_, _) => SelectTab(AppsEnabled ? Math.Clamp(_settings.Tab, 0, 1) : 1, animate: false);
        PreviewKeyDown += OnWindowKey;
        Closing += (_, _) => SavePosition();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) { _statusTimer.Stop(); _zboardTimer.Stop(); }
            else if (_tab == 1) _statusTimer.Start();
            else { _ = RefreshZBoard(); _zboardTimer.Start(); }
        };
    }

    // ---------- window chrome ----------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = Glass.Apply(this);
        HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
        RegisterHotKey(_hwnd, HotkeyId, 0x2 | 0x1 | 0x4000 /* Ctrl+Alt, no repeat */, 0x5A /* Z */);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x312 /* WM_HOTKEY */ && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            ToggleVisible();
        }
        return IntPtr.Zero;
    }

    void ToggleVisible()
    {
        if (WindowState != WindowState.Minimized && IsActive) { WindowState = WindowState.Minimized; return; }
        BringToFront();
    }

    void BringToFront()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false; // reliably take the foreground
        if (_tab == 0) { Search.Focus(); Search.SelectAll(); }
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
    }

    void OnMinimizeClick(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void OnCloseClick(object s, RoutedEventArgs e) => Close();

    void SavePosition()
    {
        if (WindowState != WindowState.Normal) return;
        _settings.Left = Left; _settings.Top = Top;
        _settings.Save();
    }

    static bool IsOnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft && top >= SystemParameters.VirtualScreenTop
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    void OnWindowKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.Control && AppsEnabled) { SelectTab(1 - _tab); e.Handled = true; }
        else if (e.Key == Key.Escape && !Search.IsKeyboardFocused) { WindowState = WindowState.Minimized; e.Handled = true; }
    }

    // ---------- pill + sliding pages ----------

    void OnTabClick(object sender, RoutedEventArgs e) => SelectTab(int.Parse((string)((FrameworkElement)sender).Tag));

    void SelectTab(int tab, bool animate = true)
    {
        _tab = tab;
        double width = Viewport.ActualWidth;
        Slide(StripX, -tab * width, animate ? 460 : 0, new ExponentialEase { Exponent = 6, EasingMode = EasingMode.EaseOut });
        Slide(ThumbX, tab * (PillThumb.ActualWidth > 0 ? PillThumb.ActualWidth : 112), animate ? 380 : 0, new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut });
        AppsTab.Foreground = tab == 0 ? (Brush)FindResource("Text") : (Brush)FindResource("Dim");
        WidgetsTab.Foreground = tab == 1 ? (Brush)FindResource("Text") : (Brush)FindResource("Dim");

        if (AppsEnabled && _settings.Tab != tab) { _settings.Tab = tab; _settings.Save(); }
        if (tab == 1) { RefreshWidget(); RefreshAutostart(); _statusTimer.Start(); _zboardTimer.Stop(); }
        else { _statusTimer.Stop(); Search.Focus(); _ = RefreshZBoard(); _zboardTimer.Start(); }
    }

    static void Slide(TranslateTransform t, double to, int ms, IEasingFunction ease)
    {
        if (ms == 0) { t.BeginAnimation(TranslateTransform.XProperty, null); t.X = to; return; }
        t.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });
    }

    void OnViewportSize(object sender, SizeChangedEventArgs e)
    {
        AppsPage.Width = WidgetsPage.Width = e.NewSize.Width;
        AppsPage.Height = WidgetsPage.Height = e.NewSize.Height;
        Slide(StripX, -_tab * e.NewSize.Width, 0, null!);
    }

    // ---------- Apps ----------

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        bool searching = Search.Text.Length > 0;
        SearchHint.Visibility = searching ? Visibility.Collapsed : Visibility.Visible;
        AppsLabel.Text = searching ? "RESULTS" : "MY APPS";
        _view.Refresh();
        UpdateAppsStatus();
    }

    bool ZBoardMatches => Search.Text.Length == 0 || "ZBoard".Contains(Search.Text.Trim(), StringComparison.CurrentCultureIgnoreCase);

    void UpdateAppsStatus()
    {
        bool none = _apps.Count == 0, searching = Search.Text.Length > 0;
        ZBoardSection.Visibility = ZBoardMatches ? Visibility.Visible : Visibility.Collapsed;
        AppsEmpty.Visibility = none && !searching ? Visibility.Visible : Visibility.Collapsed;
        AppsLabel.Visibility = searching && _view.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        AppsStatus.Visibility = searching && _view.IsEmpty && !ZBoardMatches ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddAppClick(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add an app to Z Launcher",
            Filter = "Apps and shortcuts (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|All files (*.*)|*.*",
            DereferenceLinks = false,
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true) AddApps(dlg.FileNames);
    }

    void OnAppsDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    void OnAppsDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files) AddApps(files);
    }

    void AddApps(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!System.IO.File.Exists(path) || _settings.Apps.Any(a => a.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) continue;
            var app = AppEntry.FromFile(path);
            _settings.Apps.Add(new LauncherSettings.MyApp { Name = app.Name, Path = app.Path });
            _apps.Add(app);
        }
        _settings.Save();
        UpdateAppsStatus();
    }

    void RemoveApp(AppEntry app)
    {
        _settings.Apps.RemoveAll(a => a.Path.Equals(app.Path, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        _apps.Remove(app);
        UpdateAppsStatus();
    }

    void OnSearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _view.Cast<AppEntry>().FirstOrDefault() is { } first)
        {
            Launch(first, admin: Keyboard.Modifiers.HasFlag(ModifierKeys.Control | ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Search.Text.Length > 0 && ZBoardMatches)
        {
            OpenZBoard(ZBoardControl.TailscaleUrl);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (Search.Text.Length > 0) Search.Clear();
            else WindowState = WindowState.Minimized;
            e.Handled = true;
        }
    }

    void OnTileClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is AppEntry app) Launch(app);
    }

    void Launch(AppEntry app, bool admin = false)
    {
        try
        {
            app.Launch(admin);
            Search.Clear();
            WindowState = WindowState.Minimized;
        }
        catch (Win32Exception) { /* UAC cancelled */ }
        catch (Exception ex) { AppsStatus.Text = $"Couldn't open {app.Name}: {ex.Message}"; AppsStatus.Visibility = Visibility.Visible; }
    }

    void OnTileMenu(object sender, ContextMenuEventArgs e)
    {
        var button = (Button)sender;
        if (button.DataContext is not AppEntry app || button.ContextMenu == null) return;
        var menu = button.ContextMenu;
        menu.Items.Clear();

        if (app.CanRunAsAdmin)
        {
            var admin = new MenuItem { Header = "Run as administrator" };
            admin.Click += (_, _) => Launch(app, admin: true);
            menu.Items.Add(admin);
        }
        var folder = new MenuItem { Header = "Open file location" };
        folder.Click += (_, _) => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{app.Path}\"") { UseShellExecute = true });
        menu.Items.Add(folder);
        menu.Items.Add(new Separator());
        var remove = new MenuItem { Header = "Remove from Z Launcher" };
        remove.Click += (_, _) => RemoveApp(app);
        menu.Items.Add(remove);
    }

    // ---------- Apps: ZBoard (built in) ----------

    async Task RefreshZBoard()
    {
        if (_zboardBusy || _zboardPolling) return;
        _zboardPolling = true;
        try { ShowZBoardState(await ZBoardControl.IsRunningAsync()); }
        finally { _zboardPolling = false; }
    }

    void ShowZBoardState(bool running, string? message = null)
    {
        if (_zboardBusy) return;
        ZBoardStatus.Text = message ?? (running ? "Running on localhost:3000" : "Stopped");
        ZBoardDot.Fill = (Brush)FindResource(running ? "Green" : "Faint");
        ZBoardStart.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        ZBoardRestart.Visibility = ZBoardStop.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ZBoardStart.IsEnabled = ZBoardRestart.IsEnabled = ZBoardStop.IsEnabled = true;
    }

    void SetZBoardBusy(string text)
    {
        _zboardBusy = true;
        ZBoardStatus.Text = text;
        ZBoardDot.Fill = (Brush)FindResource("Accent");
        ZBoardStart.IsEnabled = ZBoardRestart.IsEnabled = ZBoardStop.IsEnabled = false;
    }

    async void OnZBoardStart(object sender, RoutedEventArgs e)
    {
        if (_zboardBusy) return;
        bool restart = sender == ZBoardRestart;
        SetZBoardBusy(restart ? "Restarting..." : "Starting...");
        var (ok, message) = await ZBoardControl.StartAsync();
        _zboardBusy = false;
        bool running = await ZBoardControl.IsRunningAsync();
        ShowZBoardState(running, ok ? null : message);
    }

    async void OnZBoardStop(object sender, RoutedEventArgs e)
    {
        if (_zboardBusy) return;
        SetZBoardBusy("Stopping...");
        bool stopped = await ZBoardControl.StopAsync();
        _zboardBusy = false;
        ShowZBoardState(!stopped, stopped ? null : "Still running (couldn't stop it)");
    }

    void OnZBoardOpen(object sender, RoutedEventArgs e) => OpenZBoard(ZBoardControl.TailscaleUrl);

    void OnZBoardOpenMore(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var menu = new ContextMenu();
        AddOpenItems(menu);
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    void AddOpenItems(ItemsControl menu)
    {
        var tailscale = new MenuItem { Header = "Open via Tailscale", InputGestureText = "zboard.coat-halosaur.ts.net" };
        tailscale.Click += (_, _) => OpenZBoard(ZBoardControl.TailscaleUrl);
        menu.Items.Add(tailscale);
        var local = new MenuItem { Header = "Open on this PC", InputGestureText = "localhost:3000" };
        local.Click += (_, _) => OpenZBoard(ZBoardControl.LocalUrl);
        menu.Items.Add(local);
    }

    void OpenZBoard(string url)
    {
        try
        {
            ZBoardControl.Open(url);
            Search.Clear();
            WindowState = WindowState.Minimized;
        }
        catch (Exception ex) { ZBoardStatus.Text = "Couldn't open the browser: " + ex.Message; }
    }

    void OnZBoardMenu(object sender, ContextMenuEventArgs e)
    {
        var menu = ZBoardCard.ContextMenu!;
        menu.Items.Clear();
        AddOpenItems(menu);
        menu.Items.Add(new Separator());
        bool running = ZBoardStop.Visibility == Visibility.Visible;
        foreach (var (header, handler, enabled) in new (string, RoutedEventHandler, bool)[]
        {
            ("Start", OnZBoardStart, !running),
            ("Restart", (s, a) => OnZBoardStart(ZBoardRestart, a), running),
            ("Stop", OnZBoardStop, running),
        })
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled && !_zboardBusy };
            item.Click += handler;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var settings = new MenuItem { Header = "Settings..." };
        settings.Click += OnZBoardSettings;
        menu.Items.Add(settings);
        var log = new MenuItem { Header = "Open server log" };
        log.Click += (_, _) => { if (System.IO.File.Exists(ZBoardControl.ServerLog)) ZBoardControl.OpenFile(ZBoardControl.ServerLog); };
        menu.Items.Add(log);
    }

    void OnZBoardSettings(object sender, RoutedEventArgs e)
    {
        if (_zboardSettings is { IsLoaded: true }) { _zboardSettings.Activate(); return; }
        _zboardSettings = new ZBoardSettingsWindow { Owner = this };
        _zboardSettings.Restarted += () => _ = RefreshZBoard();
        _zboardSettings.Closed += (_, _) => _zboardSettings = null;
        _zboardSettings.Show();
    }

    // ---------- Widgets: ZMonitor ----------

    void RefreshWidget()
    {
        _refreshing = true;
        try
        {
            bool installed = ZMonitorControl.IsInstalled, running = ZMonitorControl.IsRunning;
            var s = ZMonitorControl.Read();
            bool visible = ZMonitorControl.Get(s, "Visible", true);

            (string text, string brush) = !installed ? ("Not installed (run build.cmd)", "Faint")
                : !running ? ("Off", "Faint")
                : visible ? ("Running", "Green")
                : ("Hidden, paused", "Dim");
            StatusText.Text = text;
            StatusDot.Fill = (Brush)FindResource(brush);

            MonitorSwitch.IsEnabled = installed;
            MonitorSwitch.IsChecked = running && visible;
            foreach (ToggleButton chip in SectionChips.Children)
                chip.IsChecked = ZMonitorControl.Get(s, (string)chip.Tag, true);
            TopmostSwitch.IsChecked = ZMonitorControl.Get(s, "Topmost", true);
            LockSwitch.IsChecked = ZMonitorControl.Get(s, "Locked", false);
            if (!OpacitySlider.IsMouseCaptureWithin && !_opacityTimer.IsEnabled)
                OpacitySlider.Value = ZMonitorControl.Get(s, "Opacity", 1.0);
            NextMonitorButton.IsEnabled = QuitButton.IsEnabled = running;
            AutostartSwitch.IsEnabled = installed && !_autostartBusy;
        }
        finally { _refreshing = false; }
    }

    // Toggle handlers listen to Checked/Unchecked (so mouse, keyboard and accessibility tools all work);
    // _refreshing marks changes made by the launcher itself, which must not be written back.
    void SetQuietly(ToggleButton toggle, bool value)
    {
        bool was = _refreshing;
        _refreshing = true;
        toggle.IsChecked = value;
        _refreshing = was;
    }

    void RefreshAutostart()
    {
        Task.Run(ZMonitorControl.TaskExists).ContinueWith(t => SetQuietly(AutostartSwitch, t.Result), TaskScheduler.FromCurrentSynchronizationContext());
    }

    void OnMonitorSwitch(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        bool on = MonitorSwitch.IsChecked == true;
        ZMonitorControl.Update(s => s["Visible"] = on);
        if (on && !ZMonitorControl.IsRunning && !ZMonitorControl.Start())
            SetQuietly(MonitorSwitch, false); // UAC prompt was cancelled
        DelayedRefresh();
    }

    void OnSectionChip(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        var chip = (ToggleButton)sender;
        bool on = chip.IsChecked == true;
        ZMonitorControl.Update(s => s[(string)chip.Tag] = on);
    }

    void OnOptionSwitch(object sender, RoutedEventArgs e)
    {
        if (_refreshing) return;
        var sw = (ToggleButton)sender;
        bool on = sw.IsChecked == true;
        ZMonitorControl.Update(s => s[(string)sw.Tag] = on);
    }

    async void OnAutostartSwitch(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _autostartBusy) return;
        bool on = AutostartSwitch.IsChecked == true;
        _autostartBusy = true;
        AutostartSwitch.IsEnabled = false;
        bool ok = await Task.Run(() => ZMonitorControl.SetAutostart(on));
        SetQuietly(AutostartSwitch, ok ? on : !on);
        _autostartBusy = false;
        AutostartSwitch.IsEnabled = true;
    }

    void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_refreshing || !IsLoaded) return;
        _opacityTimer.Stop();
        _opacityTimer.Start(); // debounce while dragging
    }

    void OnNextMonitorClick(object s, RoutedEventArgs e) => ZMonitorControl.Post(ZMonitorControl.CmdNextMonitor);

    void OnQuitClick(object s, RoutedEventArgs e)
    {
        ZMonitorControl.Post(ZMonitorControl.CmdQuit);
        DelayedRefresh();
    }

    void DelayedRefresh()
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        t.Tick += (_, _) => { t.Stop(); RefreshWidget(); };
        t.Start();
    }

    // ---------- Win32 ----------

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
}
