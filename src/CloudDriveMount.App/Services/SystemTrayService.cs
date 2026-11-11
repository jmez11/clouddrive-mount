using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using Microsoft.Win32;

namespace CloudDriveMount.App.Services;

public sealed class SystemTrayService : IDisposable
{
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppRegistryValueName = "CloudDriveMount";

    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly Func<IReadOnlyList<DriveConfig>> _getDrives;
    private readonly Action _showMainWindow;
    private readonly Action _openNewDriveDialog;
    private readonly Func<DriveConfig, Task> _toggleDriveConnection;
    private readonly Func<Task> _connectAll;
    private readonly Func<Task> _disconnectAll;
    private readonly Action _exitApplication;

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

        _menu = new ContextMenuStrip
        {
            BackColor = Color.FromArgb(36, 36, 36),
            ForeColor = Color.WhiteSmoke,
            ShowImageMargin = true
        };
        _menu.Opening += (_, _) => RebuildContextMenu();

        ExplorerIconManager.EnsureAppNotificationIdentity();
        var iconPath = ExplorerIconManager.EnsureAppTrayIconFile();
        _notifyIcon = new NotifyIcon
        {
            Icon = new Icon(iconPath),
            Text = "CloudDrive Mount — Cloud & NAS Virtual Drives",
            Visible = true,
            ContextMenuStrip = _menu
        };
        BindNotifyIconWindowAumid();

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();
    }

    public void ShowNotification(string title, string message, ToolTipIcon icon = ToolTipIcon.None)
    {
        try
        {
            ExplorerIconManager.EnsureAppNotificationIdentity();
            BindNotifyIconWindowAumid();
            _notifyIcon.ShowBalloonTip(3000, title, message, icon);
        }
        catch { }
    }

    private void BindNotifyIconWindowAumid()
    {
        try
        {
            var windowField = typeof(NotifyIcon).GetField("window", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? typeof(NotifyIcon).GetField("_window", BindingFlags.NonPublic | BindingFlags.Instance);
            if (windowField?.GetValue(_notifyIcon) is NativeWindow nw && nw.Handle != IntPtr.Zero)
            {
                ExplorerIconManager.SetWindowAppUserModelId(nw.Handle);
            }
        }
        catch { }
    }

    private void RebuildContextMenu()
    {
        _menu.Items.Clear();

        var openItem = new ToolStripMenuItem("Open CloudDrive Mount")
        {
            Font = new Font(_menu.Font, FontStyle.Bold),
            ForeColor = Color.White
        };
        openItem.Click += (_, _) => _showMainWindow();
        _menu.Items.Add(openItem);

        var newDriveItem = new ToolStripMenuItem("+ New Drive...")
        {
            ForeColor = Color.WhiteSmoke
        };
        newDriveItem.Click += (_, _) => _openNewDriveDialog();
        _menu.Items.Add(newDriveItem);

        _menu.Items.Add(new ToolStripSeparator());

        var drives = _getDrives();
        if (drives.Count == 0)
        {
            var emptyItem = new ToolStripMenuItem("No drives configured") { Enabled = false };
            _menu.Items.Add(emptyItem);
        }
        else
        {
            foreach (var drive in drives)
            {
                var statusPrefix = drive.IsConnected ? "● " : "○ ";
                var driveSubMenu = new ToolStripMenuItem(
                    $"{statusPrefix}{drive.NormalizedMountPoint}  {drive.Label} ({drive.ProtocolDisplayName})")
                {
                    ForeColor = drive.IsConnected ? Color.FromArgb(129, 199, 132) : Color.LightGray
                };

                var toggleItem = new ToolStripMenuItem(drive.IsConnected ? "Disconnect" : "Connect");
                toggleItem.Click += async (_, _) => await _toggleDriveConnection(drive);
                driveSubMenu.DropDownItems.Add(toggleItem);

                if (drive.IsConnected)
                {
                    var explorerItem = new ToolStripMenuItem($"Open {drive.NormalizedMountPoint}\\ in Explorer");
                    explorerItem.Click += (_, _) => OpenInExplorer(drive.NormalizedMountPoint);
                    driveSubMenu.DropDownItems.Add(explorerItem);
                }

                _menu.Items.Add(driveSubMenu);
            }
        }

        _menu.Items.Add(new ToolStripSeparator());

        var connectAllItem = new ToolStripMenuItem("Connect All Drives") { ForeColor = Color.WhiteSmoke };
        connectAllItem.Click += async (_, _) => await _connectAll();
        _menu.Items.Add(connectAllItem);

        var disconnectAllItem = new ToolStripMenuItem("Disconnect All Drives") { ForeColor = Color.WhiteSmoke };
        disconnectAllItem.Click += async (_, _) => await _disconnectAll();
        _menu.Items.Add(disconnectAllItem);

        _menu.Items.Add(new ToolStripSeparator());

        var autoStartItem = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsAutoStartEnabled(),
            CheckOnClick = true,
            ForeColor = Color.WhiteSmoke
        };
        autoStartItem.Click += (_, _) => SetAutoStart(autoStartItem.Checked);
        _menu.Items.Add(autoStartItem);

        _menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("Exit CloudDrive Mount")
        {
            ForeColor = Color.FromArgb(239, 154, 154)
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
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
    }
}
