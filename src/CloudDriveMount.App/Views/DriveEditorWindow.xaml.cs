using System.Windows;
using System.Windows.Controls;
using CloudDriveMount.App.Helpers;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Providers;
using CloudDriveMount.Core.Services;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace CloudDriveMount.App.Views;

public partial class DriveEditorWindow : Window
{
    private readonly DriveConfig _config;
    private readonly bool _isEditMode;
    private readonly HashSet<char> _usedDriveLetters = new();
    private bool _isInitializing = true;

    public DriveConfig ResultConfig => _config;
    public bool ConnectImmediately { get; private set; }

    public DriveEditorWindow(DriveConfig? existingConfig = null, IEnumerable<string>? usedDriveLetters = null)
    {
        InitializeComponent();

        _isEditMode = existingConfig != null;
        _config = existingConfig?.Clone() ?? new DriveConfig();

        DialogTitleText.Text = _isEditMode ? $"Edit Drive ({_config.NormalizedMountPoint})" : "New Drive";
        Title = DialogTitleText.Text;
        Icon = IconImageHelper.GetAppLogoImage();

        // Load protocol icons
        ImgProtoSftp.Source = IconImageHelper.GetProtocolImage(StorageProtocol.Sftp);
        ImgProtoFtp.Source = IconImageHelper.GetProtocolImage(StorageProtocol.Ftp);
        ImgProtoWebDav.Source = IconImageHelper.GetProtocolImage(StorageProtocol.WebDav);
        ImgProtoGDrive.Source = IconImageHelper.GetProtocolImage(StorageProtocol.GoogleDrive);
        ImgProtoR2.Source = IconImageHelper.GetProtocolImage(StorageProtocol.CloudflareR2);

        PopulateDriveLetters(usedDriveLetters ?? Enumerable.Empty<string>());
        LoadFromConfig();
        _isInitializing = false;
        UpdateUiForSelectedProtocol(resetDefaults: false);
    }

    private void PopulateDriveLetters(IEnumerable<string> usedLetters)
    {
        _usedDriveLetters.Clear();
        foreach (var s in usedLetters)
        {
            if (!string.IsNullOrWhiteSpace(s))
                _usedDriveLetters.Add(char.ToUpperInvariant(s.Trim()[0]));
        }

        foreach (var drive in System.IO.DriveInfo.GetDrives())
        {
            _usedDriveLetters.Add(char.ToUpperInvariant(drive.Name[0]));
        }

        if (_isEditMode)
        {
            _usedDriveLetters.Remove(_config.NormalizedDriveLetterChar);
        }

        DriveLetterCombo.Items.Clear();
        for (char c = 'D'; c <= 'Z'; c++)
        {
            if (!_usedDriveLetters.Contains(c))
            {
                DriveLetterCombo.Items.Add($"{c}:");
            }
        }

        if (!_isEditMode || _usedDriveLetters.Contains(_config.NormalizedDriveLetterChar))
        {
            char[] preferredOrder =
            [
                'W', 'X', 'Y', 'Z', 'V', 'U', 'T', 'S', 'R', 'Q', 'P',
                'O', 'N', 'M', 'L', 'K', 'J', 'I', 'H', 'G', 'F', 'E', 'D'
            ];
            var nextFree = preferredOrder.FirstOrDefault(c => !_usedDriveLetters.Contains(c));
            if (nextFree != '\0')
            {
                _config.DriveLetter = $"{nextFree}:";
            }
        }

        if (DriveLetterCombo.Items.Contains(_config.NormalizedMountPoint))
        {
            DriveLetterCombo.SelectedItem = _config.NormalizedMountPoint;
        }
        else if (DriveLetterCombo.Items.Count > 0)
        {
            DriveLetterCombo.SelectedIndex = 0;
            _config.DriveLetter = DriveLetterCombo.SelectedItem?.ToString() ?? _config.DriveLetter;
        }
    }

