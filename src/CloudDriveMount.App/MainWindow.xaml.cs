using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CloudDriveMount.App.Helpers;
using CloudDriveMount.App.Services;
using CloudDriveMount.App.Views;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace CloudDriveMount.App;

public partial class MainWindow : Window
{
    private readonly ConfigStore _configStore;
    private readonly DriveMountManager _mountManager;
    private readonly List<DriveConfig> _drives;
    private readonly SystemTrayService _trayService;
    private bool _isExplicitExit;
    private bool _shownTrayHint;

    public MainWindow()
    {
        InitializeComponent();

        _configStore = new ConfigStore();
        _mountManager = new DriveMountManager();
        _drives = _configStore.LoadDrives();

        foreach (var d in _drives)
        {
            d.PropertyChanged += OnDrivePropertyChanged;
        }

        var appIcon = IconImageHelper.GetAppLogoImage();
        Icon = appIcon;
        AppLogoImage.Source = appIcon;

        _trayService = new SystemTrayService(
            getDrives: () => _drives,
            showMainWindow: () => Dispatcher.Invoke(RestoreFromTray),
            openNewDriveDialog: () => Dispatcher.Invoke(() =>
            {
                RestoreFromTray();
                OpenDriveEditor(null);
            }),
            toggleDriveConnection: ToggleDriveConnectionAsync,
            connectAll: ConnectAllDrivesAsync,
            disconnectAll: DisconnectAllDrivesAsync,
            exitApplication: () => Dispatcher.Invoke(PerformExitApplication));

        RefreshEnvironmentBanner();
        RebuildDriveCards();

        Loaded += OnMainWindowLoaded;
    }

    private async void OnMainWindowLoaded(object sender, RoutedEventArgs e)
    {
        // Auto-reconnect drives marked ReconnectAtStart
        var autoDrives = _drives.Where(d => d.ReconnectAtStart && !d.SignInOnEachConnection).ToList();
        foreach (var drive in autoDrives)
        {
            try
            {
                await _mountManager.MountDriveAsync(drive);
            }
            catch
            {
                // Individual drive error is displayed on its card
            }
        }
    }

