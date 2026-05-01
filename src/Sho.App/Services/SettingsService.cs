using System.IO;
using System.Text.Json;

namespace Sho.App.Services;

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "uk";
    public string? LastFolder { get; set; }
    public bool ShowPreview { get; set; } = true;
}

public sealed class SettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "sho");
    private static readonly string SettingsPath =
        Path.Combine(SettingsDir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new AppSettings();
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch { return new AppSettings(); }
    }

    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOpts));
        }
        catch { }
    }
}
