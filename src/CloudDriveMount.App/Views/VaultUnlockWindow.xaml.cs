using System.Windows;
using CloudDriveMount.App.Helpers;
using CloudDriveMount.Core.Services;

namespace CloudDriveMount.App.Views;

public partial class VaultUnlockWindow : Window
{
    private readonly VaultMetadata _vault;

    public VaultUnlockWindow(VaultMetadata vault)
    {
        InitializeComponent();
        _vault = vault;
        Icon = IconImageHelper.GetAppLogoImage();
        Loaded += (_, _) => PasswordBox.Focus();
    }

    private void OnUnlockClick(object sender, RoutedEventArgs e)
    {
        var password = PasswordBox.Password;
        if (string.IsNullOrEmpty(password))
        {
            ShowError("Please enter your Master Password.");
            return;
        }

        bool remember = RememberCheckBox.IsChecked == true;
        if (VaultManager.TryUnlockWithPassword(_vault, password, remember))
        {
            DialogResult = true;
            Close();
            return;
        }

        ShowError("Incorrect Master Password. Please try again.");
        PasswordBox.SelectAll();
        PasswordBox.Focus();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