    private void LoadFromConfig()
    {
        switch (_config.Protocol)
        {
            case StorageProtocol.Sftp: ProtoSftpRadio.IsChecked = true; break;
            case StorageProtocol.Ftp: ProtoFtpRadio.IsChecked = true; break;
            case StorageProtocol.WebDav: ProtoWebDavRadio.IsChecked = true; break;
            case StorageProtocol.GoogleDrive: ProtoGDriveRadio.IsChecked = true; break;
            case StorageProtocol.CloudflareR2: ProtoR2Radio.IsChecked = true; break;
        }

        DriveLabelBox.Text = _config.Label;
        ReconnectCheck.IsChecked = _config.ReconnectAtStart;
        SignInEachCheck.IsChecked = _config.SignInOnEachConnection;
        LocalDiskCheck.IsChecked = _config.IsLocalDisk;
        PublicScopeRadio.IsChecked = _config.LocalDiskVisibility == LocalDiskScope.Public;
        PrivateScopeRadio.IsChecked = _config.LocalDiskVisibility == LocalDiskScope.Private;
        ProxyCheck.IsChecked = _config.UseProxy;
        ReadOnlyCheck.IsChecked = _config.ReadOnly;
        FileLockCheck.IsChecked = _config.FileLock;

        // Proxy settings
        ProxyModeCombo.SelectedIndex = _config.ProxyMode switch
        {
            CloudflaredProxyMode.ServiceTokenHeaders => 1,
            CloudflaredProxyMode.BothAccessTcpAndHeaders => 2,
            _ => 0
        };
        CloudflaredHostnameBox.Text = _config.CloudflaredAccessHostname;
        CfClientIdBox.Text = _config.CfAccessClientId;
        CfClientSecretBox.Password = CredentialProtector.Unprotect(_config.EncryptedCfAccessClientSecret);
        CloudflaredBinaryBox.Text = string.IsNullOrWhiteSpace(_config.CloudflaredBinaryPath)
            ? (CloudflaredTunnelManager.ResolveCloudflaredPath() ?? "cloudflared")
            : _config.CloudflaredBinaryPath;

        // SFTP / FTP / WebDAV
        UseTlsCheck.IsChecked = _config.UseTls;
        HostBox.Text = _config.Host;
        PortBox.Text = _config.Port.ToString();
        RemotePathBox.Text = string.IsNullOrWhiteSpace(_config.RemotePath) ? "/" : _config.RemotePath;
        UsernameBox.Text = _config.Username;
        PasswordBox.Password = CredentialProtector.Unprotect(_config.EncryptedPassword);
        AnonymousCheck.IsChecked = _config.Anonymous;
        PrivateKeyPathBox.Text = _config.PrivateKeyPath;
        PrivateKeyPassphraseBox.Password = CredentialProtector.Unprotect(_config.EncryptedPrivateKeyPassphrase);
        TlsVersionCombo.SelectedIndex = _config.TlsVersion == TlsProtocolVersion.Tls13 ? 1 : 0;
        TimeoutBox.Text = _config.TimeoutSeconds.ToString();

        // Cloudflare R2
        R2AccountIdBox.Text = _config.R2AccountId;
        R2AccessKeyIdBox.Text = _config.R2AccessKeyId;
        R2SecretKeyBox.Password = CredentialProtector.Unprotect(_config.EncryptedR2SecretAccessKey);
        if (!string.IsNullOrWhiteSpace(_config.R2BucketName))
        {
            R2BucketBox.Text = _config.R2BucketName;
            R2BucketCountText.Text = "1";
        }

        // Google Drive
        GDriveMyDriveRadio.IsChecked = _config.GoogleScope == GoogleDriveScope.MyDrive;
        GDriveSharedRadio.IsChecked = _config.GoogleScope == GoogleDriveScope.SharedWithMe;
        GDriveFolderRadio.IsChecked = _config.GoogleScope == GoogleDriveScope.Folder;
        GDriveFolderIdBox.Text = _config.GoogleFolderId;
        GDriveClientIdBox.Text = _config.GoogleClientId;
        GDriveClientSecretBox.Password = CredentialProtector.Unprotect(_config.EncryptedGoogleClientSecret);
        var refresh = CredentialProtector.Unprotect(_config.EncryptedGoogleRefreshToken);
        var access = CredentialProtector.Unprotect(_config.EncryptedGoogleAccessToken);
        GDriveTokenBox.Password = !string.IsNullOrEmpty(refresh) ? refresh : access;

        UpdateLocalDiskState();
        UpdateProxyPanelState();
    }

