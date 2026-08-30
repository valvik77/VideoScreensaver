using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace VideoScreensaver;

public static class VideoLibrary
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".wmv", ".avi"
    };

    private static readonly string ThumbnailCacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScreensaver",
        "thumbnails");

    public static IReadOnlyList<string> GetVideos(string? folder) =>
        string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)
            ? []
            : Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(path => Extensions.Contains(Path.GetExtension(path)))
                .ToList();

    public static async Task<IReadOnlyList<VideoGalleryItem>> GetGalleryItemsAsync(string? folder)
    {
        var paths = GetVideos(folder);
        var items = new List<VideoGalleryItem>();

        foreach (var path in paths)
        {
            try
            {
                var fileInfo = new FileInfo(path);
                var sizeMb = fileInfo.Length / (1024.0 * 1024.0);
                var fileName = Path.GetFileNameWithoutExtension(path);

                items.Add(new VideoGalleryItem
                {
                    Id = $"local_{GetStablePathHash(path)}",
                    Title = fileName,
                    SourceType = VideoSourceType.Local,
                    VideoUri = path,
                    PreviewUri = path,
                    Tags = Path.GetExtension(path).ToUpperInvariant(),
                    Subtitle = $"Local • {sizeMb:F1} MB"
                });
            }
            catch
            {
                // Ignore individual file access errors
            }
        }

        foreach (var item in items)
        {
            item.ThumbnailUri = await GetOrCreateThumbnailAsync(item.VideoUri);
            item.Duration = await GetVideoDurationSecondsAsync(item.VideoUri);
            if (item.Duration > 0)
            {
                item.Subtitle = $"{item.Subtitle} • {DurationFormatter.Format(item.Duration)}";
            }
        }

        return items;
    }

    /// <summary>
    /// Reads the video duration via the Windows shell (StorageFile video properties) so it can be
    /// shown as a badge on the gallery thumbnail, same as Pixabay items report their duration.
    /// </summary>
    private static async Task<int> GetVideoDurationSecondsAsync(string videoPath)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(videoPath);
            var videoProperties = await storageFile.Properties.GetVideoPropertiesAsync();
            return (int)videoProperties.Duration.TotalSeconds;
        }
        catch
        {
            // Duration is a nice-to-have for the badge; fall back to hiding it if it can't be read.
            return 0;
        }
    }

    /// <summary>
    /// Extracts a video frame thumbnail via the Windows shell (StorageFile.GetThumbnailAsync)
    /// and caches it to disk as a JPEG so it can be reused as an Image source without
    /// re-extracting it on every gallery load.
    /// </summary>
    private static async Task<string> GetOrCreateThumbnailAsync(string videoPath)
    {
        try
        {
            Directory.CreateDirectory(ThumbnailCacheDirectory);

            var lastWriteTicks = File.GetLastWriteTimeUtc(videoPath).Ticks;
            var cacheKey = $"{GetStablePathHash(videoPath)}_{lastWriteTicks:X}";
            var cachedFile = Path.Combine(ThumbnailCacheDirectory, $"{cacheKey}.jpg");

            if (File.Exists(cachedFile))
            {
                return cachedFile;
            }

            var storageFile = await StorageFile.GetFileFromPathAsync(videoPath);
            using var thumbnail = await storageFile.GetThumbnailAsync(ThumbnailMode.VideosView, 320);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return string.Empty;
            }

            using var stream = thumbnail.AsStreamForRead();
            using var fileStream = File.Create(cachedFile);
            await stream.CopyToAsync(fileStream);

            return cachedFile;
        }
        catch
        {
            // Thumbnail extraction can fail for corrupt/unsupported files; fall back to no thumbnail.
            return string.Empty;
        }
    }

    /// <summary>
    /// Regex matching the current thumbnail cache filename format: a 16-hex-char stable path hash,
    /// an underscore, and the file's last-write-time ticks in hex, e.g. "3F2A1B...C4_1A2B3C.jpg".
    /// </summary>
    private static readonly Regex CurrentCacheFileNamePattern = new(
        "^[0-9A-F]{16}_[0-9A-F]+\\.jpg$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Deletes thumbnail cache files that don't match the current naming scheme. This cleans up
    /// stale files left behind by the previous cache-key format, which used
    /// <see cref="string.GetHashCode()"/> (randomized per process) and therefore produced a fresh,
    /// never-reused duplicate on every app run. Safe to call repeatedly; failures for individual
    /// files (e.g. still open/locked) are ignored so one bad file doesn't block the rest.
    /// </summary>
    public static void PurgeStaleThumbnailCache()
    {
        if (!Directory.Exists(ThumbnailCacheDirectory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(ThumbnailCacheDirectory, "*.jpg"))
            {
                var fileName = Path.GetFileName(file);
                if (CurrentCacheFileNamePattern.IsMatch(fileName))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
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

    /// <summary>
    /// Computes a hash of the video path that is stable across process runs. Unlike
    /// <see cref="string.GetHashCode()"/>, whose result is randomized per-process for security
    /// reasons, this is safe to persist (e.g. as a thumbnail cache key or gallery item id).
    /// </summary>
    private static string GetStablePathHash(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path.ToUpperInvariant());
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..16];
    }
}
