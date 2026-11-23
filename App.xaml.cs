using System.Windows;
using System.Windows.Threading;

namespace VisorIV4;

public partial class App : Application
{
    private Mutex? _single;

    protected override void OnStartup(StartupEventArgs e)
    {
        var cfg = AppConfig.Load();
        Theme.Apply(cfg.Theme);
        Loc.Apply(cfg.Language);

        _single = new Mutex(true, @"Global\VisorIV4_SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show(Loc.T("S_AlreadyRunning"), Loc.T("S_AppName"), MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Error("Unhandled exception", a.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, a) => { Log.Error("Unobserved task exception", a.Exception); a.SetObserved(); };

        Log.Info("Application started");
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI error", e.Exception);
        e.Handled = true; // operator station: do not close the app for a single error
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.Info("Application closed");
        _single?.Dispose();
        base.OnExit(e);
    }
}
