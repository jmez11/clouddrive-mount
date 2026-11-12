using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Providers;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace CloudDriveMount.Core.FileSystem;

public sealed class CloudDriveFileSystem : FileSystemBase, IDisposable
{
    private const ushort AllocationUnit = 4096;

    // Default 2 GB retained disk cache cap per mounted drive (LRU evicted)
    private const long DefaultMaxDiskCacheBytes = 2L * 1024 * 1024 * 1024;

    // SDDL granting Full Access to SYSTEM, Administrators, Authenticated Users, and Everyone
    private const string PublicSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;AU)(A;;FA;;;WD)";
    private const string PrivateSddl = "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;AU)";

    private readonly DriveConfig _config;
    private readonly ICloudStorageProvider _provider;
    private readonly byte[] _defaultSecurityDescriptor;
    private readonly string _cacheDir;
    private readonly string _stagingDir;
    private readonly string _retainedCacheDir;
    private readonly long _maxDiskCacheBytes;
    private readonly object _lruGate = new();

    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, IReadOnlyList<CloudItemInfo> Items)> _dirCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, CloudItemInfo? Item)> _itemCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _customAclCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _activeWriteLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RetainedCacheEntry> _retainedCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long TickCount, byte[] Data)> _chunkCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _chunkFetchLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private const int ChunkSize = 1024 * 1024; // 1 MB streaming chunks
    private const int MaxCachedChunks = 64;    // 64 MB maximum in-memory cache

    private CloudVolumeQuota _cachedQuota = CloudVolumeQuota.DefaultUnbounded;
    private DateTime _quotaFetchedUtc = DateTime.MinValue;
    private string _volumeLabel;

    private sealed class RetainedCacheEntry
    {
        public required string VirtualPath { get; init; }
        public required string LocalCacheFilePath { get; init; }
        public required ulong FileSize { get; set; }
        public required ulong LastWriteTimeFileTime { get; set; }
        public long LastAccessTickCount { get; set; }
    }

    private sealed class OpenHandleContext : IDisposable
    {
        public string VirtualPath { get; set; } = "/";
        public bool IsDirectory { get; init; }
        public FileStream? StagingStream { get; set; }
        public string? StagingFilePath { get; set; }
        public bool IsMaterialized { get; set; }
        public bool IsDirty { get; set; }
        public bool DeleteOnCleanup { get; set; }
        public bool HoldsWriteLock { get; set; }
        public string? RemoteLockToken { get; set; }
        public ulong KnownFileSize { get; set; }
        public ulong CreationTimeFileTime { get; set; }
        public ulong LastAccessTimeFileTime { get; set; }
        public ulong LastWriteTimeFileTime { get; set; }
        public uint FileAttributes { get; set; }
        public DictionaryEntry[]? DirectoryEntries { get; set; }

        public void FillFileInfo(out FileInfo info)
        {
            info = default;
            info.FileAttributes = FileAttributes != 0
                ? FileAttributes
                : (IsDirectory ? (uint)System.IO.FileAttributes.Directory : (uint)System.IO.FileAttributes.Archive);
            info.ReparseTag = 0;
            info.FileSize = IsDirectory
                ? 0UL
                : (IsMaterialized && StagingStream != null ? (ulong)Math.Max(0, StagingStream.Length) : KnownFileSize);
            info.AllocationSize = (info.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
            info.CreationTime = CreationTimeFileTime;
            info.LastAccessTime = LastAccessTimeFileTime;
            info.LastWriteTime = LastWriteTimeFileTime;
            info.ChangeTime = LastWriteTimeFileTime;
            info.IndexNumber = 0;
            info.HardLinks = 0;
        }

        public void Dispose()
        {
            try
            {
                StagingStream?.Dispose();
                StagingStream = null;
            }
            catch { }

            if (!string.IsNullOrEmpty(StagingFilePath))
            {
                try
                {
                    if (File.Exists(StagingFilePath))
                        File.Delete(StagingFilePath);
                }
                catch { }
            }
        }
    }

    public CloudDriveFileSystem(
        DriveConfig config,
        ICloudStorageProvider provider,
        long maxDiskCacheBytes = DefaultMaxDiskCacheBytes)
    {
        _config = config;
        _provider = provider;
        _maxDiskCacheBytes = maxDiskCacheBytes;
        _volumeLabel = string.IsNullOrWhiteSpace(config.Label) ? config.ProtocolDisplayName : config.Label.Trim();

        var sddl = config.IsLocalDisk && config.LocalDiskVisibility == LocalDiskScope.Public
            ? PublicSddl
            : PrivateSddl;
        var rawSd = new RawSecurityDescriptor(sddl);
        _defaultSecurityDescriptor = new byte[rawSd.BinaryLength];
        rawSd.GetBinaryForm(_defaultSecurityDescriptor, 0);

        _cacheDir = Path.Combine(Path.GetTempPath(), "CloudDriveMountCache", config.Id.ToString("N"));
        _stagingDir = Path.Combine(_cacheDir, "staging");
        _retainedCacheDir = Path.Combine(_cacheDir, "retained");
        Directory.CreateDirectory(_stagingDir);
        Directory.CreateDirectory(_retainedCacheDir);
    }

    private static string ToVirtualPath(string winFspFileName)
    {
        return VirtualPathHelper.NormalizeVirtualPath(winFspFileName);
    }

    private static void FillFileInfoFromCloudItem(CloudItemInfo item, bool readOnlyVolume, out FileInfo info)
    {
        info = default;
        var attrs = item.IsDirectory
            ? System.IO.FileAttributes.Directory
            : System.IO.FileAttributes.Archive;
        if (readOnlyVolume)
            attrs |= System.IO.FileAttributes.ReadOnly;

        info.FileAttributes = (uint)attrs;
        info.ReparseTag = 0;
        info.FileSize = item.IsDirectory ? 0UL : item.Size;
        info.AllocationSize = (info.FileSize + AllocationUnit - 1) / AllocationUnit * AllocationUnit;
        info.CreationTime = (ulong)item.CreationTimeUtc.ToFileTimeUtc();
        info.LastAccessTime = (ulong)item.LastAccessTimeUtc.ToFileTimeUtc();
        info.LastWriteTime = (ulong)item.LastWriteTimeUtc.ToFileTimeUtc();
        info.ChangeTime = info.LastWriteTime;
        info.IndexNumber = 0;
        info.HardLinks = 0;
    }

    /// <summary>
    /// Lazily materializes the local staging FileStream only when Read/Write/SetFileSize is actually invoked.
    /// Uses the retained LRU disk cache if the file's size and LastWriteTime match, avoiding network downloads.
    /// </summary>
    private void EnsureStagingStreamMaterialized(OpenHandleContext ctx, bool populateExistingContent)
    {
        if (ctx.IsMaterialized && ctx.StagingStream != null)
            return;

        var tempPath = Path.Combine(_stagingDir, Guid.NewGuid().ToString("N") + ".tmp");

        if (!populateExistingContent || ctx.KnownFileSize == 0)
        {
            ctx.StagingFilePath = tempPath;
            ctx.StagingStream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            ctx.IsMaterialized = true;
            return;
        }

        // 1. Check if a valid retained disk cache file exists with matching Size & LastWriteTime
        if (_retainedCache.TryGetValue(ctx.VirtualPath, out var cacheEntry) &&
            cacheEntry.FileSize == ctx.KnownFileSize &&
            cacheEntry.LastWriteTimeFileTime == ctx.LastWriteTimeFileTime &&
            File.Exists(cacheEntry.LocalCacheFilePath))
        {
            try
            {
                File.Copy(cacheEntry.LocalCacheFilePath, tempPath, overwrite: true);
                cacheEntry.LastAccessTickCount = Environment.TickCount64;
                ctx.StagingFilePath = tempPath;
                ctx.StagingStream = new FileStream(tempPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                ctx.IsMaterialized = true;
                return;
            }
            catch
            {
                // If copy failed, fall through to fresh download
            }
        }

        // 2. Cache miss -> download from remote provider into staging stream and store in retained LRU cache
        var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        try
        {
            _provider.DownloadFileAsync(ctx.VirtualPath, stream).GetAwaiter().GetResult();
            stream.Flush();
            stream.Position = 0;
            ctx.KnownFileSize = (ulong)stream.Length;
            ctx.StagingFilePath = tempPath;
            ctx.StagingStream = stream;
            ctx.IsMaterialized = true;

            StoreInRetainedDiskCache(ctx.VirtualPath, tempPath, ctx.KnownFileSize, ctx.LastWriteTimeFileTime);
        }
        catch
        {
            stream.Dispose();
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private void StoreInRetainedDiskCache(string virtualPath, string sourceFilePath, ulong fileSize, ulong lastWriteTimeFt)
    {
        if ((long)fileSize > _maxDiskCacheBytes / 2)
            return; // Skip caching single files larger than half the entire cache budget

        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(virtualPath.ToLowerInvariant())));
            var cacheFilePath = Path.Combine(_retainedCacheDir, hash + ".cache");

            lock (_lruGate)
            {
                File.Copy(sourceFilePath, cacheFilePath, overwrite: true);
                _retainedCache[virtualPath] = new RetainedCacheEntry
                {
                    VirtualPath = virtualPath,
                    LocalCacheFilePath = cacheFilePath,
                    FileSize = fileSize,
                    LastWriteTimeFileTime = lastWriteTimeFt,
                    LastAccessTickCount = Environment.TickCount64
                };

                EvictLruIfNeededLocked();
            }
        }
        catch
        {
            // Non-fatal if disk cache write fails
        }
    }

    private void RemoveFromRetainedDiskCache(string virtualPath)
    {
        lock (_lruGate)
        {
            if (_retainedCache.TryRemove(virtualPath, out var entry))
            {
                try
                {
                    if (File.Exists(entry.LocalCacheFilePath))
                        File.Delete(entry.LocalCacheFilePath);
                }
                catch { }
            }
        }
    }

    private void EvictLruIfNeededLocked()
    {
        long totalBytes = _retainedCache.Values.Sum(e => (long)e.FileSize);
        if (totalBytes <= _maxDiskCacheBytes)
            return;

        var orderedOldestFirst = _retainedCache.Values
            .OrderBy(e => e.LastAccessTickCount)
            .ToList();

        foreach (var victim in orderedOldestFirst)
        {
            if (totalBytes <= _maxDiskCacheBytes)
                break;

            if (_retainedCache.TryRemove(victim.VirtualPath, out _))
            {
                totalBytes -= (long)victim.FileSize;
                try
                {
                    if (File.Exists(victim.LocalCacheFilePath))
                        File.Delete(victim.LocalCacheFilePath);
                }
                catch { }
            }
        }
    }

    private CloudItemInfo? GetCachedOrFetchItem(string virtualPath)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        if (norm == "/")
        {
            var now = DateTime.UtcNow;
            return new CloudItemInfo("", "/", true, 0, now, now, now);
        }

        if (_itemCache.TryGetValue(norm, out var cached) &&
            (DateTime.UtcNow - cached.FetchedUtc) < TimeSpan.FromSeconds(5))
        {
            return cached.Item;
        }

        var parent = VirtualPathHelper.GetParentPath(norm);
        if (_dirCache.TryGetValue(parent, out var dirCached) &&
            (DateTime.UtcNow - dirCached.FetchedUtc) < TimeSpan.FromSeconds(5))
        {
            var fileName = VirtualPathHelper.GetFileName(norm);
            var match = dirCached.Items.FirstOrDefault(i => string.Equals(i.Name, fileName, StringComparison.OrdinalIgnoreCase));
            _itemCache[norm] = (DateTime.UtcNow, match);
            return match;
        }

        var fetched = _provider.GetItemAsync(norm).GetAwaiter().GetResult();
        _itemCache[norm] = (DateTime.UtcNow, fetched);
        return fetched;
    }

    private IReadOnlyList<CloudItemInfo> GetCachedOrFetchDirectory(string virtualDirPath)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualDirPath);
        if (_dirCache.TryGetValue(norm, out var cached) &&
            (DateTime.UtcNow - cached.FetchedUtc) < TimeSpan.FromSeconds(5))
        {
            return cached.Items;
        }

        var items = _provider.ListDirectoryAsync(norm).GetAwaiter().GetResult();
        var now = DateTime.UtcNow;
        _dirCache[norm] = (now, items);
        foreach (var item in items)
        {
            _itemCache[item.FullPath] = (now, item);
        }
        return items;
    }

    private void InvalidatePathAndParent(string virtualPath)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        _itemCache.TryRemove(norm, out _);
        _dirCache.TryRemove(norm, out _);
        var parent = VirtualPathHelper.GetParentPath(norm);
        _dirCache.TryRemove(parent, out _);
        InvalidateChunkCache(norm);
    }

    private byte[] GetOrCreateChunk(string virtualPath, long chunkIndex, ulong fileSize)
    {
        string key = $"{virtualPath}:{chunkIndex}";
        if (_chunkCache.TryGetValue(key, out var cached))
        {
            _chunkCache[key] = (Environment.TickCount64, cached.Data);
            return cached.Data;
        }

        var sem = _chunkFetchLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        sem.Wait();
        try
        {
            if (_chunkCache.TryGetValue(key, out cached))
            {
                _chunkCache[key] = (Environment.TickCount64, cached.Data);
                return cached.Data;
            }

            long chunkStart = chunkIndex * ChunkSize;
            if (chunkStart >= (long)fileSize)
                return Array.Empty<byte>();

            int toFetch = (int)Math.Min(ChunkSize, (long)fileSize - chunkStart);
            byte[] chunkData = new byte[toFetch];

            int read = _provider.ReadFileRangeAsync(virtualPath, chunkData, 0, chunkStart, toFetch).GetAwaiter().GetResult();
            if (read < toFetch)
            {
                Array.Resize(ref chunkData, Math.Max(0, read));
            }

            _chunkCache[key] = (Environment.TickCount64, chunkData);
            TrimChunkCacheIfNeeded();
            return chunkData;
        }
        finally
        {
            sem.Release();
        }
    }

    private int ReadViaChunkCache(string virtualPath, long offset, byte[] destination, int destinationOffset, int count, ulong fileSize)
    {
        int totalRead = 0;
        while (totalRead < count)
        {
            long currentOffset = offset + totalRead;
            if (currentOffset >= (long)fileSize)
                break;

            long chunkIndex = currentOffset / ChunkSize;
            int chunkOffset = (int)(currentOffset % ChunkSize);
            byte[] chunk = GetOrCreateChunk(virtualPath, chunkIndex, fileSize);
            if (chunk.Length <= chunkOffset)
                break;

            int availableInChunk = chunk.Length - chunkOffset;
            int bytesToCopy = Math.Min(count - totalRead, availableInChunk);
            if (bytesToCopy <= 0)
                break;

            Buffer.BlockCopy(chunk, chunkOffset, destination, destinationOffset + totalRead, bytesToCopy);
            totalRead += bytesToCopy;
        }
        return totalRead;
    }

    private void TrimChunkCacheIfNeeded()
    {
        if (_chunkCache.Count <= MaxCachedChunks)
            return;

        var toEvict = _chunkCache
            .OrderBy(kvp => kvp.Value.TickCount)
            .Take(_chunkCache.Count - MaxCachedChunks + 8)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var k in toEvict)
        {
            _chunkCache.TryRemove(k, out _);
            if (_chunkFetchLocks.TryRemove(k, out var sem))
            {
                sem.Dispose();
            }
        }
    }

    private void InvalidateChunkCache(string virtualPath)
    {
        var prefix = virtualPath + ":";
        var keys = _chunkCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var key in keys)
        {
            _chunkCache.TryRemove(key, out _);
            if (_chunkFetchLocks.TryRemove(key, out var sem))
            {
                sem.Dispose();
            }
        }
    }

    public override int ExceptionHandler(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
            return STATUS_ACCESS_DENIED;
        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        int hresult = ex.HResult;
        if (unchecked((int)0x80070000) == (hresult & unchecked((int)0xFFFF0000)))
            return NtStatusFromWin32((uint)hresult & 0xFFFF);

        return STATUS_UNEXPECTED_IO_ERROR;
    }

    public override int Init(object Host0)
    {
        var host = (FileSystemHost)Host0;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = true;
        host.PostCleanupWhenModifiedOnly = true;
        host.PassQueryDirectoryPattern = true;
        host.FlushAndPurgeOnCleanup = true;
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = (uint)(_config.Id.GetHashCode() & 0x7FFFFFFF);
        // Report NTFS so Windows Explorer enables the native Sharing tab on Local Disk + Public mounts
        host.FileSystemName = "NTFS";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo VolumeInfo)
    {
        VolumeInfo = default;
        if ((DateTime.UtcNow - _quotaFetchedUtc) > TimeSpan.FromSeconds(30))
        {
            try
            {
                _cachedQuota = _provider.GetQuotaAsync().GetAwaiter().GetResult();
                _quotaFetchedUtc = DateTime.UtcNow;
            }
            catch
            {
                _cachedQuota = CloudVolumeQuota.DefaultUnbounded;
            }
        }

        VolumeInfo.TotalSize = _cachedQuota.TotalBytes;
        VolumeInfo.FreeSize = _config.ReadOnly ? 0UL : _cachedQuota.FreeBytes;
        VolumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    public override int SetVolumeLabel(string VolumeLabel, out VolumeInfo VolumeInfo)
    {
        if (_config.ReadOnly)
        {
            GetVolumeInfo(out VolumeInfo);
            return STATUS_MEDIA_WRITE_PROTECTED;
        }

        _volumeLabel = VolumeLabel;
        _config.Label = VolumeLabel;
        return GetVolumeInfo(out VolumeInfo);
    }

    public override int GetSecurityByName(
        string FileName,
        out uint FileAttributes,
        ref byte[] SecurityDescriptor)
    {
        var vpath = ToVirtualPath(FileName);
        var item = GetCachedOrFetchItem(vpath);
        if (item == null)
        {
            FileAttributes = 0;
            var parentPath = VirtualPathHelper.GetParentPath(vpath);
            if (parentPath != "/" && GetCachedOrFetchItem(parentPath) == null)
                return STATUS_OBJECT_PATH_NOT_FOUND;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }

        FillFileInfoFromCloudItem(item, _config.ReadOnly, out var fi);
        FileAttributes = fi.FileAttributes;

        if (SecurityDescriptor != null)
        {
            SecurityDescriptor = _customAclCache.TryGetValue(vpath, out var customSd)
                ? customSd
                : _defaultSecurityDescriptor;
        }

        return STATUS_SUCCESS;
    }

    public override int Create(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        uint FileAttributes,
        byte[] SecurityDescriptor,
        ulong AllocationSize,
        out object FileNode,
        out object FileDesc,
        out FileInfo FileInfo,
        out string NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;

        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var vpath = ToVirtualPath(FileName);
        if (GetCachedOrFetchItem(vpath) != null)
            return STATUS_OBJECT_NAME_COLLISION;

        bool isDirectory = 0 != (CreateOptions & FILE_DIRECTORY_FILE);
        var nowUtc = DateTime.UtcNow;
        var nowFt = (ulong)nowUtc.ToFileTimeUtc();

        if (SecurityDescriptor != null && SecurityDescriptor.Length > 0)
            _customAclCache[vpath] = SecurityDescriptor;

        if (isDirectory)
        {
            _provider.CreateDirectoryAsync(vpath).GetAwaiter().GetResult();
            InvalidatePathAndParent(vpath);
            var newDirItem = new CloudItemInfo(VirtualPathHelper.GetFileName(vpath), vpath, true, 0, nowUtc, nowUtc, nowUtc);
            _itemCache[vpath] = (nowUtc, newDirItem);

            var ctx = new OpenHandleContext
            {
                VirtualPath = vpath,
                IsDirectory = true,
                CreationTimeFileTime = nowFt,
                LastAccessTimeFileTime = nowFt,
                LastWriteTimeFileTime = nowFt,
                FileAttributes = (uint)System.IO.FileAttributes.Directory
            };
            FileDesc = ctx;
            ctx.FillFileInfo(out FileInfo);
            return STATUS_SUCCESS;
        }
        else
        {
            var tempPath = Path.Combine(_stagingDir, Guid.NewGuid().ToString("N") + ".tmp");
            var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
            // Upload initial empty placeholder and cache item so immediate GetSecurityByName succeeds
            _provider.UploadFileAsync(vpath, stream).GetAwaiter().GetResult();
            InvalidatePathAndParent(vpath);
            var newFileItem = new CloudItemInfo(VirtualPathHelper.GetFileName(vpath), vpath, false, 0, nowUtc, nowUtc, nowUtc);
            _itemCache[vpath] = (nowUtc, newFileItem);
            StoreInRetainedDiskCache(vpath, tempPath, 0, nowFt);

            var ctx = new OpenHandleContext
            {
                VirtualPath = vpath,
                IsDirectory = false,
                StagingFilePath = tempPath,
                StagingStream = stream,
                IsMaterialized = true,
                KnownFileSize = 0,
                IsDirty = false,
                CreationTimeFileTime = nowFt,
                LastAccessTimeFileTime = nowFt,
                LastWriteTimeFileTime = nowFt,
                FileAttributes = FileAttributes != 0 ? FileAttributes : (uint)System.IO.FileAttributes.Archive
            };

            if (_config.FileLock)
            {
                _activeWriteLocks[vpath] = 1;
                ctx.HoldsWriteLock = true;
                ctx.RemoteLockToken = _provider.LockFileAsync(vpath).GetAwaiter().GetResult();
            }

            FileDesc = ctx;
            ctx.FillFileInfo(out FileInfo);
            return STATUS_SUCCESS;
        }
    }

    public override int Open(
        string FileName,
        uint CreateOptions,
        uint GrantedAccess,
        out object FileNode,
        out object FileDesc,
        out FileInfo FileInfo,
        out string NormalizedName)
    {
        FileNode = default!;
        FileDesc = default!;
        FileInfo = default;
        NormalizedName = default!;

        var vpath = ToVirtualPath(FileName);
        var item = GetCachedOrFetchItem(vpath);
        if (item == null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        FillFileInfoFromCloudItem(item, _config.ReadOnly, out var baseInfo);

        if (item.IsDirectory)
        {
            var dirCtx = new OpenHandleContext
            {
                VirtualPath = vpath,
                IsDirectory = true,
                CreationTimeFileTime = baseInfo.CreationTime,
                LastAccessTimeFileTime = baseInfo.LastAccessTime,
                LastWriteTimeFileTime = baseInfo.LastWriteTime,
                FileAttributes = baseInfo.FileAttributes
            };
            FileDesc = dirCtx;
            FileInfo = baseInfo;
            return STATUS_SUCCESS;
        }

        // Check File Lock if opened for writing
        bool wantsWrite = 0 != (GrantedAccess & (uint)(FileSystemRights.WriteData | FileSystemRights.AppendData));
        if (wantsWrite && _config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        if (wantsWrite && _config.FileLock && !_activeWriteLocks.TryAdd(vpath, 1))
            return STATUS_SHARING_VIOLATION;

        string? remoteLockToken = null;
        if (wantsWrite && _config.FileLock)
        {
            try
            {
                remoteLockToken = _provider.LockFileAsync(vpath).GetAwaiter().GetResult();
            }
            catch
            {
                _activeWriteLocks.TryRemove(vpath, out _);
                return STATUS_SHARING_VIOLATION;
            }
        }

        // Lazy-open: do NOT download file content during Open().
        // It will be materialized on demand in Read(), Write(), or SetFileSize().
        var fileCtx = new OpenHandleContext
        {
            VirtualPath = vpath,
            IsDirectory = false,
            IsMaterialized = false,
            KnownFileSize = baseInfo.FileSize,
            HoldsWriteLock = wantsWrite && _config.FileLock,
            RemoteLockToken = remoteLockToken,
            CreationTimeFileTime = baseInfo.CreationTime,
            LastAccessTimeFileTime = baseInfo.LastAccessTime,
            LastWriteTimeFileTime = baseInfo.LastWriteTime,
            FileAttributes = baseInfo.FileAttributes
        };

        FileDesc = fileCtx;
        fileCtx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(
        object FileNode,
        object FileDesc0,
        uint FileAttributes,
        bool ReplaceFileAttributes,
        ulong AllocationSize,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var ctx = (OpenHandleContext)FileDesc0;
        lock (ctx)
        {
            EnsureStagingStreamMaterialized(ctx, populateExistingContent: false);
            ctx.StagingStream!.SetLength(0);
            ctx.KnownFileSize = 0;
            ctx.IsDirty = true;
            ctx.LastWriteTimeFileTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        }

        if (ReplaceFileAttributes)
            ctx.FileAttributes = FileAttributes | (uint)System.IO.FileAttributes.Archive;
        else if (FileAttributes != 0)
            ctx.FileAttributes |= FileAttributes | (uint)System.IO.FileAttributes.Archive;

        ctx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int Read(
        object FileNode,
        object FileDesc0,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        out uint PBytesTransferred)
    {
        PBytesTransferred = 0;
        var ctx = (OpenHandleContext)FileDesc0;
        if (ctx.IsDirectory)
            return STATUS_INVALID_DEVICE_REQUEST;

        if (ctx.KnownFileSize == 0 || Offset >= ctx.KnownFileSize)
            return STATUS_END_OF_FILE;

        var toRead = (int)Math.Min((ulong)Length, ctx.KnownFileSize - Offset);
        if (toRead <= 0)
            return STATUS_END_OF_FILE;

        // Case 1: Active uncommitted local write staging stream
        if (ctx.IsDirty && ctx.StagingStream != null)
        {
            lock (ctx)
            {
                if (Offset >= (ulong)ctx.StagingStream.Length)
                    return STATUS_END_OF_FILE;

                var avail = (int)Math.Min((ulong)toRead, (ulong)ctx.StagingStream.Length - Offset);
                var temp = new byte[avail];
                ctx.StagingStream.Seek((long)Offset, SeekOrigin.Begin);
                var read = ctx.StagingStream.Read(temp, 0, avail);
                if (read > 0)
                    Marshal.Copy(temp, 0, Buffer, read);
                PBytesTransferred = (uint)read;
                return STATUS_SUCCESS;
            }
        }

        // Case 2: Present in full retained disk cache
        if (_retainedCache.TryGetValue(ctx.VirtualPath, out var cacheEntry) &&
            cacheEntry.FileSize == ctx.KnownFileSize &&
            cacheEntry.LastWriteTimeFileTime == ctx.LastWriteTimeFileTime &&
            File.Exists(cacheEntry.LocalCacheFilePath))
        {
            try
            {
                using var fs = new FileStream(cacheEntry.LocalCacheFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                fs.Seek((long)Offset, SeekOrigin.Begin);
                var temp = new byte[toRead];
                var read = fs.Read(temp, 0, toRead);
                if (read > 0)
                    Marshal.Copy(temp, 0, Buffer, read);
                PBytesTransferred = (uint)read;
                cacheEntry.LastAccessTickCount = Environment.TickCount64;
                return STATUS_SUCCESS;
            }
            catch
            {
                // Fall through to on-demand chunked streaming
            }
        }

        // Case 3: Fast on-demand chunk streaming (1 MB block cache) without full-file staging
        try
        {
            var temp = new byte[toRead];
            int totalRead = ReadViaChunkCache(ctx.VirtualPath, (long)Offset, temp, 0, toRead, ctx.KnownFileSize);
            if (totalRead > 0)
                Marshal.Copy(temp, 0, Buffer, totalRead);
            PBytesTransferred = (uint)totalRead;
            return STATUS_SUCCESS;
        }
        catch
        {
            return STATUS_DEVICE_DATA_ERROR;
        }
    }

    public override int Write(
        object FileNode,
        object FileDesc0,
        IntPtr Buffer,
        ulong Offset,
        uint Length,
        bool WriteToEndOfFile,
        bool ConstrainedIo,
        out uint PBytesTransferred,
        out FileInfo FileInfo)
    {
        PBytesTransferred = 0;
        FileInfo = default;

        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var ctx = (OpenHandleContext)FileDesc0;
        if (ctx.IsDirectory)
            return STATUS_INVALID_DEVICE_REQUEST;

        lock (ctx)
        {
            // If writing from offset 0 and covering the entire existing file, no need to download old content
            bool needOldContent = WriteToEndOfFile || Offset > 0 || Length < ctx.KnownFileSize;
            EnsureStagingStreamMaterialized(ctx, populateExistingContent: needOldContent);
            if (ctx.StagingStream == null)
                return STATUS_INVALID_DEVICE_REQUEST;

            if (ConstrainedIo)
            {
                if (Offset >= (ulong)ctx.StagingStream.Length)
                    return STATUS_SUCCESS;
                if (Offset + Length > (ulong)ctx.StagingStream.Length)
                    Length = (uint)((ulong)ctx.StagingStream.Length - Offset);
            }

            var bytes = new byte[Length];
            if (Length > 0)
                Marshal.Copy(Buffer, bytes, 0, (int)Length);

            if (WriteToEndOfFile)
                ctx.StagingStream.Seek(0, SeekOrigin.End);
            else
                ctx.StagingStream.Seek((long)Offset, SeekOrigin.Begin);

            ctx.StagingStream.Write(bytes, 0, bytes.Length);
            ctx.KnownFileSize = (ulong)ctx.StagingStream.Length;
            ctx.IsDirty = true;
            ctx.LastWriteTimeFileTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            PBytesTransferred = Length;
            ctx.FillFileInfo(out FileInfo);
        }

        return STATUS_SUCCESS;
    }

    public override int Flush(
        object FileNode,
        object FileDesc0,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        if (FileDesc0 == null)
            return STATUS_SUCCESS;

        var ctx = (OpenHandleContext)FileDesc0;
        FlushContextToRemote(ctx);
        ctx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    private void FlushContextToRemote(OpenHandleContext ctx)
    {
        lock (ctx)
        {
            if (!ctx.IsDirectory && ctx.IsDirty && !ctx.DeleteOnCleanup && ctx.StagingStream != null)
            {
                ctx.StagingStream.Flush(true);
                ctx.StagingStream.Position = 0;
                _provider.UploadFileAsync(ctx.VirtualPath, ctx.StagingStream).GetAwaiter().GetResult();
                ctx.IsDirty = false;
                ctx.KnownFileSize = (ulong)ctx.StagingStream.Length;

                var now = DateTime.UtcNow;
                var nowFt = (ulong)now.ToFileTimeUtc();
                ctx.LastWriteTimeFileTime = nowFt;

                InvalidatePathAndParent(ctx.VirtualPath);
                _itemCache[ctx.VirtualPath] = (now, new CloudItemInfo(
                    VirtualPathHelper.GetFileName(ctx.VirtualPath),
                    ctx.VirtualPath,
                    false,
                    ctx.KnownFileSize,
                    DateTime.FromFileTimeUtc((long)ctx.CreationTimeFileTime),
                    now,
                    now));

                if (!string.IsNullOrEmpty(ctx.StagingFilePath))
                {
                    StoreInRetainedDiskCache(ctx.VirtualPath, ctx.StagingFilePath, ctx.KnownFileSize, nowFt);
                }
            }
        }
    }

    public override int GetFileInfo(
        object FileNode,
        object FileDesc0,
        out FileInfo FileInfo)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        ctx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object FileNode,
        object FileDesc0,
        uint FileAttributes,
        ulong CreationTime,
        ulong LastAccessTime,
        ulong LastWriteTime,
        ulong ChangeTime,
        out FileInfo FileInfo)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        if (unchecked((uint)-1) != FileAttributes && FileAttributes != 0)
            ctx.FileAttributes = FileAttributes;
        if (CreationTime != 0)
            ctx.CreationTimeFileTime = CreationTime;
        if (LastAccessTime != 0)
            ctx.LastAccessTimeFileTime = LastAccessTime;
        if (LastWriteTime != 0)
            ctx.LastWriteTimeFileTime = LastWriteTime;

        ctx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object FileNode,
        object FileDesc0,
        ulong NewSize,
        bool SetAllocationSize,
        out FileInfo FileInfo)
    {
        FileInfo = default;
        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var ctx = (OpenHandleContext)FileDesc0;
        if (!ctx.IsDirectory)
        {
            lock (ctx)
            {
                if (!SetAllocationSize || ctx.KnownFileSize > NewSize)
                {
                    EnsureStagingStreamMaterialized(ctx, populateExistingContent: NewSize > 0 && ctx.KnownFileSize > 0);
                    ctx.StagingStream!.SetLength((long)NewSize);
                    ctx.KnownFileSize = NewSize;
                    ctx.IsDirty = true;
                }
            }
        }

        ctx.FillFileInfo(out FileInfo);
        return STATUS_SUCCESS;
    }

    public override int CanDelete(
        object FileNode,
        object FileDesc0,
        string FileName)
    {
        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var ctx = (OpenHandleContext)FileDesc0;
        if (ctx.IsDirectory)
        {
            var children = GetCachedOrFetchDirectory(ctx.VirtualPath);
            if (children.Count > 0)
                return STATUS_DIRECTORY_NOT_EMPTY;
        }
        ctx.DeleteOnCleanup = true;
        return STATUS_SUCCESS;
    }

    public override void Cleanup(
        object FileNode,
        object FileDesc0,
        string FileName,
        uint Flags)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        if (0 != (Flags & CleanupDelete) || ctx.DeleteOnCleanup)
        {
            ctx.DeleteOnCleanup = true;
            ctx.IsDirty = false;
            try
            {
                if (ctx.IsDirectory)
                    _provider.DeleteDirectoryAsync(ctx.VirtualPath).GetAwaiter().GetResult();
                else
                    _provider.DeleteFileAsync(ctx.VirtualPath).GetAwaiter().GetResult();
            }
            catch { }
            InvalidatePathAndParent(ctx.VirtualPath);
            RemoveFromRetainedDiskCache(ctx.VirtualPath);
            _customAclCache.TryRemove(ctx.VirtualPath, out _);
        }
        else if (ctx.IsDirty)
        {
            try
            {
                FlushContextToRemote(ctx);
            }
            catch { }
        }
    }

    public override void Close(
        object FileNode,
        object FileDesc0)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        if (!string.IsNullOrEmpty(ctx.RemoteLockToken))
        {
            try
            {
                _provider.UnlockFileAsync(ctx.VirtualPath, ctx.RemoteLockToken).GetAwaiter().GetResult();
            }
            catch { }
            ctx.RemoteLockToken = null;
        }
        if (ctx.HoldsWriteLock)
            _activeWriteLocks.TryRemove(ctx.VirtualPath, out _);
        ctx.Dispose();
    }

    public override int Rename(
        object FileNode,
        object FileDesc0,
        string FileName,
        string NewFileName,
        bool ReplaceIfExists)
    {
        if (_config.ReadOnly)
            return STATUS_MEDIA_WRITE_PROTECTED;

        var oldVPath = ToVirtualPath(FileName);
        var newVPath = ToVirtualPath(NewFileName);

        if (string.Equals(oldVPath, newVPath, StringComparison.OrdinalIgnoreCase))
            return STATUS_SUCCESS;

        if (!ReplaceIfExists && GetCachedOrFetchItem(newVPath) != null)
            return STATUS_OBJECT_NAME_COLLISION;

        var ctx = (OpenHandleContext)FileDesc0;
        if (ctx.IsDirty)
            FlushContextToRemote(ctx);

        _provider.RenameAsync(oldVPath, newVPath, ReplaceIfExists).GetAwaiter().GetResult();
        ctx.VirtualPath = newVPath;
        InvalidatePathAndParent(oldVPath);
        InvalidatePathAndParent(newVPath);
        RemoveFromRetainedDiskCache(oldVPath);
        RemoveFromRetainedDiskCache(newVPath);
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(
        object FileNode,
        object FileDesc0,
        ref byte[] SecurityDescriptor)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        SecurityDescriptor = _customAclCache.TryGetValue(ctx.VirtualPath, out var sd)
            ? sd
            : _defaultSecurityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(
        object FileNode,
        object FileDesc0,
        AccessControlSections Sections,
        byte[] ModificationDescriptor)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        var current = _customAclCache.TryGetValue(ctx.VirtualPath, out var sd)
            ? sd
            : _defaultSecurityDescriptor;

        var status = ModifySecurityDescriptorEx(current, Sections, ModificationDescriptor, ref current);
        if (status == STATUS_SUCCESS)
            _customAclCache[ctx.VirtualPath] = current;
        return status;
    }

    public override bool ReadDirectoryEntry(
        object FileNode,
        object FileDesc0,
        string Pattern,
        string Marker,
        ref object Context,
        out string FileName,
        out FileInfo FileInfo)
    {
        var ctx = (OpenHandleContext)FileDesc0;
        if (ctx.DirectoryEntries == null)
        {
            var items = GetCachedOrFetchDirectory(ctx.VirtualPath);
            var sorted = new SortedList(StringComparer.OrdinalIgnoreCase);

            if (ctx.VirtualPath != "/")
            {
                var now = DateTime.UtcNow;
                sorted["."] = new CloudItemInfo(".", ctx.VirtualPath, true, 0, now, now, now);
                sorted[".."] = new CloudItemInfo("..", VirtualPathHelper.GetParentPath(ctx.VirtualPath), true, 0, now, now, now);
            }

            foreach (var item in items)
            {
                if (!string.IsNullOrEmpty(item.Name))
                    sorted[item.Name] = item;
            }

            ctx.DirectoryEntries = new DictionaryEntry[sorted.Count];
            sorted.CopyTo(ctx.DirectoryEntries, 0);
        }

        int index;
        if (Context == null)
        {
            index = 0;
            if (Marker != null)
            {
                for (int i = 0; i < ctx.DirectoryEntries.Length; i++)
                {
                    if (string.Compare((string)ctx.DirectoryEntries[i].Key, Marker, StringComparison.OrdinalIgnoreCase) > 0)
                    {
                        index = i;
                        break;
                    }
                    index = ctx.DirectoryEntries.Length;
                }
            }
        }
        else
        {
            index = (int)Context;
        }

        if (index < ctx.DirectoryEntries.Length)
        {
            Context = index + 1;
            var entry = ctx.DirectoryEntries[index];
            FileName = (string)entry.Key;
            FillFileInfoFromCloudItem((CloudItemInfo)entry.Value!, _config.ReadOnly, out FileInfo);
            return true;
        }

        FileName = default!;
        FileInfo = default;
        return false;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDir))
                Directory.Delete(_cacheDir, recursive: true);
        }
        catch { }
    }
}
