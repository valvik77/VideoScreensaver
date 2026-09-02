using System.Collections.ObjectModel;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace VideoScreensaver;

public sealed partial class MainWindow : Window
{
    private AppSettings _settings;
    private readonly PixabayService _pixabayService = new();

    private readonly ObservableCollection<PlaylistItem> _playlist = [];
    private readonly ObservableCollection<VideoGalleryItem> _localItems = [];
    private readonly ObservableCollection<VideoGalleryItem> _pixabayItems = [];

    private int _pixabayPage = 1;
    private int _pixabayTotalPages = 1;
    private bool _isInitialized;
    private ScreenSaverWindow? _testWindow;
    private VideoPreviewWindow? _videoPreviewWindow;

    public MainWindow()
    {
        InitializeComponent();
        _settings = SettingsService.Load();

        ConfigureWindowSize();

        PlaylistGrid.ItemsSource = _playlist;
        LocalGrid.ItemsSource = _localItems;
        PixabayGrid.ItemsSource = _pixabayItems;

        PixabayCategoryCombo.ItemsSource = PixabayCategories.List;
        PixabayCategoryCombo.SelectedIndex = 0;

        LoadSettingsToUi();

        NavView.SelectedItem = NavPlaylist;
        _isInitialized = true;

        _ = LoadInitialDataAsync();
        _ = Task.Run(VideoLibrary.PurgeStaleThumbnailCache);
        _ = Task.Run(() => VideoCacheService.PurgeOldEntries(TimeSpan.FromDays(7)));
        _ = Task.Run(() => VideoCacheService.PurgeIncompleteDownloads(TimeSpan.FromDays(1)));

        AppWindow.Closing += (_, _) => _videoPreviewWindow?.PrepareForOwnerClose();
        Closed += (_, _) =>
        {
            _videoPreviewWindow?.Shutdown();
            _videoPreviewWindow = null;
        };
    }

