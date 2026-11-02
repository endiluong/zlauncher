using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZLauncher;

/// <summary>
/// Controls the ZMonitor widget from the (non-admin) launcher:
/// settings go through %APPDATA%\ZMonitor\settings.json, and a registered window message
/// ("ZMonitor.Command") tells the running widget to reload, quit or change monitor.
/// </summary>
static class ZMonitorControl
{
    public const string TaskName = "ZMonitor";
    public static readonly string ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\ZMonitor\ZMonitor.exe");
    static readonly string SettingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"ZMonitor\settings.json");
    static readonly int CommandMessage = RegisterWindowMessage("ZMonitor.Command");

    public const int CmdReload = 1, CmdQuit = 2, CmdNextMonitor = 3;

    public static bool IsInstalled => File.Exists(ExePath);
    public static bool IsRunning => Process.GetProcessesByName("ZMonitor").Length > 0;

    public static JsonObject Read()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject(); }
        catch { return new JsonObject(); }
    }

    public static bool Get(JsonObject s, string key, bool fallback) => s[key] is JsonValue v && v.TryGetValue(out bool b) ? b : fallback;
    public static double Get(JsonObject s, string key, double fallback) => s[key] is JsonValue v && v.TryGetValue(out double d) ? d : fallback;

    /// <summary>Read-modify-write settings.json, then ask the running widget to apply it.</summary>
    public static void Update(Action<JsonObject> change)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var s = Read();
                change(s);
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
                File.WriteAllText(SettingsPath, s.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                break;
            }
            catch (IOException) { Thread.Sleep(50); } // the widget may be saving at the same moment
        }
        Post(CmdReload);
    }

    public static bool Post(int command)
    {
        var hwnd = FindWidgetWindow();
        return hwnd != IntPtr.Zero && PostMessage(hwnd, CommandMessage, new IntPtr(command), IntPtr.Zero);
    }

    /// <summary>Starts the widget: through the logon task when it exists (no UAC prompt), otherwise via UAC.</summary>
    public static bool Start()
    {
        if (TaskExists() && RunHidden("schtasks.exe", $"/run /tn \"{TaskName}\"") == 0) return true;
        try
        {
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = true });
            return true;
        }
        catch (Win32Exception) { return false; } // UAC prompt cancelled
    }

    public static bool TaskExists() => RunHidden("schtasks.exe", $"/query /tn \"{TaskName}\"") == 0;

    /// <summary>Registers / removes the logon task (runs elevated so the widget starts without a UAC prompt). Asks for UAC once.</summary>
    public static bool SetAutostart(bool on)
    {
        string script = on
            ? $"$t=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME; $t.Delay='PT10S'; " +
              $"Register-ScheduledTask -TaskName '{TaskName}' -Trigger $t -Action (New-ScheduledTaskAction -Execute '{ExePath}') " +
              "-User $env:USERNAME -RunLevel Highest -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) " +
              "-AllowStartIfOnBatteries -DontStopIfGoingOnBatteries) -Force | Out-Null"
            : $"Unregister-ScheduledTask -TaskName '{TaskName}' -Confirm:$false";
        try
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            using var p = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -WindowStyle Hidden -EncodedCommand {encoded}")
            { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden });
            p?.WaitForExit(30_000);
        }
        catch (Win32Exception) { return false; }
        return TaskExists() == on;
    }

    static int RunHidden(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true });
            if (p == null) return -1;
            p.WaitForExit(5000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    static IntPtr FindWidgetWindow()
    {
        var pids = Process.GetProcessesByName("ZMonitor").Select(p => (uint)p.Id).ToHashSet();
        if (pids.Count == 0) return IntPtr.Zero;
        IntPtr found = IntPtr.Zero;
        var title = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            title.Clear();
            GetWindowText(h, title, title.Capacity);
            if (title.ToString() == "ZMonitor") { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
}