    private StorageProtocol GetSelectedProtocol()
    {
        if (ProtoFtpRadio.IsChecked == true) return StorageProtocol.Ftp;
        if (ProtoWebDavRadio.IsChecked == true) return StorageProtocol.WebDav;
        if (ProtoGDriveRadio.IsChecked == true) return StorageProtocol.GoogleDrive;
        if (ProtoR2Radio.IsChecked == true) return StorageProtocol.CloudflareR2;
        return StorageProtocol.Sftp;
    }

    private void OnProtocolSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateUiForSelectedProtocol(resetDefaults: !_isEditMode);
    }

    private void UpdateUiForSelectedProtocol(bool resetDefaults)
    {
        var proto = GetSelectedProtocol();

        NasProtocolPanel.Visibility = proto is StorageProtocol.Sftp or StorageProtocol.Ftp or StorageProtocol.WebDav
            ? Visibility.Visible
            : Visibility.Collapsed;
        R2ProtocolPanel.Visibility = proto == StorageProtocol.CloudflareR2
            ? Visibility.Visible
            : Visibility.Collapsed;
        GoogleDriveProtocolPanel.Visibility = proto == StorageProtocol.GoogleDrive
            ? Visibility.Visible
            : Visibility.Collapsed;

        SshKeyRow.Visibility = proto == StorageProtocol.Sftp
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (resetDefaults)
        {
            DriveLabelBox.Text = proto switch
            {
                StorageProtocol.Sftp => "SFTP",
                StorageProtocol.Ftp => "FTP",
                StorageProtocol.WebDav => "WebDAV",
                StorageProtocol.GoogleDrive => "Google Drive",
                StorageProtocol.CloudflareR2 => "Cloudflare R2",
                _ => "Cloud Drive"
            };

            switch (proto)
            {
                case StorageProtocol.Sftp:
                    UseTlsCheck.IsChecked = true;
                    PortBox.Text = "22";
                    break;
                case StorageProtocol.Ftp:
                    UseTlsCheck.IsChecked = false;
                    PortBox.Text = "21";
                    break;
                case StorageProtocol.WebDav:
                    UseTlsCheck.IsChecked = true;
                    PortBox.Text = "443";
                    break;
            }
        }

        UpdateSchemeLabel();
    }

    private void OnUseTlsChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        var proto = GetSelectedProtocol();
        bool tls = UseTlsCheck.IsChecked == true;
        if (proto == StorageProtocol.WebDav && (PortBox.Text == "443" || PortBox.Text == "80"))
        {
            PortBox.Text = tls ? "443" : "80";
        }
        UpdateSchemeLabel();
    }

    private void UpdateSchemeLabel()
    {
        var proto = GetSelectedProtocol();
        bool tls = UseTlsCheck.IsChecked == true;
        SchemePrefixText.Text = proto switch
        {
            StorageProtocol.Sftp => "sftp://",
            StorageProtocol.Ftp => tls ? "ftps://" : "ftp://",
            StorageProtocol.WebDav => tls ? "https://" : "http://",
            _ => "https://"
        };
    }

    private void OnLocalDiskChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateLocalDiskState();
    }

    private void UpdateLocalDiskState()
    {
        bool isLocal = LocalDiskCheck.IsChecked == true;
        PublicScopeRadio.IsEnabled = isLocal;
        PrivateScopeRadio.IsEnabled = isLocal;
        LanShareHintText.Visibility = isLocal && PublicScopeRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnProxyCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        UpdateProxyPanelState();
    }

    private void UpdateProxyPanelState()
    {
        ProxyConfigPanel.Visibility = ProxyCheck.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnAnonymousChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        bool anon = AnonymousCheck.IsChecked == true;
        UsernameBox.IsEnabled = !anon;
        PasswordBox.IsEnabled = !anon;
    }

    private void OnGoogleScopeChanged(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        GDriveFolderIdRow.Visibility = GDriveFolderRadio.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void OnBrowseCloudflaredClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select cloudflared.exe",
            Filter = "cloudflared Executable (cloudflared.exe)|cloudflared.exe|All Executables (*.exe)|*.exe"
        };
        if (dlg.ShowDialog(this) == true)
        {
            CloudflaredBinaryBox.Text = dlg.FileName;
        }
    }

    private void OnBrowsePrivateKeyClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select SSH Private Key",
            Filter = "SSH Private Keys (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            PrivateKeyPathBox.Text = dlg.FileName;
        }
    }

    private async void OnRefreshR2BucketsClick(object sender, RoutedEventArgs e)
    {
        ValidationStatusText.Visibility = Visibility.Collapsed;
        var accountId = R2AccountIdBox.Text.Trim();
        var accessKey = R2AccessKeyIdBox.Text.Trim();
        var secretKey = R2SecretKeyBox.Password.Trim();
        var typedBucket = R2BucketBox.Text.Trim();

        if (string.IsNullOrEmpty(accountId) || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
        {
            ShowError("Enter Cloudflare R2 Account ID, Access Key ID, and Secret Access Key first.");
            return;
        }

        RefreshBucketsBtn.IsEnabled = false;
        try
        {
            var buckets = await CloudflareR2StorageProvider.DiscoverBucketsAsync(accountId, accessKey, secretKey);
            R2BucketCountText.Text = buckets.Count.ToString();
            if (buckets.Count > 0 && string.IsNullOrWhiteSpace(R2BucketBox.Text))
            {
                R2BucketBox.Text = buckets[0];
            }

            if (buckets.Count > 1)
            {
                var menu = new ContextMenu();
                foreach (var b in buckets)
                {
                    var item = new MenuItem { Header = b };
                    item.Click += (_, _) => R2BucketBox.Text = b;
                    menu.Items.Add(item);
                }
                menu.PlacementTarget = RefreshBucketsBtn;
                menu.IsOpen = true;
            }
        }
        catch (Exception ex) when (ex.Message.Contains("Access Denied", StringComparison.OrdinalIgnoreCase))
        {
            // Bucket-scoped R2 API tokens do not have account-level s3:ListAllMyBuckets permission
            if (!string.IsNullOrEmpty(typedBucket))
            {
                try
                {
                    await CloudflareR2StorageProvider.VerifyBucketAccessAsync(accountId, accessKey, secretKey, typedBucket);
                    R2BucketCountText.Text = "1";
                    ShowError($"✓ Verified access to bucket '{typedBucket}' (bucket-scoped token). Click Connect to mount!");
                }
                catch (Exception innerEx)
                {
                    ShowError($"Could not access bucket '{typedBucket}': {innerEx.Message}");
                }
            }
            else
            {
                ShowError("This R2 API token is scoped to specific bucket(s) and cannot list all buckets. Type your Bucket name directly in the Bucket box above and click Connect.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to fetch R2 buckets: {ex.Message}");
        }
        finally
        {
            RefreshBucketsBtn.IsEnabled = true;
        }
    }

    private void OnImportGoogleClientJsonClick(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select Google OAuth client_secret.json",
            Filter = "Google OAuth JSON (*.json)|*.json|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true)
            return;

        try
        {
            var json = System.IO.File.ReadAllText(dlg.FileName);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("installed", out var installed) || root.TryGetProperty("web", out installed))
            {
                if (installed.TryGetProperty("client_id", out var cid))
                    GDriveClientIdBox.Text = cid.GetString() ?? string.Empty;
                if (installed.TryGetProperty("client_secret", out var csec))
                    GDriveClientSecretBox.Password = csec.GetString() ?? string.Empty;
                ValidationStatusText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ShowError("Selected JSON does not contain an 'installed' or 'web' Google OAuth client definition.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to parse Google OAuth JSON: {ex.Message}");
        }
    }

    private async void OnGoogleOAuthSignInClick(object sender, RoutedEventArgs e)
    {
        ValidationStatusText.Visibility = Visibility.Collapsed;
        var clientId = GDriveClientIdBox.Text.Trim();
        var clientSecret = GDriveClientSecretBox.Password.Trim();

        if (string.IsNullOrEmpty(clientId))
        {
            ShowError("Enter your Google OAuth 2.0 Client ID before starting browser sign-in, or paste an Access/Refresh Token directly.");
            return;
        }

        try
        {
            var (access, refresh) = await GoogleDriveStorageProvider.AuthorizeInteractiveAsync(clientId, clientSecret);
            _config.EncryptedGoogleAccessToken = CredentialProtector.Protect(access);
            if (!string.IsNullOrEmpty(refresh))
                _config.EncryptedGoogleRefreshToken = CredentialProtector.Protect(refresh);
            GDriveTokenBox.Password = !string.IsNullOrEmpty(refresh) ? refresh : access;
        }
        catch (Exception ex)
        {
            ShowError($"Google Sign-In failed: {ex.Message}");
        }
    }

    private bool SaveToConfigAndValidate()
    {
        ValidationStatusText.Visibility = Visibility.Collapsed;

        _config.Protocol = GetSelectedProtocol();
        _config.DriveLetter = DriveLetterCombo.SelectedItem?.ToString() ?? _config.DriveLetter;
        if (_usedDriveLetters.Contains(_config.NormalizedDriveLetterChar))
        {
            ShowError($"Drive letter {_config.NormalizedMountPoint} is already in use by another drive. Please select an available letter.");
            return false;
        }

        _config.Label = string.IsNullOrWhiteSpace(DriveLabelBox.Text)
            ? _config.ProtocolDisplayName
            : DriveLabelBox.Text.Trim();

        _config.ReconnectAtStart = ReconnectCheck.IsChecked == true;
        _config.SignInOnEachConnection = SignInEachCheck.IsChecked == true;
        _config.IsLocalDisk = LocalDiskCheck.IsChecked == true;
        _config.LocalDiskVisibility = PublicScopeRadio.IsChecked == true ? LocalDiskScope.Public : LocalDiskScope.Private;
        _config.UseProxy = ProxyCheck.IsChecked == true;
        _config.ReadOnly = ReadOnlyCheck.IsChecked == true;
        _config.FileLock = FileLockCheck.IsChecked == true;

        _config.ProxyMode = ProxyModeCombo.SelectedIndex switch
        {
            1 => CloudflaredProxyMode.ServiceTokenHeaders,
            2 => CloudflaredProxyMode.BothAccessTcpAndHeaders,
            _ => CloudflaredProxyMode.AccessTcpTunnel
        };
        _config.CloudflaredAccessHostname = CloudflaredHostnameBox.Text.Trim();
        _config.CfAccessClientId = CfClientIdBox.Text.Trim();
        _config.EncryptedCfAccessClientSecret = CredentialProtector.Protect(CfClientSecretBox.Password.Trim());
        _config.CloudflaredBinaryPath = CloudflaredBinaryBox.Text.Trim();

        if (_config.Protocol is StorageProtocol.Sftp or StorageProtocol.Ftp or StorageProtocol.WebDav)
        {
            if (string.IsNullOrWhiteSpace(HostBox.Text))
            {
                ShowError("Please enter a server hostname or IP address.");
                return false;
            }
            if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
            {
                ShowError("Please enter a valid port number (1-65535).");
                return false;
            }

            _config.UseTls = UseTlsCheck.IsChecked == true;
            _config.Host = HostBox.Text.Trim();
            _config.Port = port;
            _config.RemotePath = string.IsNullOrWhiteSpace(RemotePathBox.Text) ? "/" : RemotePathBox.Text.Trim();
            _config.Anonymous = AnonymousCheck.IsChecked == true;
            _config.Username = UsernameBox.Text.Trim();
            _config.EncryptedPassword = CredentialProtector.Protect(PasswordBox.Password);
            _config.PrivateKeyPath = PrivateKeyPathBox.Text.Trim();
            _config.EncryptedPrivateKeyPassphrase = CredentialProtector.Protect(PrivateKeyPassphraseBox.Password);
            _config.TlsVersion = TlsVersionCombo.SelectedIndex == 1 ? TlsProtocolVersion.Tls13 : TlsProtocolVersion.Tls12;
            _config.TimeoutSeconds = int.TryParse(TimeoutBox.Text.Trim(), out var timeout) ? Math.Clamp(timeout, 10, 600) : 100;
        }
        else if (_config.Protocol == StorageProtocol.CloudflareR2)
        {
            if (string.IsNullOrWhiteSpace(R2AccountIdBox.Text) ||
                string.IsNullOrWhiteSpace(R2AccessKeyIdBox.Text) ||
                string.IsNullOrWhiteSpace(R2SecretKeyBox.Password) ||
                string.IsNullOrWhiteSpace(R2BucketBox.Text))
            {
                ShowError("Please enter Cloudflare R2 Account ID, Access Key ID, Secret Access Key, and Bucket name.");
                return false;
            }

            _config.R2AccountId = R2AccountIdBox.Text.Trim();
            _config.R2AccessKeyId = R2AccessKeyIdBox.Text.Trim();
            _config.EncryptedR2SecretAccessKey = CredentialProtector.Protect(R2SecretKeyBox.Password.Trim());
            _config.R2BucketName = R2BucketBox.Text.Trim();
        }
        else if (_config.Protocol == StorageProtocol.GoogleDrive)
        {
            _config.GoogleScope = GDriveSharedRadio.IsChecked == true
                ? GoogleDriveScope.SharedWithMe
                : (GDriveFolderRadio.IsChecked == true ? GoogleDriveScope.Folder : GoogleDriveScope.MyDrive);
            _config.GoogleFolderId = GDriveFolderIdBox.Text.Trim();
            _config.GoogleClientId = GDriveClientIdBox.Text.Trim();
            _config.EncryptedGoogleClientSecret = CredentialProtector.Protect(GDriveClientSecretBox.Password.Trim());

            var tokenInput = GDriveTokenBox.Password.Trim();
            if (string.IsNullOrEmpty(tokenInput) && string.IsNullOrEmpty(_config.EncryptedGoogleAccessToken))
            {
                ShowError("Please sign in with Google or provide an Access/Refresh Token.");
                return false;
            }

            if (!string.IsNullOrEmpty(tokenInput))
            {
                if (!string.IsNullOrEmpty(_config.GoogleClientId))
                    _config.EncryptedGoogleRefreshToken = CredentialProtector.Protect(tokenInput);
                else
                    _config.EncryptedGoogleAccessToken = CredentialProtector.Protect(tokenInput);
            }
        }

        _config.NotifyChanged();
        return true;
    }

    private void ShowError(string message)
    {
        ValidationStatusText.Text = message;
        ValidationStatusText.Visibility = Visibility.Visible;
    }

    private void OnConnectNowClick(object sender, RoutedEventArgs e)
    {
        if (!SaveToConfigAndValidate())
            return;

        ConnectImmediately = true;
        DialogResult = true;
        Close();
    }

    private void OnSaveOnlyClick(object sender, RoutedEventArgs e)
    {
        if (!SaveToConfigAndValidate())
            return;

        ConnectImmediately = false;
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