    private void ConfigureWindowSize()
    {
        try
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(windowHandle);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            if (appWindow?.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.Maximize();
            }
        }
        catch
        {
            // Fallback default
        }
    }

    private void LoadSettingsToUi()
    {
        FolderTextBox.Text = _settings.VideoFolder;
        ShuffleToggle.IsOn = _settings.Shuffle;
        MuteToggle.IsOn = _settings.Mute;
        FadeDurationSlider.Value = Math.Clamp(_settings.FadeSeconds, FadeDurationSlider.Minimum, FadeDurationSlider.Maximum);
        UpdateFadeDurationLabel();
        PixabayApiKeyBox.Text = _settings.PixabayApiKey;

        _playlist.Clear();
        foreach (var item in _settings.Playlist)
        {
            _playlist.Add(item);
        }
        UpdatePlaylistUiState();

        ShuffleToggle.Toggled += (_, _) => AutoSaveSettings();
        MuteToggle.Toggled += (_, _) => AutoSaveSettings();
    }

    private void FadeDurationSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        UpdateFadeDurationLabel();
        if (_isInitialized)
        {
            AutoSaveSettings();
        }
    }

    private void UpdateFadeDurationLabel()
    {
        FadeDurationLabel.Text = $"Duración del crossfade: {FadeDurationSlider.Value:F1} s";
    }

    private async Task LoadInitialDataAsync()
    {
        if (!string.IsNullOrWhiteSpace(_settings.VideoFolder))
        {
            await LoadLocalVideosAsync(_settings.VideoFolder);
        }

        await SearchPixabayAsync();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem selectedItem) return;

        var tag = selectedItem.Tag?.ToString();
        PlaylistView.Visibility = tag == "playlist" ? Visibility.Visible : Visibility.Collapsed;
        LocalView.Visibility = tag == "local" ? Visibility.Visible : Visibility.Collapsed;
        PixabayView.Visibility = tag == "pixabay" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaylistUiState()
    {
        var count = _playlist.Count;
        NavPlaylist.Content = $"Lista de Reproducción ({count})";
        PlaylistCountBadge.Text = count == 1 ? "1 vídeo seleccionado" : $"{count} vídeos seleccionados";
        EmptyPlaylistState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistGrid.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoSaveSettings()
    {
        _settings.VideoFolder = FolderTextBox.Text.Trim();
        _settings.Shuffle = ShuffleToggle.IsOn;
        _settings.Mute = MuteToggle.IsOn;
        _settings.FadeSeconds = FadeDurationSlider.Value;
        _settings.PixabayApiKey = PixabayApiKeyBox.Text.Trim();
        _settings.Playlist = _playlist.ToList();
        SettingsService.Save(_settings);
    }

    private void ShowNotification(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        NotificationInfoBar.Title = title;
        NotificationInfoBar.Message = message;
        NotificationInfoBar.Severity = severity;
        NotificationInfoBar.IsOpen = true;
    }

    #region Local Gallery

    private int _localLoadToken;

    private async void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.VideosLibrary;
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        // Setting FolderTextBox.Text also raises TextChanged, which independently triggers
        // LoadLocalVideosAsync. Temporarily detach the handler to avoid a concurrent load
        // racing on the same _localItems collection (which previously crashed the app).
        FolderTextBox.TextChanged -= FolderTextBox_TextChanged;
        try
        {
            FolderTextBox.Text = folder.Path;
        }
        finally
        {
            FolderTextBox.TextChanged += FolderTextBox_TextChanged;
        }

        await LoadLocalVideosAsync(folder.Path);
        AutoSaveSettings();
    }

    private async void FolderTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var path = FolderTextBox.Text.Trim();
        if (Directory.Exists(path))
        {
            await LoadLocalVideosAsync(path);
        }
    }

    private async Task LoadLocalVideosAsync(string folderPath)
    {
        // Guard against overlapping loads (e.g. rapid typing or picker + TextChanged firing
        // together): only the most recent call is allowed to update the UI collection.
        var token = ++_localLoadToken;
        var items = await VideoLibrary.GetGalleryItemsAsync(folderPath);

        if (token != _localLoadToken) return;

        _localItems.Clear();
        foreach (var item in items)
        {
            _localItems.Add(item);
        }

        EmptyLocalState.Visibility = _localItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        LocalGrid.Visibility = _localItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddAllLocalButton_Click(object sender, RoutedEventArgs e)
    {
        var videoPaths = VideoLibrary.GetVideos(FolderTextBox.Text.Trim());
        if (videoPaths.Count == 0)
        {
            ShowNotification("Sin vídeos", "No hay vídeos en la carpeta local para añadir.", InfoBarSeverity.Warning);
            return;
        }

        var galleryItemsByPath = _localItems.ToDictionary(item => item.VideoUri, StringComparer.OrdinalIgnoreCase);
        var addedCount = 0;
        foreach (var videoPath in videoPaths)
        {
            if (_playlist.All(p => !string.Equals(p.VideoUri, videoPath, StringComparison.OrdinalIgnoreCase)))
            {
                galleryItemsByPath.TryGetValue(videoPath, out var galleryItem);
                _playlist.Add(new PlaylistItem
                {
                    Title = galleryItem?.Title ?? Path.GetFileNameWithoutExtension(videoPath),
                    SourceType = VideoSourceType.Local,
                    VideoUri = videoPath,
                    ThumbnailUri = galleryItem?.ThumbnailUri ?? string.Empty,
                    Duration = galleryItem?.Duration ?? 0,
                    Tags = galleryItem?.Tags ?? Path.GetExtension(videoPath).ToUpperInvariant()
                });
                addedCount++;
            }
        }

        AutoSaveSettings();
        UpdatePlaylistUiState();
        ShowNotification("Vídeos añadidos", $"Se han añadido {addedCount} vídeos locales a la lista de reproducción.");
    }

    #endregion

    #region Pixabay Gallery

    private int _pixabaySearchToken;
    private bool _pixabaySearchInFlight;
    private bool _pixabaySearchPending;

    private async void PixabaySearchButton_Click(object sender, RoutedEventArgs e)
    {
        _pixabayPage = 1;
        await SearchPixabayAsync();
    }

    private async void PixabaySearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            _pixabayPage = 1;
            await SearchPixabayAsync();
        }
    }

    private async void PixabayCategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized) return;
        _pixabayPage = 1;
        await SearchPixabayAsync();
    }

    private async Task SearchPixabayAsync()
    {
        if (PixabayLoadingRing == null) return;

        // Ignore overlapping calls (e.g. rapid category changes) to avoid spamming Pixabay
        // and triggering 429 Too Many Requests; only the latest request's result is applied.
        // If a search is already in flight, mark this one as pending: the in-flight call will
        // pick it up when it finishes, instead of silently dropping it (which used to leave the
        // loading ring stuck visible and the user's search never executed).
        var token = ++_pixabaySearchToken;
        if (_pixabaySearchInFlight)
        {
            _pixabaySearchPending = true;
            return;
        }
        _pixabaySearchInFlight = true;

        PixabayLoadingRing.Visibility = Visibility.Visible;
        PixabayLoadingRing.IsActive = true;
        PixabayGrid.Opacity = 0.4;

        try
        {
            do
            {
                _pixabaySearchPending = false;
                token = _pixabaySearchToken;

                try
                {
                    var query = PixabaySearchBox?.Text?.Trim() ?? string.Empty;
                    var category = (PixabayCategoryCombo?.SelectedItem as PixabayCategory)?.Value ?? string.Empty;

                    var result = await _pixabayService.SearchVideosAsync(
                        query,
                        category,
                        page: _pixabayPage,
                        perPage: 20,
                        customApiKey: _settings.PixabayApiKey);

                    if (token != _pixabaySearchToken) continue;

                    _pixabayItems.Clear();
                    foreach (var item in result.Items)
                    {
                        _pixabayItems.Add(item);
                    }

                    _pixabayTotalPages = Math.Max(1, result.TotalPages);
                    PageIndicatorText.Text = $"Página {_pixabayPage} de {_pixabayTotalPages} ({result.TotalHits} resultados)";
                    PrevPageButton.IsEnabled = _pixabayPage > 1;
                    NextPageButton.IsEnabled = _pixabayPage < _pixabayTotalPages;
                }
                catch (PixabayRateLimitException ex)
                {
                    if (token != _pixabaySearchToken) continue;
                    PageIndicatorText.Text = "Límite de peticiones alcanzado. Espera unos segundos.";
                    ShowNotification("Demasiadas peticiones a Pixabay", ex.Message, InfoBarSeverity.Warning);
                }
                catch (PixabayApiKeyMissingException ex)
                {
                    if (token != _pixabaySearchToken) continue;
                    PageIndicatorText.Text = "Falta configurar la API Key de Pixabay.";
                    ShowNotification("API Key de Pixabay requerida", ex.Message, InfoBarSeverity.Informational);
                }
                catch (Exception ex)
                {
                    if (token != _pixabaySearchToken) continue;
                    ShowNotification("Error de conexión con Pixabay", ex.Message, InfoBarSeverity.Error);
                }
            }
            while (_pixabaySearchPending);
        }
        finally
        {
            PixabayLoadingRing.IsActive = false;
            PixabayLoadingRing.Visibility = Visibility.Collapsed;
            PixabayGrid.Opacity = 1.0;
            _pixabaySearchInFlight = false;
        }
    }

    private async void PrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pixabayPage > 1)
        {
            _pixabayPage--;
            await SearchPixabayAsync();
        }
    }

    private async void NextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pixabayPage < _pixabayTotalPages)
        {
            _pixabayPage++;
            await SearchPixabayAsync();
        }
    }

    private void ApiKeyButton_Click(object sender, RoutedEventArgs e)
    {
        PixabayApiKeyBox.Text = _settings.PixabayApiKey;
    }

    private async void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        _settings.PixabayApiKey = PixabayApiKeyBox.Text.Trim();
        AutoSaveSettings();
        ShowNotification("Clave guardada", "Clave API de Pixabay actualizada.");
        _pixabayPage = 1;
        await SearchPixabayAsync();
    }

    #endregion

    #region Hover Video Preview

    private void VideoCard_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid container) return;

        var hoverPlayer = container.Children.OfType<MediaPlayerElement>().FirstOrDefault();
        if (hoverPlayer == null) return;

        var videoUriString = container.Tag?.ToString();
        if (string.IsNullOrWhiteSpace(videoUriString)) return;

        try
        {
            hoverPlayer.Visibility = Visibility.Visible;
            if (Uri.TryCreate(videoUriString, UriKind.Absolute, out var uri))
            {
                hoverPlayer.Source = MediaSource.CreateFromUri(uri);
                hoverPlayer.MediaPlayer.IsMuted = true;
                hoverPlayer.MediaPlayer.IsLoopingEnabled = true;
                hoverPlayer.MediaPlayer.Play();
            }
        }
        catch
        {
            // Hover preview fallback
        }
    }

    private void VideoCard_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid container) return;

        var hoverPlayer = container.Children.OfType<MediaPlayerElement>().FirstOrDefault();
        if (hoverPlayer == null) return;

        try
        {
            hoverPlayer.MediaPlayer?.Pause();
            hoverPlayer.Source = null;
            hoverPlayer.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Ignore teardown issues
        }
    }

    private void VideoCard_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement container) return;

        e.Handled = true;

        // The full-quality video URI (not the low-res hover PreviewUri) lives on the bound item
        // itself; PlaylistItem and VideoGalleryItem both expose it as VideoUri.
        var videoUri = container.DataContext switch
        {
            PlaylistItem playlistItem => playlistItem.VideoUri,
            VideoGalleryItem galleryItem => galleryItem.VideoUri,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(videoUri)) return;

        // Stop the lightweight hover stream before opening the full-quality video. Keeping both
        // decoders/network streams alive at once can make the full-screen preview slow to start.
        if (container is Grid grid)
        {
            var hoverPlayer = grid.Children.OfType<MediaPlayerElement>().FirstOrDefault();
            if (hoverPlayer != null)
            {
                hoverPlayer.MediaPlayer?.Pause();
                hoverPlayer.Source = null;
                hoverPlayer.Visibility = Visibility.Collapsed;
            }
        }

        if (_videoPreviewWindow is null)
        {
            _videoPreviewWindow = new VideoPreviewWindow(videoUri, WindowNative.GetWindowHandle(this));
            _videoPreviewWindow.Dismissed += (_, _) => Activate();
            _videoPreviewWindow.Activate();
        }
        else
        {
            _videoPreviewWindow.ShowVideo(videoUri);
        }
    }

    #endregion

    #region Playlist Operations

    private void AddGalleryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string itemId }) return;

        var item = _localItems.FirstOrDefault(i => i.Id == itemId) ??
                   _pixabayItems.FirstOrDefault(i => i.Id == itemId);

        if (item == null) return;

        if (_playlist.Any(p => p.VideoUri == item.VideoUri))
        {
            ShowNotification("Ya añadido", $"'{item.Title}' ya está en tu lista de reproducción.", InfoBarSeverity.Informational);
            return;
        }

        _playlist.Add(new PlaylistItem
        {
            Title = item.Title,
            SourceType = item.SourceType,
            VideoUri = item.VideoUri,
            ThumbnailUri = item.ThumbnailUri,
            Duration = item.Duration,
            Tags = item.Tags
        });

        AutoSaveSettings();
        UpdatePlaylistUiState();
        ShowNotification("Añadido", $"'{item.Title}' se ha añadido al salvapantallas.");
    }

    private void RemoveFromPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string playlistId }) return;

        var item = _playlist.FirstOrDefault(p => p.Id == playlistId);
        if (item != null)
        {
            _playlist.Remove(item);
            AutoSaveSettings();
            UpdatePlaylistUiState();
            ShowNotification("Eliminado", $"'{item.Title}' eliminado de la lista.");
        }
    }

    private void MovePlaylistItemUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var index = -1;
        for (int i = 0; i < _playlist.Count; i++)
        {
            if (_playlist[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index > 0)
        {
            _playlist.Move(index, index - 1);
            AutoSaveSettings();
        }
    }

    private void MovePlaylistItemDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id }) return;

        var index = -1;
        for (int i = 0; i < _playlist.Count; i++)
        {
            if (_playlist[i].Id == id)
            {
                index = i;
                break;
            }
        }

        if (index >= 0 && index < _playlist.Count - 1)
        {
            _playlist.Move(index, index + 1);
            AutoSaveSettings();
        }
    }

    private void PlaylistGrid_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        AutoSaveSettings();
    }

    private void ClearPlaylistButton_Click(object sender, RoutedEventArgs e)
    {
        _playlist.Clear();
        AutoSaveSettings();
        UpdatePlaylistUiState();
        ShowNotification("Lista vaciada", "Se han eliminado todos los vídeos de la lista de reproducción.");
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        AutoSaveSettings();
        _testWindow?.Close();
        _testWindow = new ScreenSaverWindow(closeOnPointerMovement: false, onClosed: () =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                _testWindow = null;
                Activate();
            });
        });
        _testWindow.Activate();
    }

    #endregion
}
