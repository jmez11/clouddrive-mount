using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace CloudDriveMount.Core.Models;

public enum StorageProtocol
{
    Sftp,
    Ftp,
    WebDav,
    GoogleDrive,
    CloudflareR2
}

public enum LocalDiskScope
{
    Private,
    Public
}

public enum GoogleDriveScope
{
    MyDrive,
    SharedWithMe,
    Folder
}

public enum CloudflaredProxyMode
{
    None,
    AccessTcpTunnel,
    ServiceTokenHeaders,
    BothAccessTcpAndHeaders
}

public enum TlsProtocolVersion
{
    Tls12,
    Tls13
}

public enum DriveConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

public class DriveConfig : INotifyPropertyChanged
{
    private DriveConnectionState _state = DriveConnectionState.Disconnected;
    private string _statusMessage = "Disconnected";
    private int? _activeProxyPort;

    public Guid Id { get; set; } = Guid.NewGuid();
    public StorageProtocol Protocol { get; set; } = StorageProtocol.Sftp;

    /// <summary>
    /// Drive letter such as "W:", "X:", "Y:", "Z:".
    /// </summary>
    public string DriveLetter { get; set; } = "W:";

    /// <summary>
    /// Custom volume label displayed in Windows Explorer.
    /// </summary>
    public string Label { get; set; } = "SFTP";

    // Common Mount Options
    public bool ReconnectAtStart { get; set; } = true;
    public bool SignInOnEachConnection { get; set; } = false;
    public bool IsLocalDisk { get; set; } = false;
    public LocalDiskScope LocalDiskVisibility { get; set; } = LocalDiskScope.Public;
    public bool UseProxy { get; set; } = false;
    public bool ReadOnly { get; set; } = false;
    public bool FileLock { get; set; } = false;

    // Native cloudflared Proxy Configuration
    public CloudflaredProxyMode ProxyMode { get; set; } = CloudflaredProxyMode.AccessTcpTunnel;
    public string CloudflaredBinaryPath { get; set; } = "cloudflared";
    public string CloudflaredAccessHostname { get; set; } = string.Empty;
    public string CfAccessClientId { get; set; } = string.Empty;
    public string EncryptedCfAccessClientSecret { get; set; } = string.Empty;

    // SFTP / FTP / WebDAV Address & Auth
    public bool UseTls { get; set; } = true;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string RemotePath { get; set; } = "/";
    public string Username { get; set; } = string.Empty;
    public string EncryptedPassword { get; set; } = string.Empty;
    public bool Anonymous { get; set; } = false;
    public string PrivateKeyPath { get; set; } = string.Empty;
    public string EncryptedPrivateKeyPassphrase { get; set; } = string.Empty;
    public TlsProtocolVersion TlsVersion { get; set; } = TlsProtocolVersion.Tls12;
    public int TimeoutSeconds { get; set; } = 100;

    // Cloudflare R2 Settings
    public string R2AccountId { get; set; } = string.Empty;
    public string R2AccessKeyId { get; set; } = string.Empty;
    public string EncryptedR2SecretAccessKey { get; set; } = string.Empty;
    public string R2BucketName { get; set; } = string.Empty;

    // Google Drive Settings
    public GoogleDriveScope GoogleScope { get; set; } = GoogleDriveScope.MyDrive;
    public string GoogleFolderId { get; set; } = string.Empty;
    public string GoogleClientId { get; set; } = string.Empty;
    public string EncryptedGoogleClientSecret { get; set; } = string.Empty;
    public string EncryptedGoogleRefreshToken { get; set; } = string.Empty;
    public string EncryptedGoogleAccessToken { get; set; } = string.Empty;

