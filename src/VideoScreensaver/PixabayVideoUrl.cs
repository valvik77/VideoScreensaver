namespace VideoScreensaver;

internal static class PixabayVideoUrl
{
    private static readonly string[] PlaybackVariantSuffixes =
        ["_large.mp4", "_medium.mp4", "_tiny.mp4"];
    private const string SmallSuffix = "_small.mp4";

    /// <summary>
    /// Selects Pixabay's desktop-quality variant. Playback synchronization is handled by waiting
    /// for the incoming player to advance before the crossfade, so reducing every clip to the
    /// visibly softer "tiny" variant is unnecessary.
    /// </summary>
    internal static string ForSmoothPlayback(string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl) ||
            !Uri.TryCreate(videoUrl, UriKind.Absolute, out var uri) ||
            !uri.Host.EndsWith("pixabay.com", StringComparison.OrdinalIgnoreCase))
        {
            return videoUrl ?? string.Empty;
        }

        var sourceSuffix = PlaybackVariantSuffixes.FirstOrDefault(suffix =>
            uri.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        if (sourceSuffix is null)
        {
            return videoUrl;
        }

        var builder = new UriBuilder(uri)
        {
            Path = uri.AbsolutePath[..^sourceSuffix.Length] + SmallSuffix
        };
        return builder.Uri.AbsoluteUri;
    }
}
