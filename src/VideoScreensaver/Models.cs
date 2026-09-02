using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace VideoScreensaver;

public enum VideoSourceType
{
    Local,
    Pixabay
}

public sealed class PlaylistItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public VideoSourceType SourceType { get; set; } = VideoSourceType.Local;
    public string VideoUri { get; set; } = string.Empty;
    public string ThumbnailUri { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string Tags { get; set; } = string.Empty;

    public string DurationText => DurationFormatter.Format(Duration);

    public Microsoft.UI.Xaml.Visibility VisibleIfHasText(string text) =>
        string.IsNullOrEmpty(text) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? SafeImageSource(string thumbnailUri) =>
        ThumbnailHelper.CreateSafeBitmapImage(thumbnailUri);
}

public sealed class VideoGalleryItem : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public VideoSourceType SourceType { get; set; } = VideoSourceType.Local;
    public string VideoUri { get; set; } = string.Empty;
    public string PreviewUri { get; set; } = string.Empty;
    public string ThumbnailUri { get; set; } = string.Empty;
    public int Duration { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    private bool _isInPlaylist;
    public bool IsInPlaylist
    {
        get => _isInPlaylist;
        set
        {
            if (_isInPlaylist == value) return;
            _isInPlaylist = value;
            OnPropertyChanged();
        }
    }

    public string DurationText => DurationFormatter.Format(Duration);

    public Microsoft.UI.Xaml.Visibility VisibleIfHasText(string text) =>
        string.IsNullOrEmpty(text) ? Microsoft.UI.Xaml.Visibility.Collapsed : Microsoft.UI.Xaml.Visibility.Visible;

    public Microsoft.UI.Xaml.Visibility VisibleIf(bool value) =>
        value ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? SafeImageSource(string thumbnailUri) =>
        ThumbnailHelper.CreateSafeBitmapImage(thumbnailUri);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Formats a duration in whole seconds as "m:ss" (or "h:mm:ss" for videos an hour or longer)
/// for display as a badge on gallery thumbnails.
/// </summary>
internal static class DurationFormatter
{
    public static string Format(int totalSeconds)
    {
        if (totalSeconds <= 0)
        {
            return string.Empty;
        }

        var span = TimeSpan.FromSeconds(totalSeconds);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
    }
}

/// <summary>
/// Resolves an x:Bind-friendly ImageSource for gallery thumbnails, returning null instead of
/// letting Image.Source try to parse an empty/invalid string as a URI (which previously
/// caused a fatal reentrant COM crash when triggered during a synchronous input event).
/// </summary>
internal static class ThumbnailHelper
{
    public static Microsoft.UI.Xaml.Media.Imaging.BitmapImage? CreateSafeBitmapImage(string thumbnailUri)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUri))
        {
            return null;
        }

        try
        {
            var uri = File.Exists(thumbnailUri)
                ? new Uri(thumbnailUri)
                : new Uri(thumbnailUri, UriKind.RelativeOrAbsolute);
            return new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        }
        catch
        {
            return null;
        }
    }
}


public sealed class PixabayCategory
{
    public string DisplayName { get; }
    public string Value { get; }

    public PixabayCategory(string displayName, string value)
    {
        DisplayName = displayName;
        Value = value;
    }
}
