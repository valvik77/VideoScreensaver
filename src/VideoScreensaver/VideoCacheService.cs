using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace VideoScreensaver;

/// <summary>Stores complete local copies with bounded downloads and a disk quota.</summary>
public static class VideoCacheService
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoScreensaver", "videocache");
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mov", ".m4v", ".wmv", ".avi", ".webm", ".mkv" };

    public static bool IsRemote(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public static Task<string> GetOrDownloadAsync(string url, CancellationToken cancellationToken,
        string? cacheDirectory = null)
    {
        var settings = SettingsService.Load();
        return DownloadAsync(url, cancellationToken, cacheDirectory ?? CacheDirectory,
            settings.CacheLimitMb * 1024L * 1024, settings.MaxDownloadMb * 1024L * 1024, Client);
    }

    internal static async Task<string> DownloadAsync(string url, CancellationToken cancellationToken,
        string directory, long quotaBytes, long maxDownloadBytes, HttpClient client)
    {
        if (!IsRemote(url)) throw new ArgumentException("Se requiere una dirección HTTP o HTTPS.", nameof(url));
        if (quotaBytes <= 0 || maxDownloadBytes <= 0 || maxDownloadBytes > quotaBytes)
            throw new ArgumentOutOfRangeException(nameof(maxDownloadBytes));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var token = timeout.Token;
        await Gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            // Also coordinate configuration, preview and /s processes using the same cache.
            using var lease = await AcquireLeaseAsync(directory, token).ConfigureAwait(false);
            PurgeIncompleteDownloads(TimeSpan.FromDays(1), directory);
            var extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (!Extensions.Contains(extension)) extension = ".mp4";
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..32];
            var cachedFile = Path.Combine(directory, $"{hash}{extension}");
            if (File.Exists(cachedFile) && new FileInfo(cachedFile).Length > 0)
            {
                // Explicit timestamps are reliable even when NTFS last-access updates are off.
                File.SetLastWriteTimeUtc(cachedFile, DateTime.UtcNow);
                TrimCache(directory, quotaBytes, TimeSpan.FromDays(7), cachedFile);
                return cachedFile;
            }
            var temporaryFile = Path.Combine(directory, $"{hash}.{Guid.NewGuid():N}.download");
            try
            {
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var declaredSize = response.Content.Headers.ContentLength;
                if (declaredSize == 0) throw new IOException("La descarga del vídeo está vacía.");
                if (declaredSize > maxDownloadBytes)
                    throw new IOException("El vídeo supera el tamaño máximo de descarga configurado.");
                var reservedBytes = declaredSize is > 0 ? declaredSize.Value : maxDownloadBytes;
                TrimCache(directory, quotaBytes - reservedBytes, TimeSpan.FromDays(7));
                using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                long downloaded = 0;
                using (var output = new FileStream(temporaryFile, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 81920, useAsync: true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        downloaded += read;
                        if (downloaded > maxDownloadBytes)
                            throw new IOException("El vídeo supera el tamaño máximo de descarga configurado.");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
                if (downloaded == 0 || (declaredSize.HasValue && downloaded != declaredSize.Value))
                    throw new IOException("La descarga del vídeo está vacía o incompleta.");
                // A server may send more than Content-Length; account for actual bytes too.
                TrimCache(directory, quotaBytes - downloaded, TimeSpan.FromDays(7));
                token.ThrowIfCancellationRequested();
                File.Move(temporaryFile, cachedFile, overwrite: true);
                return cachedFile;
            }
            finally { TryDelete(temporaryFile); }
        }
        finally { Gate.Release(); }
    }

    private static async Task<FileStream> AcquireLeaseAsync(string directory, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(Path.Combine(directory, ".cache.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            { await Task.Delay(100, token).ConfigureAwait(false); }
        }
    }

    public static void MaintainCache()
    {
        try
        {
            var settings = SettingsService.Load();
            if (!Directory.Exists(CacheDirectory) || !Gate.Wait(0)) return;
            try
            {
                // Do not wait for a download during periodic maintenance.
                using var lease = new FileStream(Path.Combine(CacheDirectory, ".cache.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                PurgeIncompleteDownloads(TimeSpan.FromDays(1));
                TrimCache(CacheDirectory, settings.CacheLimitMb * 1024L * 1024, TimeSpan.FromDays(7));
            }
            finally { Gate.Release(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AppDiagnostics.LogUnhandledException(exception, "Cache maintenance"); }
    }

    internal static void TrimCache(string directory, long quotaBytes, TimeSpan maxAge, string? preserve = null)
    {
        if (!Directory.Exists(directory)) return;
        var files = Directory.EnumerateFiles(directory).Where(path => Extensions.Contains(Path.GetExtension(path)))
            .Select(path => new FileInfo(path)).OrderBy(file => file.LastWriteTimeUtc).ToList();
        var total = files.Sum(file => file.Length);
        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var file in files)
        {
            if (string.Equals(file.FullName, preserve, StringComparison.OrdinalIgnoreCase)) continue;
            if (file.LastWriteTimeUtc >= cutoff && total <= quotaBytes) continue;
            var length = file.Length;
            if (TryDelete(file.FullName)) total -= length;
        }
        if (total > quotaBytes)
            throw new IOException("La caché no tiene espacio disponible: hay vídeos en uso o no se pueden eliminar.");
    }

    public static void PurgeIncompleteDownloads(TimeSpan maxAge, string? cacheDirectory = null)
    {
        try
        {
            var directory = cacheDirectory ?? CacheDirectory;
            if (!Directory.Exists(directory)) return;
            foreach (var partial in Directory.EnumerateFiles(directory, "*.download"))
                if (File.GetLastWriteTimeUtc(partial) < DateTime.UtcNow - maxAge) TryDelete(partial);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static void PurgeOldEntries(TimeSpan maxAge)
    {
        try { TrimCache(CacheDirectory, long.MaxValue, maxAge); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private static bool TryDelete(string path)
    {
        try { File.Delete(path); return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }
}
