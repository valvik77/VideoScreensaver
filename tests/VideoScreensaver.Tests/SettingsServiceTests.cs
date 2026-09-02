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
