using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Xml.Linq;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.Core.Providers;

public sealed class WebDavStorageProvider : ICloudStorageProvider
{
    private static readonly XNamespace DavNs = "DAV:";
    private static readonly string PropFindXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:propfind xmlns:d=\"DAV:\">" +
        "<d:prop><d:displayname/><d:resourcetype/><d:getcontentlength/><d:getlastmodified/><d:creationdate/>" +
        "<d:quota-available-bytes/><d:quota-used-bytes/></d:prop>" +
        "</d:propfind>";

    private static readonly string LockInfoXml =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
        "<d:lockinfo xmlns:d=\"DAV:\">" +
        "<d:lockscope><d:exclusive/></d:lockscope>" +
        "<d:locktype><d:write/></d:locktype>" +
        "<d:owner><d:href>CloudDriveMount</d:href></d:owner>" +
        "</d:lockinfo>";

    private readonly DriveConfig _config;
    private readonly HttpClient _http;
    private readonly string _baseUri;
    private readonly ConcurrentDictionary<string, string> _activeLockTokens = new(StringComparer.OrdinalIgnoreCase);

    public WebDavStorageProvider(DriveConfig config, HttpMessageHandler? customHandler = null)
    {
        _config = config;

        var useTunnelPort = config.UseProxy &&
                            config.ProxyMode is CloudflaredProxyMode.AccessTcpTunnel or CloudflaredProxyMode.BothAccessTcpAndHeaders &&
                            config.ActiveProxyPort.HasValue;

        var scheme = useTunnelPort ? "http" : (config.UseTls ? "https" : "http");
        var host = useTunnelPort ? "127.0.0.1" : config.Host.Trim();
        var port = useTunnelPort
            ? config.ActiveProxyPort!.Value
            : (config.Port > 0 ? config.Port : (config.UseTls ? 443 : 80));

        _baseUri = $"{scheme}://{host}:{port}";

        var handler = customHandler ?? CreateDefaultHandler(config);
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(Math.Clamp(config.TimeoutSeconds, 10, 600))
        };

