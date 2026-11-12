namespace CloudDriveMount.Core.Providers;

public sealed record CloudItemInfo(
    string Name,
    string FullPath,
    bool IsDirectory,
    ulong Size,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    DateTime LastAccessTimeUtc,
    string? NativeId = null);

public sealed record CloudVolumeQuota(
    ulong TotalBytes,
    ulong FreeBytes)
{
    // 7.99 EB default quota for unbounded storage backends
    public static readonly CloudVolumeQuota DefaultUnbounded = new(
        TotalBytes: 8_589_934_592UL * 1024UL * 1024UL * 1023UL,
        FreeBytes: 8_589_934_592UL * 1024UL * 1024UL * 1023UL);
}

public interface ICloudStorageProvider : IDisposable
{
    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task<CloudVolumeQuota> GetQuotaAsync(CancellationToken cancellationToken = default);
    Task<CloudItemInfo?> GetItemAsync(string virtualPath, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default);
    Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default);
    Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default);
    Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default);
    Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default);
    Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default);
    Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default);

    Task<int> ReadFileRangeAsync(
        string virtualPath,
        byte[] buffer,
        int bufferOffset,
        long fileOffset,
        int count,
        CancellationToken cancellationToken = default)
    {
        using var ms = new MemoryStream();
        DownloadFileAsync(virtualPath, ms, cancellationToken).GetAwaiter().GetResult();
        ms.Position = Math.Min((long)ms.Length, fileOffset);
        return Task.FromResult(ms.Read(buffer, bufferOffset, count));
    }

    Task<string?> LockFileAsync(string virtualPath, int timeoutSeconds = 3600, CancellationToken cancellationToken = default)
        => Task.FromResult<string?>(null);

    Task UnlockFileAsync(string virtualPath, string lockToken, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

public static class VirtualPathHelper
{
    public static string NormalizeVirtualPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.Contains("//"))
            normalized = normalized.Replace("//", "/");

        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        if (normalized.Length > 1 && normalized.EndsWith('/'))
            normalized = normalized.TrimEnd('/');

        return normalized;
    }

    public static string CombineRemotePath(string baseRemotePath, string virtualPath)
    {
        var baseNorm = NormalizeVirtualPath(baseRemotePath);
        var relNorm = NormalizeVirtualPath(virtualPath);
        if (baseNorm == "/")
            return relNorm;
        if (relNorm == "/")
            return baseNorm;
        return baseNorm + relNorm;
    }

    public static string GetParentPath(string virtualPath)
    {
        var norm = NormalizeVirtualPath(virtualPath);
        if (norm == "/")
            return "/";
        var idx = norm.LastIndexOf('/');
        return idx <= 0 ? "/" : norm[..idx];
    }

    public static string GetFileName(string virtualPath)
    {
        var norm = NormalizeVirtualPath(virtualPath);
        if (norm == "/")
            return string.Empty;
        var idx = norm.LastIndexOf('/');
        return idx < 0 ? norm : norm[(idx + 1)..];
    }
}
