using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace VideoScreensaver.Tests;

[TestClass]
public sealed class PlaybackQueueTests
{
    [TestMethod]
    public void FailedCandidatesAreSkippedAndSequentialOrderWraps()
    {
        var queue = new PlaybackQueue(3);
        var now = DateTimeOffset.UtcNow;
        Assert.AreEqual(0, queue.Next(false, now));
        Assert.AreEqual(1, queue.Next(false, now));
        queue.Failed(1, now);
        Assert.AreEqual(2, queue.Next(false, now));
        Assert.AreEqual(0, queue.Next(false, now));
        Assert.AreEqual(2, queue.Next(false, now));
    }

    [TestMethod]
    public void AllFailuresWaitBeforeRetryingAndRepeatedFailureDoesNotExtendWait()
    {
        var queue = new PlaybackQueue(2);
        var now = DateTimeOffset.UtcNow;
        queue.Failed(queue.Next(false, now), now);
        queue.Failed(queue.Next(false, now), now);
        for (var seconds = 0; seconds < 30; seconds++)
        {
            Assert.AreEqual(-1, queue.Next(false, now.AddSeconds(seconds)));
            queue.Failed(1, now.AddSeconds(seconds));
        }
        Assert.AreEqual(0, queue.Next(false, now + PlaybackQueue.RetryDelay));
    }

    [TestMethod]
    public void ShuffleAvoidsImmediateRepeatAndNeverSelectsFailedClip()
    {
        var queue = new PlaybackQueue(3);
        var now = DateTimeOffset.UtcNow;
        queue.Failed(1, now);
        var previous = -1;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var next = queue.Next(true, now);
            Assert.AreNotEqual(1, next);
            Assert.AreNotEqual(previous, next);
            previous = next;
        }
    }

    [TestMethod]
    public void SingleVideoCanRepeatAndEmptyQueueHasNoCandidate()
    {
        var now = DateTimeOffset.UtcNow;
        var queue = new PlaybackQueue(1);
        Assert.AreEqual(0, queue.Next(true, now));
        Assert.AreEqual(0, queue.Next(true, now));
        Assert.AreEqual(-1, new PlaybackQueue(0).Next(true, now));
    }

}
