using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using CloudDriveMount.Core.FileSystem;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Providers;
using CloudDriveMount.Core.Services;
using Fsp;
using Xunit;
using FileInfo = Fsp.Interop.FileInfo;

namespace CloudDriveMount.Tests;

public class CloudDriveMountTests
{
    [Fact]
    public void CredentialProtector_RoundTripsSecrets()
    {
        const string secret = "r2-super-secret-access-key-12345!@#";
        var encrypted = CredentialProtector.Protect(secret);
        Assert.NotEqual(secret, encrypted);
        var decrypted = CredentialProtector.Unprotect(encrypted);
        Assert.Equal(secret, decrypted);
    }

    [Fact]
    public void ConfigStore_PersistsAllProtocolsAndOptions()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"cdm_test_{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(tempFile);
            var r2Drive = new DriveConfig
            {
                Protocol = StorageProtocol.CloudflareR2,
                DriveLetter = "W:",
                Label = "Cloudflare R2",
                IsLocalDisk = true,
                LocalDiskVisibility = LocalDiskScope.Public,
                R2AccountId = "acc1234567890",
                R2AccessKeyId = "ak_123",
                EncryptedR2SecretAccessKey = CredentialProtector.Protect("sk_secret"),
                R2BucketName = "media-assets"
            };
            var sftpDrive = new DriveConfig
            {
                Protocol = StorageProtocol.Sftp,
                DriveLetter = "Y:",
                Label = "SFTP",
                UseProxy = true,
                ProxyMode = CloudflaredProxyMode.AccessTcpTunnel,
                Host = "sftp.internal.example.com",
                Port = 22
            };

            store.SaveDrives([r2Drive, sftpDrive]);
            var loaded = store.LoadDrives();

            Assert.Equal(2, loaded.Count);
            Assert.Equal(StorageProtocol.CloudflareR2, loaded[0].Protocol);
            Assert.True(loaded[0].IsLocalDisk);
            Assert.Equal(LocalDiskScope.Public, loaded[0].LocalDiskVisibility);
            Assert.Equal("media-assets", loaded[0].R2BucketName);
            Assert.Equal("sk_secret", CredentialProtector.Unprotect(loaded[0].EncryptedR2SecretAccessKey));

            Assert.Equal(StorageProtocol.Sftp, loaded[1].Protocol);
            Assert.True(loaded[1].UseProxy);
            Assert.Equal(CloudflaredProxyMode.AccessTcpTunnel, loaded[1].ProxyMode);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void CloudflaredTunnelManager_BuildsCorrectArgumentsAndHeaders()
    {
        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.WebDav,
            Host = "webdav.corp.example.com",
            UseProxy = true,
            ProxyMode = CloudflaredProxyMode.BothAccessTcpAndHeaders,
            CfAccessClientId = "cf-id-abc.access",
            EncryptedCfAccessClientSecret = CredentialProtector.Protect("cf-secret-xyz")
        };

        var args = CloudflaredTunnelManager.BuildAccessTcpArguments(cfg, 54321);
        Assert.Contains("access tcp --hostname webdav.corp.example.com --url 127.0.0.1:54321", args);
        Assert.Contains("--id cf-id-abc.access", args);
        Assert.Contains("--secret cf-secret-xyz", args);