    [JsonIgnore]
    public DriveConnectionState State
    {
        get => _state;
        set
        {
            if (_state != value)
            {
                _state = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsConnected));
                OnPropertyChanged(nameof(IsBusy));
            }
        }
    }

    [JsonIgnore]
    public bool IsConnected => _state == DriveConnectionState.Connected;

    [JsonIgnore]
    public bool IsBusy => _state == DriveConnectionState.Connecting;

    [JsonIgnore]
    public string StatusMessage
    {
        get => _statusMessage;
        set
        {
            if (_statusMessage != value)
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public int? ActiveProxyPort
    {
        get => _activeProxyPort;
        set
        {
            if (_activeProxyPort != value)
            {
                _activeProxyPort = value;
                OnPropertyChanged();
            }
        }
    }

    [JsonIgnore]
    public string ProtocolDisplayName => Protocol switch
    {
        StorageProtocol.Sftp => "SFTP",
        StorageProtocol.Ftp => "FTP",
        StorageProtocol.WebDav => "WebDAV",
        StorageProtocol.GoogleDrive => "Google Drive",
        StorageProtocol.CloudflareR2 => "Cloudflare R2",
        _ => Protocol.ToString()
    };

    [JsonIgnore]
    public string EndpointSummary => Protocol switch
    {
        StorageProtocol.CloudflareR2 => $"r2://{R2AccountId}/{R2BucketName}",
        StorageProtocol.GoogleDrive => $"gdrive://{GoogleScope}" + (GoogleScope == GoogleDriveScope.Folder ? $"/{GoogleFolderId}" : ""),
        StorageProtocol.WebDav => $"{(UseTls ? "https" : "http")}://{Host}:{Port}{ NormalizePath(RemotePath) }",
        StorageProtocol.Ftp => $"{(UseTls ? "ftps" : "ftp")}://{Host}:{Port}{ NormalizePath(RemotePath) }",
        StorageProtocol.Sftp => $"sftp://{Host}:{Port}{ NormalizePath(RemotePath) }",
        _ => Host
    };

    [JsonIgnore]
    public string MountModeBadge
    {
        get
        {
            var mode = IsLocalDisk ? $"Local Disk ({LocalDiskVisibility})" : "Network Drive";
            if (ReadOnly) mode += " • R/O";
            if (FileLock) mode += " • File Lock";
            if (UseProxy && ProxyMode != CloudflaredProxyMode.None) mode += " • cloudflared";
            return mode;
        }
    }

    public char NormalizedDriveLetterChar
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DriveLetter)) return 'W';
            return char.ToUpperInvariant(DriveLetter.Trim()[0]);
        }
    }

    public string NormalizedMountPoint => $"{NormalizedDriveLetterChar}:";

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "/";
        return path.StartsWith('/') ? path : "/" + path;
    }

    public DriveConfig Clone()
    {
        return new DriveConfig
        {
            Id = Id,
            Protocol = Protocol,
            DriveLetter = DriveLetter,
            Label = Label,
            ReconnectAtStart = ReconnectAtStart,
            SignInOnEachConnection = SignInOnEachConnection,
            IsLocalDisk = IsLocalDisk,
            LocalDiskVisibility = LocalDiskVisibility,
            UseProxy = UseProxy,
            ReadOnly = ReadOnly,
            FileLock = FileLock,
            ProxyMode = ProxyMode,
            CloudflaredBinaryPath = CloudflaredBinaryPath,
            CloudflaredAccessHostname = CloudflaredAccessHostname,
            CfAccessClientId = CfAccessClientId,
            EncryptedCfAccessClientSecret = EncryptedCfAccessClientSecret,
            UseTls = UseTls,
            Host = Host,
            Port = Port,
            RemotePath = RemotePath,
            Username = Username,
            EncryptedPassword = EncryptedPassword,
            Anonymous = Anonymous,
            PrivateKeyPath = PrivateKeyPath,
            EncryptedPrivateKeyPassphrase = EncryptedPrivateKeyPassphrase,
            TlsVersion = TlsVersion,
            TimeoutSeconds = TimeoutSeconds,
            R2AccountId = R2AccountId,
            R2AccessKeyId = R2AccessKeyId,
            EncryptedR2SecretAccessKey = EncryptedR2SecretAccessKey,
            R2BucketName = R2BucketName,
            GoogleScope = GoogleScope,
            GoogleFolderId = GoogleFolderId,
            GoogleClientId = GoogleClientId,
            EncryptedGoogleClientSecret = EncryptedGoogleClientSecret,
            EncryptedGoogleRefreshToken = EncryptedGoogleRefreshToken,
            EncryptedGoogleAccessToken = EncryptedGoogleAccessToken
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyChanged([CallerMemberName] string? propertyName = null)
    {
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(ProtocolDisplayName));
        OnPropertyChanged(nameof(EndpointSummary));
        OnPropertyChanged(nameof(MountModeBadge));
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
