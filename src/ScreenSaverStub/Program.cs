using System.Diagnostics;
using Microsoft.Win32;

// Windows' classic "Change screen saver" dialog (desk.cpl) only lists .scr files found directly
// in %WINDIR%\System32 (or SysWOW64) - it never looks inside Program Files or per-user install
// locations. VideoScreensaver.exe is a self-contained WinUI 3 app with ~400 supporting files, so
// copying the whole thing into System32 is impractical. Instead, this tiny stub is what actually
// gets installed as System32\VideoScreensaver.scr: it just relays the same command-line arguments
// (/c, /s, /p:<handle>) to the real application, wherever the installer placed it, and exits with
// its process' exit code once the real app finishes (screen saver mode) or immediately after
// launching it (configure mode never needs to block the caller).
const string RegistryKeyPath = @"SOFTWARE\Video Screensaver";
const string RegistryValueName = "InstallDir";

var installDir = (string?)Registry.GetValue($@"HKEY_LOCAL_MACHINE\{RegistryKeyPath}", RegistryValueName, null)
    ?? (string?)Registry.GetValue($@"HKEY_CURRENT_USER\{RegistryKeyPath}", RegistryValueName, null);

if (string.IsNullOrWhiteSpace(installDir))
{
    return;
}

var targetExe = Path.Combine(installDir, "VideoScreensaver.exe");
if (!File.Exists(targetExe))
{
    return;
}

var startInfo = new ProcessStartInfo(targetExe)
{
    UseShellExecute = false,
    WorkingDirectory = installDir,
};

foreach (var argument in args)
{
    startInfo.ArgumentList.Add(argument);
}

using var process = Process.Start(startInfo);
process?.WaitForExit();
