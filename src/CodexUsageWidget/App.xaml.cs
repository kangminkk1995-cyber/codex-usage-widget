using System.Threading;
using System.Windows;

namespace CodexUsageWidget;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        if (UninstallManager.IsRequested(e.Args))
        {
            Environment.ExitCode = UninstallManager.Run(e.Args);
            Shutdown(Environment.ExitCode);
            return;
        }

        _singleInstance = new Mutex(true, "CodexUsageWidget.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Diagnostics.Initialize();
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.ReleaseMutex();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
