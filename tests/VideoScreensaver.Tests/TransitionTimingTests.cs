namespace VideoScreensaver.Tests;

[TestClass]
public sealed class TransitionTimingTests
{
    [TestMethod]
    public void TailReserve_GrowsWithDurationAndIsBounded()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(3), TransitionTiming.GetTailReserve(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(TimeSpan.FromSeconds(5), TransitionTiming.GetTailReserve(TimeSpan.FromSeconds(20)));
        Assert.AreEqual(TimeSpan.FromSeconds(6), TransitionTiming.GetTailReserve(TimeSpan.FromSeconds(60)));
    }

    [TestMethod]
    public void TransitionStart_LeavesFadeAndTailOutsideVisiblePlayback()
    {
        var start = TransitionTiming.GetTransitionStart(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(4));

        Assert.AreEqual(TimeSpan.FromSeconds(9), start);
    }

    [TestMethod]
    public void EffectiveFade_ShortensWhenTransitionStartsLate()
    {
        var duration = TransitionTiming.GetEffectiveFadeDuration(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(13),
            TimeSpan.FromSeconds(4));

        Assert.AreEqual(TimeSpan.FromSeconds(2), duration);
    }

    [TestMethod]
    public void EffectiveFade_HasSafeMinimumAfterReservedTailWasReached()
    {
        var duration = TransitionTiming.GetEffectiveFadeDuration(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(16),
            TimeSpan.FromSeconds(4));

        Assert.AreEqual(TimeSpan.FromMilliseconds(150), duration);
    }
}
