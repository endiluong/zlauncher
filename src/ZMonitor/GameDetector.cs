using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ZMonitor;

/// <summary>
/// Decides whether a process is a game:
/// 1. its exe is in Windows' own game list (HKCU\System\GameConfigStore, what Game Bar uses),
/// 2. it lives in a game library folder (Steam, Epic, Xbox, Riot, GOG, ...),
/// 3. it is listed in settings.GameExes, or
/// 4. its window covers the whole monitor (fullscreen / borderless) and it isn't in settings.IgnoreExes.
/// </summary>
sealed class GameDetector
{
    static readonly string[] LibraryFolders =
    [
        @"\steamapps\common\", @"\Epic Games\", @"\XboxGames\", @"\WindowsApps\", @"\Riot Games\",
        @"\GOG Games\", @"\GOG Galaxy\Games\", @"\EA Games\", @"\Ubisoft Game Launcher\games\", @"\Battle.net\Games\",
    ];

    readonly Settings _settings;
    readonly Dictionary<int, (string Path, bool Game)> _cache = new();
    HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);
    long _knownLoaded;

    public GameDetector(Settings settings) => _settings = settings;

    public bool IsGame(int pid, IntPtr hwnd)
    {
        if (Environment.TickCount64 - _knownLoaded > 60_000) LoadKnownGames();

        if (!_cache.TryGetValue(pid, out var entry))
        {
            string path = ExePath(pid);
            string exe = Path.GetFileNameWithoutExtension(path);
            bool ignored = _settings.IgnoreExes.Contains(exe, StringComparer.OrdinalIgnoreCase);
            bool game = !ignored && path.Length > 0 &&
                (_known.Contains(path)
                 || _settings.GameExes.Contains(exe, StringComparer.OrdinalIgnoreCase)
                 || LibraryFolders.Any(f => path.Contains(f, StringComparison.OrdinalIgnoreCase) && !exe.Contains("launcher", StringComparison.OrdinalIgnoreCase)));
            if (_cache.Count > 300) _cache.Clear();
            _cache[pid] = entry = (path, game);
        }
        if (entry.Game) return true;

        // Unlisted games: fullscreen or borderless-fullscreen foreground window.
        if (hwnd != IntPtr.Zero && IsFullscreen(hwnd)
            && !_settings.IgnoreExes.Contains(Path.GetFileNameWithoutExtension(entry.Path), StringComparer.OrdinalIgnoreCase))
        {
            _cache[pid] = (entry.Path, true);
            return true;
        }
        return false;
    }

    static bool IsFullscreen(IntPtr hwnd)
    {
        if (hwnd == Native.GetShellWindow()) return false;
        var cls = new StringBuilder(64);
        Native.GetClassName(hwnd, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        if (!Native.GetWindowRect(hwnd, out var r)) return false;
        var mon = Native.MonitorOf(hwnd);
        return mon != null && r.Left <= mon.Bounds.Left && r.Top <= mon.Bounds.Top && r.Right >= mon.Bounds.Right && r.Bottom >= mon.Bounds.Bottom;
    }

    void LoadKnownGames()
    {
        _knownLoaded = Environment.TickCount64;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore\Children");
            if (key != null)
                foreach (var name in key.GetSubKeyNames())
                {
                    using var child = key.OpenSubKey(name);
                    if (child?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
                }
        }
        catch { /* registry unavailable: fall back to the other rules */ }
        _known = set;
        // Re-evaluate cached "not a game" answers against the fresh list.
        foreach (var (pid, e) in _cache.ToList())
            if (!e.Game && _known.Contains(e.Path)) _cache[pid] = (e.Path, true);
    }

    static string ExePath(int pid)
    {
        try { return Process.GetProcessById(pid).MainModule?.FileName ?? ""; }
        catch { return ""; }
    }
}
