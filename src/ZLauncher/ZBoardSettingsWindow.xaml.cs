using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ZLauncher;

public partial class ZBoardSettingsWindow : Window
{
    EnvFile? _env;
    readonly List<(EnvFile.Entry? entry, TextBox? newKey, Func<string> value)> _rows = new();
    bool _refreshing, _bootBusy;
    DateTime _bootChecked;

    /// <summary>Raised after a successful restart so the main window can refresh the tile.</summary>
    public event Action? Restarted;

    public ZBoardSettingsWindow()
    {
        InitializeComponent();
        EnvPathText.Text = "~/zboard/.env  (" + ZBoardControl.EnvPath + ")";
        Loaded += async (_, _) => { LoadEnv(); await RefreshBoot(); };
        // Re-check the boot task whenever the window comes back to the front (it may have changed elsewhere).
        Activated += async (_, _) => { if (!_bootBusy && DateTime.Now - _bootChecked > TimeSpan.FromSeconds(3)) await RefreshBoot(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        Closing += (_, e) =>
        {
            if (_env == null || !HasEdits()) return;
            var r = MessageBox.Show(this, "Save your changes to ZBoard's .env?", "ZBoard settings", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) e.Cancel = true;
            else if (r == MessageBoxResult.Yes && !Save()) e.Cancel = true;
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Glass.Apply(this);
    }

    void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch (InvalidOperationException) { }
    }

    void OnCloseClick(object s, RoutedEventArgs e) => Close();

    // ---------- Environment ----------

    void LoadEnv()
    {
        EnvRows.Items.Clear();
        _rows.Clear();
        EnvError.Visibility = Visibility.Collapsed;
        try { _env = EnvFile.Load(ZBoardControl.EnvPath); }
        catch (Exception ex)
        {
            _env = null;
            ShowError(ex is FileNotFoundException or DirectoryNotFoundException
                ? "No .env found. Is WSL running and ZBoard at ~/zboard?" : "Couldn't read .env: " + ex.Message);
            SaveButton.IsEnabled = SaveRestartButton.IsEnabled = false;
            return;
        }
        SaveButton.IsEnabled = SaveRestartButton.IsEnabled = true;
        foreach (var entry in _env.Entries) AddRow(entry, null);
    }

    void AddRow(EnvFile.Entry? entry, TextBox? newKey)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        if (newKey != null) grid.Children.Add(newKey);
        else grid.Children.Add(new TextBlock
        {
            Text = entry!.Key, FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = entry.Key,
        });

        bool secret = entry?.IsSecret == true;
        var text = new TextBox { Style = (Style)FindResource("Field"), Text = entry?.Value ?? "", Margin = new Thickness(8, 0, 0, 0) };
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        Func<string> value = () => text.Text;

        if (secret)
        {
            var masked = new PasswordBox { Style = (Style)FindResource("SecretField"), Password = entry!.Value, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(masked, 1);
            grid.Children.Add(masked);
            text.Visibility = Visibility.Collapsed;
            var eye = new ToggleButton { Style = (Style)FindResource("EyeToggle"), ToolTip = "Show / hide" };
            Grid.SetColumn(eye, 2);
            eye.Checked += (_, _) => { text.Text = masked.Password; masked.Visibility = Visibility.Collapsed; text.Visibility = Visibility.Visible; };
            eye.Unchecked += (_, _) => { masked.Password = text.Text; text.Visibility = Visibility.Collapsed; masked.Visibility = Visibility.Visible; };
            grid.Children.Add(eye);
            value = () => eye.IsChecked == true ? text.Text : masked.Password;
        }

        _rows.Add((entry, newKey, value));
        EnvRows.Items.Add(grid);
    }

    void OnAddVariable(object sender, RoutedEventArgs e)
    {
        if (_env == null) return;
        var key = new TextBox { Style = (Style)FindResource("Field"), FontFamily = new FontFamily("Cascadia Mono, Consolas"), ToolTip = "Variable name, e.g. MY_SETTING" };
        AddRow(null, key);
        key.Focus();
    }

    bool HasEdits() => _rows.Any(r => r.entry == null ? r.newKey!.Text.Trim().Length > 0 : r.value() != r.entry.Value);

    bool Save()
    {
        if (_env == null) return false;
        EnvError.Visibility = Visibility.Collapsed;
        foreach (var (entry, newKey, value) in _rows)
        {
            if (entry != null) { entry.Value = value(); continue; }
            var key = newKey!.Text.Trim();
            if (key.Length == 0) continue;
            if (!Regex.IsMatch(key, "^[A-Za-z_][A-Za-z0-9_]*$")) { ShowError($"\"{key}\" isn't a valid variable name (letters, digits and _)."); return false; }
            if (_env.Entries.Any(x => x.Key == key)) { ShowError($"{key} is already in the file."); return false; }
        }
        foreach (var (entry, newKey, value) in _rows.Where(r => r.entry == null && r.newKey!.Text.Trim().Length > 0))
            _env.Add(newKey!.Text.Trim()).Value = value();
        if (_env.Entries.Any(x => x.Value.Contains('\n'))) { ShowError("Values can't span several lines."); return false; }

        try { _env.Save(ZBoardControl.EnvPath); }
        catch (Exception ex) { ShowError("Couldn't save .env: " + ex.Message); return false; }
        LoadEnv(); // re-read, so new rows become normal rows
        ShowStatus("Saved. ZBoard reads .env when it starts.", "Dim");
        return true;
    }

    void OnSave(object sender, RoutedEventArgs e) => Save();

    async void OnSaveRestart(object sender, RoutedEventArgs e)
    {
        if (!Save()) return;
        SaveRestartButton.IsEnabled = false;
        ShowStatus("Saved. Restarting ZBoard...", "Dim");
        var (ok, message) = await ZBoardControl.StartAsync();
        ShowStatus(ok ? "Saved and restarted. " + message : message, ok ? "Green" : "Red");
        SaveRestartButton.IsEnabled = true;
        if (ok) Restarted?.Invoke();
    }

    void ShowError(string message) { EnvError.Text = message; EnvError.Visibility = Visibility.Visible; }

    void ShowStatus(string message, string brush)
    {
        EnvStatus.Text = message;
        EnvStatus.Foreground = (Brush)FindResource(brush);
        EnvStatus.Visibility = Visibility.Visible;
    }

    // ---------- Start at boot ----------

    async Task RefreshBoot()
    {
        _bootChecked = DateTime.Now;
        BootSwitch.IsEnabled = false;
        var info = await ZBoardControl.GetBootInfoAsync();
        _refreshing = true;
        BootSwitch.IsChecked = info.State is ZBoardControl.BootState.Registered or ZBoardControl.BootState.Outdated;
        _refreshing = false;
        BootText.Text = info.Detail;
        BootDot.Fill = (Brush)FindResource(info.State switch
        {
            ZBoardControl.BootState.Registered => "Green",
            ZBoardControl.BootState.Outdated => "Red",
            _ => "Faint",
        });
        BootSwitch.IsEnabled = info.State != ZBoardControl.BootState.Unknown && !_bootBusy;
    }

    async void OnBootSwitch(object sender, RoutedEventArgs e)
    {
        if (_refreshing || _bootBusy) return;
        bool on = BootSwitch.IsChecked == true;
        _bootBusy = true;
        BootSwitch.IsEnabled = false;
        BootText.Text = on ? "Waiting for Windows (admin rights, then your password)..." : "Waiting for Windows (admin rights)...";
        bool ok = await ZBoardControl.SetBootAsync(on);
        _bootBusy = false;
        await RefreshBoot();
        if (!ok) BootText.Text = (on ? "Not set up (cancelled or failed). " : "Couldn't remove it (cancelled or failed). ") + BootText.Text;
    }

    // ---------- Logs ----------

    void OnServerLog(object s, RoutedEventArgs e) => OpenPath(ZBoardControl.ServerLog);
    void OnAutostartLog(object s, RoutedEventArgs e) => OpenPath(ZBoardControl.AutostartLog);
    void OnOpenFolder(object s, RoutedEventArgs e) => OpenPath(ZBoardControl.RepoPath);

    void OpenPath(string path)
    {
        LogStatus.Visibility = Visibility.Collapsed;
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            LogStatus.Text = "Not found: " + path;
            LogStatus.Visibility = Visibility.Visible;
            return;
        }
        if (Directory.Exists(path)) ZBoardControl.Open(path);
        else ZBoardControl.OpenFile(path);
    }
}
