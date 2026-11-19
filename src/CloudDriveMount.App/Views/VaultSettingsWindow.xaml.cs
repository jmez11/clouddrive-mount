using System.Windows;
using System.Windows.Media;
using CloudDriveMount.App.Helpers;
using CloudDriveMount.Core.Models;
using CloudDriveMount.Core.Services;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace CloudDriveMount.App.Views;

public partial class VaultSettingsWindow : Window
{
    private readonly ConfigStore _configStore;
    private readonly IList<DriveConfig> _drives;

    public VaultSettingsWindow(ConfigStore configStore, IList<DriveConfig> drives)
    {
        InitializeComponent();
        _configStore = configStore;
        _drives = drives;
        Icon = IconImageHelper.GetAppLogoImage();
        RefreshUi();
    }

    private void RefreshUi()
    {
        if (_configStore.IsPortableMode)
        {
            ModeStatusText.Text = "● Portable Mode (config stored next to CloudDriveMount.exe)";
            ModeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(129, 199, 132));
            SwitchModeBtn.Content = "Switch to System Mode";
        }
        else
        {
            ModeStatusText.Text = "● System Mode (config stored in Windows ProgramData)";
            ModeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(144, 202, 249));
            SwitchModeBtn.Content = "Switch to Portable Mode";
        }
        ConfigPathText.Text = $"Config file: {_configStore.ConfigPath}";

        var vault = _configStore.CurrentVault;
        if (vault != null)
        {
            bool remembered = VaultManager.HasRememberedKey(vault.VaultId);
            VaultStatusText.Text = remembered
                ? "● Protected with Master Password (AES-256-GCM) — Remembered on this PC"
                : "● Protected with Master Password (AES-256-GCM) — Prompt on next launch";
            VaultStatusText.Foreground = new SolidColorBrush(Color.FromRgb(129, 199, 132));

            PasswordLabel.Text = "New Master Password";
            SetPasswordBtn.Content = "Update Master Password";
            RememberCheckBox.IsChecked = remembered;
            ForgetPcBtn.Visibility = remembered ? Visibility.Visible : Visibility.Collapsed;
            RemovePasswordBtn.Visibility = Visibility.Visible;
        }
        else
        {
            VaultStatusText.Text = "● Machine-Only Encryption (Windows DPAPI — will NOT decrypt on other PCs)";
            VaultStatusText.Foreground = new SolidColorBrush(Color.FromRgb(255, 213, 79));

            PasswordLabel.Text = "Master Password";
            SetPasswordBtn.Content = "Enable Master Password";
            RememberCheckBox.IsChecked = true;
            ForgetPcBtn.Visibility = Visibility.Collapsed;
            RemovePasswordBtn.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSwitchModeClick(object sender, RoutedEventArgs e)
    {
        if (_configStore.IsPortableMode)
        {
            var result = MessageBox.Show(
                this,
                "Switch to System Mode (C:\\ProgramData\\CloudDriveMount\\drives.json) and remove the portable drives.json next to CloudDriveMount.exe?",
                "Switch to System Mode",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
                return;

            _configStore.SwitchMode(
                portable: false,
                _drives,
                deletePreviousPortableFile: result == MessageBoxResult.Yes);
            ShowFeedback("Switched to System Mode.", isError: false);
        }
        else
        {
            _configStore.SwitchMode(portable: true, _drives);
            ShowFeedback(
                _configStore.CurrentVault == null
                    ? "Switched to Portable Mode! Tip: set a Master Password below so credentials work on other PCs."
                    : "Switched to Portable Mode!",
                isError: false);
        }

        RefreshUi();
    }

    private void OnSetPasswordClick(object sender, RoutedEventArgs e)
    {
        var pw = NewPasswordBox.Password;
        var confirm = ConfirmPasswordBox.Password;

        if (string.IsNullOrWhiteSpace(pw) || pw.Length < 4)
        {
            ShowFeedback("Please enter a Master Password (at least 4 characters).", isError: true);
            return;
        }

        if (!string.Equals(pw, confirm, StringComparison.Ordinal))
        {
            ShowFeedback("Passwords do not match.", isError: true);
            return;
        }

        bool remember = RememberCheckBox.IsChecked == true;
        bool wasProtected = _configStore.CurrentVault != null;
        VaultManager.EnableOrChangeMasterPassword(_configStore, _drives, pw, remember);

        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        RefreshUi();
        ShowFeedback(
            wasProtected
                ? "Master Password updated and all credentials re-encrypted."
                : "Master Password enabled! All drive credentials are now encrypted with portable AES-256-GCM.",
            isError: false);
    }

    private void OnForgetPcClick(object sender, RoutedEventArgs e)
    {
        var vault = _configStore.CurrentVault;
        if (vault == null)
            return;

        VaultManager.ForgetThisPc(vault.VaultId);
        RefreshUi();
        ShowFeedback("Cleared local DPAPI unlock cache. Next launch on this PC will prompt for your Master Password.", isError: false);
    }

    private void OnRemovePasswordClick(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            this,
            "Remove the Master Password and revert credentials to Windows DPAPI (machine-only) encryption?\n\nCredentials will no longer decrypt if moved to another PC.",
            "Remove Master Password",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        VaultManager.DisableMasterPassword(_configStore, _drives);
        NewPasswordBox.Clear();
        ConfirmPasswordBox.Clear();
        RefreshUi();
        ShowFeedback("Master Password removed. Credentials reverted to machine-only DPAPI.", isError: false);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ShowFeedback(string message, bool isError)
    {
        FeedbackText.Text = message;
        FeedbackText.Foreground = isError
            ? new SolidColorBrush(Color.FromRgb(239, 154, 154))
            : new SolidColorBrush(Color.FromRgb(129, 199, 132));
        FeedbackText.Visibility = Visibility.Visible;
    }
}
