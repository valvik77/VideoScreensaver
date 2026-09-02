using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public class VideoCacheServiceTests
{
    [TestMethod]
    [DataRow("https://cdn.pixabay.com/video.mp4", true)]
    [DataRow("http://example.test/clip.mp4", true)]
    [DataRow("C:\\Videos\\clip.mp4", false)]
    [DataRow("file:///C:/Videos/clip.mp4", false)]
    public void IsRemote_RecognizesHttpSourcesOnly(string target, bool expected)
    {
        Assert.AreEqual(expected, VideoCacheService.IsRemote(target));
    }

    [TestMethod]
    public void PurgeIncompleteDownloads_RemovesOnlyExpiredPartialFiles()
    {
        var cacheDirectory = Path.Combine(Path.GetTempPath(), "VideoScreensaver.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cacheDirectory);
        try
        {
            var oldPartial = Path.Combine(cacheDirectory, "old.download");
            var recentPartial = Path.Combine(cacheDirectory, "recent.download");
            var completedVideo = Path.Combine(cacheDirectory, "completed.mp4");
            File.WriteAllText(oldPartial, "partial");
            File.WriteAllText(recentPartial, "partial");
            File.WriteAllText(completedVideo, "complete");
            File.SetLastWriteTimeUtc(oldPartial, DateTime.UtcNow - TimeSpan.FromHours(2));

            VideoCacheService.PurgeIncompleteDownloads(TimeSpan.FromHours(1), cacheDirectory);

            Assert.IsFalse(File.Exists(oldPartial));
            Assert.IsTrue(File.Exists(recentPartial));
            Assert.IsTrue(File.Exists(completedVideo));
        }
        finally
        {
            if (Directory.Exists(cacheDirectory))
            {
                Directory.Delete(cacheDirectory, recursive: true);
            }
        }
    }
}
