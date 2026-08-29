using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Translucid.Core;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;

namespace Translucid.App;

/// <summary>Janelinha de preferências aberta pelo ícone da bandeja.</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private bool _initializing = true;

    public SettingsWindow()
    {
        InitializeComponent();
        AutoStartToggle.IsChecked = AppSettings.IsAutoStartEnabled();
        BottomToggle.IsChecked = AppSettings.Current.AlwaysOnBottom;
        LyricsToggle.IsChecked = AppSettings.Current.LyricsEnabled;
        HotkeysToggle.IsChecked = AppSettings.Current.HotkeysEnabled;
        ExtendedResizeToggle.IsChecked = AppSettings.Current.ExtendedResizeEnabled;
        Closed += (_, _) => _instance = null;
        _initializing = false;
    }

    /// <summary>Abre a janela (uma única instância), perto do widget.</summary>
    public static void ShowOrFocus()
    {
        if (_instance is { } existing && existing.IsVisible)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow();
        _instance = window;

        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            var area = SystemParameters.WorkArea;
            window.Left = Math.Clamp(main.Left + 48, area.Left, area.Right - window.Width);
            window.Top = Math.Clamp(main.Top + 48, area.Top, area.Bottom - window.Height);
        }

        window.Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DesktopFx.TryCornerRounding(hwnd);
        // Fundo SEMPRE presente (translúcido): sem ele a janela não captura
        // cliques nas áreas vazias e o arraste não funciona.
        Shell.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x0A, 0x0B, 0x0D));
        if (!DesktopFx.EnableAcrylic(hwnd))
        {
            Shell.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0D));
        }

        DesktopFx.HideFromAltTab(hwnd);
        DesktopFx.RoundCorners(hwnd, 14);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject { } source &&
            FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }
        DragMove();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.SetAutoStart(toggle.IsChecked == true);
        }
    }

    private void LyricsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.LyricsEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void BottomToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.AlwaysOnBottom = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void HotkeysToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.HotkeysEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void ExtendedResizeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.ExtendedResizeEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }
}