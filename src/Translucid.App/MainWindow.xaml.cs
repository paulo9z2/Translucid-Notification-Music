using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Translucid.Core;

using Color = System.Windows.Media.Color;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;
using Panel = System.Windows.Controls.Panel;
using Application = System.Windows.Application;

namespace Translucid.App;

public partial class MainWindow : Window
{
    private const double DefaultHeight = 172;
    private const double LyricsExpandedHeight = 408;
    private const double PaletteSeconds = 1.4;
    private static readonly byte[] PaletteAlphas = { 0x40, 0x34, 0x2A };

    private readonly MediaTracker _tracker = new();
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _palette = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly DispatcherTimer _bottom = new() { Interval = TimeSpan.FromSeconds(1) };
    private IntPtr _hwnd;

    private MediaUpdate _media = MediaUpdate.Idle;
    private DateTime _positionStamp;
    private TimeSpan _positionAtStamp;

    private bool _locked = true;

    // capa: duas camadas para o slide trocar a arte
    private int _coverLayer;
    private byte[]? _coverBytes;
    private long _hideToken;

    // paleta: gradiente lento na cor da capa
    private readonly Color[] _cur = new Color[3];
    private readonly Color[] _from = new Color[3];
    private readonly Color[] _tgt = new Color[3];
    private DateTime _paletteStart;

    // letras
    private LyricLine[]? _lyrics;
    private string? _lyricsKey;
    private int _lyricsIndex = -1;
    private bool _lyricsExpanded;
    private long _lyricsToken;

    // overlay de volume
    private long _volumeToken;

