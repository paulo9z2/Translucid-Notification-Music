using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Translucid.Core;

namespace Translucid.App;

public partial class MainWindow : Window
{
    private readonly MediaTracker _tracker = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private MediaUpdate _media = MediaUpdate.Idle;
    private DateTime _positionStamp;
    private TimeSpan _positionAtStamp;

    private bool _locked = true;
    private bool _hasSavedPosition;
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Translucid", "ui.json");

    private sealed class UiSettings
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public bool Locked { get; set; } = true;
    }

    public MainWindow()
    {
        InitializeComponent();
        _tick.Tick += (_, _) => RenderPosition();
        SizeChanged += (_, _) => RenderPosition();
        Closing += (_, _) => SaveSettings();
        Closing += Window_Closing;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSettings();
        if (!_hasSavedPosition)
        {
            PositionWindow();
        }

        ApplyLockVisual();
        _tracker.Updated += OnMediaUpdated;
        await _tracker.StartAsync();
        _tick.Start();
    }

    private void PositionWindow()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 24;
        Top = area.Top + 24;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DesktopFx.TryCornerRounding(hwnd);
        if (!DesktopFx.EnableAcrylic(hwnd))
        {
            Shell.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0D));
            CoverFrame.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        }
        DesktopFx.HideFromAltTab(hwnd);
        DesktopFx.RoundCorners(hwnd, 14);
    }

    private void OnMediaUpdated(MediaUpdate update)
    {
        Dispatcher.Invoke(() =>
        {
            _media = update;
            TitleText.Text = string.IsNullOrWhiteSpace(update.Title) ? "Nada tocando" : update.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(update.Artist)
                ? update.AppName
                : $"{update.Artist}  •  {update.AppName}";

            if (update.Thumbnail is { Length: > 0 })
            {
                CoverImage.Source = BytesToBitmap(update.Thumbnail);
            }
            else
            {
                CoverImage.Source = null;
            }

            PlayButton.Content = update.IsPlaying ? "\uE769" : "\uE768";
            PlayButton.IsEnabled = update.CanPlayPause;
            NextButton.IsEnabled = update.CanNext;
            PrevButton.IsEnabled = update.CanPrevious;

            _positionAtStamp = update.Position;
            _positionStamp = DateTime.UtcNow;
            RenderPosition();
        });
    }

    private void RenderPosition()
    {
        var position = _media.IsPlaying && _media.Duration > TimeSpan.Zero
            ? _positionAtStamp + (DateTime.UtcNow - _positionStamp)
            : _positionAtStamp;

        if (position > _media.Duration && _media.Duration > TimeSpan.Zero)
        {
            position = _media.Duration;
        }

        CurrentTimeText.Text = FormatTime(position);
        DurationText.Text = FormatTime(_media.Duration);

        var width = _media.Duration > TimeSpan.Zero
            ? Math.Clamp(position.TotalSeconds / _media.Duration.TotalSeconds, 0, 1) * ProgressTrackWidth()
            : 0;
        ProgressFill.Width = width;
    }

    private double ProgressTrackWidth()
    {
        var parent = ProgressFill.Parent as FrameworkElement;
        return parent?.ActualWidth ?? 0;
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes}:{t.Seconds:D2}";

    private static BitmapImage BytesToBitmap(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject { } source &&
            FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }
        DragMove();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        _locked = !_locked;
        ApplyLockVisual();
        SaveSettings();
        e.Handled = true;
    }

    private void ApplyLockVisual()
    {
        LockIcon.Text = _locked ? "\uE72E" : "\uE785";
        LockIcon.Opacity = _locked ? 0.35 : 1.0;
        Shell.BorderBrush = new SolidColorBrush(_locked
            ? System.Windows.Media.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : System.Windows.Media.Color.FromArgb(0xFF, 0x6F, 0xD3, 0xFF));
        Cursor = _locked ? System.Windows.Input.Cursors.Arrow : System.Windows.Input.Cursors.SizeAll;
    }

    private void SaveSettings()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            var settings = new UiSettings { Left = Left, Top = Top, Locked = _locked };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // nunca deixa um erro de persistencia derrubar o widget
        }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return;
            }

            var settings = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(SettingsPath));
            if (settings is not null)
            {
                Left = settings.Left;
                Top = settings.Top;
                _locked = settings.Locked;
                _hasSavedPosition = true;
            }
        }
        catch
        {
            // corrompeu? ignora e usa o padrao
        }
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private async void PrevButton_Click(object sender, RoutedEventArgs e) =>
        await _tracker.PreviousAsync();

    private async void PlayButton_Click(object sender, RoutedEventArgs e) =>
        await _tracker.TogglePlayPauseAsync();

    private async void NextButton_Click(object sender, RoutedEventArgs e) =>
        await _tracker.NextAsync();

    private void CloseButton_Click(object sender, MouseButtonEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object sender, CancelEventArgs e)
    {
        var app = (App)System.Windows.Application.Current;
        if (!app.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            app.NotifyHidden();
        }
    }
}