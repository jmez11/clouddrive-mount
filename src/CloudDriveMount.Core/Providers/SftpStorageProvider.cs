using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using Renci.SshNet;

namespace CloudDriveMount.Core.Providers;

public sealed class SftpStorageProvider : ICloudStorageProvider
{
    private readonly DriveConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SftpClient? _client;

    public SftpStorageProvider(DriveConfig config)
    {
        _config = config;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureConnectedLockedAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SftpClient> EnsureConnectedLockedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
            return _client;

        _client?.Dispose();

        // When cloudflared access tcp tunnel is active, connect to 127.0.0.1:<ActiveProxyPort>
        var host = _config.UseProxy &&
                   _config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                   _config.ActiveProxyPort.HasValue
            ? "127.0.0.1"
            : _config.Host.Trim();

        var port = _config.UseProxy &&
                   _config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                   _config.ActiveProxyPort.HasValue
            ? _config.ActiveProxyPort.Value
            : (_config.Port > 0 ? _config.Port : 22);

        var username = _config.Anonymous ? "anonymous" : _config.Username.Trim();
        var password = CredentialProtector.Unprotect(_config.EncryptedPassword);
        var methods = new List<AuthenticationMethod>();

        if (!string.IsNullOrWhiteSpace(_config.PrivateKeyPath) && File.Exists(_config.PrivateKeyPath))
        {
            var passphrase = CredentialProtector.Unprotect(_config.EncryptedPrivateKeyPassphrase);
            var keyFile = string.IsNullOrEmpty(passphrase)
                ? new PrivateKeyFile(_config.PrivateKeyPath)
                : new PrivateKeyFile(_config.PrivateKeyPath, passphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(username, keyFile));
        }

        if (!string.IsNullOrEmpty(password) || methods.Count == 0)
        {
            methods.Add(new PasswordAuthenticationMethod(username, password));
        }

        var connInfo = new ConnectionInfo(host, port, username, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 10, 600))
        };

        _client = new SftpClient(connInfo);
        await _client.ConnectAsync(cancellationToken);
        return _client;
    }

    public Task<CloudVolumeQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(CloudVolumeQuota.DefaultUnbounded);
    }

    public async Task<CloudItemInfo?> GetItemAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        if (norm == "/")
        {
            return new CloudItemInfo("", "/", true, 0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);
        }

        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, norm);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            if (!client.Exists(remotePath))
                return null;

            var attrs = client.GetAttributes(remotePath);
            var modTime = attrs.LastWriteTimeUtc == default ? DateTime.UtcNow : attrs.LastWriteTimeUtc;
            var accTime = attrs.LastAccessTimeUtc == default ? modTime : attrs.LastAccessTimeUtc;
            return new CloudItemInfo(
                VirtualPathHelper.GetFileName(norm),
                norm,
                attrs.IsDirectory,
                attrs.IsDirectory ? 0UL : (ulong)Math.Max(0, attrs.Size),
                modTime,
                modTime,
                accTime);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var normDir = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);
        var remoteDir = VirtualPathHelper.CombineRemotePath(_config.RemotePath, normDir);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            var entries = await Task.Run(() => client.ListDirectory(remoteDir).ToList(), cancellationToken);
            var results = new List<CloudItemInfo>();

            foreach (var item in entries)
            {
                if (item.Name is "." or "..")
                    continue;

                var childVirtual = normDir == "/" ? $"/{item.Name}" : $"{normDir}/{item.Name}";
                var modTime = item.LastWriteTimeUtc == default ? DateTime.UtcNow : item.LastWriteTimeUtc;
                var accTime = item.LastAccessTimeUtc == default ? modTime : item.LastAccessTimeUtc;
                results.Add(new CloudItemInfo(
                    item.Name,
                    childVirtual,
                    item.IsDirectory,
                    item.IsDirectory ? 0UL : (ulong)Math.Max(0, item.Length),
                    modTime,
                    modTime,
                    accTime));
            }

            return results;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default)
    {
        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            await Task.Run(() => client.DownloadFile(remotePath, destination), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default)
    {
        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            source.Position = 0;
            await Task.Run(() => client.UploadFile(source, remotePath, canOverride: true), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var remoteDir = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualDirectoryPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            if (!client.Exists(remoteDir))
                client.CreateDirectory(remoteDir);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            if (client.Exists(remotePath))
                client.DeleteFile(remotePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var remoteDir = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualDirectoryPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            if (client.Exists(remoteDir))
                client.DeleteDirectory(remoteDir);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var oldRemote = VirtualPathHelper.CombineRemotePath(_config.RemotePath, oldVirtualPath);
        var newRemote = VirtualPathHelper.CombineRemotePath(_config.RemotePath, newVirtualPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            if (overwrite && client.Exists(newRemote))
                client.DeleteFile(newRemote);
            client.RenameFile(oldRemote, newRemote);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_client is { IsConnected: true })
                _client.Disconnect();
        }
        catch { }
        _client?.Dispose();
        _gate.Dispose();
    }
}
