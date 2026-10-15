using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Numerics;
using System.Runtime.InteropServices;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using WinRT.Interop;

namespace VideoScreensaver;

public sealed partial class ScreenSaverWindow : Window
{
    private readonly TimeSpan FadeDuration;
    private readonly AppSettings _settings = SettingsService.Load();
    private readonly MediaPlayer _mediaPlayerA;
    private readonly MediaPlayer _mediaPlayerB;
    private readonly List<string> _videos;
    private readonly PlaybackCoordinator _playback;
    private readonly HashSet<Task> _playbackTasks = [];
    private readonly Dictionary<PlaybackSlot, Action> _unsubscribeMediaEvents = [];
    private bool _mediaReleased;
    private readonly nint _previewParent;
    private readonly Action? _onClosed;
    private readonly bool _closeOnPointerMovement;
    private Action? _completeActiveTransition;

    // Media and composition stay in the window; sequencing and lifecycle live in the coordinator.
    private MediaPlayer? ActivePlayer => _playback.ActiveSlot is { } slot ? GetPlayer(slot) : null;
    private FrameworkElement? ActiveElement => _playback.ActiveSlot is { } slot ? GetElement(slot) : null;
    private MediaPlayer GetPlayer(PlaybackSlot slot) => slot == PlaybackSlot.A ? _mediaPlayerA : _mediaPlayerB;
    private MediaPlayerElement GetElement(PlaybackSlot slot) => slot == PlaybackSlot.A ? PlayerA : PlayerB;

    private readonly DispatcherQueueTimer _watchdogTimer;
    private Windows.Foundation.Point? _pointerOrigin;
    private NativePoint? _nativePointerOrigin;
    private DateTimeOffset _pointerExitArmedAt;
    private bool _cursorHidden;

    public ScreenSaverWindow(nint previewParent = 0, bool closeOnPointerMovement = true, Action? onClosed = null)
    {
        _previewParent = previewParent;
        _onClosed = onClosed;
        _closeOnPointerMovement = closeOnPointerMovement;
        InitializeComponent();

        FadeDuration = TimeSpan.FromSeconds(Math.Clamp(_settings.FadeSeconds, 0.5, 30));

        if (_settings.Playlist.Count > 0)
        {
            _videos = _settings.Playlist
                .Select(p => p.VideoUri)
                .Where(uri => !string.IsNullOrWhiteSpace(uri))
                .ToList();
        }
        else
        {
            _videos = VideoLibrary.GetVideos(_settings.VideoFolder).ToList();
        }

        _playback = new PlaybackCoordinator(_videos.Count, _settings.Shuffle);
        _mediaPlayerA = PlayerA.MediaPlayer;
        _mediaPlayerB = PlayerB.MediaPlayer;
        _mediaPlayerA.CommandManager.IsEnabled = false;
        _mediaPlayerB.CommandManager.IsEnabled = false;
        _mediaPlayerA.IsMuted = _settings.Mute;
        _mediaPlayerB.IsMuted = _settings.Mute;

        _watchdogTimer = DispatcherQueue.CreateTimer();
        _watchdogTimer.Interval = TimeSpan.FromMilliseconds(40);
        _watchdogTimer.Tick += (_, _) => CheckTransitionWatchdog();
        if (_videos.Count > 0) _watchdogTimer.Start();

        Closed += async (_, _) =>
        {
            _playback.Close();
            _completeActiveTransition = null;
            _watchdogTimer.Stop();
            foreach (var unsubscribe in _unsubscribeMediaEvents.Values) unsubscribe();
            _unsubscribeMediaEvents.Clear();
            RestoreCursor();
            ElementCompositionPreview.GetElementVisual(PlayerA).StopAnimation("Opacity");
            ElementCompositionPreview.GetElementVisual(PlayerB).StopAnimation("Opacity");
            // Await event unsubscription before disposing their native media objects.
            try { await Task.WhenAll(_playbackTasks.ToArray()); }
            catch (Exception exception) { AppDiagnostics.LogUnhandledException(exception, "Playback shutdown"); }
            ReleaseMediaPlayers();
            _playback.Dispose();
            _onClosed?.Invoke();
        };

        Root.Loaded += (_, _) =>
        {
            ConfigureWindow(previewParent);
            if (previewParent == 0)
            {
                // Installs the native window hook used for reliable ESC handling. In actual
                // screensaver mode it also hides the cursor; test mode keeps it visible.
                HideCursor();
                Root.Focus(FocusState.Programmatic);
                Activate();
                SetForegroundWindow(WindowNative.GetWindowHandle(this));
                ArmPointerExit();
            }
        };

        if (_videos.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            PlayNext();
        }
    }