        if (!config.Anonymous && !string.IsNullOrWhiteSpace(config.Username))
        {
            var password = CredentialProtector.Unprotect(config.EncryptedPassword);
            var rawAuth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.Username.Trim()}:{password}"));
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", rawAuth);
        }

        // Native Cloudflare Access Service Token headers
        foreach (var kv in CloudflaredTunnelManager.GetServiceTokenHeaders(config))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation(kv.Key, kv.Value);
        }
    }

    private static HttpMessageHandler CreateDefaultHandler(DriveConfig config)
    {
        var handler = new SocketsHttpHandler
        {
            PreAuthenticate = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };

        handler.SslOptions.EnabledSslProtocols = config.TlsVersion == TlsProtocolVersion.Tls13
            ? SslProtocols.Tls13 | SslProtocols.Tls12
            : SslProtocols.Tls12;
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;

        if (!config.Anonymous && !string.IsNullOrWhiteSpace(config.Username))
        {
            var password = CredentialProtector.Unprotect(config.EncryptedPassword);
            handler.Credentials = new NetworkCredential(config.Username.Trim(), password);
        }

        return handler;
    }

    private Uri BuildTargetUri(string virtualPath, bool trailingSlash = false)
    {
        var remote = VirtualPathHelper.CombineRemotePath(_config.RemotePath, virtualPath);
        var segments = remote.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var pathPart = "/" + string.Join("/", segments);
        if (trailingSlash && !pathPart.EndsWith('/'))
            pathPart += "/";

        return new Uri(_baseUri + pathPart);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildTargetUri("/"))
        {
            Content = new StringContent(PropFindXml, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", "0");

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
        {
            throw new InvalidOperationException($"WebDAV connection failed with HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}).");
        }
    }

    public async Task<CloudVolumeQuota> GetQuotaAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildTargetUri("/"))
            {
                Content = new StringContent(PropFindXml, Encoding.UTF8, "application/xml")
            };
            req.Headers.Add("Depth", "0");

            using var resp = await _http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
                return CloudVolumeQuota.DefaultUnbounded;

            var xml = await resp.Content.ReadAsStringAsync(cancellationToken);
            var doc = XDocument.Parse(xml);
            var availStr = doc.Descendants(DavNs + "quota-available-bytes").FirstOrDefault()?.Value;
            var usedStr = doc.Descendants(DavNs + "quota-used-bytes").FirstOrDefault()?.Value;

            if (long.TryParse(availStr, out var avail) && avail > 0)
            {
                long.TryParse(usedStr, out var used);
                var free = (ulong)Math.Max(0, avail);
                var total = free + (ulong)Math.Max(0, used);
                return new CloudVolumeQuota(total, free);
            }
        }
        catch
        {
            // Fall through to default unbounded quota
        }

        return CloudVolumeQuota.DefaultUnbounded;
    }

    public async Task<CloudItemInfo?> GetItemAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        if (norm == "/")
            return new CloudItemInfo("", "/", true, 0, DateTime.UtcNow, DateTime.UtcNow, DateTime.UtcNow);

        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildTargetUri(norm))
        {
            Content = new StringContent(PropFindXml, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", "0");

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            return null;

        var xml = await resp.Content.ReadAsStringAsync(cancellationToken);
        var items = ParsePropFindResponse(xml, norm, includeSelf: true);
        return items.FirstOrDefault();
    }

    public async Task<IReadOnlyList<CloudItemInfo>> ListDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        var normDir = VirtualPathHelper.NormalizeVirtualPath(virtualDirectoryPath);
        using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), BuildTargetUri(normDir, trailingSlash: true))
        {
            Content = new StringContent(PropFindXml, Encoding.UTF8, "application/xml")
        };
        req.Headers.Add("Depth", "1");

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            return Array.Empty<CloudItemInfo>();

        var xml = await resp.Content.ReadAsStringAsync(cancellationToken);
        return ParsePropFindResponse(xml, normDir, includeSelf: false);
    }

    public static IReadOnlyList<CloudItemInfo> ParsePropFindResponse(string xmlContent, string queriedVirtualPath, bool includeSelf)
    {
        var results = new List<CloudItemInfo>();
        if (string.IsNullOrWhiteSpace(xmlContent))
            return results;

        var doc = XDocument.Parse(xmlContent);
        var responses = doc.Descendants(DavNs + "response").ToList();
        var normQuery = VirtualPathHelper.NormalizeVirtualPath(queriedVirtualPath);

        for (int i = 0; i < responses.Count; i++)
        {
            if (!includeSelf && i == 0 && responses.Count > 1)
                continue;

            var r = responses[i];
            var href = r.Element(DavNs + "href")?.Value ?? string.Empty;
            var decodedHref = Uri.UnescapeDataString(href);
            if (Uri.TryCreate(decodedHref, UriKind.Absolute, out var absUri))
                decodedHref = absUri.LocalPath;

            var cleanHref = decodedHref.TrimEnd('/');
            var name = Path.GetFileName(cleanHref);
            if (string.IsNullOrEmpty(name) && !includeSelf)
                continue;

            var prop = r.Descendants(DavNs + "prop").FirstOrDefault();
            if (prop == null)
                continue;

            var isDir = prop.Element(DavNs + "resourcetype")?.Element(DavNs + "collection") != null;
            ulong size = 0;
            if (!isDir && ulong.TryParse(prop.Element(DavNs + "getcontentlength")?.Value, out var parsedLen))
                size = parsedLen;

            var lastMod = DateTime.UtcNow;
            if (DateTime.TryParse(prop.Element(DavNs + "getlastmodified")?.Value, out var parsedMod))
                lastMod = parsedMod.ToUniversalTime();

            var created = lastMod;
            if (DateTime.TryParse(prop.Element(DavNs + "creationdate")?.Value, out var parsedCreated))
                created = parsedCreated.ToUniversalTime();

            var fullVirt = includeSelf
                ? normQuery
                : (normQuery == "/" ? $"/{name}" : $"{normQuery}/{name}");

            // Avoid duplicating self if server returned non-first self entry
            if (!includeSelf && string.Equals(fullVirt, normQuery, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new CloudItemInfo(name, fullVirt, isDir, size, created, lastMod, lastMod));
        }

        return results;
    }

    public async Task DownloadFileAsync(string virtualPath, Stream destination, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.GetAsync(BuildTargetUri(virtualPath), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        await resp.Content.CopyToAsync(destination, cancellationToken);
    }

    public async Task UploadFileAsync(string virtualPath, Stream source, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        source.Position = 0;
        using var content = new StreamContent(source);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var req = new HttpRequestMessage(HttpMethod.Put, BuildTargetUri(norm))
        {
            Content = content
        };
        AttachLockTokenHeaderIfPresent(req, norm);
        using var resp = await _http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
    }

    public async Task CreateDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(new HttpMethod("MKCOL"), BuildTargetUri(virtualDirectoryPath, trailingSlash: true));
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MethodNotAllowed)
            resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteFileAsync(string virtualPath, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        using var req = new HttpRequestMessage(HttpMethod.Delete, BuildTargetUri(norm));
        AttachLockTokenHeaderIfPresent(req, norm);
        using var resp = await _http.SendAsync(req, cancellationToken);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }

    public async Task DeleteDirectoryAsync(string virtualDirectoryPath, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.DeleteAsync(BuildTargetUri(virtualDirectoryPath, trailingSlash: true), cancellationToken);
        if (resp.StatusCode != HttpStatusCode.NotFound)
            resp.EnsureSuccessStatusCode();
    }

    public async Task RenameAsync(string oldVirtualPath, string newVirtualPath, bool overwrite, CancellationToken cancellationToken = default)
    {
        var normOld = VirtualPathHelper.NormalizeVirtualPath(oldVirtualPath);
        using var req = new HttpRequestMessage(new HttpMethod("MOVE"), BuildTargetUri(normOld));
        req.Headers.Add("Destination", BuildTargetUri(newVirtualPath).ToString());
        req.Headers.Add("Overwrite", overwrite ? "T" : "F");
        AttachLockTokenHeaderIfPresent(req, normOld);
        using var resp = await _http.SendAsync(req, cancellationToken);
        resp.EnsureSuccessStatusCode();
    }

    public async Task<string?> LockFileAsync(string virtualPath, int timeoutSeconds = 3600, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        using var req = new HttpRequestMessage(new HttpMethod("LOCK"), BuildTargetUri(norm))
        {
            Content = new StringContent(LockInfoXml, Encoding.UTF8, "application/xml")
        };
        req.Headers.TryAddWithoutValidation("Timeout", $"Second-{Math.Clamp(timeoutSeconds, 60, 86400)}");

        using var resp = await _http.SendAsync(req, cancellationToken);
        if (resp.StatusCode == HttpStatusCode.Locked)
        {
            // HResult 0x80070020 = ERROR_SHARING_VIOLATION -> WinFsp maps to STATUS_SHARING_VIOLATION
            throw new IOException("Remote WebDAV resource is locked by another user.", unchecked((int)0x80070020));
        }

        if (!resp.IsSuccessStatusCode)
            return null;

        string? token = null;
        if (resp.Headers.TryGetValues("Lock-Token", out var headerVals))
        {
            token = headerVals.FirstOrDefault()?.Trim('<', '>', ' ');
        }

        if (string.IsNullOrEmpty(token))
        {
            var xml = await resp.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(xml))
            {
                try
                {
                    var doc = XDocument.Parse(xml);
                    token = doc.Descendants(DavNs + "locktoken")
                        .Elements(DavNs + "href")
                        .FirstOrDefault()?.Value.Trim('<', '>', ' ');
                }
                catch { }
            }
        }

        if (!string.IsNullOrEmpty(token))
        {
            _activeLockTokens[norm] = token;
        }
        return token;
    }

    public async Task UnlockFileAsync(string virtualPath, string lockToken, CancellationToken cancellationToken = default)
    {
        var norm = VirtualPathHelper.NormalizeVirtualPath(virtualPath);
        _activeLockTokens.TryRemove(norm, out _);

        if (string.IsNullOrWhiteSpace(lockToken))
            return;

        var formatted = lockToken.StartsWith('<') ? lockToken : $"<{lockToken}>";
        using var req = new HttpRequestMessage(new HttpMethod("UNLOCK"), BuildTargetUri(norm));
        req.Headers.TryAddWithoutValidation("Lock-Token", formatted);
        using var resp = await _http.SendAsync(req, cancellationToken);
    }

    private void AttachLockTokenHeaderIfPresent(HttpRequestMessage req, string normalizedVirtualPath)
    {
        if (_activeLockTokens.TryGetValue(normalizedVirtualPath, out var token) && !string.IsNullOrWhiteSpace(token))
        {
            var formatted = token.StartsWith('<') ? token : $"<{token}>";
            req.Headers.TryAddWithoutValidation("If", $"({formatted})");
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
