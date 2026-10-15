namespace VideoScreensaver;

/// <summary>Chooses candidates without retrying a broken clip in a tight loop.</summary>
internal sealed class PlaybackQueue(int count)
{
    private readonly HashSet<int> _failed = [];
    private int _last = -1;
    private DateTimeOffset _retryAt;
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public int Next(bool shuffle, DateTimeOffset now)
    {
        if (count == 0) return -1;
        if (_failed.Count == count)
        {
            if (now < _retryAt) return -1;
            _failed.Clear();
        }

        var candidates = Enumerable.Range(1, count)
            .Select(offset => (_last + offset) % count)
            .Where(index => !_failed.Contains(index)).ToArray();
        if (shuffle && candidates.Length > 1)
            candidates = candidates.Where(index => index != _last).ToArray();
        _last = shuffle ? candidates[Random.Shared.Next(candidates.Length)] : candidates[0];
        return _last;
    }

    public void Failed(int index, DateTimeOffset now)
    {
        if (index < 0 || index >= count) return;
        if (_failed.Add(index) && _failed.Count == count) _retryAt = now + RetryDelay;
    }

}