    private void OnDrivePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.Invoke(RebuildDriveCards);
    }

    private void RefreshEnvironmentBanner()
    {
        bool winfspInstalled = DriveMountManager.IsWinFspInstalled();
        if (winfspInstalled)
        {
            WinFspStatusText.Text = "● WinFsp Kernel Driver: Installed & Ready";
            WinFspStatusText.Foreground = new SolidColorBrush(Color.FromRgb(129, 199, 132));
            InstallWinFspBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            WinFspStatusText.Text = "▲ WinFsp Kernel Driver: Not Installed";
            WinFspStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 183, 77));
            InstallWinFspBtn.Visibility = Visibility.Visible;
        }

        var cfPath = CloudflaredTunnelManager.ResolveCloudflaredPath();
        CloudflaredStatusText.Text = cfPath != null
            ? $"cloudflared: Ready ({ System.IO.Path.GetFileName(cfPath) })"
            : "cloudflared: Not in PATH (configure path in Proxy settings)";
    }

    private void RebuildDriveCards()
    {
        DrivesItemsControl.Items.Clear();
        EmptyStatePanel.Visibility = _drives.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var drive in _drives)
        {
            DrivesItemsControl.Items.Add(BuildDriveCard(drive));
        }
    }

    private UIElement BuildDriveCard(DriveConfig drive)
    {
        var cardBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)),
            BorderBrush = drive.IsConnected
                ? new SolidColorBrush(Color.FromRgb(67, 160, 71))
                : new SolidColorBrush(Color.FromRgb(62, 62, 62)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Column 0: Protocol Icon (same icon shown in Windows Explorer)
        var iconImg = new Image
        {
            Width = 42,
            Height = 42,
            Source = IconImageHelper.GetProtocolImage(drive.Protocol, drive.IsLocalDisk),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left
        };
        Grid.SetColumn(iconImg, 0);
        grid.Children.Add(iconImg);

        // Column 1: Drive Info & Status
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        var letterBadge = new Border
        {
            Background = drive.IsConnected
                ? new SolidColorBrush(Color.FromRgb(46, 125, 50))
                : new SolidColorBrush(Color.FromRgb(66, 66, 66)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0)
        };
        letterBadge.Child = new TextBlock
        {
            Text = drive.NormalizedMountPoint,
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Foreground = Brushes.White
        };
        titleRow.Children.Add(letterBadge);

        titleRow.Children.Add(new TextBlock
        {
            Text = drive.Label,
            FontWeight = FontWeights.SemiBold,
            FontSize = 15,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        titleRow.Children.Add(new TextBlock
        {
            Text = $"[{drive.ProtocolDisplayName}]",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(144, 202, 249)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });

        titleRow.Children.Add(new TextBlock
        {
            Text = drive.MountModeBadge,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromRgb(176, 190, 197)),
            VerticalAlignment = VerticalAlignment.Center
        });

        infoStack.Children.Add(titleRow);

        var subRow = new StackPanel { Orientation = Orientation.Horizontal };
        subRow.Children.Add(new TextBlock
        {
            Text = drive.EndpointSummary,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(158, 158, 158)),
            Margin = new Thickness(0, 0, 14, 0)
        });

        var statusColor = drive.State switch
        {
            DriveConnectionState.Connected => Color.FromRgb(129, 199, 132),
            DriveConnectionState.Connecting => Color.FromRgb(255, 213, 79),
            DriveConnectionState.Error => Color.FromRgb(239, 154, 154),
            _ => Color.FromRgb(140, 140, 140)
        };

        subRow.Children.Add(new TextBlock
        {
            Text = $"• {drive.StatusMessage}",
            FontSize = 12,
            Foreground = new SolidColorBrush(statusColor)
        });

        infoStack.Children.Add(subRow);
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // Column 2: Action Buttons
        var btnStack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        var connectBtn = new Button
        {
            Content = drive.IsConnected ? "Disconnect" : (drive.IsBusy ? "Connecting..." : "Connect"),
            IsEnabled = !drive.IsBusy,
            Width = 96,
            Margin = new Thickness(0, 0, 6, 0),
            Style = (Style)FindResource(drive.IsConnected ? "ToolbarBtn" : "AccentBtn")
        };
        connectBtn.Click += async (_, _) => await ToggleDriveConnectionAsync(drive);
        btnStack.Children.Add(connectBtn);

        if (drive.IsConnected)
        {
            var openBtn = new Button
            {
                Content = "Explorer",
                Width = 76,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)FindResource("ToolbarBtn")
            };
            openBtn.Click += (_, _) => SystemTrayService.OpenInExplorer(drive.NormalizedMountPoint);
            btnStack.Children.Add(openBtn);
        }

        var editBtn = new Button
        {
            Content = "Edit",
            Width = 64,
            Margin = new Thickness(0, 0, 6, 0),
            IsEnabled = !drive.IsBusy,
            Style = (Style)FindResource("ToolbarBtn")
        };
        editBtn.Click += (_, _) => OpenDriveEditor(drive);
        btnStack.Children.Add(editBtn);

        var deleteBtn = new Button
        {
            Content = "✕",
            Width = 36,
            ToolTip = "Remove Drive",
            IsEnabled = !drive.IsConnected && !drive.IsBusy,
            Style = (Style)FindResource("ToolbarBtn")
        };
        deleteBtn.Click += (_, _) => RemoveDrive(drive);
        btnStack.Children.Add(deleteBtn);

        Grid.SetColumn(btnStack, 2);
        grid.Children.Add(btnStack);

        cardBorder.Child = grid;
        return cardBorder;
    }

    private async Task ToggleDriveConnectionAsync(DriveConfig drive)
    {
        try
        {
            if (drive.IsConnected)
            {
                await _mountManager.UnmountDriveAsync(drive);
                FooterStatusText.Text = $"Unmounted {drive.NormalizedMountPoint} ({drive.Label}).";
            }
            else
            {
                await _mountManager.MountDriveAsync(drive);
                FooterStatusText.Text = $"Mounted {drive.Label} on {drive.NormalizedMountPoint}\\.";
                _trayService.ShowNotification(
                    "Drive Connected",
                    $"{drive.Label} ({drive.ProtocolDisplayName}) is now available at {drive.NormalizedMountPoint}\\");
            }
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = $"Error on {drive.NormalizedMountPoint}: {ex.Message}";
        }
        finally
        {
            RebuildDriveCards();
        }
    }

    private async Task ConnectAllDrivesAsync()
    {
        foreach (var drive in _drives.Where(d => !d.IsConnected && !d.IsBusy).ToList())
        {
            try
            {
                await _mountManager.MountDriveAsync(drive);
            }
            catch
            {
                // Error shown on card
            }
        }
        RebuildDriveCards();
    }

    private async Task DisconnectAllDrivesAsync()
    {
        foreach (var drive in _drives.Where(d => d.IsConnected).ToList())
        {
            await _mountManager.UnmountDriveAsync(drive);
        }
        RebuildDriveCards();
    }

    private async void OpenDriveEditor(DriveConfig? existingDrive)
    {
        var usedLetters = _drives
            .Where(d => existingDrive == null || d.Id != existingDrive.Id)
            .Select(d => d.NormalizedMountPoint);
        var editor = new DriveEditorWindow(existingDrive, usedLetters)
        {
            Owner = IsVisible ? this : null
        };

        if (editor.ShowDialog() == true)
        {
            bool wasConnected = existingDrive?.IsConnected == true;
            if (existingDrive != null && wasConnected)
            {
                await _mountManager.UnmountDriveAsync(existingDrive);
            }

            var updated = editor.ResultConfig;
            updated.PropertyChanged += OnDrivePropertyChanged;

            if (existingDrive != null)
            {
                existingDrive.PropertyChanged -= OnDrivePropertyChanged;
                var idx = _drives.IndexOf(existingDrive);
                if (idx >= 0)
                    _drives[idx] = updated;
            }
            else
            {
                _drives.Add(updated);
            }

            _configStore.SaveDrives(_drives);
            RebuildDriveCards();

            if (editor.ConnectImmediately || wasConnected)
            {
                await ToggleDriveConnectionAsync(updated);
            }
        }
    }

    private void RemoveDrive(DriveConfig drive)
    {
        drive.PropertyChanged -= OnDrivePropertyChanged;
        _drives.Remove(drive);
        _configStore.SaveDrives(_drives);
        RebuildDriveCards();
    }

    private void OnNewDriveClick(object sender, RoutedEventArgs e)
    {
        OpenDriveEditor(null);
    }

    private async void OnConnectAllClick(object sender, RoutedEventArgs e)
    {
        await ConnectAllDrivesAsync();
    }

    private async void OnDisconnectAllClick(object sender, RoutedEventArgs e)
    {
        await DisconnectAllDrivesAsync();
    }

    private async void OnInstallWinFspClick(object sender, RoutedEventArgs e)
    {
        InstallWinFspBtn.IsEnabled = false;
        try
        {
            FooterStatusText.Text = "Installing bundled WinFsp kernel driver (winfsp.msi)...";
            await DriveMountManager.InstallBundledWinFspAsync();
            RefreshEnvironmentBanner();
            FooterStatusText.Text = DriveMountManager.IsWinFspInstalled()
                ? "WinFsp driver installed! You can now mount drives."
                : "WinFsp installation did not complete.";
        }
        catch (Exception ex)
        {
            FooterStatusText.Text = $"WinFsp install error: {ex.Message}";
        }
        finally
        {
            InstallWinFspBtn.IsEnabled = true;
        }
    }

    private void OnMinimizeToTrayClick(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void OnQuitApplicationClick(object sender, RoutedEventArgs e)
    {
        PerformExitApplication();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _mountManager.Dispose();
        _trayService.Dispose();
        base.OnClosing(e);
    }

    public void HideToTray()
    {
        Hide();
        if (!_shownTrayHint)
        {
            _shownTrayHint = true;
            _trayService.ShowNotification(
                "CloudDrive Mount is running in the System Tray",
                "Double-click the tray icon to open the window or right-click to manage drives.");
        }
    }

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void PerformExitApplication()
    {
        _isExplicitExit = true;
        _mountManager.Dispose();
        _trayService.Dispose();
        Application.Current.Shutdown();
    }
}