    private void ReleaseMediaPlayers()
    {
        if (_mediaReleased) return;
        _mediaReleased = true;
        // Detach XAML on its owning thread; decoder teardown must not block that thread.
        PlayerA.SetMediaPlayer(null);
        PlayerB.SetMediaPlayer(null);
        MediaPlayerCleanup.Release(_mediaPlayerA);
        MediaPlayerCleanup.Release(_mediaPlayerB);
    }

    private void ConfigureWindow(nint previewParent)
    {
        if (previewParent != 0)
        {
            var windowHandle = WindowNative.GetWindowHandle(this);
            var style = GetWindowLongPtr(windowHandle, GwlStyle).ToInt64();
            SetWindowLongPtr(windowHandle, GwlStyle, new nint((style & ~WsPopup) | WsChild | WsVisible));
            SetParent(windowHandle, previewParent);

            if (GetClientRect(previewParent, out var bounds))
            {
                SetWindowPos(
                    windowHandle,
                    nint.Zero,
                    0,
                    0,
                    bounds.Right - bounds.Left,
                    bounds.Bottom - bounds.Top,
                    SwpNoZOrder | SwpFrameChanged | SwpShowWindow);
            }
            return;
        }
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
    }

    private nint _origWndProc;
    private WndProcDelegate? _wndProcDelegate;
    private bool _windowHookInstalled;

    private void HideCursor()
    {
        if (_previewParent != 0 || _windowHookInstalled) return;

        // WM_SETCURSOR-based hiding: WinUI's input stack re-sets the system arrow cursor on
        // every pointer update (movement, enter, etc.), which made a plain ShowCursor(false)
        // call get silently undone. Subclassing the window and swallowing WM_SETCURSOR with
        // SetCursor(null) keeps the cursor reliably hidden without needing to re-issue anything
        // on each pointer event.
        var windowHandle = WindowNative.GetWindowHandle(this);
        _wndProcDelegate = WndProc;
        _origWndProc = SetWindowLongPtr(windowHandle, GwlpWndProc, Marshal.GetFunctionPointerForDelegate(_wndProcDelegate));
        _windowHookInstalled = true;
        _cursorHidden = _closeOnPointerMovement;
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam)
    {
        if ((msg == WmKeyDown || msg == WmSysKeyDown) && wParam == VkEscape)
        {
            DispatcherQueue.TryEnqueue(ExitScreenSaver);
            return 0;
        }

        // MediaPlayerElement can consume WinUI pointer events. Observe the native message too so
        // the test window and the real screensaver always leave when the user moves the mouse.
        if (msg == WmMouseMove && _closeOnPointerMovement)
        {
            HandleNativePointerMovement();
        }

        if (msg == WmSetCursor && _cursorHidden)
        {
            SetCursor(nint.Zero);
            return 1;
        }

        return CallWindowProc(_origWndProc, hWnd, msg, wParam, lParam);
    }

    private void RestoreCursor()
    {
        if (!_windowHookInstalled) return;

        var windowHandle = WindowNative.GetWindowHandle(this);
        SetWindowLongPtr(windowHandle, GwlpWndProc, _origWndProc);
        _wndProcDelegate = null;
        _windowHookInstalled = false;
        _cursorHidden = false;
    }

