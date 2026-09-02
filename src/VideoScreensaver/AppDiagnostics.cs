using System.Runtime.InteropServices;
using System.Text;

namespace VideoScreensaver;

internal static class AppDiagnostics
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VideoScreensaver",
        "logs");

    public static void LogUnhandledException(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var entry = $"[{DateTimeOffset.UtcNow:O}] {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(LogDirectory, "errors.log"), entry, Encoding.UTF8);
        }
        catch
        {
            // Error logging must never hide or replace the original failure.
        }
    }

    public static void ShowControlledError()
    {
        MessageBox(
            nint.Zero,
            "Se produjo un error inesperado. La aplicación se cerrará o puede continuar de forma limitada. " +
            "Consulta el registro de errores para más información.",
            "Video Screensaver",
            MbOk | MbIconError);
    }

    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(nint owner, string text, string caption, uint type);
}
