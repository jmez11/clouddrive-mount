using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.Core.Providers;

public sealed class CloudflareR2StorageProvider : ICloudStorageProvider
{
    private readonly DriveConfig _config;
    private readonly AmazonS3Client _s3;
    private readonly string _bucket;

    public CloudflareR2StorageProvider(DriveConfig config)
    {
        _config = config;
        _bucket = config.R2BucketName.Trim();
        var secret = CredentialProtector.Unprotect(config.EncryptedR2SecretAccessKey).Trim();
        _s3 = CreateR2Client(config.R2AccountId.Trim(), config.R2AccessKeyId.Trim(), secret, config.TimeoutSeconds);
    }

    public static AmazonS3Client CreateR2Client(string accountId, string accessKeyId, string secretAccessKey, int timeoutSeconds = 100)
    {
        if (string.IsNullOrWhiteSpace(accountId))
            throw new ArgumentException("Cloudflare R2 Account ID is required.", nameof(accountId));

        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        var s3Config = new AmazonS3Config
        {
            ServiceURL = $"https://{accountId.Trim()}.r2.cloudflarestorage.com",
            ForcePathStyle = true,
            AuthenticationRegion = "auto",
            Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 10, 600))
        };

        return new AmazonS3Client(credentials, s3Config);
    }

    public static async Task<IReadOnlyList<string>> DiscoverBucketsAsync(
        string accountId,
        string accessKeyId,
        string secretAccessKey,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateR2Client(accountId, accessKeyId, secretAccessKey, 30);
        var response = await client.ListBucketsAsync(cancellationToken);
        return response.Buckets
            .Select(b => b.BucketName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task VerifyBucketAccessAsync(
        string accountId,
        string accessKeyId,
        string secretAccessKey,
        string bucketName,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateR2Client(accountId, accessKeyId, secretAccessKey, 30);
        await client.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = bucketName.Trim(),
            MaxKeys = 1
        }, cancellationToken);
    }

    private static string ToObjectKey(string virtualPath, bool isDirectory = false)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath).TrimStart('/');
        if (string.IsNullOrEmpty(norm))
            return string.Empty;
        return isDirectory && !norm.EndsWith('/') ? norm + "/" : norm;
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_bucket))
            throw new InvalidOperationException("Cloudflare R2 bucket name is required.");

        var req = new ListObjectsV2Request
        {
            BucketName = _bucket,
            MaxKeys = 1
        };
        await _s3.ListObjectsV2Async(req, cancellationToken);
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

        var key = ToObjectKey(norm, isDirectory: false);

        // 1. Check exact file object
        try
        {
            var meta = await _s3.GetObjectMetadataAsync(new GetObjectMetadataRequest
            {
                BucketName = _bucket,
                Key = key
            }, cancellationToken);

            var mod = meta.LastModified.ToUniversalTime();
            return new CloudItemInfo(
                VirtualPathHelper.GetFileName(norm),
                norm,
                IsDirectory: false,
                Size: (ulong)Math.Max(0, meta.ContentLength),
                CreationTimeUtc: mod,
                LastWriteTimeUtc: mod,
                LastAccessTimeUtc: mod);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Check directory marker or prefix children
        }

        // 2. Check directory marker (key + "/") or prefix existence
        var dirPrefix = ToObjectKey(norm, isDirectory: true);
        var listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = dirPrefix,
            MaxKeys = 1
        }, cancellationToken);

        if (listResp.KeyCount > 0 || listResp.S3Objects.Count > 0 || listResp.CommonPrefixes.Count > 0)
        {
            var now = DateTime.UtcNow;
            return new CloudItemInfo(
                VirtualPathHelper.GetFileName(norm),
                norm,
                IsDirectory: true,
                Size: 0,
                CreationTimeUtc: now,
                LastWriteTimeUtc: now,
                LastAccessTimeUtc: now);
        }

        return null;
    }

    public async Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var normDir = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);
        var prefix = normDir == "/" ? string.Empty : ToObjectKey(normDir, isDirectory: true);

        var items = new Dictionary<string, CloudItemInfo>(StringComparer.OrdinalIgnoreCase);
        string? continuationToken = null;

        do
        {
            var req = new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = prefix,
                Delimiter = "/",
                ContinuationToken = continuationToken
            };

            var resp = await _s3.ListObjectsV2Async(req, cancellationToken);

            foreach (var commonPrefix in resp.CommonPrefixes)
            {
                var rel = commonPrefix[prefix.Length..].TrimEnd('/');
                if (string.IsNullOrEmpty(rel))
                    continue;

                var childVirt = normDir == "/" ? $"/{rel}" : $"{normDir}/{rel}";
                var now = DateTime.UtcNow;
                items[rel] = new CloudItemInfo(rel, childVirt, true, 0, now, now, now);
            }

            foreach (var obj in resp.S3Objects)
            {
                if (obj.Key == prefix)
                    continue; // Directory placeholder itself

                var rel = obj.Key[prefix.Length..];
                if (string.IsNullOrEmpty(rel))
                    continue;

                var isDirMarker = rel.EndsWith('/');
                var cleanName = rel.TrimEnd('/');
                if (cleanName.Contains('/'))
                    continue;

                var childVirt = normDir == "/" ? $"/{cleanName}" : $"{normDir}/{cleanName}";
                var mod = obj.LastModified.ToUniversalTime();
                items[cleanName] = new CloudItemInfo(
                    cleanName,
                    childVirt,
                    isDirMarker,
                    isDirMarker ? 0UL : (ulong)Math.Max(0, obj.Size),
                    mod,
                    mod,
                    mod);
            }

            continuationToken = resp.IsTruncated ? resp.NextContinuationToken : null;
        } while (!string.IsNullOrEmpty(continuationToken));

        return items.Values.ToList();
    }

    public async Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default)
    {
        var key = ToObjectKey(virtualPath, isDirectory: false);
        using var resp = await _s3.GetObjectAsync(_bucket, key, cancellationToken);
        await resp.ResponseStream.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default)
    {
        var key = ToObjectKey(virtualPath, isDirectory: false);
        source.Position = 0;
        var putReq = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = source,
            AutoCloseStream = false,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };
        await _s3.PutObjectAsync(putReq, cancellationToken);
    }

    public async Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var dirKey = ToObjectKey(virtualDirectoryPath, isDirectory: true);
        if (string.IsNullOrEmpty(dirKey))
            return;

        using var empty = new MemoryStream(Array.Empty<byte>());
        var putReq = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = dirKey,
            InputStream = empty,
            DisablePayloadSigning = true,
            DisableDefaultChecksumValidation = true
        };
        await _s3.PutObjectAsync(putReq, cancellationToken);
    }

    public async Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var key = ToObjectKey(virtualPath, isDirectory: false);
        await _s3.DeleteObjectAsync(_bucket, key, cancellationToken);
    }

    public async Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var prefix = ToObjectKey(virtualDirectoryPath, isDirectory: true);
        var listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = prefix
        }, cancellationToken);

        foreach (var obj in listResp.S3Objects)
        {
            await _s3.DeleteObjectAsync(_bucket, obj.Key, cancellationToken);
        }
    }

    public async Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var item = await GetItemAsync(oldVirtualPath, cancellationToken);
        if (item == null)
            throw new FileNotFoundException($"Source object '{oldVirtualPath}' not found in R2.");

        if (!item.IsDirectory)
        {
            var oldKey = ToObjectKey(oldVirtualPath, false);
            var newKey = ToObjectKey(newVirtualPath, false);
            await _s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = oldKey,
                DestinationBucket = _bucket,
                DestinationKey = newKey
            }, cancellationToken);
            await _s3.DeleteObjectAsync(_bucket, oldKey, cancellationToken);
        }
        else
        {
            var oldPrefix = ToObjectKey(oldVirtualPath, true);
            var newPrefix = ToObjectKey(newVirtualPath, true);
            var listResp = await _s3.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = _bucket,
                Prefix = oldPrefix
            }, cancellationToken);

            foreach (var obj in listResp.S3Objects)
            {
                var suffix = obj.Key[oldPrefix.Length..];
                var destKey = newPrefix + suffix;
                await _s3.CopyObjectAsync(new CopyObjectRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = obj.Key,
                    DestinationBucket = _bucket,
                    DestinationKey = destKey
                }, cancellationToken);
                await _s3.DeleteObjectAsync(_bucket, obj.Key, cancellationToken);
            }
        }
    }

    public void Dispose()
    {
        _s3.Dispose();
    }
}