    public void RequestClose() => ExitScreenSaver();

    private void ExitScreenSaver()
    {
        if (_previewParent != 0 || _playback.IsClosed) return;

        // ESC can be observed both by the native window procedure and by WinUI input. The first
        // exit signal wins. AppWindow.Destroy avoids Window.Close's E_ABORT race, which could
        // leave a still-playing fullscreen HWND orphaned after the configuration window returned.
        _playback.Close();
        _watchdogTimer.Stop();
        RestoreCursor();

        // A real screensaver is an isolated /s process, not a reusable application window.
        // End it immediately instead of waiting for Media Foundation to synchronously tear down
        // active decoder sessions. This returns control to Windows without a frozen gray screen.
        if (_onClosed is null)
        {
            Environment.Exit(0);
            return;
        }

        AppWindow.Destroy();
    }

    private const int GwlStyle = -16;
    private const int GwlpWndProc = -4;
    private const long WsChild = 0x40000000L;
    private const long WsPopup = 0x80000000L;
    private const long WsVisible = 0x10000000L;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseMove = 0x0200;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private static readonly nint VkEscape = new(0x1B);

    private delegate nint WndProcDelegate(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
    private static extern nint CallWindowProc(nint prevWndProc, nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetParent(nint childWindow, nint newParent);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint window, int index, nint newValue);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private void PlayNext()
    {
        switch (_playback.RequestAdvance())
        {
            case PlaybackAdvance.Load: BeginPreload(); break;
            case PlaybackAdvance.Start: StartTransitionToPreloaded(); break;
        }
    }

    private void SubscribeMediaEvents(PlaybackAttempt attempt)
    {
        if (_unsubscribeMediaEvents.Remove(attempt.Slot, out var unsubscribe)) unsubscribe();
        var player = GetPlayer(attempt.Slot);
        TypedEventHandler<MediaPlayer, object> ended = (_, _) =>
            DispatcherQueue.TryEnqueue(() => HandleMediaEnded(player, attempt.Id));
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, _) =>
            DispatcherQueue.TryEnqueue(() => HandleMediaFailed(player, attempt.Id));
        player.MediaEnded += ended;
        player.MediaFailed += failed;
        _unsubscribeMediaEvents[attempt.Slot] = () =>
        {
            player.MediaEnded -= ended;
            player.MediaFailed -= failed;
        };
    }

    private void HandleMediaEnded(MediaPlayer player, long attemptId)
    {
        if (_playback.IsClosed) return;
        if (!_playback.IsActive(attemptId))
        {
            if (_playback.State == PlaybackState.Transitioning && _playback.IsOutgoing(attemptId))
                _completeActiveTransition?.Invoke();
            return;
        }
        PlayNext();
        if ((_playback.TransitionRequested || _playback.State == PlaybackState.Starting)
            && !_playback.ActiveFailed && ReferenceEquals(player, ActivePlayer))
        {
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
        }
    }

    private void HandleMediaFailed(MediaPlayer player, long attemptId)
    {
        if (!_playback.IsActive(attemptId)) return;
        if (_playback.State == PlaybackState.Transitioning && _playback.Attempt is { } incoming)
        {
            FailPreload(incoming, new IOException("El vídeo entrante falló durante la transición."));
            return;
        }
        _playback.FailActive(DateTimeOffset.UtcNow);
        PlayNext();
    }

    private void BeginPreload()
    {
        var attempt = _playback.BeginPreload(DateTimeOffset.UtcNow);
        if (attempt is null)
        {
            if (_playback.State == PlaybackState.WaitingForRetry)
                _watchdogTimer.Interval = TimeSpan.FromSeconds(1);
            if (_playback.State == PlaybackState.WaitingForRetry
                && (ActivePlayer is null || _playback.ActiveFailed))
            {
                EmptyTitle.Text = "No se pudo reproducir ningún vídeo";
                EmptyDescription.Text = "Comprueba los archivos y la conexión. Se reintentará en 30 segundos.";
                EmptyState.Visibility = Visibility.Visible;
                Canvas.SetZIndex(EmptyState, 2);
            }
            return;
        }
        _watchdogTimer.Interval = TimeSpan.FromMilliseconds(40);
        var element = GetElement(attempt.Slot);
        ElementCompositionPreview.GetElementVisual(element).StopAnimation("Opacity");
        element.Opacity = 0;
        GetPlayer(attempt.Slot).IsMuted = true;
        SubscribeMediaEvents(attempt);
        TrackPlaybackTask(OpenPreloadSourceAsync(attempt));
    }

