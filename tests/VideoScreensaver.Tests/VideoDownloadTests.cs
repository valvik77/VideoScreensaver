using System.Net;
using System.Net.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public sealed class VideoDownloadTests
{
    private const string Url = "https://example.test/clip.mp4";

    [TestMethod]
    public async Task ConcurrentRequestsReuseCompletedDownload()
    {
        using var workspace = new Workspace();
        var calls = 0;
        using var client = Client(async token =>
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(30, token);
            return new ByteArrayContent([1, 2, 3]);
        });
        var paths = await Task.WhenAll(Download(workspace, client), Download(workspace, client));
        Assert.AreEqual(1, calls);
        Assert.AreEqual(paths[0], paths[1]);
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(paths[0]));
        Assert.AreEqual(0, Directory.GetFiles(workspace.Path, "*.download").Length);
    }

    [TestMethod]
    public async Task OversizedDeclaredDownloadLeavesNoCachedVideo()
    {
        using var workspace = new Workspace();
        using var client = Client(_ => Task.FromResult<HttpContent>(new ByteArrayContent(new byte[21])));
        await Assert.ThrowsExactlyAsync<IOException>(() => Download(workspace, client));
        AssertNoVideos(workspace);
    }

    [TestMethod]
    public async Task OversizedUnknownLengthDownloadIsStoppedAndPartialDeleted()
    {
        using var workspace = new Workspace();
        using var client = Client(_ => Task.FromResult<HttpContent>(new StreamContent(new UnknownLengthStream(new byte[21]))));
        await Assert.ThrowsExactlyAsync<IOException>(() => Download(workspace, client));
        AssertNoVideos(workspace);
    }

    [TestMethod]
    public async Task EmptyDownloadIsRejected()
    {
        using var workspace = new Workspace();
        using var client = Client(_ => Task.FromResult<HttpContent>(new ByteArrayContent([])));
        await Assert.ThrowsExactlyAsync<IOException>(() => Download(workspace, client));
        AssertNoVideos(workspace);
    }

    [TestMethod]
    public async Task CancellationDuringBodyReadRemovesPartialFile()
    {
        using var workspace = new Workspace();
        using var cancellation = new CancellationTokenSource();
        using var client = Client(_ => Task.FromResult<HttpContent>(new StreamContent(new WaitingStream(cancellation))));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Download(workspace, client, cancellation.Token));
        AssertNoVideos(workspace);
    }

    [TestMethod]
    public void QuotaEvictsOldestAndPreservesRecentVideo()
    {
        using var workspace = new Workspace();
        var oldest = System.IO.Path.Combine(workspace.Path, "old.mp4");
        var recent = System.IO.Path.Combine(workspace.Path, "recent.mp4");
        File.WriteAllBytes(oldest, new byte[15]);
        File.WriteAllBytes(recent, new byte[15]);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-1));
        VideoCacheService.TrimCache(workspace.Path, 20, TimeSpan.FromDays(7));
        Assert.IsFalse(File.Exists(oldest));
        Assert.IsTrue(File.Exists(recent));
    }

    [TestMethod]
    public void LockedVideoCannotSilentlyExceedQuota()
    {
        using var workspace = new Workspace();
        var path = System.IO.Path.Combine(workspace.Path, "locked.mp4");
        File.WriteAllBytes(path, new byte[15]);
        using var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.ThrowsExactly<IOException>(() => VideoCacheService.TrimCache(workspace.Path, 10, TimeSpan.FromDays(7)));
        Assert.IsTrue(File.Exists(path));
    }

    [TestMethod]
    public async Task CacheHitRefreshesExplicitUseTimestampWithoutNetwork()
    {
        using var workspace = new Workspace();
        var calls = 0;
        using var client = Client(_ =>
        {
            calls++;
            return Task.FromResult<HttpContent>(new ByteArrayContent([1]));
        });
        var path = await Download(workspace, client);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-10));
        await Download(workspace, client);
        Assert.AreEqual(1, calls);
        Assert.IsTrue(File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddMinutes(-1));
    }

    private static Task<string> Download(Workspace workspace, HttpClient client, CancellationToken token = default) =>
        VideoCacheService.DownloadAsync(Url, token, workspace.Path, 40, 20, client);

    private static void AssertNoVideos(Workspace workspace)
    {
        Assert.AreEqual(0, Directory.GetFiles(workspace.Path, "*.mp4").Length);
        Assert.AreEqual(0, Directory.GetFiles(workspace.Path, "*.download").Length);
    }

    private static HttpClient Client(Func<CancellationToken, Task<HttpContent>> content) => new(new Handler(content));

    private sealed class Handler(Func<CancellationToken, Task<HttpContent>> content) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { Content = await content(token) };
    }

    private sealed class UnknownLengthStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class WaitingStream(CancellationTokenSource cancellation) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Workspace : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VideoScreensaver.Tests", Guid.NewGuid().ToString("N"));
        public Workspace() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
