using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
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
    // Media containers do not always report an end timestamp that exactly matches the last
    // decodable frame. Starting slightly early keeps the outgoing player away from MediaEnded
    // until its opacity has already reached zero.
    // The crossfade must finish while the outgoing video is still moving. This reserve keeps its
    // final (occasionally non-decodable or repeated) timestamps completely outside the visible
    // transition instead of using a frozen last frame as part of the fade.
    private static readonly TimeSpan TransitionLead = TimeSpan.FromSeconds(2);

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

    // If a video is shorter than the crossfade duration, its MediaEnded (or a late
    // PositionChanged) can fire while the PREVIOUS crossfade is still running. PlayNext() bails
    // out in that case (see _isTransitioning guard) so we don't step on the in-progress
    // animation, but without remembering the request the video would just sit frozen on its
    // last frame forever since nothing else would ever call PlayNext() again. This flag makes
    // sure that request is replayed as soon as the current crossfade finishes.
    private bool _pendingPlayNext;

    // Some containers/codecs report a NaturalDuration that doesn't quite match the last
    // actually-decodable frame, so the outgoing player can raise MediaEnded a bit early - while
    // the crossfade animation is still running. Left alone, the outgoing video would just freeze
    // on its last rendered frame for whatever time is left of the fade (more noticeable the
    // longer FadeDuration is configured). These let HandleMediaEnded jump the in-progress
    // crossfade straight to its finished state instead of waiting for the animation to time out.
    private MediaPlayer? _crossFadeOutgoingPlayer;
    private Action? _forceCompleteCrossFade;

    // MediaPlaybackSession.PositionChanged does not fire on every frame - it's throttled/batched
    // by the platform (observed roughly every ~200-250ms, sometimes more under load), so relying
    // on it alone to detect "we're within FadeDuration of the end" can miss the window entirely:
    // the last PositionChanged before the end might still be earlier than transitionStart, and
    // the next one never arrives because MediaEnded fires (and freezes the last frame) first.
    // A dedicated high-frequency timer checks the position directly instead, closing that gap.
    private readonly DispatcherQueueTimer _watchdogTimer;
    private Windows.Foundation.Point? _pointerOrigin;
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

    private void ExitScreenSaver()
    {
        if (_previewParent != 0) return;
        RestoreCursor();
        Close();
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

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
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
        // _activePlayer already points at the incoming player as soon as the crossfade begins
        // (see StartTransitionToPreloaded), so MediaEnded firing on the OUTGOING player here is
        // expected and normally ignored below. But some containers/codecs report a
        // NaturalDuration that runs out slightly before the last actually-decodable frame, so the
        // outgoing player can raise MediaEnded while the opacity animation is still running -
        // left alone it would just freeze on its last frame for whatever time is left of the
        // fade. Snap the in-progress crossfade straight to its finished state instead.
        if (ReferenceEquals(player, _crossFadeOutgoingPlayer))
        {
            _forceCompleteCrossFade?.Invoke();
            return;
        }

        // Once a crossfade starts, _activePlayer is the incoming player. MediaEnded from the
        // outgoing player is therefore expected and must not queue yet another transition.
        if (!ReferenceEquals(player, _activePlayer))
        {
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

        var preloadElement = _usingPlayerA ? PlayerA : PlayerB;
        var preloadPlayer = _usingPlayerA ? _mediaPlayerA : _mediaPlayerB;
        _usingPlayerA = !_usingPlayerA;

        ElementCompositionPreview.GetElementVisual(preloadElement).StopAnimation("Opacity");
        preloadElement.Opacity = 0;

        _preloadedPlayer = preloadPlayer;
        _preloadedElement = preloadElement;
        _preloadedIndex = preloadIndex;
        _preloadedReady = false;

        _ = OpenPreloadSourceAsync(preloadPlayer, videoTarget);
    }

    /// <summary>
    /// Resolves the actual media source for <paramref name="videoTarget"/> and assigns it to
    /// <paramref name="preloadPlayer"/>. Remote (Pixabay) videos are downloaded to a local cache
    /// first instead of being played directly via HTTP streaming - direct streaming was found to
    /// stall/rebuffer near the end of the clip, freezing the screensaver on the last frame during
    /// a crossfade, something that never happened with local files (see VideoCacheService).
    /// </summary>
    private async Task OpenPreloadSourceAsync(MediaPlayer preloadPlayer, string videoTarget)
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

                    // Prime the player by playing a fraction of a second and pausing again right
                    // after its first frame renders, while there's no time pressure (we're still
                    // well ahead of the actual transition). This used to happen only right before
                    // the crossfade animation started (see StartTransitionToPreloaded), which ate
                    // into the timing margin the transition was scheduled with - if that margin
                    // ran out (network hiccup, disk latency, background load, ...) the outgoing
                    // video could reach its natural end and freeze on the last frame before the
                    // fade even began. Priming here means the incoming player already has a
                    // rendered frame ready and paused, so the transition can start instantly with
                    // no further waiting.
                    //
                    // Some clips (particular codec/container quirks) never raise a PositionChanged
                    // event here at all - the previous code would then wait on "primed" forever,
                    // which left _preloadedReady stuck at false. That, in turn, made PlayNext()
                    // slow the OUTGOING video down indefinitely (see the PlaybackRate = 0.2
                    // branch) since it kept thinking the preload was still "in flight", and once
                    // that video reached MediaEnded anyway it had nothing left to play and froze.
                    // A short timeout guarantees priming always completes one way or another.
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
                        player.PlaybackSession.Position = TimeSpan.Zero;
                        _preloadedReady = true;

                        // No video playing yet (very first video of the session): start it
                        // as soon as it's ready, there's nothing to crossfade from.
                        if (_activePlayer is null)
                        {
                            StartTransitionToPreloaded();
                            return;
                        }

                        // If PlayNext already ran and is just waiting on us, kick off the
                        // transition now that the preload has actually finished priming.
                        if (!_isTransitioning
                            && (_transitionRequested || ShouldBeginTransition(_activePlayer.PlaybackSession)))
                        {
                            StartTransitionToPreloaded();
                        }
                    }
                    primed = (session, _) =>
                    {
                        if (session.Position <= TimeSpan.Zero) return;
                        DispatcherQueue.TryEnqueue(CompletePriming);
                    };
                    primingTimeoutTimer.Tick += (_, _) => DispatcherQueue.TryEnqueue(CompletePriming);
                    player.PlaybackSession.PositionChanged += primed;
                    primingTimeoutTimer.Start();
                    player.Play();
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
            preloadPlayer.Source = MediaSource.CreateFromUri(uri);
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
        incomingPlayer.Play();

        if (isInitialVideo || outgoingPlayer is null || outgoingElement is null)
        {
            _activePlayer = incomingPlayer;
            _activeElement = incomingElement;
            incomingElement.Opacity = 1;
            // Immediately start preloading the video after this one so it's ready well in
            // advance of the next transition too.
            BeginPreload(advanceIndex: true);
        }
        else
        {
            // Wait for the incoming player to actually render its first frame after Play()
            // before starting the opacity crossfade - otherwise the animation runs over a still
            // black/blank frame, which looks like a fade-to-black instead of a true crossfade.
            // Guarded with a timeout too: if priming earlier already fell back on ITS timeout
            // (i.e. this clip never actually raises PositionChanged), waiting here again would
            // hang forever and the crossfade would never start, leaving the video stuck.
            var startCompleted = false;
            var startTimeoutTimer = DispatcherQueue.CreateTimer();
            startTimeoutTimer.Interval = TimeSpan.FromMilliseconds(500);
            TypedEventHandler<MediaPlaybackSession, object>? positionChanged = null;
            void CompleteStart()
            {
                if (startCompleted) return;
                startCompleted = true;
                startTimeoutTimer.Stop();
                incomingPlayer.PlaybackSession.PositionChanged -= positionChanged;

                _activePlayer = incomingPlayer;
                _activeElement = incomingElement;
                // Only kick off the next preload once the crossfade actually finishes and
                // the outgoing player has released its Source (see CrossFade's onCompleted).
                CrossFade(outgoingElement, outgoingPlayer, incomingElement,
                    onCompleted: () => BeginPreload(advanceIndex: true));
            }
            positionChanged = (session, _) =>
            {
                if (session.Position <= TimeSpan.Zero) return;
                DispatcherQueue.TryEnqueue(CompleteStart);
            };
            startTimeoutTimer.Tick += (_, _) => DispatcherQueue.TryEnqueue(CompleteStart);
            incomingPlayer.PlaybackSession.PositionChanged += positionChanged;
            startTimeoutTimer.Start();
        }
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

        var requiredRemainingTime = FadeDuration + TransitionLead;
        var transitionStart = naturalDuration > requiredRemainingTime
            ? naturalDuration - requiredRemainingTime
            : TimeSpan.FromMilliseconds(50);

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

        var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.InsertKeyFrame(0f, 1f);
        fadeOut.InsertKeyFrame(1f, 0f, smoothEasing);
        fadeOut.Duration = fadeDuration;

        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(1f, 1f, smoothEasing);
        fadeIn.Duration = fadeDuration;

        var completed = false;
        void Complete()
        {
            // Guard against running twice: both the batch's natural Completed event and a
            // forced early completion (see _forceCompleteCrossFade) call into this.
            if (completed) return;
            completed = true;

            _crossFadeOutgoingPlayer = null;
            _forceCompleteCrossFade = null;

            outgoingVisual.StopAnimation("Opacity");
            incomingVisual.StopAnimation("Opacity");
            outgoing.Opacity = 0;
            incoming.Opacity = 1;
            outgoingPlayer.Pause();
            outgoingPlayer.Source = null;
            _isTransitioning = false;
            // Only preload the video after this one once the outgoing player has actually
            // released its Source - preloading earlier could reuse (and reassign the Source
            // of) the player that was still mid-fade, cutting the outgoing video off abruptly
            // and looking like a fade-to-black instead of a true crossfade.
            onCompleted?.Invoke();

            // Replay a PlayNext() request that arrived while this crossfade was still
            // running (e.g. a clip shorter than FadeDuration reached its end mid-fade) -
            // otherwise the newly-active video would stay frozen on its last frame forever.
            if (_pendingPlayNext)
            {
                _pendingPlayNext = false;
                PlayNext();
            }
        }

        // Tracked so HandleMediaEnded can jump straight to the finished state if the outgoing
        // player runs out of decodable frames before the animation's scheduled duration elapses.
        _crossFadeOutgoingPlayer = outgoingPlayer;
        _forceCompleteCrossFade = () => DispatcherQueue.TryEnqueue(Complete);

        // Use a scoped batch so the final XAML Opacity values are only applied in code once the
        // composition animations actually finish, instead of being overwritten immediately
        // (which previously made the fade invisible because the plain property assignment ran
        // synchronously right after starting the animation).
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(Complete);

        outgoingVisual.StartAnimation("Opacity", fadeOut);
        incomingVisual.StartAnimation("Opacity", fadeIn);

        batch.End();
    }

    private TimeSpan GetEffectiveFadeDuration(MediaPlaybackSession outgoingSession)
    {
        var remaining = outgoingSession.NaturalDuration - outgoingSession.Position;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(150);
        }

        var availableBeforeLastFrame = remaining - TimeSpan.FromMilliseconds(250);
        if (availableBeforeLastFrame < TimeSpan.FromMilliseconds(150))
        {
            availableBeforeLastFrame = TimeSpan.FromMilliseconds(150);
        }

        return availableBeforeLastFrame < FadeDuration
            ? availableBeforeLastFrame
            : FadeDuration;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ExitScreenSaver();
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
