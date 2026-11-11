using System.Threading;
using System.Windows;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.App;

public partial class App : System.Windows.Application
{
    private Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        ExplorerIconManager.EnsureAppNotificationIdentity();
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(true, "Local\\CloudDriveMount_SingleInstance_v1", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "CloudDrive Mount is already running in the Windows System Tray.",
                "CloudDrive Mount",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;

        bool startInTray = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (startInTray)
        {
            mainWindow.HideToTray();
        }
        else
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
