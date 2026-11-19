using System.Threading;
using System.Windows;
using CloudDriveMount.App.Views;
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

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        bool forcePortable = e.Args.Any(a => string.Equals(a, "--portable", StringComparison.OrdinalIgnoreCase));
        var configStore = new ConfigStore(forcePortable: forcePortable);
        var vault = configStore.LoadVaultMetadata();
        if (vault != null && !VaultManager.TryAutoUnlock(vault))
        {
            var unlockWindow = new VaultUnlockWindow(vault);
            if (unlockWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        var mainWindow = new MainWindow(configStore);
        MainWindow = mainWindow;
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        mainWindow.StartAutoReconnect();

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
