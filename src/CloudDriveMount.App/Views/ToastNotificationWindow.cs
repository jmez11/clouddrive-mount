using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using CloudDriveMount.App.Helpers;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;

namespace CloudDriveMount.App.Views;

public sealed class ToastNotificationWindow : Window
{
    private static ToastNotificationWindow? _activeToast;
    private readonly DispatcherTimer _closeTimer;
    private bool _isClosingAnimated;

    public static void ShowToast(string title, string message, Action? onClick = null)
    {
        if (Application.Current?.Dispatcher == null)
            return;

        Application.Current.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                if (_activeToast != null)
                {
                    try { _activeToast.CloseImmediate(); } catch { }
                    _activeToast = null;
                }

                var toast = new ToastNotificationWindow(title, message, onClick);
                _activeToast = toast;
                toast.Show();
            }
            catch { }
        });
    }

    private ToastNotificationWindow(string title, string message, Action? onClick)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 368;

        var outerGrid = new Grid { Margin = new Thickness(12) };

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(32, 34, 38)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(64, 68, 76)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 12, 14, 14),
            Cursor = onClick != null ? Cursors.Hand : Cursors.Arrow,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 16,
                ShadowDepth = 4,
                Opacity = 0.45
            }
        };

        var rootStack = new StackPanel();

        // Header row: App icon + "CloudDrive Mount" + Close button
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var appLogo = new Image
        {
            Width = 18,
            Height = 18,
            Source = IconImageHelper.GetAppLogoImage(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        RenderOptions.SetBitmapScalingMode(appLogo, BitmapScalingMode.HighQuality);
        Grid.SetColumn(appLogo, 0);
        headerGrid.Children.Add(appLogo);

        var appNameText = new TextBlock
        {
            Text = "CloudDrive Mount",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 206, 214)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(appNameText, 1);
        headerGrid.Children.Add(appNameText);

        var closeBtn = new Button
        {
            Content = "✕",
            Width = 22,
            Height = 22,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.FromRgb(160, 166, 174)),
            FontSize = 11,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, e) =>
        {
            e.Handled = true;
            BeginFadeOut();
        };
        Grid.SetColumn(closeBtn, 2);
        headerGrid.Children.Add(closeBtn);

        rootStack.Children.Add(headerGrid);

        // Title
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        rootStack.Children.Add(titleBlock);

        // Body message
        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(184, 190, 198)),
            TextWrapping = TextWrapping.Wrap
        };
        rootStack.Children.Add(messageBlock);

        card.Child = rootStack;
        card.MouseLeftButtonUp += (_, _) =>
        {
            onClick?.Invoke();
            BeginFadeOut();
        };

        outerGrid.Children.Add(card);
        Content = outerGrid;

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3800) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer.Stop();
            BeginFadeOut();
        };

        Loaded += OnToastLoaded;
    }

    private void OnToastLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 8;
        Top = workArea.Bottom - ActualHeight - 8;

        Opacity = 0;
        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        BeginAnimation(OpacityProperty, fadeIn);
        _closeTimer.Start();
    }

    private void BeginFadeOut()
    {
        if (_isClosingAnimated)
            return;
        _isClosingAnimated = true;
        _closeTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) => CloseImmediate();
        BeginAnimation(OpacityProperty, fadeOut);
    }

    private void CloseImmediate()
    {
        _closeTimer.Stop();
        if (ReferenceEquals(_activeToast, this))
            _activeToast = null;
        try { Close(); } catch { }
    }
}
