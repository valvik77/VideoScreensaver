using Microsoft.UI.Xaml;

namespace VideoScreensaver;

public partial class App : Application
{
    private Window? _window;
    private bool _isScreenSaverMode;
    private bool _hasShownUnhandledError;

    public App()
    {
        InitializeComponent();
        UnhandledException += (sender, e) =>
        {
            AppDiagnostics.LogUnhandledException(e.Exception, "WinUI UnhandledException");
            e.Handled = true;
            if (!_isScreenSaverMode && !_hasShownUnhandledError)
            {
                _hasShownUnhandledError = true;
                AppDiagnostics.ShowControlledError();
            }
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
            {
                AppDiagnostics.LogUnhandledException(exception, "AppDomain UnhandledException");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppDiagnostics.LogUnhandledException(e.Exception, "TaskScheduler UnobservedTaskException");
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var mode = ScreenSaverMode.Parse(Environment.GetCommandLineArgs().Skip(1));
        _isScreenSaverMode = mode.Kind is ScreenSaverModeKind.Run or ScreenSaverModeKind.Preview;
        _window = mode.Kind is ScreenSaverModeKind.Run or ScreenSaverModeKind.Preview
            ? new ScreenSaverWindow(mode.PreviewHandle)
            : new MainWindow();
        _window.Activate();
    }
}
