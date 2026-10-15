namespace VideoScreensaver;

internal enum PlaybackState
{
    Idle, Loading, Priming, Ready, Starting, Transitioning, Playing, WaitingForRetry, Closed
}

internal enum PlaybackSlot { A, B }
internal enum PlaybackAdvance { None, Load, Wait, Start }

internal sealed record PlaybackAttempt(long Id, int Index, PlaybackSlot Slot, CancellationToken Token);

/// <summary>
/// Owns playback state on the UI thread, independently of WinUI and MediaPlayer.
/// Every asynchronous result must carry its attempt: stale results cannot advance a new clip.
/// </summary>
internal sealed class PlaybackCoordinator(int count, bool shuffle) : IDisposable
{
    private readonly PlaybackQueue _queue = new(count);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _attemptCancellation;
    private long _generation;
    private bool _pendingAdvance;
    private (PlaybackSlot? Slot, int Index, long? Id, bool Failed)? _outgoing;

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public PlaybackSlot? ActiveSlot { get; private set; }
    public int ActiveIndex { get; private set; } = -1;
    public long? ActiveAttemptId { get; private set; }
    public bool ActiveFailed { get; private set; }
    public PlaybackAttempt? Attempt { get; private set; }
    public bool TransitionRequested { get; private set; }
    public bool IsClosed => State == PlaybackState.Closed;
    public bool IsTransitioning => State is PlaybackState.Starting or PlaybackState.Transitioning;
    public bool CanPreload => State is PlaybackState.Idle or PlaybackState.Playing or PlaybackState.WaitingForRetry;

    public bool IsCurrent(PlaybackAttempt attempt) => !IsClosed && Attempt?.Id == attempt.Id;
    public bool IsActive(long attemptId) => !IsClosed && ActiveAttemptId == attemptId;
    public bool IsOutgoing(long attemptId) => !IsClosed && _outgoing?.Id == attemptId;

    public PlaybackAttempt? BeginPreload(DateTimeOffset now)
    {
        if (!CanPreload) return null;
        var index = _queue.Next(shuffle, now);
        if (index < 0)
        {
            State = count == 0 ? PlaybackState.Idle : PlaybackState.WaitingForRetry;
            return null;
        }

        _attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Attempt = new PlaybackAttempt(++_generation, index,
            ActiveSlot == PlaybackSlot.A ? PlaybackSlot.B : PlaybackSlot.A, _attemptCancellation.Token);
        State = PlaybackState.Loading;
        return Attempt;
    }

    public bool Opened(PlaybackAttempt attempt) => Move(attempt, PlaybackState.Loading, PlaybackState.Priming);
    public bool Ready(PlaybackAttempt attempt) => Move(attempt, PlaybackState.Priming, PlaybackState.Ready);

    public PlaybackAdvance RequestAdvance()
    {
        if (IsClosed) return PlaybackAdvance.None;
        if (IsTransitioning)
        {
            _pendingAdvance = true;
            return PlaybackAdvance.None;
        }
        TransitionRequested = true;
        if (State == PlaybackState.Ready) return PlaybackAdvance.Start;
        return Attempt is null ? PlaybackAdvance.Load : PlaybackAdvance.Wait;
    }

    public bool BeginStart(PlaybackAttempt attempt)
    {
        if (!Move(attempt, PlaybackState.Ready, PlaybackState.Starting)) return false;
        TransitionRequested = false;
        return true;
    }

    public bool Activate(PlaybackAttempt attempt)
    {
        if (!IsCurrent(attempt) || State != PlaybackState.Starting) return false;
        var initial = ActiveSlot is null;
        if (!initial) _outgoing = (ActiveSlot, ActiveIndex, ActiveAttemptId, ActiveFailed);
        ActiveSlot = attempt.Slot;
        ActiveIndex = attempt.Index;
        ActiveAttemptId = attempt.Id;
        ActiveFailed = false;
        State = initial ? PlaybackState.Playing : PlaybackState.Transitioning;
        if (initial) ClearAttempt(cancel: false);
        return true;
    }

    public bool CompleteTransition(PlaybackAttempt attempt, out bool advance)
    {
        advance = false;
        if (!IsCurrent(attempt) || State != PlaybackState.Transitioning) return false;
        ClearAttempt(cancel: false);
        _outgoing = null;
        State = PlaybackState.Playing;
        advance = _pendingAdvance;
        _pendingAdvance = false;
        return true;
    }

    public bool Fail(PlaybackAttempt attempt, DateTimeOffset now)
    {
        if (!IsCurrent(attempt)) return false;
        _queue.Failed(attempt.Index, now);
        if (_outgoing is { } outgoing)
        {
            ActiveSlot = outgoing.Slot;
            ActiveIndex = outgoing.Index;
            ActiveAttemptId = outgoing.Id;
            ActiveFailed = outgoing.Failed;
            _outgoing = null;
        }
        TransitionRequested |= _pendingAdvance;
        _pendingAdvance = false;
        ClearAttempt(cancel: true);
        State = ActiveSlot is null ? PlaybackState.Idle : PlaybackState.Playing;
        return true;
    }

    public void FailActive(DateTimeOffset now)
    {
        if (IsClosed || ActiveSlot is null) return;
        _queue.Failed(ActiveIndex, now);
        ActiveFailed = true;
    }

    public bool CancelPending()
    {
        if (IsClosed || Attempt is null || State == PlaybackState.Transitioning) return false;
        ClearAttempt(cancel: true);
        _pendingAdvance = false;
        TransitionRequested = false;
        State = ActiveSlot is null ? PlaybackState.Idle : PlaybackState.Playing;
        return true;
    }

    public void Close()
    {
        if (IsClosed) return;
        // Invalidate callbacks before cancellation continuations can run.
        State = PlaybackState.Closed;
        _pendingAdvance = false;
        TransitionRequested = false;
        _outgoing = null;
        ClearAttempt(cancel: true);
        _lifetime.Cancel();
    }

    public void Dispose()
    {
        Close();
        _lifetime.Dispose();
    }

    private bool Move(PlaybackAttempt attempt, PlaybackState expected, PlaybackState next)
    {
        if (!IsCurrent(attempt) || State != expected) return false;
        State = next;
        return true;
    }

    private void ClearAttempt(bool cancel)
    {
        Attempt = null;
        var cancellation = _attemptCancellation;
        _attemptCancellation = null;
        if (cancel) cancellation?.Cancel();
        cancellation?.Dispose();
    }
}
