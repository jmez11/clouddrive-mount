using System.Text.Json;
using CloudDriveMount.Core.Models;

namespace CloudDriveMount.Core.Services;

public sealed class VaultMetadata
{
    public string VaultId { get; set; } = string.Empty;
    public string SaltBase64 { get; set; } = string.Empty;
    public int Iterations { get; set; } = 300_000;
    public string KeyCheck { get; set; } = string.Empty;
}

internal sealed class VaultConfigEnvelope
{
    public int Version { get; set; } = 1;
    public VaultMetadata? Vault { get; set; }
    public List<DriveConfig> Drives { get; set; } = new();
}

public class ConfigStore
{
    private string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigStore(string? customPath = null, bool forcePortable = false)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _configPath = customPath;
            IsPortableMode = string.Equals(
                Path.GetFullPath(_configPath),
                Path.GetFullPath(GetPortableConfigPath()),
                StringComparison.OrdinalIgnoreCase);
        }
        else if (forcePortable || File.Exists(GetPortableConfigPath()))
        {
            _configPath = GetPortableConfigPath();
            IsPortableMode = true;
        }
        else
        {
            _configPath = GetSystemConfigPath();
            IsPortableMode = false;
        }
    }

    public string ConfigPath => _configPath;
    public bool IsPortableMode { get; private set; }
    public VaultMetadata? CurrentVault { get; set; }

    public static string GetPortableConfigPath() =>
        Path.Combine(AppContext.BaseDirectory, "drives.json");

    public static string GetSystemConfigPath()
    {
        var baseDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CloudDriveMount");
        try
        {
            Directory.CreateDirectory(baseDir);
        }
        catch
        {
            baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "CloudDriveMount");
            Directory.CreateDirectory(baseDir);
        }
        return Path.Combine(baseDir, "drives.json");
    }

    public VaultMetadata? LoadVaultMetadata()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                CurrentVault = null;
                return null;
            }

            var json = File.ReadAllText(_configPath).TrimStart();
            if (json.StartsWith('{'))
            {
                var envelope = JsonSerializer.Deserialize<VaultConfigEnvelope>(json, JsonOptions);
                CurrentVault = envelope?.Vault;
                return CurrentVault;
            }

            CurrentVault = null;
            return null;
        }
        catch
        {
            CurrentVault = null;
            return null;
        }
    }

    public List<DriveConfig> LoadDrives()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new List<DriveConfig>();

            var json = File.ReadAllText(_configPath).TrimStart();
            if (json.StartsWith('{'))
            {
                var envelope = JsonSerializer.Deserialize<VaultConfigEnvelope>(json, JsonOptions);
                if (envelope != null)
                {
                    CurrentVault = envelope.Vault;
                    return envelope.Drives ?? new List<DriveConfig>();
                }
                return new List<DriveConfig>();
            }

            CurrentVault = null;
            return JsonSerializer.Deserialize<List<DriveConfig>>(json, JsonOptions) ?? new List<DriveConfig>();
        }
        catch
        {
            return new List<DriveConfig>();
        }
    }

    public void SaveDrives(IEnumerable<DriveConfig> drives)
    {
        var dir = Path.GetDirectoryName(_configPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        string json;
        if (CurrentVault != null)
        {
            var envelope = new VaultConfigEnvelope
            {
                Version = 1,
                Vault = CurrentVault,
                Drives = drives.ToList()
            };
            json = JsonSerializer.Serialize(envelope, JsonOptions);
        }
        else
        {
            json = JsonSerializer.Serialize(drives.ToList(), JsonOptions);
        }

        File.WriteAllText(_configPath, json);
    }

    public void SwitchMode(bool portable, IEnumerable<DriveConfig> drives, bool deletePreviousPortableFile = false)
    {
        var previousPath = _configPath;
        _configPath = portable ? GetPortableConfigPath() : GetSystemConfigPath();
        IsPortableMode = portable;
        SaveDrives(drives);

        if (!portable && deletePreviousPortableFile &&
            !string.Equals(previousPath, _configPath, StringComparison.OrdinalIgnoreCase) &&
            File.Exists(previousPath))
        {
            try { File.Delete(previousPath); } catch { }
        }
    }
}
