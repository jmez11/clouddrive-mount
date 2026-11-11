using System.Text.Json;
using CloudDriveMount.Core.Models;

namespace CloudDriveMount.Core.Services;

public class ConfigStore
{
    private readonly string _configPath;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public ConfigStore(string? customPath = null)
    {
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            _configPath = customPath;
        }
        else
        {
            // Portable mode: if drives.json exists next to CloudDriveMount.exe, use it directly
            var portablePath = Path.Combine(AppContext.BaseDirectory, "drives.json");
            if (File.Exists(portablePath))
            {
                _configPath = portablePath;
                return;
            }

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
            _configPath = Path.Combine(baseDir, "drives.json");
        }
    }

    public string ConfigPath => _configPath;

    public List<DriveConfig> LoadDrives()
    {
        try
        {
            if (!File.Exists(_configPath))
                return new List<DriveConfig>();

            var json = File.ReadAllText(_configPath);
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

        var json = JsonSerializer.Serialize(drives.ToList(), JsonOptions);
        File.WriteAllText(_configPath, json);
    }
}
