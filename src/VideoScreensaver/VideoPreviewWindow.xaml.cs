using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.Media.Core;
using WinRT.Interop;

namespace VideoScreensaver;

/// <summary>
/// Simple full-screen video player opened by double-clicking a thumbnail in the gallery, so the
/// user can preview a video at full size/quality without having to add it to the playlist first.
/// </summary>
public sealed partial class VideoPreviewWindow : Window
{
    private string _videoUri;
    private readonly nint _ownerHandle;
    private CancellationTokenSource? _loadingCancellation;
    private nint _windowHandle;
    private nint _originalWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private bool _isLoaded;
    private bool _isDismissed;
    private bool _allowClose;
    private bool _isClosed;
    private bool _escapeHotKeyRegistered;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _focusRetryTimer;

    public event EventHandler? Dismissed;

    public VideoPreviewWindow(string videoUri, nint ownerHandle)
    {
        InitializeComponent();
        Title = "Vista previa de vídeo - Video Screensaver";
        _videoUri = videoUri;
        _ownerHandle = ownerHandle;

        Player.MediaPlayer.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(DismissPreview);
        Player.MediaPlayer.MediaOpened += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDismissed) return;
            LoadingPanel.Visibility = Visibility.Collapsed;
            Player.MediaPlayer.Play();
        });
        Player.MediaPlayer.MediaFailed += (_, args) => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isDismissed) return;
            LoadingPanel.Visibility = Visibility.Visible;
            LoadingText.Text = $"No se pudo reproducir el vídeo.\n{args.ErrorMessage}";
        });

        Closed += (_, _) =>
        {
            _isClosed = true;
            UnregisterPreviewHotKey();
            _focusRetryTimer?.Stop();
            _loadingCancellation?.Cancel();
            RestoreWindowProcedure();
            Player.Source = null;
            Player.MediaPlayer.Dispose();
            _loadingCancellation?.Dispose();
            _loadingCancellation = null;
        };

        Root.Loaded += Root_Loaded;
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        _windowHandle = WindowNative.GetWindowHandle(this);
        if (_ownerHandle != 0)
        {
            SetWindowLongPtr(_windowHandle, GwlpHwndParent, _ownerHandle);
        }
        InstallWindowProcedure();
        _isLoaded = true;
        PresentAndLoadVideo();
    }

    public void ShowVideo(string videoUri)
    {
        _videoUri = videoUri;
        if (_isLoaded)
        {
            PresentAndLoadVideo();
        }
        else
        {
            Activate();
        }
    }

    public void Shutdown()
    {
        if (_isClosed) return;
        _allowClose = true;
        _loadingCancellation?.Cancel();
        Close();
    }

    public void PrepareForOwnerClose()
    {
        // The owner is about to be destroyed. Do not intercept the WM_CLOSE that Windows sends
        // to this owned window, otherwise the preview can survive as an orphaned fullscreen HWND.
        _allowClose = true;
        _focusRetryTimer?.Stop();
        UnregisterPreviewHotKey();
        _loadingCancellation?.Cancel();
        ReleaseTopmostCore();
    }

    private async void PresentAndLoadVideo()
    {
        _loadingCancellation?.Cancel();
        _loadingCancellation?.Dispose();
        _loadingCancellation = new CancellationTokenSource();
        var cancellationToken = _loadingCancellation.Token;
        _isDismissed = false;

        Player.MediaPlayer.Pause();
        Player.Source = null;
        LoadingPanel.Visibility = Visibility.Visible;
        LoadingText.Text = "Preparando vídeo…";

        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        AppWindow.Show(activateWindow: true);
        ShowWindow(_windowHandle, SwRestore);

        // Keep the preview briefly in the topmost band while Windows completes reactivation of
        // this previously hidden HWND. Demoting it synchronously here allowed the configuration
        // window to retake the foreground from the second opening onwards.
        SetWindowPos(
            _windowHandle,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpShowWindow);
        FocusPreviewWindow();
        RegisterPreviewHotKey();
        ScheduleFocusRetry();

        try
        {
            var playbackTarget = _videoUri;
            if (VideoCacheService.IsRemote(playbackTarget))
            {
                LoadingText.Text = "Descargando vídeo…";
                playbackTarget = await VideoCacheService.GetOrDownloadAsync(playbackTarget, cancellationToken);
            }

            if (_isDismissed || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            LoadingText.Text = "Cargando vídeo…";
            var sourceUri = VideoCacheService.IsRemote(playbackTarget)
                ? new Uri(playbackTarget)
                : new Uri(Path.GetFullPath(playbackTarget));
            Player.Source = MediaSource.CreateFromUri(sourceUri);
        }
        catch (OperationCanceledException)
        {
            // Dismissing the preview while a remote video is downloading is expected.
        }
        catch (Exception ex)
        {
            if (!_isDismissed)
            {
                LoadingText.Text = $"No se pudo abrir el vídeo.\n{ex.Message}";
            }
        }
    }

    private void InstallWindowProcedure()
    {
        if (_windowHandle == 0 || _originalWndProc != 0)
        {
            return;
        }

        _wndProcDelegate = WindowProcedure;
        _originalWndProc = SetWindowLongPtr(
            _windowHandle,
            GwlpWndProc,
            Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
    }

    private nint WindowProcedure(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == WmHotKey && wParam == PreviewEscapeHotKeyId)
        {
            DispatcherQueue.TryEnqueue(DismissPreview);
            return 0;
        }

        if (message == WmClose && !_allowClose)
        {
            DispatcherQueue.TryEnqueue(DismissPreview);
            return 0;
        }

        if ((message == WmKeyDown || message == WmSysKeyDown) && wParam == VkEscape)
        {
            // Consume every key-down (including auto-repeat) but keep this window alive until
            // the physical key is released. Otherwise the configuration window can regain focus
            // while Escape is still down and interpret the remainder of the same keystroke.
            return 0;
        }

        if ((message == WmKeyUp || message == WmSysKeyUp) && wParam == VkEscape)
        {
            DispatcherQueue.TryEnqueue(DismissPreview);
            return 0;
        }

        return CallWindowProc(_originalWndProc, hwnd, message, wParam, lParam);
    }

    private void RestoreWindowProcedure()
    {
        if (_windowHandle != 0 && _originalWndProc != 0)
        {
            SetWindowLongPtr(_windowHandle, GwlpWndProc, _originalWndProc);
            _originalWndProc = 0;
        }

        _wndProcDelegate = null;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
        }
    }

    private void Root_KeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            DismissPreview();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        DismissPreview();
    }

    private void DismissPreview()
    {
        if (_isDismissed) return;
        _isDismissed = true;
        _focusRetryTimer?.Stop();
        UnregisterPreviewHotKey();
        ReleaseTopmostCore();
        _loadingCancellation?.Cancel();
        Player.MediaPlayer.Pause();
        Player.Source = null;
        AppWindow.Hide();
        Dismissed?.Invoke(this, EventArgs.Empty);
    }

    private void FocusPreviewWindow()
    {
        if (_isDismissed) return;
        Activate();
        BringWindowToTop(_windowHandle);
        SetForegroundWindow(_windowHandle);
        SetActiveWindow(_windowHandle);
        SetFocus(_windowHandle);
        Root.Focus(FocusState.Programmatic);
    }

    private void ScheduleFocusRetry()
    {
        _focusRetryTimer?.Stop();
        _focusRetryTimer ??= DispatcherQueue.CreateTimer();
        _focusRetryTimer.Interval = TimeSpan.FromMilliseconds(200);
        _focusRetryTimer.IsRepeating = false;
        _focusRetryTimer.Tick -= RetryFocus;
        _focusRetryTimer.Tick += RetryFocus;
        _focusRetryTimer.Start();
    }

    private void RetryFocus(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args) =>
        FocusPreviewWindow();

    private void RegisterPreviewHotKey()
    {
        UnregisterPreviewHotKey();
        _escapeHotKeyRegistered = RegisterHotKey(
            _windowHandle,
            PreviewEscapeHotKeyId.ToInt32(),
            ModNoRepeat,
            (uint)VkEscape.ToInt32());
    }

    private void UnregisterPreviewHotKey()
    {
        if (!_escapeHotKeyRegistered) return;
        UnregisterHotKey(_windowHandle, PreviewEscapeHotKeyId.ToInt32());
        _escapeHotKeyRegistered = false;
    }

    private void ReleaseTopmostCore()
    {
        SetWindowPos(
            _windowHandle,
            HwndNotTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private const int GwlpWndProc = -4;
    private const int GwlpHwndParent = -8;
    private const int SwRestore = 9;
    private const uint WmClose = 0x0010;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmHotKey = 0x0312;
    private const uint ModNoRepeat = 0x4000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTopmost = new(-1);
    private static readonly nint HwndNotTopmost = new(-2);
    private static readonly nint VkEscape = 0x1B;
    private static readonly nint PreviewEscapeHotKeyId = 0x5650;

    private delegate nint WndProcDelegate(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint newLong);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(nint previousWndProc, nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint hwnd, int id);

}
