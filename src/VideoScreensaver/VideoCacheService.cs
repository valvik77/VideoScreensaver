using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VideoScreensaver;

/// <summary>
/// Downloads remote videos (currently only Pixabay clips) to a local disk cache before playback,
/// instead of letting MediaPlayer stream them directly over HTTP. Direct streaming was found to
/// stall/rebuffer right near the end of the clip - visible as the screensaver freezing on the
/// last frame during a crossfade - which never happened with local files. Downloading up front
/// (as part of the existing preload-ahead-of-time flow) avoids that rebuffering entirely, at the
/// cost of a bit of disk space and a short wait the first time a given clip is played.
/// </summary>
public static class VideoCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScreensaver",
        "videocache");

    private static readonly HttpClient HttpClient = new()
    {
        // Downloading a ~30s Pixabay clip should never legitimately take this long, but a
        // generous timeout avoids aborting on a slow connection.
        Timeout = TimeSpan.FromMinutes(5)
    };

    public static bool IsRemote(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Returns the local path of a cached copy of <paramref name="url"/>, downloading it first if
    /// it isn't already cached.
    /// </summary>
    public static async Task<string> GetOrDownloadAsync(
        string url,
        CancellationToken cancellationToken,
        string? cacheDirectory = null)
    {
        var targetDirectory = cacheDirectory ?? CacheDirectory;
        Directory.CreateDirectory(targetDirectory);
        PurgeIncompleteDownloads(TimeSpan.FromDays(1), targetDirectory);

        var extension = TryGetExtension(url);
        var cachedFile = Path.Combine(targetDirectory, $"{GetStableUrlHash(url)}{extension}");

        if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 0)
        {
            return cachedFile;
        }

        // Download to a temp file first so a half-finished download (e.g. app closed mid-way)
        // never looks like a valid, complete cache entry to a later run.
        var tempFile = Path.Combine(
            targetDirectory,
            $"{GetStableUrlHash(url)}.{Guid.NewGuid():N}.download");
        try
        {
            using (var response = await HttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var fileStream = new FileStream(tempFile, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await responseStream.CopyToAsync(fileStream, cancellationToken);
            }

            // Move within the same directory is atomic. A concurrent request can only replace
            // this file with its own complete download, never with a partial one.
            File.Move(tempFile, cachedFile, overwrite: true);
            return cachedFile;
        }
        finally
        {
            TryDeleteTemporaryFile(tempFile);
        }
    }

    public static void PurgeIncompleteDownloads(TimeSpan maxAge, string? cacheDirectory = null)
    {
        var targetDirectory = cacheDirectory ?? CacheDirectory;
        if (!Directory.Exists(targetDirectory)) return;

        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var partialFile in Directory.EnumerateFiles(targetDirectory, "*.download"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(partialFile) < cutoff)
                    {
                        File.Delete(partialFile);
                    }
                }
                catch
                {
                    // A download still in progress may be locked; leave it for the next cleanup.
                }
            }
        }
        catch
        {
            // Cache cleanup is best effort only.
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
            // A cleanup failure must not mask a download/cancellation error. The periodic
            // incomplete-download purge will make another best-effort attempt later.
        }
    }

    /// <summary>
    /// Deletes cached video files that haven't been played in a while, so the cache doesn't grow
    /// forever as new Pixabay clips are watched. Safe to call repeatedly; failures for individual
    /// files (e.g. still open/locked because it's mid-download or mid-playback) are ignored.
    /// </summary>
    public static void PurgeOldEntries(TimeSpan maxAge)
    {
        if (!Directory.Exists(CacheDirectory))
        {
            return;
        }

        try
        {
            var cutoff = DateTime.UtcNow - maxAge;
            foreach (var file in Directory.EnumerateFiles(CacheDirectory))
            {
                try
                {
                    // LastAccessTime is refreshed every time MediaPlayer opens the cached file, so
                    // this purges clips that genuinely haven't been watched recently rather than
                    // ones that just happen to be old.
                    if (File.GetLastAccessTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // File may be locked or already removed; skip and continue purging the rest.
                }
            }
        }
        catch
        {
            // Best-effort cleanup; ignore directory enumeration failures (e.g. permissions).
        }
    }

    private static string TryGetExtension(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var extension = Path.GetExtension(path);
            return string.IsNullOrEmpty(extension) ? ".mp4" : extension;
        }
        catch
        {
            return ".mp4";
        }
    }

    /// <summary>
    /// Computes a hash of the URL that is stable across process runs, so the same Pixabay clip
    /// always maps to the same cache file instead of downloading a fresh copy on every run (see
    /// VideoLibrary.GetStablePathHash for the same reasoning applied to thumbnails).
    /// </summary>
    private static string GetStableUrlHash(string url)
    {
        var bytes = Encoding.UTF8.GetBytes(url);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }
}