    public MainWindow()
    {
        InitializeComponent();
        for (var i = 0; i < 3; i++)
        {
            _cur[i] = _tgt[i] = Color.FromArgb(PaletteAlphas[i], 0x0A, 0x0B, 0x0D);
        }

        _tick.Tick += (_, _) =>
        {
            RenderPosition();
            SyncLyrics();
        };
        _palette.Tick += PaletteTick;
        _bottom.Tick += (_, _) =>
        {
            if (AppSettings.Current.AlwaysOnBottom && IsVisible)
            {
                DesktopFx.PlaceBelowWindows(_hwnd);
            }
        };
        SizeChanged += (_, _) => RenderPosition();
        Closing += (_, _) => SaveSettings();
        Closing += Window_Closing;
        AppSettings.Current.Changed += OnSettingsChanged;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyPalette();
        LoadSettings();
        if (!AppSettings.Current.HasSavedPosition)
        {
            PositionWindow();
        }

        LyricsToggleButton.Visibility = AppSettings.Current.LyricsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyLockVisual();
        ApplyBottomBehavior();
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
        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DesktopFx.TryCornerRounding(_hwnd);
        if (!DesktopFx.EnableAcrylic(_hwnd))
        {
            Shell.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0D));
            CoverFrame.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        }

        DesktopFx.HideFromAltTab(_hwnd);
        DesktopFx.RoundCorners(_hwnd, 14);
    }

    /// <summary>
    /// A região de recorte (SetWindowRgn) fica FIXA no tamanho da janela quando
    /// aplicada — ao expandir para as letras, tudo abaixo da altura original era
    /// cortado pelo DWM. Reaplica a região a cada mudança de tamanho.
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            DesktopFx.RoundCorners(_hwnd, 14);
        }
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

            var bytes = update.Thumbnail;
            if (bytes is { Length: > 0 })
            {
                if (!SameCover(bytes))
                {
                    ++_hideToken; // cancela qualquer fade-out pendente
                    ShowCover(bytes);
                    ExtractPalette(bytes);
                }
            }
            else
            {
                HideCover();
            }

            PlayButton.Content = update.IsPlaying ? "\uE769" : "\uE768";
            PlayButton.IsEnabled = update.CanPlayPause;
            NextButton.IsEnabled = update.CanNext;
            PrevButton.IsEnabled = update.CanPrevious;

            _positionAtStamp = update.Position;
            _positionStamp = DateTime.UtcNow;
            RenderPosition();
            MaybeFetchLyrics(update);
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

    // ---------------------------------------------------------------- capa

    private bool SameCover(byte[] bytes) =>
        _coverBytes is { } prev && prev.Length == bytes.Length && bytes.AsSpan().SequenceEqual(prev);

    /// <summary>Capa nova desliza da direita; a antiga sai pela esquerda.</summary>
    private void ShowCover(byte[] bytes)
    {
        var visible = _coverLayer == 0 ? CoverImageA : CoverImageB;
        var incoming = _coverLayer == 0 ? CoverImageB : CoverImageA;
        var visibleX = _coverLayer == 0 ? CoverXformA : CoverXformB;
        var incomingX = _coverLayer == 0 ? CoverXformB : CoverXformA;

        incoming.Source = BytesToBitmap(bytes);
        _coverBytes = bytes;

        if (visible.Source is null)
        {
            // primeira capa: sem animação
            visible.Source = incoming.Source;
            incoming.Source = null;
            visible.Opacity = 1;
            visibleX.X = 0;
            incomingX.X = 0;
            _coverLayer = 0;
            return;
        }

        Panel.SetZIndex(incoming, 10);
        Panel.SetZIndex(visible, 5);

        incoming.Opacity = 0;
        Animate(incomingX, TranslateTransform.XProperty, 86, 0, 420);
        Animate(incoming, UIElement.OpacityProperty, 0, 1, 250);

        Animate(visibleX, TranslateTransform.XProperty, 0, -86, 400);
        Animate(visible, UIElement.OpacityProperty, 1, 0, 300);

        _coverLayer = _coverLayer == 0 ? 1 : 0;
    }

    private void HideCover()
    {
        _coverBytes = null;
        var token = ++_hideToken;
        FadeCover(CoverImageA, CoverXformA, token);
        FadeCover(CoverImageB, CoverXformB, token);
        SetDarkTarget();
    }

    private void FadeCover(Image image, TranslateTransform xform, long token)
    {
        if (image.Source is null)
        {
            return;
        }

        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
        anim.Completed += (_, _) =>
        {
            if (token != _hideToken)
            {
                return;
            }

            image.Source = null;
            image.Opacity = 0;
            xform.X = 0;
            _coverLayer = 0;
        };
        image.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    // ------------------------------------------------------------- paleta

    private void StartPaletteTransition()
    {
        Array.Copy(_cur, _from, 3);
        _paletteStart = DateTime.Now;
        _palette.Start();
    }

    private void PaletteTick(object? sender, EventArgs e)
    {
        var t = Math.Clamp((DateTime.Now - _paletteStart).TotalSeconds / PaletteSeconds, 0, 1);
        var k = t * t * (3 - 2 * t);
        for (var i = 0; i < 3; i++)
        {
            _cur[i] = Lerp(_from[i], _tgt[i], k);
        }

        ApplyPalette();
        if (t >= 1)
        {
            _palette.Stop();
        }
    }

    private void ApplyPalette()
    {
        PaletteTop.Color = _cur[0];
        PaletteMid.Color = _cur[1];
        PaletteBot.Color = _cur[2];
    }

    private void SetDarkTarget()
    {
        for (var i = 0; i < 3; i++)
        {
            _tgt[i] = Color.FromArgb(PaletteAlphas[i], 0x0A, 0x0B, 0x0D);
        }

        StartPaletteTransition();
    }

    /// <summary>Tira 3 cores da capa (topo/meio/base) para o degradê.</summary>
    private void ExtractPalette(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 16;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            var w = image.PixelWidth;
            var h = image.PixelHeight;
            if (w <= 0 || h <= 0)
            {
                SetDarkTarget();
                return;
            }

            var pixels = new byte[w * h * 4];
            image.CopyPixels(pixels, w * 4, 0);

            for (var band = 0; band < 3; band++)
            {
                var y0 = band * h / 3;
                var y1 = (band + 1) * h / 3;
                if (y1 <= y0)
                {
                    y1 = y0 + 1;
                }

                long r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                {
                    for (var x = 0; x < w; x++)
                    {
                        var i = (y * w + x) * 4;
                        r += pixels[i];
                        g += pixels[i + 1];
                        b += pixels[i + 2];
                    }
                }

                var n = (y1 - y0) * w;
                _tgt[band] = Color.FromArgb(
                    PaletteAlphas[band], (byte)(r / n), (byte)(g / n), (byte)(b / n));
            }

            StartPaletteTransition();
        }
        catch
        {
            SetDarkTarget();
        }
    }

    private static Color Lerp(Color a, Color b, double k) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * k),
        (byte)(a.R + (b.R - a.R) * k),
        (byte)(a.G + (b.G - a.G) * k),
        (byte)(a.B + (b.B - a.B) * k));

    private static void Animate(IAnimatable target, DependencyProperty property, double from, double to, int ms)
    {
        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        target.BeginAnimation(property, anim);
    }

    // ------------------------------------------------------------- volume

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_lyricsExpanded && LyricsScroll.Visibility == Visibility.Visible)
        {
            var pos = e.GetPosition(LyricsScroll);
            if (pos.X >= 0 && pos.Y >= 0 &&
                pos.X <= LyricsScroll.ActualWidth && pos.Y <= LyricsScroll.ActualHeight)
            {
                return; // rolagem vai para a letra
            }
        }

        var steps = e.Delta / 120;
        if (!VolumeMixer.Adjust(_media.AppProcessName, steps))
        {
            return;
        }

        e.Handled = true;
        ShowVolume(VolumeMixer.Get(_media.AppProcessName) ?? 0f);
    }

    private void ShowVolume(float volume)
    {
        var token = ++_volumeToken;
        VolumeText.Text = $"{(int)Math.Round(volume * 100)}%";
        VolumeText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80)));

        var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        fade.Tick += (_, _) =>
        {
            fade.Stop();
            if (token == _volumeToken)
            {
                VolumeText.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(350)));
            }
        };
        fade.Start();
    }

    // ------------------------------------------------------------- letras

    private void MaybeFetchLyrics(MediaUpdate update)
    {
        if (!AppSettings.Current.LyricsEnabled || ReferenceEquals(update, MediaUpdate.Idle))
        {
            return;
        }

        var key = $"{update.Title}\u0001{update.Artist}";
        if (string.IsNullOrWhiteSpace(update.Title) || key == _lyricsKey)
        {
            return;
        }

        _lyricsKey = key;
        _lyrics = null;
        _lyricsIndex = -1;
        SetLyricsStatus("Buscando letras…");

        var token = ++_lyricsToken;
        _ = FetchLyricsAsync(update.Title, update.Artist, token);
    }

    private async Task FetchLyricsAsync(string title, string artist, long token)
    {
        var lines = await LyricsService.GetAsync(title, artist);
        if (token != _lyricsToken || !AppSettings.Current.LyricsEnabled)
        {
            return;
        }

        if (lines is { Length: > 0 })
        {
            _lyrics = lines;
            ShowLyricsContent();
        }
        else
        {
            SetLyricsStatus("Sem letras encontradas para esta música");
        }
    }

    private void SetLyricsStatus(string text)
    {
        LyricsStatusText.Text = text;
        LyricsStatusText.Visibility = Visibility.Visible;
        LyricsScroll.Visibility = Visibility.Collapsed;
    }

    private void ShowLyricsContent()
    {
        if (_lyrics is not { Length: > 0 })
        {
            return;
        }

        LyricsItems.ItemsSource = _lyrics.Select(l => l.Text).ToArray();
        _lyricsIndex = -1;
        LyricsScroll.Visibility = Visibility.Visible;
        LyricsStatusText.Visibility = Visibility.Collapsed;
    }

    private void SyncLyrics()
    {
        if (!_lyricsExpanded || _lyrics is not { Length: > 0 } lines)
        {
            return;
        }

        var position = _media.IsPlaying
            ? _positionAtStamp + (DateTime.UtcNow - _positionStamp)
            : _positionAtStamp;
        position += TimeSpan.FromMilliseconds(300);

        var index = 0;
        while (index < lines.Length - 1 && lines[index + 1].Time <= position)
        {
            index++;
        }

        if (index != _lyricsIndex)
        {
            _lyricsIndex = index;
            StyleLyricsLines(lines, index);
        }

        // Centraliza pela POSIÇÃO REAL da linha na árvore visual (as linhas têm
        // altura variável: a ativa é maior; pitch fixo acumula desvio).
        CenterActiveLyric();
    }

    private void CenterActiveLyric()
    {
        if (_lyricsIndex < 0 ||
            LyricsItems.ItemContainerGenerator.ContainerFromIndex(_lyricsIndex)
                is not ContentPresenter presenter)
        {
            return;
        }

        // O estilo da linha ativa acabou de mudar (fonte maior): força o layout
        // antes de medir, senão a geometria lida está desatualizada.
        LyricsScroll.UpdateLayout();

        // Mede em coordenadas do CONTEÚDO do ScrollViewer — não da janela —
        // para ficar imune ao recorte/transformação visual da janela.
        if (LyricsScroll.Content is not System.Windows.Controls.StackPanel stack)
        {
            return;
        }

        // Posição do topo da linha dentro do StackPanel raiz do conteúdo.
        var lineTopInContent = presenter.TransformToVisual(stack).Transform(new System.Windows.Point(0, 0)).Y;

        // Centro da viewport, medindo com a viewport REAL (recorte incluso).
        var target = lineTopInContent
                     - (LyricsScroll.ViewportHeight - presenter.ActualHeight) / 2.0;

        var maxOffset = Math.Max(0, LyricsScroll.ExtentHeight - LyricsScroll.ViewportHeight);
        AnimateLyricsScroll(Math.Clamp(target, 0, maxOffset));
    }

    /// <summary>Rola o painel suavemente até o alvo (estilo apps de música).</summary>
    private void AnimateLyricsScroll(double target)
    {
        if (Math.Abs(LyricsScroll.VerticalOffset - target) < 0.5)
        {
            return;
        }

        // Para qualquer animação de render anterior antes de registrar outra.
        CompositionTarget.Rendering -= OnRenderingScroll;
        _scrollFrom = LyricsScroll.VerticalOffset;
        _scrollTo = target;
        _scrollStart = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRenderingScroll;
    }

    private double _scrollFrom;
    private double _scrollTo;
    private DateTime _scrollStart;

    /// <summary>Anima VerticalOffset manualmente via frame de composição.</summary>
    private void OnRenderingScroll(object? sender, EventArgs e)
    {
        var t = (DateTime.UtcNow - _scrollStart).TotalMilliseconds / 260.0;
        if (t >= 1)
        {
            LyricsScroll.ScrollToVerticalOffset(_scrollTo);
            CompositionTarget.Rendering -= OnRenderingScroll;
            return;
        }

        var k = 1 - Math.Pow(1 - t, 3); // easeOutCubic
        LyricsScroll.ScrollToVerticalOffset(
            _scrollFrom + (_scrollTo - _scrollFrom) * k);
    }

    /// <summary>Efeito spicy-lyrics: ativa em destaque, passadas apagadas, futuras visíveis.</summary>
    private void StyleLyricsLines(LyricLine[] lines, int active)
    {
        for (var i = 0; i < lines.Length; i++)
        {
            if (LyricsItems.ItemContainerGenerator.ContainerFromIndex(i) is not ContentPresenter presenter ||
                presenter.ContentTemplate?.FindName("LineText", presenter) is not TextBlock text)
            {
                continue;
            }

            // 3 estados como Spotify/Apple Music: passada < futura < ativa.
            var isActive = i == active;
            var isPast = i < active;

            text.FontSize = isActive ? 15.5 : 12.5;
            text.FontWeight = isActive ? FontWeights.SemiBold : FontWeights.Normal;
            text.Foreground = new SolidColorBrush(
                isActive ? Colors.White
                : isPast ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)   // passada: bem apagada
                         : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)); // futura: legível
            text.Effect = isActive
                ? new DropShadowEffect { Color = Colors.White, Opacity = 0.55, BlurRadius = 9, ShadowDepth = 0 }
                : null;
        }
    }

    private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricsExpanded)
        {
            CollapseLyrics();
            return;
        }

        _lyricsExpanded = true;
        var heightAnim = new DoubleAnimation(Height, LyricsExpandedHeight, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(HeightProperty, heightAnim);
        Animate(LyricsChevronRotate, RotateTransform.AngleProperty, 0, 180, 280);
        LyricsPanel.Visibility = Visibility.Visible;

        if (_lyrics is { Length: > 0 })
        {
            ShowLyricsContent();
            SyncLyrics();
        }
        else if (ReferenceEquals(_media, MediaUpdate.Idle))
        {
            SetLyricsStatus("Ponha uma música tocando para ver a letra");
        }
        else
        {
            SetLyricsStatus(_lyricsKey is null ? "Buscando letras…" : "Sem letras encontradas para esta música");
        }
    }

    private void CollapseLyrics()
    {
        _lyricsExpanded = false;
        var heightAnim = new DoubleAnimation(Height, DefaultHeight, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        heightAnim.Completed += (_, _) => LyricsPanel.Visibility = Visibility.Collapsed;
        BeginAnimation(HeightProperty, heightAnim);
        Animate(LyricsChevronRotate, RotateTransform.AngleProperty, 180, 0, 280);
    }

    private void OnSettingsChanged()
    {
        LyricsToggleButton.Visibility = AppSettings.Current.LyricsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyBottomBehavior();

        if (!AppSettings.Current.LyricsEnabled)
        {
            ++_lyricsToken;
            _lyricsKey = null;
            _lyrics = null;

            if (_lyricsExpanded)
            {
                CollapseLyrics();
            }
        }
        else if (!ReferenceEquals(_media, MediaUpdate.Idle))
        {
            _lyricsKey = null;
            MaybeFetchLyrics(_media);
        }
    }

    // ------------------------------------------------- general

    private void ApplyBottomBehavior()
    {
        if (AppSettings.Current.AlwaysOnBottom)
        {
            if (!_bottom.IsEnabled)
            {
                _bottom.Start();
            }

            if (IsVisible && _hwnd != IntPtr.Zero)
            {
                DesktopFx.PlaceBelowWindows(_hwnd);
            }
        }
        else
        {
            _bottom.Stop();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject { } source &&
            FindAncestor<Button>(source) is not null)
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
            ? Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0x6F, 0xD3, 0xFF));
        Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
    }

    private void SaveSettings()
    {
        var settings = AppSettings.Current;
        settings.Left = Left;
        settings.Top = Top;
        settings.Locked = _locked;
        settings.Save();
    }

    private void LoadSettings()
    {
        var settings = AppSettings.Current;
        if (!settings.HasSavedPosition)
        {
            return;
        }

        Left = settings.Left;
        Top = settings.Top;
        _locked = settings.Locked;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = VisualTreeHelper.GetParent(current);
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
        var app = (App)Application.Current;
        if (!app.IsQuitting)
        {
            e.Cancel = true;
            Hide();
            app.NotifyHidden();
        }
    }
}