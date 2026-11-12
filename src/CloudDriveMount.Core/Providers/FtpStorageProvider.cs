using System.Security.Authentication;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using FluentFTP;

namespace CloudDriveMount.Core.Providers;

public sealed class FtpStorageProvider : ICloudStorageProvider
{
    private readonly DriveConfig _config;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AsyncFtpClient? _client;

    public FtpStorageProvider(DriveConfig config)
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

    private async Task<AsyncFtpClient> EnsureConnectedLockedAsync(CancellationToken cancellationToken)
    {
        if (_client is { IsConnected: true })
            return _client;

        if (_client != null)
            await _client.DisposeAsync();

        var host = _config.UseProxy &&
                   _config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                   _config.ActiveProxyPort.HasValue
            ? "127.0.0.1"
            : _config.Host.Trim();

        var port = _config.UseProxy &&
                   _config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                   _config.ActiveProxyPort.HasValue
            ? _config.ActiveProxyPort.Value
            : (_config.Port > 0 ? _config.Port : 21);

        var user = _config.Anonymous ? "anonymous" : _config.Username.Trim();
        var pass = _config.Anonymous ? "anonymous@example.com" : CredentialProtector.Unprotect(_config.EncryptedPassword);

        var ftpConfig = new FtpConfig
        {
            EncryptionMode = _config.UseTls ? FtpEncryptionMode.Auto : FtpEncryptionMode.None,
            SslProtocols = _config.TlsVersion == TlsProtocolVersion.Tls13
                ? SslProtocols.Tls13 | SslProtocols.Tls12
                : SslProtocols.Tls12,
            ValidateAnyCertificate = true,
            ConnectTimeout = Math.Clamp(_config.TimeoutSeconds, 10, 600) * 1000,
            ReadTimeout = Math.Clamp(_config.TimeoutSeconds, 10, 600) * 1000,
            DataConnectionType = FtpDataConnectionType.AutoPassive
        };

        _client = new AsyncFtpClient(host, user, pass, port, ftpConfig);
        await _client.AutoConnect(cancellationToken);
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
            return new CloudItemInfo("", "/", true, 0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, norm);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            var info = await client.GetObjectInfo(remotePath, dateModified: true, token: cancellationToken);
            if (info == null)
                return null;

            var mod = info.Modified == default ? DateTime.UtcNow : info.Modified.ToUniversalTime();
            var cr = info.Created == default ? mod : info.Created.ToUniversalTime();
            var isDir = info.Type == FtpObjectType.Directory;
            return new CloudItemInfo(
                info.Name,
                norm,
                isDir,
                isDir ? 0UL : (ulong)Math.Max(0, info.Size),
                cr,
                mod,
                mod);
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
            var items = await client.GetListing(remoteDir, FtpListOption.Modify, cancellationToken);
            var list = new List<CloudItemInfo>();

            foreach (var item in items)
            {
                if (string.IsNullOrEmpty(item.Name) || item.Name is "." or "..")
                    continue;

                var childVirtual = normDir == "/" ? $"/{item.Name}" : $"{normDir}/{item.Name}";
                var isDir = item.Type == FtpObjectType.Directory;
                var mod = item.Modified == default ? DateTime.UtcNow : item.Modified.ToUniversalTime();
                var cr = item.Created == default ? mod : item.Created.ToUniversalTime();

                list.Add(new CloudItemInfo(
                    item.Name,
                    childVirtual,
                    isDir,
                    isDir ? 0UL : (ulong)Math.Max(0, item.Size),
                    cr,
                    mod,
                    mod));
            }

            return list;
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
            await client.DownloadStream(destination, remotePath, token: cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> ReadFileRangeAsync(
        string virtualPath,
        byte[] buffer,
        int bufferOffset,
        long fileOffset,
        int count,
        CancellationToken cancellationToken = default)
    {
        var remotePath = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualPath);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var client = await EnsureConnectedLockedAsync(cancellationToken);
            using var stream = await client.OpenRead(remotePath, FtpDataType.Binary, fileOffset, token: cancellationToken);
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(bufferOffset + totalRead, count - totalRead), cancellationToken);
                if (read == 0) break;
                totalRead += read;
            }
            return totalRead;
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
            await client.UploadStream(source, remotePath, FtpRemoteExists.Overwrite, createRemoteDir: true, token: cancellationToken);
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
            await client.CreateDirectory(remoteDir, force: true, cancellationToken);
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
            if (await client.FileExists(remotePath, cancellationToken))
                await client.DeleteFile(remotePath, cancellationToken);
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
            if (await client.DirectoryExists(remoteDir, cancellationToken))
                await client.DeleteDirectory(remoteDir, cancellationToken);
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
            await client.MoveFile(
                oldRemote,
                newRemote,
                overwrite ? FtpRemoteExists.Overwrite : FtpRemoteExists.Skip,
                cancellationToken);
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
            _client?.Dispose();
        }
        catch { }
        _gate.Dispose();
    }
}
