using Windows.Media.Playback;

namespace VideoScreensaver;

internal static class MediaPlayerCleanup
{
    public static void Release(MediaPlayer? player)
    {
        if (player is null) return;
        _ = Task.Run(() =>
        {
            try { player.Dispose(); }
            catch (Exception exception)
            {
                AppDiagnostics.LogUnhandledException(exception, "MediaPlayer cleanup");
            }
        });
    }
}
