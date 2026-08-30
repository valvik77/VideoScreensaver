using System.Text.Json;

namespace VideoScreensaver;

public sealed class AppSettings
{
    public string VideoFolder { get; set; } = string.Empty;
    public bool Shuffle { get; set; } = true;
    public bool Mute { get; set; } = true;
    public double FadeSeconds { get; set; } = 6.0;
    public string PixabayApiKey { get; set; } = string.Empty;
    public List<PlaylistItem> Playlist { get; set; } = [];
}

public static class SettingsService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoScreensaver");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }
}
