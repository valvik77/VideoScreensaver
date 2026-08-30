using Microsoft.UI.Xaml;

namespace VideoScreensaver;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (sender, e) =>
        {
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mode = ScreenSaverMode.Parse(Environment.GetCommandLineArgs().Skip(1));
        _window = mode.Kind is ScreenSaverModeKind.Run or ScreenSaverModeKind.Preview
            ? new ScreenSaverWindow(mode.PreviewHandle)
            : new MainWindow();
        _window.Activate();
    }
}
