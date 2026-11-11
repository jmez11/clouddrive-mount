using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.ServiceProcess;
using System.Text;
using CloudDriveMount.Core.FileSystem;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Providers;
using Fsp;
using Microsoft.Win32;

namespace CloudDriveMount.Core.Services;

public sealed class ActiveMountSession : IDisposable
{
    public required DriveConfig Config { get; init; }
    public required ICloudStorageProvider Provider { get; init; }
    public required CloudDriveFileSystem FileSystem { get; init; }
    public required FileSystemHost Host { get; init; }
    public IntPtr KernelMountMgrVolumeHandle { get; set; }

    public void Dispose()
    {
        if (KernelMountMgrVolumeHandle != IntPtr.Zero)
        {
            DriveMountManager.UnpublishMountPointViaKernelMountMgr(KernelMountMgrVolumeHandle);
            KernelMountMgrVolumeHandle = IntPtr.Zero;
        }

        try
        {
            Host.Unmount();
            Host.Dispose();
        }
        catch { }

        try
        {
            FileSystem.Dispose();
        }
        catch { }

        try
        {
            Provider.Dispose();
        }
        catch { }
    }
}

public class DriveMountManager : IDisposable
{
    private readonly ConcurrentDictionary<Guid, ActiveMountSession> _mounts = new();
    private readonly CloudflaredTunnelManager _tunnelManager;

    public DriveMountManager(CloudflaredTunnelManager? tunnelManager = null)
    {
        _tunnelManager = tunnelManager ?? new CloudflaredTunnelManager();
    }

    public CloudflaredTunnelManager TunnelManager => _tunnelManager;

    public static bool IsWinFspInstalled()
    {
        try
        {
            using var sc = new ServiceController("WinFsp.Launcher");
            _ = sc.Status;
            return true;
        }
        catch
        {
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            return Directory.Exists(Path.Combine(pf86, "WinFsp", "bin")) ||
                   Directory.Exists(Path.Combine(pf, "WinFsp", "bin"));
        }
    }

    public static string? GetBundledWinFspMsiPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "winfsp.msi"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "lib", "winfsp", "winfsp.msi"),
            @"C:\dev\clouddrive-mount\lib\winfsp\winfsp.msi"
        ];

        foreach (var path in candidates)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                return full;
        }
        return null;
    }

    public static async Task<bool> InstallBundledWinFspAsync()
    {
        var msiPath = GetBundledWinFspMsiPath()
            ?? throw new FileNotFoundException("Bundled winfsp.msi was not found.");

        var psi = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            Arguments = $"/i \"{msiPath}\" /passive /norestart",
            UseShellExecute = true,
            Verb = "runas"
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            return false;

        await proc.WaitForExitAsync();
        return proc.ExitCode == 0 || IsWinFspInstalled();
    }

    public static ICloudStorageProvider CreateProvider(DriveConfig config)
    {
        return config.Protocol switch
        {
            StorageProtocol.Sftp => new SftpStorageProvider(config),
            StorageProtocol.Ftp => new FtpStorageProvider(config),
            StorageProtocol.WebDav => new WebDavStorageProvider(config),
            StorageProtocol.CloudflareR2 => new CloudflareR2StorageProvider(config),
            StorageProtocol.GoogleDrive => new GoogleDriveStorageProvider(config),
            _ => throw new NotSupportedException($"Unsupported protocol: {config.Protocol}")
        };
    }

    public async Task MountDriveAsync(DriveConfig config, CancellationToken cancellationToken = default)
    {
        if (_mounts.ContainsKey(config.Id))
            return;

        var conflicting = _mounts.Values.FirstOrDefault(
            m => m.Config.Id != config.Id && m.Config.NormalizedDriveLetterChar == config.NormalizedDriveLetterChar);
        if (conflicting != null)
        {
            config.State = DriveConnectionState.Error;
            config.StatusMessage = $"Drive letter {config.NormalizedMountPoint} is already in use by '{conflicting.Config.Label}'.";
            throw new InvalidOperationException(config.StatusMessage);
        }

        config.State = DriveConnectionState.Connecting;
        config.StatusMessage = "Connecting...";

        try
        {
            if (!IsWinFspInstalled())
            {
                throw new InvalidOperationException(
                    "WinFsp kernel driver is not installed. Click 'Install WinFsp Driver' in the top bar to install the bundled driver.");
            }

            // 1. Start native cloudflared access tcp tunnel if enabled
            if (config.UseProxy &&
                config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                config.Protocol is StorageProtocol.Sftp or StorageProtocol.Ftp or StorageProtocol.WebDav)
            {
                config.StatusMessage = "Starting cloudflared tunnel...";
                await _tunnelManager.StartTunnelAsync(config, cancellationToken);
            }
            else
            {
                config.ActiveProxyPort = null;
            }

            // 2. Connect to storage backend
            config.StatusMessage = $"Connecting to {config.ProtocolDisplayName}...";
            var provider = CreateProvider(config);
            try
            {
                await provider.ConnectAsync(cancellationToken);
            }
            catch
            {
                provider.Dispose();
                throw;
            }

            // 3. Configure WinFsp FileSystemHost & UNC prefix
            var fs = new CloudDriveFileSystem(config, provider);
            var host = new FileSystemHost(fs);
            string? uncPrefix = null;

            if (config.IsLocalDisk)
            {
                // Empty/null prefix causes WinFsp to mount as a Local Disk (\Device\WinFsp.Disk)
                host.Prefix = null;
                TryEnableWinFspMountManager();
            }
            else
            {
                // Non-empty UNC prefix causes WinFsp to mount as a Network Drive (\Device\WinFsp.Net).
                // Keep spaces and dots intact (and do NOT append _P / _X) so the share name matches the user's label.
                var shareName = SanitizeUncShareName(config.Label, config.ProtocolDisplayName);
                uncPrefix = $@"\CloudDriveMount\{shareName}";
                host.Prefix = uncPrefix;
            }

            // 4. Register drive icon & label in Windows Explorer prior to mount
            ExplorerIconManager.RegisterDriveIconAndLabel(config, uncPrefix);

            byte[]? mountSd = null;
            if (config.IsLocalDisk)
            {
                // Grant SYSTEM (SY) and Administrators (BA) full access so Windows LanmanServer (SMB Sharing)
                // and elevated processes can access the volume; when Public is selected, also grant Everyone (WD).
                var sddl = config.LocalDiskVisibility == LocalDiskScope.Public
                    ? "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)(A;;GA;;;WD)"
                    : "O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;AU)";
                var rawSd = new RawSecurityDescriptor(sddl);
                mountSd = new byte[rawSd.BinaryLength];
                rawSd.GetBinaryForm(mountSd, 0);
            }

            var mountPoint = config.NormalizedMountPoint;
            config.StatusMessage = $"Mounting {mountPoint}...";

            int status = host.Mount(mountPoint, mountSd, false, 0);
            if (status < 0)
            {
                fs.Dispose();
                provider.Dispose();
                throw new IOException($"WinFsp failed to mount {mountPoint} (NTSTATUS: 0x{status:X8}).");
            }

            IntPtr kernelMountMgrVolHandle = IntPtr.Zero;
            if (config.IsLocalDisk)
            {
                // Register the WinFsp disk device with the Windows Kernel MountPointManager (\GLOBAL??\X:)
                // via the WinFsp kernel driver so Session 0 (LanmanServer / Windows Explorer Advanced Sharing)
                // and elevated processes can resolve the drive letter without requiring CloudDriveMount to run as SYSTEM.
                kernelMountMgrVolHandle = PublishMountPointViaKernelMountMgr(host, mountPoint, config.Id);
            }

            // Re-apply MountPoints2 _LabelFromReg and drive icon after mount so Windows Explorer
            // shows "Plex Max (P:)" instead of "Plex Max (\\CloudDriveMount) (P:)".
            ExplorerIconManager.RegisterDriveIconAndLabel(config, uncPrefix);

            var session = new ActiveMountSession
            {
                Config = config,
                Provider = provider,
                FileSystem = fs,
                Host = host,
                KernelMountMgrVolumeHandle = kernelMountMgrVolHandle
            };

            _mounts[config.Id] = session;
            config.State = DriveConnectionState.Connected;
            config.StatusMessage = config.ActiveProxyPort.HasValue
                ? $"Mounted on {mountPoint} (cloudflared :{config.ActiveProxyPort.Value})"
                : $"Mounted on {mountPoint}";
        }
        catch (Exception ex)
        {
            _tunnelManager.StopTunnel(config.Id);
            config.ActiveProxyPort = null;
            config.State = DriveConnectionState.Error;
            config.StatusMessage = ex.Message;
            throw;
        }
    }

    public Task UnmountDriveAsync(DriveConfig config)
    {
        string? uncPrefix = null;
        if (!config.IsLocalDisk)
        {
            uncPrefix = $@"\CloudDriveMount\{SanitizeUncShareName(config.Label, config.ProtocolDisplayName)}";
        }

        if (_mounts.TryRemove(config.Id, out var session))
        {
            session.Dispose();
        }

        _tunnelManager.StopTunnel(config.Id);
        config.ActiveProxyPort = null;
        ExplorerIconManager.UnregisterDriveIconAndLabel(config.NormalizedDriveLetterChar, uncPrefix);

        config.State = DriveConnectionState.Disconnected;
        config.StatusMessage = "Disconnected";
        return Task.CompletedTask;
    }

    public void UnmountAll()
    {
        foreach (var kv in _mounts.ToArray())
        {
            if (_mounts.TryRemove(kv.Key, out var session))
            {
                string? uncPrefix = session.Config.IsLocalDisk
                    ? null
                    : $@"\CloudDriveMount\{SanitizeUncShareName(session.Config.Label, session.Config.ProtocolDisplayName)}";
                session.Dispose();
                ExplorerIconManager.UnregisterDriveIconAndLabel(session.Config.NormalizedDriveLetterChar, uncPrefix);
                session.Config.State = DriveConnectionState.Disconnected;
                session.Config.StatusMessage = "Disconnected";
            }
        }
        _tunnelManager.Dispose();
    }

    private static string SanitizeUncShareName(string? label, string fallback)
    {
        var raw = string.IsNullOrWhiteSpace(label) ? fallback : label.Trim();
        var sb = new StringBuilder();
        foreach (var ch in raw)
        {
            // Allow spaces, dots, hyphens, underscores, parentheses, and letters/digits
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '.' or '-' or '_' or '(' or ')')
                sb.Append(ch);
        }
        var cleaned = sb.ToString().Trim();
        return cleaned.Length > 0 ? cleaned : fallback;
    }

    private static void TryEnableWinFspMountManager()
    {
        try
        {
            using var key64 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WinFsp", writable: true);
            key64?.SetValue("MountUseMountmgrFromFSD", 1, RegistryValueKind.DWord);
            key64?.SetValue("MountBroadcastDriveChange", 1, RegistryValueKind.DWord);
        }
        catch { }

        try
        {
            using var key32 = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\WinFsp", writable: true);
            key32?.SetValue("MountUseMountmgrFromFSD", 1, RegistryValueKind.DWord);
            key32?.SetValue("MountBroadcastDriveChange", 1, RegistryValueKind.DWord);
        }
        catch { }
    }

    /// <summary>
    /// Instructs the WinFsp kernel driver (winfsp-x64.sys) to register the volume as a MountDev
    /// and publish its drive letter in the Windows Kernel MountPointManager (\GLOBAL??\X:).
    /// This allows Session 0 services (such as LanmanServer for Windows Explorer Advanced Sharing)
    /// and elevated processes to resolve the drive letter even when CloudDriveMount runs as a normal user.
    /// </summary>
    internal static IntPtr PublishMountPointViaKernelMountMgr(FileSystemHost host, string mountPoint, Guid volumeId)
    {
        try
        {
            var fsPtrField = typeof(FileSystemHost).GetField(
                "_FileSystemPtr",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (fsPtrField?.GetValue(host) is not IntPtr fsPtr || fsPtr == IntPtr.Zero)
                return IntPtr.Zero;

            // In 64-bit FSP_FILE_SYSTEM:
            //   0..1:   UINT16 Version
            //   8..15:  PVOID UserContext
            //   16..527: WCHAR VolumeName[256]
            //   528..535: HANDLE VolumeHandle
            var volHandle = Marshal.ReadIntPtr(fsPtr + 528);
            if (volHandle == IntPtr.Zero)
                return IntPtr.Zero;

            var uniqueId = volumeId == Guid.Empty ? Guid.NewGuid() : volumeId;
            int st1 = FspFsctlMakeMountdev(volHandle, false, ref uniqueId);
            if (st1 >= 0)
            {
                int st2 = FspFsctlUseMountmgr(volHandle, mountPoint);
                if (st2 >= 0)
                    return volHandle;
            }
        }
        catch
        {
            // Fall back gracefully if native call fails
        }
        return IntPtr.Zero;
    }

    internal static void UnpublishMountPointViaKernelMountMgr(IntPtr volumeHandle)
    {
        if (volumeHandle == IntPtr.Zero)
            return;

        try
        {
            FspFsctlUseMountmgr(volumeHandle, null);
        }
        catch { }
    }

    [DllImport("winfsp-x64.dll", ExactSpelling = true)]
    private static extern int FspFsctlMakeMountdev(IntPtr VolumeHandle, [MarshalAs(UnmanagedType.U1)] bool Persistent, ref Guid UniqueId);

    [DllImport("winfsp-x64.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int FspFsctlUseMountmgr(IntPtr VolumeHandle, string? MountPoint);

    public void Dispose()
    {
        UnmountAll();
    }
}
