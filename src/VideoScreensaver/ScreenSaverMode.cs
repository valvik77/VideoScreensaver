namespace VideoScreensaver;

public enum ScreenSaverModeKind { Configure, Run, Preview }

public sealed record ScreenSaverMode(ScreenSaverModeKind Kind, nint PreviewHandle = 0)
{
    public static ScreenSaverMode Parse(IEnumerable<string> arguments)
    {
        var values = arguments.ToArray();
        if (values.Length == 0)
            return new(ScreenSaverModeKind.Configure);

        var first = values[0].Trim();

        if (first.StartsWith("/s", StringComparison.OrdinalIgnoreCase) || first.StartsWith("-s", StringComparison.OrdinalIgnoreCase))
            return new(ScreenSaverModeKind.Run);

        if (first.StartsWith("/p", StringComparison.OrdinalIgnoreCase) || first.StartsWith("-p", StringComparison.OrdinalIgnoreCase))
        {
            var handleString = first.Contains(':') 
                ? first.Split(':', 2)[1]
                : values.ElementAtOrDefault(1);

            nint.TryParse(handleString, out var handle);
            return new(ScreenSaverModeKind.Preview, handle);
        }

        if (first.StartsWith("/c", StringComparison.OrdinalIgnoreCase) || first.StartsWith("-c", StringComparison.OrdinalIgnoreCase))
            return new(ScreenSaverModeKind.Configure);

        return new(ScreenSaverModeKind.Configure);
    }
}
