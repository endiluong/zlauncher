using System.Diagnostics;
using System.IO;
using System.Windows;

namespace ZMonitor;

public partial class App : Application
{
    Mutex? _single;

    void OnStartup(object sender, StartupEventArgs e)
    {
        // Diagnostics: ZMonitor.exe --dump <file>   |   ZMonitor.exe --probe-fps <file> [seconds]
        if (e.Args.Length >= 2 && e.Args[0] == "--dump")
        {
            using var hw = new HardwareSensors();
            hw.Open();
            hw.Read();
            Thread.Sleep(1200);
            File.WriteAllText(e.Args[1], hw.Dump());
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--probe-fps")
        {
            int secs = e.Args.Length >= 3 ? int.Parse(e.Args[2]) : 8;
            using var t = new FrameRateTracker { ProbeCounts = new() };
            var ok = t.Start();
            Thread.Sleep(secs * 1000);
            var lines = new List<string> { $"started={ok} error={t.LastError}" };
            lock (t.ProbeCounts)
                foreach (var ((g, id, pid), n) in t.ProbeCounts.OrderByDescending(kv => kv.Value).Take(60))
                    lines.Add($"{g} id={id,-4} pid={pid,-6} {ProcName(pid),-28} n={n}  {t.Read(pid)}");
            File.WriteAllLines(e.Args[1], lines);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, ex) => { Log(ex.Exception.ToString()); ex.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log(ex.ExceptionObject.ToString() ?? "");

        _single = new Mutex(true, @"Global\ZMonitor.SingleInstance", out bool first);
        if (!first) { Shutdown(); return; }

        var w = new MainWindow();
        MainWindow = w;
        w.Show();
    }

    public static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZMonitor");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "log.txt"), $"{DateTime.Now:s} {message}{Environment.NewLine}");
        }
        catch { }
    }

    static string ProcName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch { return "?"; }
    }
}
