using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public class SettingsServiceTests
{
    [TestMethod]
    public void Save_ProtectsApiKeyAndLoadRestoresIt()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.Path, "settings.json");
        var settings = new AppSettings
        {
            VideoFolder = "C:\\Videos",
            Shuffle = false,
            Mute = false,
            FadeSeconds = 3.5,
            PixabayApiKey = "secret-pixabay-key",
            Playlist = [new PlaylistItem { Title = "Clip", VideoUri = "C:\\Videos\\clip.mp4" }]
        };

        SettingsService.Save(settings, settingsPath);

        var persistedJson = File.ReadAllText(settingsPath);
        var loaded = SettingsService.Load(settingsPath);

        Assert.IsFalse(persistedJson.Contains(settings.PixabayApiKey, StringComparison.Ordinal));
        StringAssert.Contains(persistedJson, "PixabayApiKeyProtected");
        Assert.AreEqual(settings.PixabayApiKey, loaded.PixabayApiKey);
        Assert.AreEqual(settings.VideoFolder, loaded.VideoFolder);
        Assert.AreEqual(1, loaded.Playlist.Count);
        Assert.AreEqual("Clip", loaded.Playlist[0].Title);
    }

    [TestMethod]
    public void Load_LegacyPlaintextApiKey_RemainsUsableUntilNextSave()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.Path, "settings.json");
        File.WriteAllText(settingsPath, "{\"PixabayApiKey\":\"legacy-key\"}");

        var loaded = SettingsService.Load(settingsPath);
        SettingsService.Save(loaded, settingsPath);
        var migratedJson = File.ReadAllText(settingsPath);

        Assert.AreEqual("legacy-key", loaded.PixabayApiKey);
        Assert.IsFalse(migratedJson.Contains("legacy-key", StringComparison.Ordinal));
        StringAssert.Contains(migratedJson, "PixabayApiKeyProtected");
    }

    [TestMethod]
    public void Load_MigratesExistingPixabayVideoToCrossfadeVariant()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.Path, "settings.json");
        File.WriteAllText(settingsPath,
            """
            {
              "Playlist": [{
                "SourceType": 1,
                "VideoUri": "https://cdn.pixabay.com/video/2024/10/18/236928_large.mp4"
              }]
            }
            """);

        var loaded = SettingsService.Load(settingsPath);

        Assert.AreEqual(
            "https://cdn.pixabay.com/video/2024/10/18/236928_small.mp4",
            loaded.Playlist[0].VideoUri);
    }

    [TestMethod]
    public void Load_DoesNotRewriteLocalOrAlreadySizedVideos()
    {
        using var workspace = new TemporaryWorkspace();
        var settingsPath = Path.Combine(workspace.Path, "settings.json");
        File.WriteAllText(settingsPath,
            """
            {
              "Playlist": [
                { "SourceType": 0, "VideoUri": "C:\\Videos\\large.mp4" },
                { "SourceType": 1, "VideoUri": "https://cdn.pixabay.com/video/clip_small.mp4" }
              ]
            }
            """);

        var loaded = SettingsService.Load(settingsPath);

        Assert.AreEqual("C:\\Videos\\large.mp4", loaded.Playlist[0].VideoUri);
        Assert.AreEqual(
            "https://cdn.pixabay.com/video/clip_small.mp4",
            loaded.Playlist[1].VideoUri);
    }

    [TestMethod]
    public void Load_FiltersNullPlaylistItemsAndClampsInvalidValues()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Path, "settings.json");
        File.WriteAllText(path, """
            { "FadeSeconds": -50, "CacheLimitMb": -1, "MaxDownloadMb": 999999,
              "Playlist": [null, {"VideoUri": ""}, {"VideoUri": "C:\\clip.mp4"}] }
            """);
        var settings = SettingsService.Load(path);
        Assert.AreEqual(0.5, settings.FadeSeconds);
        Assert.AreEqual(64, settings.CacheLimitMb);
        Assert.AreEqual(64, settings.MaxDownloadMb);
        Assert.AreEqual(1, settings.Playlist.Count);
    }

    [TestMethod]
    public void Save_WhenReplacementFailsPreservesOriginalAndRemovesTemporaryFile()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Path, "settings.json");
        SettingsService.Save(new AppSettings { VideoFolder = "original" }, path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            try
            {
                SettingsService.Save(new AppSettings { VideoFolder = "changed" }, path);
                Assert.Fail("Replacing a locked configuration should fail.");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        Assert.AreEqual("original", SettingsService.Load(path).VideoFolder);
        Assert.AreEqual(0, Directory.GetFiles(workspace.Path, "*.tmp").Length);
    }

    [TestMethod]
    public void CacheLimitsSurviveSaving()
    {
        using var workspace = new TemporaryWorkspace();
        var path = Path.Combine(workspace.Path, "settings.json");
        SettingsService.Save(new AppSettings { CacheLimitMb = 1024, MaxDownloadMb = 128 }, path);
        var settings = SettingsService.Load(path);
        Assert.AreEqual(1024, settings.CacheLimitMb);
        Assert.AreEqual(128, settings.MaxDownloadMb);
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "VideoScreensaver.Tests",
            Guid.NewGuid().ToString("N"));

        public TemporaryWorkspace() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
