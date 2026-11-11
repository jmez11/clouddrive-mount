using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.Core.Providers;

public sealed class GoogleDriveStorageProvider : ICloudStorageProvider
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string ShortcutMimeType = "application/vnd.google-apps.shortcut";
    private const string GoogleAppsPrefix = "application/vnd.google-apps.";
    private const string FilesEndpoint = "https://www.googleapis.com/drive/v3/files";
    private const string UploadEndpoint = "https://www.googleapis.com/upload/drive/v3/files";
    private const string AboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=storageQuota";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    private readonly DriveConfig _config;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, string> _pathToIdCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _webShortcutContentCache = new(StringComparer.OrdinalIgnoreCase);
    private string _accessToken = string.Empty;
    private DateTime _tokenExpiryUtc = DateTime.MinValue;

    public GoogleDriveStorageProvider(DriveConfig config, HttpMessageHandler? customHandler = null)
    {
        _config = config;
        _http = customHandler != null ? new HttpClient(customHandler) : new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 15, 600));
        _accessToken = CredentialProtector.Unprotect(config.EncryptedGoogleAccessToken);
    }

    private string RootFolderId => _config.GoogleScope switch
    {
        GoogleDriveScope.Folder when !string.IsNullOrWhiteSpace(_config.GoogleFolderId) => _config.GoogleFolderId.Trim(),
        _ => "root"
    };

    public static async Task<(string AccessToken, string RefreshToken)> AuthorizeInteractiveAsync(
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Google OAuth Client ID is required.");

        var port = CloudflaredTunnelManager.AllocateEphemeralPort();
        var redirectUri = $"http://127.0.0.1:{port}/callback/";

        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var codeVerifier = Base64UrlEncode(verifierBytes);
        var challengeBytes = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        var codeChallenge = Base64UrlEncode(challengeBytes);

        var scope = Uri.EscapeDataString("https://www.googleapis.com/auth/drive");
        var authUrl =
            $"https://accounts.google.com/o/oauth2/v2/auth?response_type=code" +
            $"&client_id={Uri.EscapeDataString(clientId.Trim())}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&scope={scope}&access_type=offline&prompt=consent" +
            $"&code_challenge={codeChallenge}&code_challenge_method=S256";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        Process.Start(new ProcessStartInfo
        {
            FileName = authUrl,
            UseShellExecute = true
        });

        var contextTask = listener.GetContextAsync();
        var completed = await Task.WhenAny(contextTask, Task.Delay(TimeSpan.FromMinutes(3), cancellationToken));
        if (completed != contextTask)
            throw new TimeoutException("Timed out waiting for Google OAuth sign-in in browser.");

        var ctx = await contextTask;
        var code = ctx.Request.QueryString["code"];
        var html = "<html><body style='font-family:Segoe UI;background:#1e1e1e;color:#fff;padding:40px;text-align:center;'>" +
                   "<h2>Google Drive Connected</h2><p>You can close this browser tab and return to CloudDrive Mount.</p></body></html>";
        var buffer = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentLength64 = buffer.Length;
        await ctx.Response.OutputStream.WriteAsync(buffer, cancellationToken);
        ctx.Response.Close();
        listener.Stop();

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Google OAuth did not return an authorization code.");

        using var http = new HttpClient();
        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = clientId.Trim(),
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
            ["code_verifier"] = codeVerifier
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
            form["client_secret"] = clientSecret.Trim();

        using var resp = await http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        var access = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
        var refresh = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        return (access, refresh);
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static byte[] BuildInternetShortcutBytes(string url)
    {
        var content = $"[InternetShortcut]\r\nURL={url}\r\n";
        return Encoding.UTF8.GetBytes(content);
    }

    public static string BuildFallbackGoogleAppsUrl(string fileId, string? mimeType)
    {
        return mimeType?.ToLowerInvariant() switch
        {
            "application/vnd.google-apps.document" => $"https://docs.google.com/document/d/{fileId}/edit",
            "application/vnd.google-apps.spreadsheet" => $"https://docs.google.com/spreadsheets/d/{fileId}/edit",
            "application/vnd.google-apps.presentation" => $"https://docs.google.com/presentation/d/{fileId}/edit",
            "application/vnd.google-apps.drawing" => $"https://docs.google.com/drawings/d/{fileId}/edit",
            "application/vnd.google-apps.form" => $"https://docs.google.com/forms/d/{fileId}/edit",
            _ => $"https://drive.google.com/open?id={fileId}"
        };
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_accessToken) && DateTime.UtcNow < _tokenExpiryUtc)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
            return;
        }

        var refreshToken = CredentialProtector.Unprotect(_config.EncryptedGoogleRefreshToken);
        if (!string.IsNullOrWhiteSpace(refreshToken) && !string.IsNullOrWhiteSpace(_config.GoogleClientId))
        {
            var form = new Dictionary<string, string>
            {
                ["client_id"] = _config.GoogleClientId.Trim(),
                ["refresh_token"] = refreshToken.Trim(),
                ["grant_type"] = "refresh_token"
            };
            var secret = CredentialProtector.Unprotect(_config.EncryptedGoogleClientSecret);
            if (!string.IsNullOrWhiteSpace(secret))
                form["client_secret"] = secret.Trim();

            using var resp = await _http.PostAsync(TokenEndpoint, new FormUrlEncodedContent(form), cancellationToken);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("access_token", out var at))
                {
                    _accessToken = at.GetString() ?? string.Empty;
                    var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3500;
                    _tokenExpiryUtc = DateTime.UtcNow.AddSeconds(expiresIn - 60);
                    _config.EncryptedGoogleAccessToken = CredentialProtector.Protect(_accessToken);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(_accessToken))
            throw new InvalidOperationException("Google Drive requires an Access Token or OAuth Refresh Token.");

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        if (_tokenExpiryUtc == DateTime.MinValue)
            _tokenExpiryUtc = DateTime.UtcNow.AddMinutes(45);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        _pathToIdCache["/"] = RootFolderId;
        await GetQuotaAsync(cancellationToken);
    }

    public async Task<CloudVolumeQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureAuthenticatedAsync(cancellationToken);
            using var resp = await _http.GetAsync(AboutEndpoint, cancellationToken);
            if (!resp.IsSuccessStatusCode)
                return CloudVolumeQuota.DefaultUnbounded;

            var json = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("storageQuota", out var sq))
            {
                ulong limit = sq.TryGetProperty("limit", out var l) && ulong.TryParse(l.GetString(), out var lv)
                    ? lv
                    : CloudVolumeQuota.DefaultUnbounded.TotalBytes;
                ulong usage = sq.TryGetProperty("usage", out var u) && ulong.TryParse(u.GetString(), out var uv)
                    ? uv
                    : 0UL;
                ulong free = limit > usage ? limit - usage : 0UL;
                return new CloudVolumeQuota(limit, free);
            }
        }
        catch
        {
            // Fallback to default unbounded quota
        }

        return CloudVolumeQuota.DefaultUnbounded;
    }

    public async Task<CloudItemInfo?> GetItemAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        if (norm == "/")
            return new CloudItemInfo("", "/", true, 0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow, RootFolderId);

        var parentPath = VirtualPathHelper.GetParentPath(norm);
        var name = VirtualPathHelper.GetFileName(norm);
        var children = await ListDirectoryAsync(parentPath, cancellationToken);
        return children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<string?> ResolveFolderIdAsync(string virtualPath, CancellationToken cancellationToken)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        if (norm == "/")
            return RootFolderId;

        if (_pathToIdCache.TryGetValue(norm, out var cachedId))
            return cachedId;

        var item = await GetItemAsync(norm, cancellationToken);
        if (item?.NativeId != null)
        {
            _pathToIdCache[norm] = item.NativeId;
            return item.NativeId;
        }

        return null;
    }

    public async Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var normDir = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);

        string query;
        if (normDir == "/" && _config.GoogleScope == GoogleDriveScope.SharedWithMe)
        {
            query = "sharedWithMe = true and trashed = false";
        }
        else
        {
            var folderId = await ResolveFolderIdAsync(normDir, cancellationToken);
            if (string.IsNullOrEmpty(folderId))
                return Array.Empty<CloudItemInfo>();
            query = $"'{EscapeQueryLiteral(folderId)}' in parents and trashed = false";
        }

        var url = $"{FilesEndpoint}?q={Uri.EscapeDataString(query)}" +
                  "&fields=files(id,name,mimeType,size,createdTime,modifiedTime,webViewLink,shortcutDetails)&pageSize=1000";

        using var resp = await _http.GetAsync(url, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var results = new List<CloudItemInfo>();
        if (!doc.RootElement.TryGetProperty("files", out var filesElem))
            return results;

        foreach (var f in filesElem.EnumerateArray())
        {
            var id = f.GetProperty("id").GetString() ?? string.Empty;
            var name = f.GetProperty("name").GetString() ?? string.Empty;
            if (string.IsNullOrEmpty(name))
                continue;

            var mime = f.TryGetProperty("mimeType", out var mt) ? mt.GetString() ?? string.Empty : string.Empty;

            // Handle Drive folder shortcuts by resolving to target folder ID
            if (string.Equals(mime, ShortcutMimeType, StringComparison.OrdinalIgnoreCase) &&
                f.TryGetProperty("shortcutDetails", out var sd) &&
                sd.TryGetProperty("targetMimeType", out var tmt) &&
                string.Equals(tmt.GetString(), FolderMimeType, StringComparison.OrdinalIgnoreCase) &&
                sd.TryGetProperty("targetId", out var tid) &&
                !string.IsNullOrWhiteSpace(tid.GetString()))
            {
                id = tid.GetString()!;
                mime = FolderMimeType;
            }

            var isDir = string.Equals(mime, FolderMimeType, StringComparison.OrdinalIgnoreCase);
            bool isGoogleWebDoc = !isDir && mime.StartsWith(GoogleAppsPrefix, StringComparison.OrdinalIgnoreCase);

            ulong size = 0;
            if (isGoogleWebDoc)
            {
                if (!name.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
                    name += ".url";

                var webLink = f.TryGetProperty("webViewLink", out var wvl) && !string.IsNullOrWhiteSpace(wvl.GetString())
                    ? wvl.GetString()!
                    : BuildFallbackGoogleAppsUrl(id, mime);

                var shortcutBytes = BuildInternetShortcutBytes(webLink);
                size = (ulong)shortcutBytes.Length;

                var shortcutVirtPath = normDir == "/" ? $"/{name}" : $"{normDir}/{name}";
                _webShortcutContentCache[shortcutVirtPath] = shortcutBytes;
            }
            else if (!isDir && f.TryGetProperty("size", out var sz) && ulong.TryParse(sz.GetString(), out var parsedSize))
            {
                size = parsedSize;
            }

            var created = f.TryGetProperty("createdTime", out var ct) && DateTime.TryParse(ct.GetString(), out var cv)
                ? cv.ToUniversalTime()
                : DateTime.UtcNow;
            var modified = f.TryGetProperty("modifiedTime", out var mtime) && DateTime.TryParse(mtime.GetString(), out var mv)
                ? mv.ToUniversalTime()
                : created;

            var childVirt = normDir == "/" ? $"/{name}" : $"{normDir}/{name}";
            _pathToIdCache[childVirt] = id;
            results.Add(new CloudItemInfo(name, childVirt, isDir, size, created, modified, modified, id));
        }

        return results;
    }

    public async Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);

        // If this is a Google Docs/Sheets/Slides/Link (.url), serve the [InternetShortcut] content directly
        if (_webShortcutContentCache.TryGetValue(norm, out var shortcutBytes))
        {
            await destination.WriteAsync(shortcutBytes, cancellationToken);
            return;
        }

        await EnsureAuthenticatedAsync(cancellationToken);
        var item = await GetItemAsync(norm, cancellationToken)
            ?? throw new FileNotFoundException($"Google Drive file not found: {norm}");

        // Check again in case GetItemAsync populated _webShortcutContentCache via ListDirectoryAsync
        if (_webShortcutContentCache.TryGetValue(norm, out shortcutBytes))
        {
            await destination.WriteAsync(shortcutBytes, cancellationToken);
            return;
        }

        var url = $"{FilesEndpoint}/{Uri.EscapeDataString(item.NativeId!)}?alt=media";
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await resp.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        var existing = await GetItemAsync(norm, cancellationToken);
        source.Position = 0;

        if (existing?.NativeId != null)
        {
            using var content = new StreamContent(source);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var req = new HttpRequestMessage(HttpMethod.Patch, $"{UploadEndpoint}/{Uri.EscapeDataString(existing.NativeId)}?uploadType=media")
            {
                Content = content
            };
            using var resp = await _http.SendAsync(req, cancellationToken);
            resp.EnsureSuccessStatusCode();
        }
        else
        {
            var parentPath = VirtualPathHelper.GetParentPath(norm);
            var parentId = await ResolveFolderIdAsync(parentPath, cancellationToken) ?? RootFolderId;
            var fileName = VirtualPathHelper.GetFileName(norm);

            var metaObj = new
            {
                name = fileName,
                parents = new[] { parentId }
            };
            var metaJson = JsonSerializer.Serialize(metaObj);
            using var createResp = await _http.PostAsync(
                $"{FilesEndpoint}?fields=id",
                new StringContent(metaJson, Encoding.UTF8, "application/json"),
                cancellationToken);
            createResp.EnsureSuccessStatusCode();

            using var createDoc = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync(cancellationToken));
            var fileId = createDoc.RootElement.GetProperty("id").GetString()!;
            _pathToIdCache[norm] = fileId;

            if (source.Length > 0)
            {
                using var mediaContent = new StreamContent(source);
                mediaContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"{UploadEndpoint}/{Uri.EscapeDataString(fileId)}?uploadType=media")
                {
                    Content = mediaContent
                };
                using var patchResp = await _http.SendAsync(patchReq, cancellationToken);
                patchResp.EnsureSuccessStatusCode();
            }
        }
    }

    public async Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);
        var existing = await GetItemAsync(norm, cancellationToken);
        if (existing != null)
            return;

        var parentPath = VirtualPathHelper.GetParentPath(norm);
        var parentId = await ResolveFolderIdAsync(parentPath, cancellationToken) ?? RootFolderId;
        var name = VirtualPathHelper.GetFileName(norm);

        var metaObj = new
        {
            name,
            mimeType = FolderMimeType,
            parents = new[] { parentId }
        };
        var metaJson = JsonSerializer.Serialize(metaObj);
        using var resp = await _http.PostAsync(
            $"{FilesEndpoint}?fields=id",
            new StringContent(metaJson, Encoding.UTF8, "application/json"),
            cancellationToken);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cancellationToken));
        if (doc.RootElement.TryGetProperty("id", out var idElem))
            _pathToIdCache[norm] = idElem.GetString()!;
    }

    public async Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        await DeleteByVirtualPathAsync(virtualPath, cancellationToken);
    }

    public async Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        await DeleteByVirtualPathAsync(virtualDirectoryPath, cancellationToken);
    }

    private async Task DeleteByVirtualPathAsync(string virtualPath, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        var item = await GetItemAsync(norm, cancellationToken);
        if (item?.NativeId == null)
            return;

        using var resp = await _http.DeleteAsync($"{FilesEndpoint}/{Uri.EscapeDataString(item.NativeId)}", cancellationToken);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
        _pathToIdCache.TryRemove(norm, out _);
        _webShortcutContentCache.TryRemove(norm, out _);
    }

    public async Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var normOld = VirtualPathHelper.NormalizeVirtualPath(oldVirtualPath);
        var normNew = VirtualPathHelper.NormalizeVirtualPath(newVirtualPath);

        var sourceItem = await GetItemAsync(normOld, cancellationToken)
            ?? throw new FileNotFoundException($"Google Drive item not found: {normOld}");

        if (overwrite)
        {
            var destExisting = await GetItemAsync(normNew, cancellationToken);
            if (destExisting != null)
                await DeleteByVirtualPathAsync(normNew, cancellationToken);
        }

        var oldParent = VirtualPathHelper.GetParentPath(normOld);
        var newParent = VirtualPathHelper.GetParentPath(normNew);
        var newName = VirtualPathHelper.GetFileName(normNew);

        // If renaming a synthetic Google Docs .url shortcut, strip the .url suffix on Google Drive
        if (_webShortcutContentCache.ContainsKey(normOld) && newName.EndsWith(".url", StringComparison.OrdinalIgnoreCase))
        {
            newName = newName[..^4];
        }

        var queryParams = new List<string>();
        if (!string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase))
        {
            var oldParentId = await ResolveFolderIdAsync(oldParent, cancellationToken) ?? RootFolderId;
            var newParentId = await ResolveFolderIdAsync(newParent, cancellationToken) ?? RootFolderId;
            queryParams.Add($"addParents={Uri.EscapeDataString(newParentId)}");
            queryParams.Add($"removeParents={Uri.EscapeDataString(oldParentId)}");
        }

        var querySuffix = queryParams.Count > 0 ? "?" + string.Join("&", queryParams) : string.Empty;
        var patchUrl = $"{FilesEndpoint}/{Uri.EscapeDataString(sourceItem.NativeId!)}{querySuffix}";
        var body = JsonSerializer.Serialize(new { name = newName });

        using var req = new HttpRequestMessage(HttpMethod.Patch, patchUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await _http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();

        _pathToIdCache.TryRemove(normOld, out _);
        _pathToIdCache[normNew] = sourceItem.NativeId!;
        if (_webShortcutContentCache.TryRemove(normOld, out var shortcutBytes))
            _webShortcutContentCache[normNew] = shortcutBytes;
    }

    private static string EscapeQueryLiteral(string value) => value.Replace("\\", "\\\\").Replace("'", "\\'");

    public void Dispose()
    {
        _http.Dispose();
    }
}
