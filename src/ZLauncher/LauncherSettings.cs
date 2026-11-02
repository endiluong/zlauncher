using System.IO;
using System.Text.Json;

namespace ZLauncher;

sealed class LauncherSettings
{
    /// <summary>The apps shown on the Apps page, in display order.</summary>
    public List<MyApp> Apps { get; set; } = new();
    /// <summary>0 = Apps, 1 = Widgets.</summary>
    public int Tab { get; set; }
    public double? Left { get; set; }
    public double? Top { get; set; }

    public sealed class MyApp
    {
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
    }

    static readonly string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ZLauncher");
    static readonly string FilePath = System.IO.Path.Combine(Dir, "settings.json");

    public static LauncherSettings Load()
    {
        try { return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* best effort */ }
    }
}
