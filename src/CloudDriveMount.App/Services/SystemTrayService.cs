using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using CloudDriveMount.App.Views;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using Microsoft.Win32;

namespace CloudDriveMount.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "CloudDriveMount";

    private const int WM_TRAYICON = 0x8001;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private readonly HwndSource _hwndSource;
    private readonly IntPtr _hIcon;
    private readonly ContextMenu _menu;
    private readonly Func<IReadOnlyList<DriveConfig>> _getDrives;
    private readonly Action _showMainWindow;
    private readonly Action _openNewDriveDialog;
    private readonly Func<DriveConfig, Task> _toggleDriveConnection;
    private readonly Func<Task> _connectAll;
    private readonly Func<Task> _disconnectAll;
    private readonly Action _exitApplication;
    private bool _disposed;

    public SystemTrayService(
        Func<IReadOnlyList<DriveConfig>> getDrives,
        Action showMainWindow,
        Action openNewDriveDialog,
        Func<DriveConfig, Task> toggleDriveConnection,
        Func<Task> connectAll,
        Func<Task> disconnectAll,
        Action exitApplication)
    {
        _getDrives = getDrives;
        _showMainWindow = showMainWindow;
        _openNewDriveDialog = openNewDriveDialog;
        _toggleDriveConnection = toggleDriveConnection;
        _connectAll = connectAll;
        _disconnectAll = disconnectAll;
        _exitApplication = exitApplication;

        _menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
            Background = new SolidColorBrush(Color.FromRgb(36, 36, 36)),
            Foreground = Brushes.WhiteSmoke,
            BorderBrush = new SolidColorBrush(Color.FromRgb(68, 68, 68)),
            BorderThickness = new Thickness(1)
        };

        ExplorerIconManager.EnsureAppNotificationIdentity();
        var iconPath = ExplorerIconManager.EnsureAppTrayIconFile();
        _hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);

        var parameters = new HwndSourceParameters("CloudDriveMountTrayHost")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0
        };
        _hwndSource = new HwndSource(parameters);
        _hwndSource.AddHook(WndProc);

        var nid = CreateNotifyIconData();
        Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    public void ShowNotification(string title, string message)
    {
        try
        {
            ToastNotificationWindow.ShowToast(title, message, _showMainWindow);
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int mouseMsg = (int)(lParam.ToInt64() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONDBLCLK)
            {
                _showMainWindow();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
            {
                RebuildContextMenu();
                SetForegroundWindow(_hwndSource.Handle);
                _menu.IsOpen = true;
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _hwndSource.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "CloudDrive Mount — Cloud & NAS Virtual Drives",
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private void RebuildContextMenu()
    {
        _menu.Items.Clear();

        var openItem = new MenuItem
        {
            Header = "Open CloudDrive Mount",
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White
        };
        openItem.Click += (_, _) => _showMainWindow();
        _menu.Items.Add(openItem);

        var newDriveItem = new MenuItem
        {
            Header = "+ New Drive...",
            Foreground = Brushes.WhiteSmoke
        };
        newDriveItem.Click += (_, _) => _openNewDriveDialog();
        _menu.Items.Add(newDriveItem);

        _menu.Items.Add(new Separator());

        var drives = _getDrives();
        if (drives.Count == 0)
        {
            _menu.Items.Add(new MenuItem
            {
                Header = "No drives configured",
                IsEnabled = false
            });
        }
        else
        {
            foreach (var drive in drives)
            {
                var statusPrefix = drive.IsConnected ? "● " : "○ ";
                var driveSubMenu = new MenuItem
                {
                    Header = $"{statusPrefix}{drive.NormalizedMountPoint}  {drive.Label} ({drive.ProtocolDisplayName})",
                    Foreground = drive.IsConnected
                        ? new SolidColorBrush(Color.FromRgb(129, 199, 132))
                        : Brushes.LightGray
                };

                var toggleItem = new MenuItem { Header = drive.IsConnected ? "Disconnect" : "Connect" };
                toggleItem.Click += async (_, _) => await _toggleDriveConnection(drive);
                driveSubMenu.Items.Add(toggleItem);

                if (drive.IsConnected)
                {
                    var explorerItem = new MenuItem { Header = $"Open {drive.NormalizedMountPoint}\\ in Explorer" };
                    explorerItem.Click += (_, _) => OpenInExplorer(drive.NormalizedMountPoint);
                    driveSubMenu.Items.Add(explorerItem);
                }

                _menu.Items.Add(driveSubMenu);
            }
        }

        _menu.Items.Add(new Separator());

        var connectAllItem = new MenuItem { Header = "Connect All Drives", Foreground = Brushes.WhiteSmoke };
        connectAllItem.Click += async (_, _) => await _connectAll();
        _menu.Items.Add(connectAllItem);

        var disconnectAllItem = new MenuItem { Header = "Disconnect All Drives", Foreground = Brushes.WhiteSmoke };
        disconnectAllItem.Click += async (_, _) => await _disconnectAll();
        _menu.Items.Add(disconnectAllItem);

        _menu.Items.Add(new Separator());

        var autoStartItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = IsAutoStartEnabled(),
            Foreground = Brushes.WhiteSmoke
        };
        autoStartItem.Click += (_, _) => SetAutoStart(autoStartItem.IsChecked);
        _menu.Items.Add(autoStartItem);

        _menu.Items.Add(new Separator());

        var exitItem = new MenuItem
        {
            Header = "Exit CloudDrive Mount",
            Foreground = new SolidColorBrush(Color.FromRgb(239, 154, 154))
        };
        exitItem.Click += (_, _) => _exitApplication();
        _menu.Items.Add(exitItem);
    }

    public static void OpenInExplorer(string mountPoint)
    {
        try
        {
            var path = mountPoint.EndsWith('\\') ? mountPoint : mountPoint + "\\";
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = path,
                UseShellExecute = true
            });
        }
        catch { }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(AppRegistryValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);
            if (enable)
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath))
                    key?.SetValue(AppRegistryValueName, $"\"{exePath}\" --tray");
            }
            else
            {
                key?.DeleteValue(AppRegistryValueName, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            var nid = CreateNotifyIconData();
            Shell_NotifyIconW(NIM_DELETE, ref nid);
        }
        catch { }

        try { _hwndSource.Dispose(); } catch { }
        if (_hIcon != IntPtr.Zero)
        {
            try { DestroyIcon(_hIcon); } catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}
