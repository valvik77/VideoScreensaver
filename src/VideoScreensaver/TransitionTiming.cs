namespace VideoScreensaver;

/// <summary>
/// Calculates crossfade timing while deliberately excluding the unreliable tail of a video.
/// Some containers keep advancing after their decoder has stopped producing distinct frames.
/// </summary>
internal static class TransitionTiming
{
    private static readonly TimeSpan MinimumTailReserve = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaximumTailReserve = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan MinimumFadeDuration = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan IncomingStartupAllowance = TimeSpan.FromSeconds(2);

    internal static TimeSpan GetTailReserve(TimeSpan naturalDuration)
    {
        // Longer ambient clips are more likely to contain a sizeable timestamp/static-frame tail.
        // Reserve 25%, bounded so normal clips are not cut excessively.
        var proportionalSeconds = naturalDuration.TotalSeconds * 0.25;
        return TimeSpan.FromSeconds(Math.Clamp(
            proportionalSeconds,
            MinimumTailReserve.TotalSeconds,
            MaximumTailReserve.TotalSeconds));
    }

    internal static TimeSpan GetTransitionStart(TimeSpan naturalDuration, TimeSpan fadeDuration)
    {
        // The incoming decoder is started while the outgoing video remains fully visible. Allow
        // it time to produce several frames without consuming either the fade or tail reserve.
        var requiredRemainingTime =
            IncomingStartupAllowance + fadeDuration + GetTailReserve(naturalDuration);
        return naturalDuration > requiredRemainingTime
            ? naturalDuration - requiredRemainingTime
            : TimeSpan.FromMilliseconds(50);
    }

    internal static TimeSpan GetEffectiveFadeDuration(
        TimeSpan naturalDuration,
        TimeSpan currentPosition,
        TimeSpan configuredFadeDuration)
    {
        var availableBeforeReservedTail =
            naturalDuration - currentPosition - GetTailReserve(naturalDuration);

        if (availableBeforeReservedTail <= MinimumFadeDuration)
        {
            return MinimumFadeDuration;
        }

        return availableBeforeReservedTail < configuredFadeDuration
            ? availableBeforeReservedTail
            : configuredFadeDuration;
    }
}
