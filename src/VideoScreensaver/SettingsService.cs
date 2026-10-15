using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoScreensaver;

public sealed class AppSettings
{
    public string VideoFolder { get; set; } = string.Empty;
    public bool Shuffle { get; set; } = true;
    public bool Mute { get; set; } = true;
    public double FadeSeconds { get; set; } = 6.0;
    public int CacheLimitMb { get; set; } = 2048;
    public int MaxDownloadMb { get; set; } = 512;
    [System.Text.Json.Serialization.JsonIgnore]
    public string PixabayApiKey { get; set; } = string.Empty;
    public List<PlaylistItem> Playlist { get; set; } = [];
}

public static class SettingsService
{
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoScreensaver");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings Load(string? settingsFilePath = null)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(
                File.ReadAllText(settingsFilePath ?? FilePath));
            if (persisted is null)
            {
                return new AppSettings();
            }

            var playlist = (persisted.Playlist ?? [])
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.VideoUri)).ToList();
            foreach (var item in playlist.Where(item => item.SourceType == VideoSourceType.Pixabay))
            {
                // Migrate existing playlists too; otherwise only newly-added gallery items would
                // benefit and old high-load URLs would continue stalling during crossfades.
                item.VideoUri = PixabayVideoUrl.ForSmoothPlayback(item.VideoUri);
            }

            return new AppSettings
            {
                VideoFolder = persisted.VideoFolder ?? string.Empty,
                Shuffle = persisted.Shuffle,
                Mute = persisted.Mute,
                FadeSeconds = double.IsFinite(persisted.FadeSeconds) ? Math.Clamp(persisted.FadeSeconds, 0.5, 30) : 6,
                CacheLimitMb = Math.Clamp(persisted.CacheLimitMb, 64, 102400),
                MaxDownloadMb = Math.Clamp(persisted.MaxDownloadMb, 16, Math.Clamp(persisted.CacheLimitMb, 64, 102400)),
                Playlist = playlist,
                // PixabayApiKey is retained solely to migrate configurations created before
                // DPAPI protection was introduced. It is never written again.
                PixabayApiKey = UnprotectApiKey(persisted.PixabayApiKeyProtected) ??
                                persisted.PixabayApiKey ?? string.Empty
            };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings, string? settingsFilePath = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var targetFile = settingsFilePath ?? FilePath;
        var targetDirectory = Path.GetDirectoryName(targetFile);
        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new ArgumentException("La ruta del archivo de configuración debe incluir un directorio.", nameof(settingsFilePath));
        }

        Directory.CreateDirectory(targetDirectory);
        var persisted = new PersistedSettings
        {
            VideoFolder = settings.VideoFolder,
            Shuffle = settings.Shuffle,
            Mute = settings.Mute,
            FadeSeconds = settings.FadeSeconds,
            CacheLimitMb = settings.CacheLimitMb,
            MaxDownloadMb = settings.MaxDownloadMb,
            Playlist = settings.Playlist,
            PixabayApiKeyProtected = ProtectApiKey(settings.PixabayApiKey)
        };

        var temporaryFile = Path.Combine(targetDirectory, $".{Path.GetFileName(targetFile)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryFile, JsonSerializer.Serialize(persisted, JsonOptions));
            File.Move(temporaryFile, targetFile, overwrite: true);
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryFile);
        }
    }

    private static string? ProtectApiKey(string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(apiKey),
            optionalEntropy: null,
            DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string? UnprotectApiKey(string? protectedApiKey)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey))
        {
            return null;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(protectedApiKey);
            var clearBytes = ProtectedData.Unprotect(
                protectedBytes,
                optionalEntropy: null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clearBytes);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static void TryDeleteTemporaryFile(string temporaryFile)
    {
        try
        {
            if (File.Exists(temporaryFile))
            {
                File.Delete(temporaryFile);
            }
        }
        catch
        {
            // Cleanup is best effort. Never hide the exception that caused Save to fail.
        }
    }

    private sealed class PersistedSettings
    {
        public string? VideoFolder { get; set; }
        public bool Shuffle { get; set; } = true;
        public bool Mute { get; set; } = true;
        public double FadeSeconds { get; set; } = 6.0;
        public int CacheLimitMb { get; set; } = 2048;
        public int MaxDownloadMb { get; set; } = 512;
        public string? PixabayApiKey { get; set; }
        public string? PixabayApiKeyProtected { get; set; }
        public List<PlaylistItem>? Playlist { get; set; }
    }
}
