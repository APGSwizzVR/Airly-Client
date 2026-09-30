using System.IO;
using System.Text.Json;

namespace AirlyClient;

public sealed class AppSettings
{
    public string SimBriefPilotId { get; set; } = string.Empty;
    public string AirlyId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool StartWithWindows { get; set; }
    public bool AutoConnect { get; set; }
    public bool EnableAtcAudio { get; set; } = true;
    public bool EnableMultiplayer { get; set; } = true;
    public bool AutomaticModelMatching { get; set; } = true;
    public string ThemeMode { get; set; } = "Dark";
    public string AccentColor { get; set; } = "#8A96A3";
    public string UpdateFirstSeenVersion { get; set; } = string.Empty;
    public DateTimeOffset? UpdateFirstSeenUtc { get; set; }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Airly",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new AppSettings();

            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}