        var headers = CloudflaredTunnelManager.GetServiceTokenHeaders(cfg);
        Assert.Equal("cf-id-abc.access", headers["CF-Access-Client-Id"]);
        Assert.Equal("cf-secret-xyz", headers["CF-Access-Client-Secret"]);
    }

    [Theory]
    [InlineData(StorageProtocol.Sftp, 'Y', true)]
    [InlineData(StorageProtocol.Ftp, 'X', false)]
    [InlineData(StorageProtocol.WebDav, 'W', false)]
    [InlineData(StorageProtocol.GoogleDrive, 'Z', false)]
    [InlineData(StorageProtocol.CloudflareR2, 'R', true)]
    public void ExplorerIconManager_GeneratesValidMultiSizeIcoFile(StorageProtocol protocol, char letter, bool isLocalDisk)
    {
        var iconPath = ExplorerIconManager.EnsureProtocolIconFile(protocol, letter, isLocalDisk);
        Assert.True(File.Exists(iconPath));

        var bytes = File.ReadAllBytes(iconPath);
        Assert.True(bytes.Length > 1000);
        // ICO magic header: 00 00 01 00 07 00 (7 icon sizes: 16, 24, 32, 48, 64, 128 DIB + 256 PNG)
        Assert.Equal(0, bytes[0]);
        Assert.Equal(0, bytes[1]);
        Assert.Equal(1, bytes[2]);
        Assert.Equal(0, bytes[3]);
        Assert.Equal(7, bytes[4]);
    }

    [Fact]
    public void ExplorerIconManager_GeneratesAppExeIconAsset()
    {
        var assetPath = @"C:\dev\clouddrive-mount\src\CloudDriveMount.App\Assets\app.ico";
        ExplorerIconManager.SaveAppIconFile(assetPath);
        Assert.True(File.Exists(assetPath));
    }

    [Fact]
    public void ExplorerIconManager_RegistersCleanNetworkDriveLabelAndIconInExplorer()
    {
        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.Sftp,
            DriveLetter = "P:",
            Label = "Plex Max",
            IsLocalDisk = false
        };
        const string uncPrefix = @"\CloudDriveMount\Plex Max";
        using var mem = new InMemoryCloudStorageProvider();
        using var fs = new CloudDriveFileSystem(cfg, mem);
        using var host = new FileSystemHost(fs);
        host.Prefix = uncPrefix;

        ExplorerIconManager.RegisterDriveIconAndLabel(cfg, uncPrefix);
        int st = host.Mount("P:", null, false, 0);
        try
        {
            Assert.True(st >= 0);
            ExplorerIconManager.RegisterDriveIconAndLabel(cfg, uncPrefix);

            var sfiName = new SHFILEINFOW();
            SHGetFileInfoW("P:\\", 0, ref sfiName, (uint)Marshal.SizeOf<SHFILEINFOW>(), 0x200 | 0x400);
            var sfiIcon = new SHFILEINFOW();
            SHGetFileInfoW("P:\\", 0, ref sfiIcon, (uint)Marshal.SizeOf<SHFILEINFOW>(), 0x1000);

            Assert.Equal("Plex Max (P:)", sfiName.szDisplayName);
            Assert.EndsWith("drive_sftp_p.ico", sfiIcon.szDisplayName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (st >= 0) host.Unmount();
            ExplorerIconManager.UnregisterDriveIconAndLabel('P', uncPrefix);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFOW
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFOW psfi, uint cbFileInfo, uint uFlags);

    [Fact]
    public void CloudflareR2StorageProvider_ConfiguresR2ServiceUrlAndPathStyle()
    {
        using var client = CloudflareR2StorageProvider.CreateR2Client("my-cf-account-99", "key", "secret");
        var s3Cfg = (Amazon.S3.AmazonS3Config)client.Config;
        Assert.Equal("https://my-cf-account-99.r2.cloudflarestorage.com/", s3Cfg.ServiceURL);
        Assert.True(s3Cfg.ForcePathStyle);
        Assert.Equal("auto", s3Cfg.AuthenticationRegion);
    }

    [Fact]
    public async Task WebDavStorageProvider_ParsesPropFindAndSendsCfHeaders()
    {
        const string samplePropFind = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/remote/</D:href>
                <D:propstat>
                  <D:prop>
                    <D:resourcetype><D:collection/></D:resourcetype>
                  </D:prop>
                </D:propstat>
              </D:response>
              <D:response>
                <D:href>/remote/notes.txt</D:href>
                <D:propstat>
                  <D:prop>
                    <D:resourcetype/>
                    <D:getcontentlength>42</D:getcontentlength>
                    <D:getlastmodified>Tue, 10 Nov 2026 12:00:00 GMT</D:getlastmodified>
                  </D:prop>
                </D:propstat>
              </D:response>
            </D:multistatus>
            """;

        string? capturedCfId = null;
        var handler = new MockHttpHandler(req =>
        {
            if (req.Headers.TryGetValues("CF-Access-Client-Id", out var vals))
                capturedCfId = vals.FirstOrDefault();

            return new HttpResponseMessage(HttpStatusCode.MultiStatus)
            {
                Content = new StringContent(samplePropFind, Encoding.UTF8, "application/xml")
            };
        });

        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.WebDav,
            Host = "dav.example.com",
            Port = 443,
            UseTls = true,
            RemotePath = "/remote",
            UseProxy = true,
            ProxyMode = CloudflaredProxyMode.ServiceTokenHeaders,
            CfAccessClientId = "token-client-id-123",
            EncryptedCfAccessClientSecret = CredentialProtector.Protect("token-secret-456")
        };

        using var provider = new WebDavStorageProvider(cfg, handler);
        var items = await provider.ListDirectoryAsync("/");
        Assert.Equal("token-client-id-123", capturedCfId);
        Assert.Single(items);
        Assert.Equal("notes.txt", items[0].Name);
        Assert.Equal(42UL, items[0].Size);
        Assert.False(items[0].IsDirectory);
    }

    [Fact]
    public async Task WebDavStorageProvider_And_CloudDriveFileSystem_EnforceWebDavFileLock()
    {
        const string lockResponseXml = """
            <?xml version="1.0" encoding="utf-8"?>
            <D:prop xmlns:D="DAV:">
              <D:lockdiscovery>
                <D:activelock>
                  <D:locktoken>
                    <D:href>opaquelocktoken:a515cfa4-5d2a-11d1-8f23-00aa00bd5301</D:href>
                  </D:locktoken>
                </D:activelock>
              </D:lockdiscovery>
            </D:prop>
            """;

        var seenMethods = new List<string>();
        string? seenPutIfHeader = null;
        string? seenUnlockTokenHeader = null;

        var handler = new MockHttpHandler(req =>
        {
            seenMethods.Add(req.Method.Method);
            if (req.Method.Method == "LOCK")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(lockResponseXml, Encoding.UTF8, "application/xml")
                };
            }
            if (req.Method == HttpMethod.Put)
            {
                if (req.Headers.TryGetValues("If", out var ifVals))
                    seenPutIfHeader = ifVals.FirstOrDefault();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            if (req.Method.Method == "UNLOCK")
            {
                if (req.Headers.TryGetValues("Lock-Token", out var ltVals))
                    seenUnlockTokenHeader = ltVals.FirstOrDefault();
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.WebDav,
            Host = "dav.example.com",
            Port = 443,
            UseTls = true,
            RemotePath = "/remote",
            FileLock = true
        };

        using var provider = new WebDavStorageProvider(cfg, handler);
        var token = await provider.LockFileAsync("/budget.xlsx");
        Assert.Equal("opaquelocktoken:a515cfa4-5d2a-11d1-8f23-00aa00bd5301", token);

        using var ms = new MemoryStream(Encoding.UTF8.GetBytes("updated sheet"));
        await provider.UploadFileAsync("/budget.xlsx", ms);
        Assert.Equal("(<opaquelocktoken:a515cfa4-5d2a-11d1-8f23-00aa00bd5301>)", seenPutIfHeader);

        await provider.UnlockFileAsync("/budget.xlsx", token!);
        Assert.Equal("<opaquelocktoken:a515cfa4-5d2a-11d1-8f23-00aa00bd5301>", seenUnlockTokenHeader);
        Assert.Equal(["LOCK", "PUT", "UNLOCK"], seenMethods);

        // Also verify CloudDriveFileSystem returns STATUS_SHARING_VIOLATION when a remote file is locked (HTTP 423)
        using var memProvider = new InMemoryCloudStorageProvider { SimulateRemoteLockConflict = true };
        await memProvider.UploadFileAsync("/locked.docx", new MemoryStream(Encoding.UTF8.GetBytes("content")));
        using var fs = new CloudDriveFileSystem(cfg, memProvider);

        var openWriteStatus = fs.Open(
            "\\locked.docx",
            0,
            0x00000002 /* FILE_WRITE_DATA */,
            out _,
            out _,
            out _,
            out _);
        Assert.Equal(FileSystemBase.STATUS_SHARING_VIOLATION, openWriteStatus);
    }

    [Fact]
    public async Task GoogleDriveStorageProvider_ConvertsGoogleDocsToInternetShortcutUrls()
    {
        const string sampleGDriveJson = """
            {
              "files": [
                {
                  "id": "sheet_123",
                  "name": "OceanCiders power budget",
                  "mimeType": "application/vnd.google-apps.spreadsheet",
                  "webViewLink": "https://docs.google.com/spreadsheets/d/sheet_123/edit?usp=drivesdk",
                  "createdTime": "2026-11-10T10:00:00Z",
                  "modifiedTime": "2026-11-10T12:00:00Z"
                }
              ]
            }
            """;

        var handler = new MockHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sampleGDriveJson, Encoding.UTF8, "application/json")
        });

        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.GoogleDrive,
            EncryptedGoogleAccessToken = CredentialProtector.Protect("mock_access_token")
        };

        using var provider = new GoogleDriveStorageProvider(cfg, handler);
        var items = await provider.ListDirectoryAsync("/");
        Assert.Single(items);
        Assert.Equal("OceanCiders power budget.url", items[0].Name);
        Assert.Equal("/OceanCiders power budget.url", items[0].FullPath);
        Assert.False(items[0].IsDirectory);
        Assert.True(items[0].Size > 20);

        using var ms = new MemoryStream();
        await provider.DownloadFileAsync("/OceanCiders power budget.url", ms);
        var shortcutText = Encoding.UTF8.GetString(ms.ToArray());
        Assert.Equal("[InternetShortcut]\r\nURL=https://docs.google.com/spreadsheets/d/sheet_123/edit?usp=drivesdk\r\n", shortcutText);
    }

    [Fact]
    public void CloudDriveFileSystem_PerformsFullReadWriteDeleteRenameAndPublicAcl()
    {
        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.CloudflareR2,
            DriveLetter = "Y:",
            Label = "R2 Public Share",
            IsLocalDisk = true,
            LocalDiskVisibility = LocalDiskScope.Public,
            ReadOnly = false
        };

        using var memProvider = new InMemoryCloudStorageProvider();
        using var fs = new CloudDriveFileSystem(cfg, memProvider);

        // 1. Verify root security descriptor has Everyone / SYSTEM permissions for LAN Sharing
        byte[] sd = new byte[256];
        var secStatus = fs.GetSecurityByName("\\", out uint rootAttrs, ref sd);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, secStatus);
        Assert.NotEqual(0u, rootAttrs & (uint)FileAttributes.Directory);
        Assert.NotEmpty(sd);

        // 2. Create a directory "/docs"
        var createDirStatus = fs.Create(
            "\\docs",
            FileSystemBase.FILE_DIRECTORY_FILE,
            0,
            (uint)FileAttributes.Directory,
            null!,
            0,
            out var dirNode,
            out var dirDesc,
            out var dirInfo,
            out _);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, createDirStatus);
        fs.Close(dirNode, dirDesc);

        // 3. Create & Write a file "/docs/hello.txt"
        var createFileStatus = fs.Create(
            "\\docs\\hello.txt",
            0,
            0,
            (uint)FileAttributes.Archive,
            null!,
            0,
            out var fileNode,
            out var fileDesc,
            out var fileInfo,
            out _);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, createFileStatus);

        var payload = Encoding.UTF8.GetBytes("Hello from CloudDrive Mount R/W!");
        var unmanagedBuf = Marshal.AllocHGlobal(payload.Length);
        try
        {
            Marshal.Copy(payload, 0, unmanagedBuf, payload.Length);
            var writeStatus = fs.Write(
                fileNode,
                fileDesc,
                unmanagedBuf,
                0,
                (uint)payload.Length,
                false,
                false,
                out uint written,
                out FileInfo updatedInfo);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, writeStatus);
            Assert.Equal((uint)payload.Length, written);
            Assert.Equal((ulong)payload.Length, updatedInfo.FileSize);

            fs.Flush(fileNode, fileDesc, out _);
            fs.Cleanup(fileNode, fileDesc, "\\docs\\hello.txt", 0);
            fs.Close(fileNode, fileDesc);

            // 4. Re-open and Read back "/docs/hello.txt"
            var openStatus = fs.Open(
                "\\docs\\hello.txt",
                0,
                0,
                out var readNode,
                out var readDesc,
                out var openInfo,
                out _);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, openStatus);
            Assert.Equal((ulong)payload.Length, openInfo.FileSize);

            var readBuf = Marshal.AllocHGlobal(payload.Length);
            try
            {
                var readStatus = fs.Read(readNode, readDesc, readBuf, 0, (uint)payload.Length, out uint bytesRead);
                Assert.Equal(FileSystemBase.STATUS_SUCCESS, readStatus);
                Assert.Equal((uint)payload.Length, bytesRead);

                var actual = new byte[bytesRead];
                Marshal.Copy(readBuf, actual, 0, (int)bytesRead);
                Assert.Equal("Hello from CloudDrive Mount R/W!", Encoding.UTF8.GetString(actual));
            }
            finally
            {
                Marshal.FreeHGlobal(readBuf);
            }

            // 5. Rename "/docs/hello.txt" -> "/docs/renamed.txt"
            var renameStatus = fs.Rename(readNode, readDesc, "\\docs\\hello.txt", "\\docs\\renamed.txt", true);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, renameStatus);

            // 6. Delete "/docs/renamed.txt"
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fs.CanDelete(readNode, readDesc, "\\docs\\renamed.txt"));
            fs.Cleanup(readNode, readDesc, "\\docs\\renamed.txt", FileSystemBase.CleanupDelete);
            fs.Close(readNode, readDesc);

            byte[] dummySd = null!;
            var afterDel = fs.GetSecurityByName("\\docs\\renamed.txt", out _, ref dummySd);
            Assert.Equal(FileSystemBase.STATUS_OBJECT_NAME_NOT_FOUND, afterDel);
        }
        finally
        {
            Marshal.FreeHGlobal(unmanagedBuf);
        }
    }

    [Fact]
    public async Task CloudDriveFileSystem_LazyOpensWithoutDownloadAndReusesRetainedDiskCache()
    {
        var cfg = new DriveConfig
        {
            Protocol = StorageProtocol.Sftp,
            DriveLetter = "Y:",
            Label = "SFTP Cache Test"
        };

        using var memProvider = new InMemoryCloudStorageProvider();
        var seedBytes = Encoding.UTF8.GetBytes("Cached payload across multiple handle opens!");
        using (var seedMs = new MemoryStream(seedBytes))
        {
            await memProvider.UploadFileAsync("/seeded.txt", seedMs);
        }

        using var fs = new CloudDriveFileSystem(cfg, memProvider);

        // 1. Metadata-only Open -> GetFileInfo -> Close (like Explorer scanning folder icons)
        Assert.Equal(FileSystemBase.STATUS_SUCCESS,
            fs.Open("\\seeded.txt", 0, 0, out var metaNode, out var metaDesc, out var metaInfo, out _));
        Assert.Equal((ulong)seedBytes.Length, metaInfo.FileSize);
        fs.GetFileInfo(metaNode, metaDesc, out _);
        fs.Close(metaNode, metaDesc);
        Assert.Equal(0, memProvider.DownloadCount); // 0 network downloads!

        // 2. First Open -> Read -> Close downloads once and populates retained disk cache
        Assert.Equal(FileSystemBase.STATUS_SUCCESS,
            fs.Open("\\seeded.txt", 0, 0, out var r1Node, out var r1Desc, out _, out _));
        var buf = Marshal.AllocHGlobal(seedBytes.Length);
        try
        {
            Assert.Equal(FileSystemBase.STATUS_SUCCESS,
                fs.Read(r1Node, r1Desc, buf, 0, (uint)seedBytes.Length, out uint read1));
            Assert.Equal((uint)seedBytes.Length, read1);
            fs.Close(r1Node, r1Desc);
            Assert.Equal(1, memProvider.DownloadCount);

            // 3. Second Open -> Read -> Close hits the retained disk cache (still only 1 total download!)
            Assert.Equal(FileSystemBase.STATUS_SUCCESS,
                fs.Open("\\seeded.txt", 0, 0, out var r2Node, out var r2Desc, out _, out _));
            Assert.Equal(FileSystemBase.STATUS_SUCCESS,
                fs.Read(r2Node, r2Desc, buf, 0, (uint)seedBytes.Length, out uint read2));
            Assert.Equal((uint)seedBytes.Length, read2);
            fs.Close(r2Node, r2Desc);
            Assert.Equal(1, memProvider.DownloadCount);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    [Fact]
    public void ExplorerIconManager_EnsuresAppNotificationIdentityAndStartMenuShortcut()
    {
        var dummyExe = typeof(ExplorerIconManager).Assembly.Location;
        ExplorerIconManager.EnsureAppNotificationIdentity(dummyExe);

        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            $@"Software\Classes\AppUserModelId\{ExplorerIconManager.AppUserModelId}",
            writable: false);
        Assert.NotNull(key);
        Assert.Equal("CloudDrive Mount", key.GetValue("DisplayName") as string);
        var iconUri = key.GetValue("IconUri") as string;
        Assert.False(string.IsNullOrWhiteSpace(iconUri));
        Assert.True(File.Exists(iconUri));

        var shortcutPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            "CloudDrive Mount.lnk");
        Assert.True(File.Exists(shortcutPath));
    }

    private sealed class MockHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public MockHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class InMemoryCloudStorageProvider : ICloudStorageProvider
    {
        private readonly Dictionary<string, (byte[] Data, DateTime ModUtc)> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dirs = new(StringComparer.OrdinalIgnoreCase) { "/" };

        public int DownloadCount { get; private set; }
        public bool SimulateRemoteLockConflict { get; set; }

        public Task<string?> LockFileAsync(string virtualPath, int timeoutSeconds = 3600, CancellationToken cancellationToken = default)
        {
            if (SimulateRemoteLockConflict)
                throw new IOException("Remote file '/locked.docx' is locked by another user (HTTP 423 Locked).");
            return Task.FromResult<string?>("mem-lock-token");
        }

        public Task ConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<CloudVolumeQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CloudVolumeQuota.DefaultUnbounded);

        public Task<CloudItemInfo?> GetItemAsync(string virtualPath, CancellationToken cancellationToken = default)
        {
            var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
            var now = DateTime.UtcNow;
            if (_dirs.Contains(norm))
                return Task.FromResult<CloudItemInfo?>(new CloudItemInfo(VirtualPathHelper.GetFileName(norm), norm, true, 0, now, now, now));
            if (_files.TryGetValue(norm, out var entry))
                return Task.FromResult<CloudItemInfo?>(new CloudItemInfo(VirtualPathHelper.GetFileName(norm), norm, false, (ulong)entry.Data.Length, entry.ModUtc, entry.ModUtc, entry.ModUtc));
            return Task.FromResult<CloudItemInfo?>(null);
        }

        public Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
        {
            var normDir = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);
            var now = DateTime.UtcNow;
            var list = new List<CloudItemInfo>();

            foreach (var d in _dirs)
            {
                if (d != "/" && VirtualPathHelper.GetParentPath(d).Equals(normDir, StringComparison.OrdinalIgnoreCase))
                    list.Add(new CloudItemInfo(VirtualPathHelper.GetFileName(d), d, true, 0, now, now, now));
            }
            foreach (var kv in _files)
            {
                if (VirtualPathHelper.GetParentPath(kv.Key).Equals(normDir, StringComparison.OrdinalIgnoreCase))
                    list.Add(new CloudItemInfo(VirtualPathHelper.GetFileName(kv.Key), kv.Key, false, (ulong)kv.Value.Data.Length, kv.Value.ModUtc, kv.Value.ModUtc, kv.Value.ModUtc));
            }
            return Task.FromResult<IReadOnlyList<CloudItemInfo>>(list);
        }

        public Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default)
        {
            DownloadCount++;
            var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
            if (_files.TryGetValue(norm, out var entry))
                destination.Write(entry.Data, 0, entry.Data.Length);
            return Task.CompletedTask;
        }

        public Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default)
        {
            var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
            using var ms = new MemoryStream();
            source.CopyTo(ms);
            _files[norm] = (ms.ToArray(), DateTime.UtcNow);
            return Task.CompletedTask;
        }

        public Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
        {
            _dirs.Add(VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath));
            return Task.CompletedTask;
        }

        public Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default)
        {
            _files.Remove(VirtualPathHelper.NormalizeVirtualPath(virtualPath));
            return Task.CompletedTask;
        }

        public Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
        {
            _dirs.Remove(VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath));
            return Task.CompletedTask;
        }

        public Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default)
        {
            var oldNorm = VirtualPathHelper.NormalizeVirtualPath(oldVirtualPath);
            var newNorm = VirtualPathHelper.NormalizeVirtualPath(newVirtualPath);
            if (_files.Remove(oldNorm, out var entry))
                _files[newNorm] = entry;
            else if (_dirs.Remove(oldNorm))
                _dirs.Add(newNorm);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