    private void TrackPlaybackTask(Task task)
    {
        _playbackTasks.Add(task);
        _ = ObservePlaybackTaskAsync(task);
    }

    private async Task ObservePlaybackTaskAsync(Task task)
    {
        try { await task; }
        catch (Exception exception) { AppDiagnostics.LogUnhandledException(exception, "Playback operation"); }
        finally { _playbackTasks.Remove(task); }
    }

    private async Task OpenPreloadSourceAsync(PlaybackAttempt attempt)
    {
        var player = GetPlayer(attempt.Slot);
        var element = GetElement(attempt.Slot);
        try
        {
            var target = _videos[attempt.Index];
            var localPath = VideoCacheService.IsRemote(target)
                ? await VideoCacheService.GetOrDownloadAsync(target, attempt.Token) : target;
            if (!_playback.IsCurrent(attempt)) return;
            var uri = Uri.TryCreate(localPath, UriKind.Absolute, out var absolute)
                ? absolute : new Uri(Path.GetFullPath(localPath));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TypedEventHandler<MediaPlayer, object> opened = (_, _) => completion.TrySetResult();
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, args) =>
                completion.TrySetException(new IOException(args.ErrorMessage));
            player.MediaOpened += opened;
            player.MediaFailed += failed;
            try
            {
                element.Source = MediaSource.CreateFromUri(uri);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), attempt.Token);
            }
            finally
            {
                player.MediaOpened -= opened;
                player.MediaFailed -= failed;
            }
            if (!_playback.Opened(attempt)) return;
            await WaitForProgressAsync(player, TimeSpan.FromMilliseconds(120), TimeSpan.FromSeconds(3), attempt.Token);
            if (!_playback.Ready(attempt)) return;
            player.Pause();
            if (ActivePlayer is null || _playback.TransitionRequested || ShouldBeginTransition(ActivePlayer.PlaybackSession))
                StartTransitionToPreloaded();
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
        catch (Exception exception) { FailPreload(attempt, exception); }
    }

    private void FailPreload(PlaybackAttempt attempt, Exception exception)
    {
        if (!_playback.Fail(attempt, DateTimeOffset.UtcNow)) return;
        AppDiagnostics.LogUnhandledException(exception, "Video preload");
        _completeActiveTransition = null;
        var player = GetPlayer(attempt.Slot);
        var element = GetElement(attempt.Slot);
        ElementCompositionPreview.GetElementVisual(element).StopAnimation("Opacity");
        element.Opacity = 0;
        player.Pause();
        element.Source = null;
        if (ActiveElement is { } activeElement)
        {
            ElementCompositionPreview.GetElementVisual(activeElement).StopAnimation("Opacity");
            activeElement.Opacity = 1;
        }
        DispatcherQueue.TryEnqueue(BeginPreload);
    }

    private static async Task WaitForProgressAsync(MediaPlayer player, TimeSpan target,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<MediaPlaybackSession, object> advanced = (session, _) =>
        {
            if (session.Position >= target) completion.TrySetResult();
        };
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, args) =>
            completion.TrySetException(new IOException(args.ErrorMessage));
        player.PlaybackSession.PositionChanged += advanced;
        player.MediaFailed += failed;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            player.Play();
            if (player.PlaybackSession.Position >= target) completion.TrySetResult();
            try { await completion.Task.WaitAsync(timeout, cancellationToken); }
            // Some codecs advance without reporting PositionChanged. Check actual progress at timeout.
            catch (TimeoutException) when (player.PlaybackSession.Position >= target) { }
        }
        finally
        {
            player.PlaybackSession.PositionChanged -= advanced;
            player.MediaFailed -= failed;
        }
    }

    private void StartTransitionToPreloaded()
    {
        var attempt = _playback.Attempt;
        if (attempt is null || !_playback.BeginStart(attempt)) return;
        var incomingPlayer = GetPlayer(attempt.Slot);
        var incomingElement = GetElement(attempt.Slot);
        var outgoingPlayer = ActivePlayer;
        var outgoingElement = ActiveElement;
        if (outgoingPlayer is null || outgoingElement is null)
        {
            if (!_playback.Activate(attempt)) return;
            EmptyState.Visibility = Visibility.Collapsed;
            incomingElement.Opacity = 1;
            incomingPlayer.IsMuted = _settings.Mute;
            incomingPlayer.Play();
            BeginPreload();
        }
        else
            TrackPlaybackTask(StartCrossFadeAsync(attempt, outgoingElement, outgoingPlayer, incomingElement, incomingPlayer));
    }

    private async Task StartCrossFadeAsync(PlaybackAttempt attempt, FrameworkElement outgoingElement,
        MediaPlayer outgoingPlayer, FrameworkElement incomingElement, MediaPlayer incomingPlayer)
    {
        try
        {
            var target = incomingPlayer.PlaybackSession.Position + TimeSpan.FromMilliseconds(120);
            await WaitForProgressAsync(incomingPlayer, target, TimeSpan.FromSeconds(2), attempt.Token);
            if (!_playback.Activate(attempt)) return;
            EmptyState.Visibility = Visibility.Collapsed;
            incomingPlayer.IsMuted = _settings.Mute;
            CrossFade(attempt, outgoingElement, outgoingPlayer, incomingElement);
        }
        catch (OperationCanceledException) when (attempt.Token.IsCancellationRequested) { }
        catch (Exception exception) { FailPreload(attempt, exception); }
    }

    private void CheckTransitionWatchdog()
    {
        if (_playback.IsClosed) return;
        if (_playback.CanPreload) BeginPreload();
        var player = ActivePlayer;
        if (_playback.IsTransitioning || player is null || _playback.ActiveFailed) return;
        if (ShouldBeginTransition(player.PlaybackSession)) PlayNext();
    }

    private bool ShouldBeginTransition(MediaPlaybackSession session) =>
        session.NaturalDuration > TimeSpan.Zero
        && session.Position >= TransitionTiming.GetTransitionStart(session.NaturalDuration, FadeDuration);

    private void CrossFade(
        PlaybackAttempt attempt,
        FrameworkElement outgoing,
        MediaPlayer outgoingPlayer,
        FrameworkElement incoming)
    {
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        var incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);
        outgoingVisual.StopAnimation("Opacity");
        incomingVisual.StopAnimation("Opacity");
        outgoing.Opacity = 1;
        incoming.Opacity = 0;

        // PlayerA/PlayerB alternate as outgoing/incoming across successive transitions, but their
        // declaration order in the Grid never changes - whichever was declared last always paints
        // on top. Without forcing the incoming element above the outgoing one here, roughly half
        // of all transitions (whenever the incoming element happens to be the one declared first)
        // would fade in underneath the still-opaque outgoing surface, making the animation
        // completely invisible and the video switch look like a hard cut instead of a crossfade.
        Canvas.SetZIndex(outgoing, 0);
        Canvas.SetZIndex(incoming, 1);

        // Respect the configured duration when possible, but never let the opacity animation
        // outlive the decodable portion of a short outgoing clip. Leaving a small tail margin
        // also absorbs rounding differences between NaturalDuration and the final timestamp.
        var fadeDuration = GetEffectiveFadeDuration(outgoingPlayer.PlaybackSession);
        var smoothEasing = compositor.CreateCubicBezierEasingFunction(
            new Vector2(0.42f, 0f),
            new Vector2(0.58f, 1f));

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, smoothEasing);
        fadeIn.Duration = fadeDuration;

        // Use a scoped batch so the final XAML Opacity values are only applied in code once the
        // composition animations actually finish, instead of being overwritten immediately
        // (which previously made the fade invisible because the plain property assignment ran
        // synchronously right after starting the animation).
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        void CompleteTransition()
        {
            if (!_playback.CompleteTransition(attempt, out var advance)) return;
            _completeActiveTransition = null;

            outgoingVisual.StopAnimation("Opacity");
            incomingVisual.StopAnimation("Opacity");
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            outgoingPlayer.Pause();
            SetMediaSource(outgoing, null);
            BeginPreload();
            if (advance) PlayNext();
        }

        _completeActiveTransition = () => DispatcherQueue.TryEnqueue(CompleteTransition);
        batch.Completed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                CompleteTransition();
                batch.Dispose();
            });
        };

        // An opaque incoming video fading from 0 to 1 already replaces the outgoing pixels with
        // the mathematically correct crossfade weights. Keep the outgoing surface at opacity 1
        // underneath so WinUI continues presenting its frames normally; animating both video
        // surfaces could make the outgoing one briefly retain its last presented frame.
        incomingVisual.StartAnimation("Opacity", fadeIn);

        batch.End();
    }

    private TimeSpan GetEffectiveFadeDuration(MediaPlaybackSession outgoingSession)
    {
        return TransitionTiming.GetEffectiveFadeDuration(
            outgoingSession.NaturalDuration,
            outgoingSession.Position,
            FadeDuration);
    }

    private static void SetMediaSource(FrameworkElement element, MediaSource? source)
    {
        if (element is MediaPlayerElement mediaElement)
        {
            mediaElement.Source = source;
        }
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ExitScreenSaver();
    }

    private void ArmPointerExit()
    {
        if (_previewParent != 0 || !_closeOnPointerMovement)
        {
            return;
        }

        // The click used to launch the test window often produces a trailing WM_MOUSEMOVE.
        // Keep the saver open briefly, then require actual movement from that initial position.
        _pointerExitArmedAt = DateTimeOffset.UtcNow.AddMilliseconds(750);
        _pointerOrigin = null;
        _nativePointerOrigin = GetCursorPos(out var point) ? point : null;
    }

    private void HandleNativePointerMovement()
    {
        if (_previewParent != 0 || _playback.IsClosed || !_closeOnPointerMovement)
        {
            return;
        }

        if (!GetCursorPos(out var point))
        {
            return;
        }

        if (DateTimeOffset.UtcNow < _pointerExitArmedAt)
        {
            _nativePointerOrigin = point;
            return;
        }

        if (_nativePointerOrigin is null)
        {
            _nativePointerOrigin = point;
            return;
        }

        if (Math.Abs(point.X - _nativePointerOrigin.Value.X) > 10 || Math.Abs(point.Y - _nativePointerOrigin.Value.Y) > 10)
        {
            DispatcherQueue.TryEnqueue(ExitScreenSaver);
        }
    }

    private void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_previewParent != 0 || !_closeOnPointerMovement)
        {
            return;
        }

        // HideCursor is idempotent (no-ops once the WM_SETCURSOR subclass is installed), so this
        // just guarantees the cursor is hidden as soon as the first pointer event arrives even
        // if Root.Loaded hasn't run yet.
        HideCursor();

        var point = e.GetCurrentPoint(Root).Position;
        if (DateTimeOffset.UtcNow < _pointerExitArmedAt)
        {
            _pointerOrigin = point;
            return;
        }

        if (_pointerOrigin is null)
        {
            _pointerOrigin = point;
            return;
        }
        if (Math.Abs(point.X - _pointerOrigin.Value.X) > 10 || Math.Abs(point.Y - _pointerOrigin.Value.Y) > 10)
        {
            ExitScreenSaver();
        }
    }
}
