using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace ZLauncher;

public partial class App : Application
{
    Mutex? _single;

    void OnStartup(object sender, StartupEventArgs e)
    {
        _single = new Mutex(true, @"Local\ZLauncher.SingleInstance", out bool first);
        if (!first)
        {
            // Already running: bring that window forward instead.
            var other = Process.GetProcessesByName("ZLauncher").FirstOrDefault(p => p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero);
            if (other != null) { ShowWindow(other.MainWindowHandle, 9 /* SW_RESTORE */); SetForegroundWindow(other.MainWindowHandle); }
            Shutdown();
            return;
        }

        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
