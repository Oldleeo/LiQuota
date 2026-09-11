using System.IO;
using System.Text.Json;

namespace CodexQuotaWidget;

internal sealed class AppSettings
{
    private static readonly string SettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiQuota");
    private static readonly string SettingsPath = Path.Combine(SettingsDirectory, "settings.json");

    public int HorizontalOffset { get; set; }
    public int VerticalOffset { get; set; }
    public string Theme { get; set; } = "System";
    public bool LowQuotaNotifications { get; set; }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.HorizontalOffset = Math.Clamp(settings.HorizontalOffset, -30, 30);
                    settings.VerticalOffset = Math.Clamp(settings.VerticalOffset, -20, 20);
                    settings.Theme = settings.Theme is "Light" or "Dark" ? settings.Theme : "System";
                    return settings;
                }
            }
        }
        catch
        {
            // A corrupt settings file should never prevent the badge from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(SettingsDirectory);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }
}
