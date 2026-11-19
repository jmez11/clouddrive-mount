using System.Reflection;
using System.Text.Json;

namespace CloudDriveMount.Core.Providers;

/// <summary>
/// Provides Google Drive OAuth 2.0 client credentials bundled at build time or injected via environment.
/// </summary>
public static class GoogleDriveClientConfig
{
    private static (string ClientId, string ClientSecret)? _cached;

    public static (string ClientId, string ClientSecret) GetCredentials()
    {
        if (_cached.HasValue && !string.IsNullOrWhiteSpace(_cached.Value.ClientId))
            return _cached.Value;

        // 1. Check environment variable GOOGLE_DRIVE_JSON
        var envJson = Environment.GetEnvironmentVariable("GOOGLE_DRIVE_JSON");
        if (!string.IsNullOrWhiteSpace(envJson))
        {
            var parsed = ParseClientJson(envJson);
            if (!string.IsNullOrWhiteSpace(parsed.ClientId))
            {
                _cached = parsed;
                return parsed;
            }
        }

        // 2. Check embedded resource 'google_drive_client.json'
        var asm = typeof(GoogleDriveClientConfig).Assembly;
        var resourceName = asm.GetManifestResourceNames()
            .FirstOrDefault(r => r.EndsWith("google_drive_client.json", StringComparison.OrdinalIgnoreCase));
        if (resourceName != null)
        {
            using var stream = asm.GetManifestResourceStream(resourceName);
            if (stream != null)
            {
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var parsed = ParseClientJson(json);
                if (!string.IsNullOrWhiteSpace(parsed.ClientId))
                {
                    _cached = parsed;
                    return parsed;
                }
            }
        }

        // 3. Check local files next to application or in Resources folder
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "google_drive_client.json"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "google_drive_client.json"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "CloudDriveMount.Core", "Resources", "google_drive_client.json")
        ];

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var parsed = ParseClientJson(json);
                    if (!string.IsNullOrWhiteSpace(parsed.ClientId))
                    {
                        _cached = parsed;
                        return parsed;
                    }
                }
                catch { }
            }
        }

        return (string.Empty, string.Empty);
    }

    public static (string ClientId, string ClientSecret) ParseClientJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return (string.Empty, string.Empty);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("installed", out var installed) || root.TryGetProperty("web", out installed))
            {
                var id = installed.TryGetProperty("client_id", out var cid) ? cid.GetString() ?? "" : "";
                var secret = installed.TryGetProperty("client_secret", out var csec) ? csec.GetString() ?? "" : "";
                return (id.Trim(), secret.Trim());
            }

            if (root.TryGetProperty("client_id", out var directId))
            {
                var id = directId.GetString() ?? "";
                var secret = root.TryGetProperty("client_secret", out var directSec) ? directSec.GetString() ?? "" : "";
                return (id.Trim(), secret.Trim());
            }
        }
        catch { }

        return (string.Empty, string.Empty);
    }

    /// <summary>
    /// Resets cached credentials (primarily for unit testing).
    /// </summary>
    public static void ResetCache()
    {
        _cached = null;
    }
}
