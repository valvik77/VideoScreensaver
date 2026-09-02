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
    private readonly Random _random = new();
    private readonly nint _previewParent;
    private readonly Action? _onClosed;
    private readonly bool _closeOnPointerMovement;
    private int _index = -1;
    private bool _usingPlayerA = true;
    private MediaPlayer? _activePlayer;
    private FrameworkElement? _activeElement;

    // The next video is opened well ahead of time (as soon as the current one starts) instead of
    // only a few seconds before the crossfade, so it's already fully buffered by the time the
    // transition needs to happen. Without this, opening+buffering could take longer than the
    // remaining playback time of the outgoing video, which reached its natural end and froze on
    // its last frame while we were still waiting for the incoming video to open.
    private MediaPlayer? _preloadedPlayer;
    private FrameworkElement? _preloadedElement;
    private int _preloadedIndex = -1;
    private bool _preloadedReady;
    private bool _transitionRequested;

    private bool _isTransitioning;
    private bool _isClosing;
    private Action? _completeActiveTransition;

    // If a video is shorter than the crossfade duration, its MediaEnded (or a late
    // PositionChanged) can fire while the PREVIOUS crossfade is still running. PlayNext() bails
    // out in that case (see _isTransitioning guard) so we don't step on the in-progress
    // animation, but without remembering the request the video would just sit frozen on its
    // last frame forever since nothing else would ever call PlayNext() again. This flag makes
    // sure that request is replayed as soon as the current crossfade finishes.
    private bool _pendingPlayNext;

    // MediaPlaybackSession.PositionChanged does not fire on every frame - it's throttled/batched
    // by the platform (observed roughly every ~200-250ms, sometimes more under load), so relying
    // on it alone to detect "we're within FadeDuration of the end" can miss the window entirely:
    // the last PositionChanged before the end might still be earlier than transitionStart, and
    // the next one never arrives because MediaEnded fires (and freezes the last frame) first.
    // A dedicated high-frequency timer checks the position directly instead, closing that gap.
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

        _mediaPlayerA = PlayerA.MediaPlayer;
        _mediaPlayerB = PlayerB.MediaPlayer;
        _mediaPlayerA.CommandManager.IsEnabled = false;
        _mediaPlayerB.CommandManager.IsEnabled = false;
        _mediaPlayerA.IsMuted = _settings.Mute;
        _mediaPlayerB.IsMuted = _settings.Mute;

        _mediaPlayerA.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => HandleMediaEnded(_mediaPlayerA));
        _mediaPlayerA.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => HandleMediaFailed(_mediaPlayerA));
        _mediaPlayerB.MediaEnded += (_, _) => DispatcherQueue.TryEnqueue(() => HandleMediaEnded(_mediaPlayerB));
        _mediaPlayerB.MediaFailed += (_, _) => DispatcherQueue.TryEnqueue(() => HandleMediaFailed(_mediaPlayerB));
        _watchdogTimer = DispatcherQueue.CreateTimer();
        _watchdogTimer.Interval = TimeSpan.FromMilliseconds(40);
        _watchdogTimer.Tick += (_, _) => CheckTransitionWatchdog();
        _watchdogTimer.Start();

        Closed += (_, _) =>
        {
            _isClosing = true;
            _watchdogTimer.Stop();
            RestoreCursor();
            _mediaPlayerA.Dispose();
            _mediaPlayerB.Dispose();
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
        if (_previewParent != 0 || _isClosing) return;

        // ESC can be observed both by the native window procedure and by WinUI input. The first
        // exit signal wins. AppWindow.Destroy avoids Window.Close's E_ABORT race, which could
        // leave a still-playing fullscreen HWND orphaned after the configuration window returned.
        _isClosing = true;
        RestoreCursor();
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
        if (_videos.Count == 0) return;

        // A crossfade is already running (e.g. this video ended/near-ended earlier than
        // expected, shorter than FadeDuration). Remember the request instead of dropping it -
        // it will be replayed once the current crossfade's onCompleted callback runs, otherwise
        // this video would stay frozen on its last frame indefinitely.
        if (_isTransitioning)
        {
            _pendingPlayNext = true;
            return;
        }

        // If a preload already finished for the upcoming video (the normal case), reuse it
        // instead of opening the source again from scratch.
        if (_preloadedPlayer is not null && _preloadedElement is not null && _preloadedReady)
        {
            StartTransitionToPreloaded();
            return;
        }

        // A preload is already in flight but hasn't finished opening yet. Keep the current video
        // at its normal rate and remember the transition request. The old implementation changed
        // PlaybackRate to 0.2 here, which was the visible "frame-by-frame" slowdown reported for
        // clips whose next item took longer to prepare.
        if (_preloadedPlayer is not null)
        {
            _transitionRequested = true;
            return;
        }

        // No preload in flight at all (e.g. very first video, or a clip so short the preload
        // never had a chance to start) - kick one off now; MediaOpened will start playback once
        // it's actually ready.
        _transitionRequested = _activePlayer is not null;
        BeginPreload(advanceIndex: true);
    }

    private void HandleMediaEnded(MediaPlayer player)
    {
        // Once a crossfade starts, _activePlayer is the incoming player. If the outgoing decoder
        // reaches its real end earlier than the container's reported NaturalDuration, its last
        // frame would otherwise remain static underneath the remainder of the opacity animation.
        // Complete the transition instead of displaying that frozen frame.
        if (!ReferenceEquals(player, _activePlayer))
        {
            if (_isTransitioning)
            {
                _completeActiveTransition?.Invoke();
            }
            return;
        }

        if (_isTransitioning)
        {
            _pendingPlayNext = true;
            return;
        }

        PlayNext();

        // In the exceptional case where the next item is still downloading/decoding, loop the
        // current clip at normal speed instead of leaving its final frame frozen. As soon as the
        // preload becomes ready, _transitionRequested makes it crossfade immediately.
        if (_transitionRequested && ReferenceEquals(player, _activePlayer))
        {
            player.PlaybackSession.Position = TimeSpan.Zero;
            player.Play();
        }
    }

    private void HandleMediaFailed(MediaPlayer player)
    {
        // OpenPreloadSourceAsync owns failures from the hidden/preloading player and skips the
        // broken item. Only advance here when the player visible on screen fails.
        if (ReferenceEquals(player, _activePlayer))
        {
            PlayNext();
        }
    }

    /// <summary>
    /// Opens the next video on the currently-hidden player well ahead of when it's actually
    /// needed, so that by the time the crossfade should start, the incoming player only needs a
    /// (near-instant) Play() call instead of waiting on MediaOpened. This is what prevents the
    /// outgoing video from stalling on its last frame while the incoming one is still buffering.
    /// </summary>
    private void BeginPreload(bool advanceIndex)
    {
        if (_preloadedPlayer is not null || _videos.Count == 0) return;

        if (advanceIndex)
        {
            _index = _settings.Shuffle ? _random.Next(_videos.Count) : (_index + 1) % _videos.Count;
        }

        var preloadIndex = _index;
        var videoTarget = _videos[preloadIndex];

        MediaPlayerElement preloadElement = _usingPlayerA ? PlayerA : PlayerB;
        var preloadPlayer = _usingPlayerA ? _mediaPlayerA : _mediaPlayerB;
        _usingPlayerA = !_usingPlayerA;

        ElementCompositionPreview.GetElementVisual(preloadElement).StopAnimation("Opacity");
        preloadElement.Opacity = 0;

        _preloadedPlayer = preloadPlayer;
        _preloadedElement = preloadElement;
        _preloadedIndex = preloadIndex;
        _preloadedReady = false;

        _ = OpenPreloadSourceAsync(preloadElement, preloadPlayer, videoTarget);
    }

    /// <summary>
    /// Resolves the actual media source for <paramref name="videoTarget"/> and assigns it through
    /// <paramref name="preloadElement"/>. Remote (Pixabay) videos are downloaded to a local cache
    /// first instead of being played directly via HTTP streaming - direct streaming was found to
    /// stall/rebuffer near the end of the clip, freezing the screensaver on the last frame during
    /// a crossfade, something that never happened with local files (see VideoCacheService).
    /// </summary>
    private async Task OpenPreloadSourceAsync(
        MediaPlayerElement preloadElement,
        MediaPlayer preloadPlayer,
        string videoTarget)
    {
        try
        {
            var localPath = VideoCacheService.IsRemote(videoTarget)
                ? await VideoCacheService.GetOrDownloadAsync(videoTarget, CancellationToken.None)
                : videoTarget;

            // The preload could have been abandoned (e.g. superseded by MediaFailed retry, or the
            // window closed) while the download above was in flight - don't touch a player that's
            // no longer the current preload target.
            if (!ReferenceEquals(_preloadedPlayer, preloadPlayer)) return;

            var uri = Uri.TryCreate(localPath, UriKind.Absolute, out var resultUri)
                ? resultUri
                : new Uri(Path.GetFullPath(localPath));

            TypedEventHandler<MediaPlayer, object>? mediaOpened = null;
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailed = null;
            mediaOpened = (player, _) =>
            {
                player.MediaOpened -= mediaOpened;
                player.MediaFailed -= mediaFailed;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_preloadedPlayer, player)) return;

                    // Prime the decoder well ahead of the actual transition (there's no time
                    // pressure yet) instead of only starting it right when the crossfade begins.
                    // Starting a second video decoder exactly at transition time made the
                    // outgoing video's decoder briefly starve/stall - visible as a freeze on its
                    // current frame right as the fade started, worse the longer FadeDuration was.
                    // Unlike the old priming cycle, we deliberately do NOT seek back to position
                    // zero after pausing: that seek was asynchronous and could still be settling
                    // when the crossfade started, leaving a static first frame visible during the
                    // fade. Pausing at whatever small position it reached avoids that race; losing
                    // a fraction of a second of the very start of the clip is not noticeable.
                    PrimeIncomingPlayer(player);
                });
            };
            mediaFailed = (player, _) =>
            {
                player.MediaFailed -= mediaFailed;
                player.MediaOpened -= mediaOpened;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (!ReferenceEquals(_preloadedPlayer, player)) return;
                    // Drop the broken preload and try the next one instead.
                    _preloadedPlayer = null;
                    _preloadedElement = null;
                    _preloadedReady = false;
                    BeginPreload(advanceIndex: true);
                });
            };
            preloadPlayer.MediaOpened += mediaOpened;
            preloadPlayer.MediaFailed += mediaFailed;
            // Keep source ownership in MediaPlayerElement. Mixing element-owned rendering with
            // direct MediaPlayer.Source assignments is explicitly unsupported by WinUI and can
            // make the element retain a stale video surface across a source switch.
            preloadElement.Source = MediaSource.CreateFromUri(uri);
        }
        catch
        {
            if (!ReferenceEquals(_preloadedPlayer, preloadPlayer)) return;
            _preloadedPlayer = null;
            _preloadedElement = null;
            _preloadedReady = false;
            // Skip broken item (e.g. download failed)
            DispatcherQueue.TryEnqueue(() => BeginPreload(advanceIndex: true));
        }
    }

    /// <summary>
    /// Plays the incoming (hidden, opacity 0) player briefly so its decoder is warmed up and
    /// producing frames, then pauses it in place - without seeking back to zero, since that seek
    /// is asynchronous and could still be settling once the crossfade actually starts. This runs
    /// well ahead of the transition, so it doesn't compete with the outgoing video's decoder for
    /// resources right when the fade begins (which is what caused the outgoing video to freeze
    /// for a moment at the start of the crossfade).
    /// </summary>
    private void PrimeIncomingPlayer(MediaPlayer player)
    {
        var primingCompleted = false;
        var primingTimeoutTimer = DispatcherQueue.CreateTimer();
        primingTimeoutTimer.Interval = TimeSpan.FromMilliseconds(800);
        TypedEventHandler<MediaPlaybackSession, object>? primed = null;

        void CompletePriming()
        {
            if (primingCompleted) return;
            primingCompleted = true;
            primingTimeoutTimer.Stop();
            player.PlaybackSession.PositionChanged -= primed;

            if (!ReferenceEquals(_preloadedPlayer, player)) return;
            player.Pause();
            _preloadedReady = true;

            if (_activePlayer is null)
            {
                StartTransitionToPreloaded();
                return;
            }

            if (!_isTransitioning
                && (_transitionRequested || ShouldBeginTransition(_activePlayer.PlaybackSession)))
            {
                StartTransitionToPreloaded();
            }
        }

        primed = (session, _) =>
        {
            // A couple of frames of real forward progress is enough to confirm the decoder has
            // actually started producing output, without keeping it running (and burning
            // resources) for longer than needed this far ahead of the transition.
            if (session.Position < TimeSpan.FromMilliseconds(120)) return;
            DispatcherQueue.TryEnqueue(CompletePriming);
        };
        // Some clips (particular codec/container quirks) never raise a PositionChanged event
        // here at all - a short timeout guarantees priming always completes one way or another,
        // instead of leaving _preloadedReady stuck at false forever.
        primingTimeoutTimer.Tick += (_, _) => DispatcherQueue.TryEnqueue(CompletePriming);
        player.PlaybackSession.PositionChanged += primed;
        primingTimeoutTimer.Start();
        player.Play();
    }

    private void StartTransitionToPreloaded()
    {
        var incomingPlayer = _preloadedPlayer;
        var incomingElement = _preloadedElement;
        if (incomingPlayer is null || incomingElement is null) return;

        _index = _preloadedIndex;
        _preloadedPlayer = null;
        _preloadedElement = null;
        _preloadedReady = false;
        _transitionRequested = false;

        var isInitialVideo = _activePlayer is null;
        var outgoingPlayer = _activePlayer;
        var outgoingElement = _activeElement;

        _isTransitioning = !isInitialVideo;

        if (isInitialVideo || outgoingPlayer is null || outgoingElement is null)
        {
            _activePlayer = incomingPlayer;
            _activeElement = incomingElement;
            incomingElement.Opacity = 1;
            incomingPlayer.Play();
            // Immediately start preloading the video after this one so it's ready well in
            // advance of the next transition too.
            BeginPreload(advanceIndex: true);
        }
        else
        {
            StartCrossFadeWhenIncomingAdvances(
                outgoingElement,
                outgoingPlayer,
                incomingElement,
                incomingPlayer);
        }
    }

    private void StartCrossFadeWhenIncomingAdvances(
        FrameworkElement outgoingElement,
        MediaPlayer outgoingPlayer,
        FrameworkElement incomingElement,
        MediaPlayer incomingPlayer)
    {
        var startCompleted = false;
        var fallbackTimer = DispatcherQueue.CreateTimer();
        fallbackTimer.Interval = TimeSpan.FromSeconds(2);
        TypedEventHandler<MediaPlaybackSession, object>? positionChanged = null;
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs>? mediaFailed = null;

        void StopWaiting()
        {
            fallbackTimer.Stop();
            incomingPlayer.PlaybackSession.PositionChanged -= positionChanged;
            incomingPlayer.MediaFailed -= mediaFailed;
        }

        void CompleteStart()
        {
            if (startCompleted) return;
            startCompleted = true;
            StopWaiting();

            if (_isClosing) return;
            _activePlayer = incomingPlayer;
            _activeElement = incomingElement;
            CrossFade(outgoingElement, outgoingPlayer, incomingElement,
                onCompleted: () => BeginPreload(advanceIndex: true));
        }

        void AbortStart()
        {
            if (startCompleted) return;
            startCompleted = true;
            StopWaiting();

            if (_isClosing) return;
            incomingPlayer.Pause();
            SetMediaSource(incomingElement, null);
            incomingElement.Opacity = 0;
            _isTransitioning = false;

            // Reuse the same hidden player for the next candidate. The outgoing video remains
            // active and moving while the unusable incoming clip is skipped.
            _usingPlayerA = ReferenceEquals(incomingPlayer, _mediaPlayerA);
            BeginPreload(advanceIndex: true);
        }

        positionChanged = (session, _) =>
        {
            // Requiring several frames of forward progress avoids fading into the cached first
            // frame while the decoder is still resuming. The outgoing player keeps moving and
            // remains fully visible during this preparation interval.
            if (session.Position < TimeSpan.FromMilliseconds(120)) return;
            DispatcherQueue.TryEnqueue(CompleteStart);
        };
        mediaFailed = (_, _) => DispatcherQueue.TryEnqueue(AbortStart);
        fallbackTimer.Tick += (_, _) =>
        {
            if (incomingPlayer.PlaybackSession.Position >= TimeSpan.FromMilliseconds(120))
            {
                CompleteStart();
            }
            else
            {
                AbortStart();
            }
        };
        incomingPlayer.PlaybackSession.PositionChanged += positionChanged;
        incomingPlayer.MediaFailed += mediaFailed;
        fallbackTimer.Start();
        incomingPlayer.Play();
    }

    private void CheckTransitionWatchdog()
    {
        var player = _activePlayer;
        if (_isTransitioning || player is null)
        {
            return;
        }

        var session = player.PlaybackSession;
        if (!ShouldBeginTransition(session))
        {
            return;
        }

        // By this point the next video has normally already been preloading since this one
        // started, so PlayNext can just play it back instantly instead of waiting on MediaOpened.
        PlayNext();
    }

    private bool ShouldBeginTransition(MediaPlaybackSession session)
    {
        var naturalDuration = session.NaturalDuration;
        if (naturalDuration <= TimeSpan.Zero)
        {
            return false;
        }

        var transitionStart = TransitionTiming.GetTransitionStart(naturalDuration, FadeDuration);
        return session.Position >= transitionStart;
    }

    private void CrossFade(
        FrameworkElement outgoing,
        MediaPlayer outgoingPlayer,
        FrameworkElement incoming,
        Action? onCompleted = null)
    {
        var compositor = Microsoft.UI.Xaml.Media.CompositionTarget.GetCompositorForCurrentThread();
        var outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        var incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);
        outgoingVisual.StopAnimation("Opacity");
        incomingVisual.StopAnimation("Opacity");
        outgoing.Opacity = 1;
        incoming.Opacity = 0;

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
        var transitionCompleted = false;
        void CompleteTransition()
        {
            if (transitionCompleted) return;
            transitionCompleted = true;
            _completeActiveTransition = null;

            outgoingVisual.StopAnimation("Opacity");
            incomingVisual.StopAnimation("Opacity");
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            outgoingPlayer.Pause();
            SetMediaSource(outgoing, null);
            _isTransitioning = false;
            // Only preload the video after this one once the outgoing player has actually
            // released its Source - preloading earlier could reuse (and reassign the Source
            // of) the player that was still mid-fade, cutting the outgoing video off abruptly
            // and looking like a fade-to-black instead of a true crossfade.
            onCompleted?.Invoke();

            // Replay a PlayNext() request that arrived while this crossfade was still
            // running (e.g. a clip shorter than FadeDuration reached its end mid-fade).
            if (_pendingPlayNext)
            {
                _pendingPlayNext = false;
                PlayNext();
            }
        }

        _completeActiveTransition = () => DispatcherQueue.TryEnqueue(CompleteTransition);
        batch.Completed += (_, _) =>
        {
            DispatcherQueue.TryEnqueue(CompleteTransition);
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
        if (_previewParent != 0 || _isClosing || !_closeOnPointerMovement)
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
