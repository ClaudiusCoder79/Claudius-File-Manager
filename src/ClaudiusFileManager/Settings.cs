using System.Text.Json;

namespace ClaudiusFileManager;

public sealed class Settings
{
    public string? Left { get; set; }
    public string? Right { get; set; }
    public bool ShowHidden { get; set; }
    public bool ShowFolderSizes { get; set; } = true;
    public bool ShowNavPane { get; set; } = true;
    public double NavPaneWidth { get; set; } = 240;
    public bool NavThisPcExpanded { get; set; }
    public double Width { get; set; } = 1320;
    public double Height { get; set; } = 820;
    public bool Maximized { get; set; }
    public string Theme { get; set; } = "System";
    public string? Language { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudiusFileManager", "settings.json");

    public static Settings Load()
    {
        try
        {
            // First start after the rename: carry over the settings saved by Claudius Explorer.
            var path = FilePath;
            if (!File.Exists(path))
            {
                var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                       "ClaudiusExplorer", "settings.json");
                if (File.Exists(old)) path = old;
            }
            return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings();
        }
        catch { return new Settings(); }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
