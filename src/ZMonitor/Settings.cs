using System.IO;
using System.Text.Json;

namespace ZMonitor;

sealed class Settings
{
    /// <summary>Monitor device name, e.g. \\.\DISPLAY2.</summary>
    public string? Monitor { get; set; }
    /// <summary>Offset of the window's top-left from that monitor's top-left, in physical pixels.</summary>
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public bool HasPosition { get; set; }
    public bool Topmost { get; set; } = true;
    public bool Locked { get; set; }
    /// <summary>Widget shown (false = hidden and paused; set by Z Launcher or the Hide menu).</summary>
    public bool Visible { get; set; } = true;
    /// <summary>Sections switched on/off from Z Launcher.</summary>
    public bool ShowFps { get; set; } = true;
    public bool ShowCpu { get; set; } = true;
    public bool ShowGpu { get; set; } = true;
    public bool ShowRam { get; set; } = true;
    public bool ShowNet { get; set; } = true;
    public bool FpsExpanded { get; set; } = true;
    public bool CpuExpanded { get; set; } = true;
    public bool GpuExpanded { get; set; } = true;
    public bool RamExpanded { get; set; } = true;
    public bool NetworkExpanded { get; set; }
    public double Opacity { get; set; } = 1.0;
    /// <summary>Colour laid over the acrylic (#AARRGGBB). Raise the alpha for darker, more contrasty glass.</summary>
    public string Tint { get; set; } = "#38000000";
    /// <summary>Extra exe names (without .exe) to always treat as games.</summary>
    public List<string> GameExes { get; set; } = new();
    /// <summary>Exe names never treated as games, even when fullscreen.</summary>
    public List<string> IgnoreExes { get; set; } = new()
    {
        "explorer", "SearchHost", "StartMenuExperienceHost", "ShellExperienceHost", "ApplicationFrameHost", "LockApp",
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "claude", "Code", "Discord", "Spotify",
        "vlc", "mpc-hc64", "PotPlayerMini64", "Teams", "steamwebhelper", "steam", "EpicGamesLauncher",
        "RiotClientServices", "LeagueClientUx", "Battle.net", "EADesktop", "RadeonSoftware", "ZMonitor",
    };

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZMonitor");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    /// <summary>Copies every setting from <paramref name="other"/> (used when Z Launcher changed the file).</summary>
    public void ReloadFrom(Settings other)
    {
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanRead && p.CanWrite))
            p.SetValue(this, p.GetValue(other));
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { /* best effort */ }
    }
}
