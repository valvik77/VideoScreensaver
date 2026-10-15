using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public sealed class PlaybackCoordinatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void InitialPlaybackRequiresOpeningAndPrimingBeforeActivation()
    {
        using var coordinator = new PlaybackCoordinator(3, false);
        Assert.AreEqual(PlaybackAdvance.Load, coordinator.RequestAdvance());
        var attempt = coordinator.BeginPreload(Now)!;
        Assert.AreEqual(PlaybackSlot.A, attempt.Slot);
        Assert.AreEqual(PlaybackState.Loading, coordinator.State);
        Assert.IsNull(coordinator.ActiveSlot);
        Assert.IsFalse(coordinator.Ready(attempt));
        Assert.IsFalse(coordinator.Activate(attempt));
        Assert.IsTrue(coordinator.Opened(attempt));
        Assert.AreEqual(PlaybackState.Priming, coordinator.State);
        Assert.IsTrue(coordinator.Ready(attempt));
        Assert.AreEqual(PlaybackAdvance.Start, coordinator.RequestAdvance());
        Assert.IsTrue(coordinator.BeginStart(attempt));
        Assert.IsTrue(coordinator.Activate(attempt));
        Assert.AreEqual(PlaybackState.Playing, coordinator.State);
        Assert.AreEqual(0, coordinator.ActiveIndex);
        Assert.IsNull(coordinator.Attempt);
    }

    [TestMethod]
    public void FailedPreloadNeverReplacesActivePlayerAndRetriesSameHiddenSlot()
    {
        using var coordinator = Playing();
        var attempt = coordinator.BeginPreload(Now)!;
        Assert.AreEqual(PlaybackSlot.B, attempt.Slot);
        Assert.IsTrue(coordinator.Fail(attempt, Now));
        Assert.IsTrue(attempt.Token.IsCancellationRequested);
        Assert.AreEqual(PlaybackSlot.A, coordinator.ActiveSlot);
        Assert.AreEqual(0, coordinator.ActiveIndex);
        var retry = coordinator.BeginPreload(Now)!;
        Assert.AreEqual(PlaybackSlot.B, retry.Slot);
        Assert.AreEqual(2, retry.Index);
        Assert.IsFalse(coordinator.Opened(attempt));
        Assert.IsFalse(coordinator.Ready(attempt));
        Assert.IsFalse(coordinator.Fail(attempt, Now));
        Assert.AreEqual(retry, coordinator.Attempt);
    }

    [TestMethod]
    public void RequestDuringLoadIsRememberedUntilClipIsReady()
    {
        using var coordinator = Playing();
        var attempt = coordinator.BeginPreload(Now)!;
        Assert.AreEqual(PlaybackAdvance.Wait, coordinator.RequestAdvance());
        Assert.IsTrue(coordinator.TransitionRequested);
        coordinator.Opened(attempt);
        coordinator.Ready(attempt);
        Assert.IsTrue(coordinator.TransitionRequested);
        Assert.AreEqual(PlaybackAdvance.Start, coordinator.RequestAdvance());
        coordinator.BeginStart(attempt);
        Assert.IsFalse(coordinator.TransitionRequested);
    }

    [TestMethod]
    public void EarlyVideoEndDuringStartOrFadeRequestsOnlyOneSubsequentAdvance()
    {
        using var coordinator = Playing();
        var attempt = ReadyNext(coordinator);
        coordinator.BeginStart(attempt);
        Assert.AreEqual(PlaybackAdvance.None, coordinator.RequestAdvance());
        coordinator.Activate(attempt);
        Assert.AreEqual(PlaybackState.Transitioning, coordinator.State);
        Assert.IsNull(coordinator.BeginPreload(Now));
        Assert.AreEqual(PlaybackAdvance.None, coordinator.RequestAdvance());
        Assert.AreEqual(PlaybackAdvance.None, coordinator.RequestAdvance());
        Assert.IsTrue(coordinator.CompleteTransition(attempt, out var advance));
        Assert.IsTrue(advance);
        Assert.IsFalse(coordinator.CompleteTransition(attempt, out advance));
        Assert.IsFalse(advance);
        Assert.AreEqual(PlaybackState.Playing, coordinator.State);
        Assert.AreEqual(PlaybackSlot.B, coordinator.ActiveSlot);
        Assert.AreEqual(PlaybackSlot.A, coordinator.BeginPreload(Now)!.Slot);
    }

    [TestMethod]
    public void FailureWhileResumingKeepsOutgoingVideoAndRejectsLateResume()
    {
        using var coordinator = Playing();
        var attempt = ReadyNext(coordinator);
        coordinator.BeginStart(attempt);
        coordinator.Fail(attempt, Now);
        var retry = coordinator.BeginPreload(Now)!;
        Assert.IsFalse(coordinator.Activate(attempt));
        Assert.AreEqual(PlaybackSlot.A, coordinator.ActiveSlot);
        Assert.AreEqual(PlaybackState.Loading, coordinator.State);
        Assert.AreEqual(retry, coordinator.Attempt);
    }

    [TestMethod]
    public void FailureDuringFadeRestoresOutgoingAndIgnoresAnimationCompletion()
    {
        using var coordinator = Playing();
        var attempt = ReadyNext(coordinator);
        coordinator.BeginStart(attempt);
        coordinator.Activate(attempt);
        Assert.AreEqual(PlaybackSlot.B, coordinator.ActiveSlot);
        Assert.IsTrue(coordinator.Fail(attempt, Now));
        Assert.AreEqual(PlaybackSlot.A, coordinator.ActiveSlot);
        Assert.AreEqual(0, coordinator.ActiveIndex);
        Assert.AreEqual(PlaybackState.Playing, coordinator.State);
        Assert.IsFalse(coordinator.CompleteTransition(attempt, out _));
        Assert.AreEqual(PlaybackSlot.B, coordinator.BeginPreload(Now)!.Slot);
    }

    [TestMethod]
    public void AllBrokenVideosEnterCooldownAndCanRecoverLater()
    {
        using var coordinator = new PlaybackCoordinator(2, false);
        coordinator.Fail(coordinator.BeginPreload(Now)!, Now);
        coordinator.Fail(coordinator.BeginPreload(Now)!, Now);
        Assert.IsNull(coordinator.BeginPreload(Now));
        Assert.AreEqual(PlaybackState.WaitingForRetry, coordinator.State);
        Assert.IsNull(coordinator.BeginPreload(Now.AddSeconds(29)));
        var recovered = coordinator.BeginPreload(Now.AddSeconds(30))!;
        Assert.AreEqual(PlaybackState.Loading, coordinator.State);
        coordinator.Opened(recovered);
        coordinator.Ready(recovered);
        coordinator.BeginStart(recovered);
        Assert.IsTrue(coordinator.Activate(recovered));
        Assert.AreEqual(PlaybackState.Playing, coordinator.State);
    }

    [TestMethod]
    public void FailedActiveVideoDoesNotInvalidateHealthyPreload()
    {
        using var coordinator = Playing();
        var attempt = ReadyNext(coordinator);
        coordinator.FailActive(Now);
        Assert.IsTrue(coordinator.ActiveFailed);
        Assert.AreEqual(PlaybackAdvance.Start, coordinator.RequestAdvance());
        coordinator.BeginStart(attempt);
        coordinator.Activate(attempt);
        Assert.IsFalse(coordinator.ActiveFailed);
        coordinator.CompleteTransition(attempt, out _);
        Assert.AreEqual(2, coordinator.BeginPreload(Now)!.Index);
    }

    [TestMethod]
    public async Task CancelPendingUnblocksAsyncWorkWithoutMarkingVideoAsBroken()
    {
        using var coordinator = Playing();
        var attempt = coordinator.BeginPreload(Now)!;
        var pending = Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token);
        Assert.IsTrue(coordinator.CancelPending());
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await pending);
        Assert.AreEqual(PlaybackSlot.A, coordinator.ActiveSlot);
        Assert.IsFalse(coordinator.Opened(attempt));
        var next = coordinator.BeginPreload(Now)!;
        coordinator.Fail(next, Now);
        Assert.AreEqual(0, coordinator.BeginPreload(Now)!.Index);
        coordinator.CancelPending();
        Assert.AreEqual(attempt.Index, coordinator.BeginPreload(Now)!.Index);
    }

    [TestMethod]
    [DataRow("Idle")]
    [DataRow("Loading")]
    [DataRow("Priming")]
    [DataRow("Ready")]
    [DataRow("Starting")]
    [DataRow("Transitioning")]
    [DataRow("Playing")]
    [DataRow("WaitingForRetry")]
    public void CloseFromEveryStateRejectsLateCallbacksAndNewPlayback(string state)
    {
        using var coordinator = AtState(state);
        var attempt = coordinator.Attempt;
        var cancelledAfterClosed = false;
        using var registration = attempt?.Token.Register(() => cancelledAfterClosed = coordinator.IsClosed);
        coordinator.Close();
        coordinator.Close();
        Assert.AreEqual(PlaybackState.Closed, coordinator.State);
        Assert.IsFalse(coordinator.CanPreload);
        Assert.IsNull(coordinator.BeginPreload(Now.AddMinutes(1)));
        Assert.AreEqual(PlaybackAdvance.None, coordinator.RequestAdvance());
        Assert.IsFalse(coordinator.CancelPending());
        if (attempt is not null)
        {
            Assert.IsTrue(cancelledAfterClosed);
            Assert.IsTrue(attempt.Token.IsCancellationRequested);
            Assert.IsFalse(coordinator.Opened(attempt));
            Assert.IsFalse(coordinator.Ready(attempt));
            Assert.IsFalse(coordinator.BeginStart(attempt));
            Assert.IsFalse(coordinator.Activate(attempt));
            Assert.IsFalse(coordinator.Fail(attempt, Now));
            Assert.IsFalse(coordinator.CompleteTransition(attempt, out _));
        }
    }

    [TestMethod]
    public async Task CloseCancelsPendingDecoderWait()
    {
        using var coordinator = AtState("Priming");
        var attempt = coordinator.Attempt!;
        var decoderWait = Task.Delay(Timeout.InfiniteTimeSpan, attempt.Token);
        coordinator.Close();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await decoderWait);
        Assert.IsFalse(coordinator.Ready(attempt));
    }

    [TestMethod]
    public void PreviousAnimationCannotCompleteNewTransition()
    {
        using var coordinator = Playing();
        var first = ReadyNext(coordinator);
        coordinator.BeginStart(first);
        coordinator.Activate(first);
        coordinator.CompleteTransition(first, out _);
        var second = ReadyNext(coordinator);
        coordinator.BeginStart(second);
        coordinator.Activate(second);
        Assert.IsFalse(coordinator.CompleteTransition(first, out _));
        Assert.AreEqual(PlaybackState.Transitioning, coordinator.State);
        Assert.AreEqual(second, coordinator.Attempt);
        Assert.IsTrue(coordinator.CompleteTransition(second, out _));
    }

    [TestMethod]
    public void LateMediaEventsCannotAffectReusedPlayerOrClosedCoordinator()
    {
        using var coordinator = Playing();
        var original = coordinator.ActiveAttemptId!.Value;
        var incoming = ReadyNext(coordinator);
        coordinator.BeginStart(incoming);
        coordinator.Activate(incoming);
        Assert.IsFalse(coordinator.IsActive(original));
        Assert.IsTrue(coordinator.IsOutgoing(original));
        Assert.IsTrue(coordinator.IsActive(incoming.Id));
        coordinator.CompleteTransition(incoming, out _);
        Assert.IsFalse(coordinator.IsOutgoing(original));
        var reusedSlot = coordinator.BeginPreload(Now)!;
        Assert.AreEqual(PlaybackSlot.A, reusedSlot.Slot);
        Assert.IsFalse(coordinator.IsCurrent(new PlaybackAttempt(original, 0, PlaybackSlot.A, default)));
        coordinator.Close();
        Assert.IsFalse(coordinator.IsActive(incoming.Id));
        Assert.IsFalse(coordinator.IsOutgoing(original));
    }

    private static PlaybackCoordinator Playing()
    {
        var coordinator = new PlaybackCoordinator(3, false);
        var attempt = ReadyNext(coordinator);
        coordinator.BeginStart(attempt);
        coordinator.Activate(attempt);
        return coordinator;
    }

    private static PlaybackAttempt ReadyNext(PlaybackCoordinator coordinator)
    {
        var attempt = coordinator.BeginPreload(Now)!;
        coordinator.Opened(attempt);
        coordinator.Ready(attempt);
        return attempt;
    }

    private static PlaybackCoordinator AtState(string state)
    {
        var desired = Enum.Parse<PlaybackState>(state);
        if (desired == PlaybackState.Playing) return Playing();
        var coordinator = desired == PlaybackState.Transitioning ? Playing() : new PlaybackCoordinator(3, false);
        if (desired == PlaybackState.Idle) return coordinator;
        if (desired == PlaybackState.WaitingForRetry)
        {
            for (var index = 0; index < 3; index++) coordinator.Fail(coordinator.BeginPreload(Now)!, Now);
            coordinator.BeginPreload(Now);
            return coordinator;
        }
        var attempt = coordinator.BeginPreload(Now)!;
        if (desired == PlaybackState.Loading) return coordinator;
        coordinator.Opened(attempt);
        if (desired == PlaybackState.Priming) return coordinator;
        coordinator.Ready(attempt);
        if (desired == PlaybackState.Ready) return coordinator;
        coordinator.BeginStart(attempt);
        if (desired == PlaybackState.Starting) return coordinator;
        coordinator.Activate(attempt);
        return coordinator;
    }
}
