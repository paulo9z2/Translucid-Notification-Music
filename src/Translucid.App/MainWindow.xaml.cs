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
using Run = System.Windows.Documents.Run;
using Application = System.Windows.Application;

namespace Translucid.App;

public partial class MainWindow : Window
{
    private const double DefaultHeight = 172;
    private const double LyricsExpandedHeight = 408;
    private const double DefaultWidth = 440;
    private const double MinScale = 0.7;
    private const double MaxScale = 1.6;
    private const double PaletteSeconds = 1.4;
    private static readonly byte[] PaletteAlphas = { 0x40, 0x34, 0x2A };
    private bool _isInitializingSize = true;

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
        _isInitializingSize = false;

        LyricsToggleButton.Visibility = AppSettings.Current.LyricsEnabled
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyLockVisual();
        ApplyBottomBehavior();
        _tracker.Updated += OnMediaUpdated;
        await _tracker.StartAsync();
        _tick.Start();
        _ = CheckForUpdatesAsync();
    }

    // ------------------------------------------------------------- update

    private UpdateChecker.UpdateInfo? _update;

    /// <summary>Verifica o GitHub em background; se houver release novo, mostra o pill azul.</summary>
    private async Task CheckForUpdatesAsync()
    {
        var info = await UpdateChecker.CheckAsync(App.CurrentVersion).ConfigureAwait(true);
        if (info is null)
        {
            return;
        }

        await Dispatcher.InvokeAsync(() =>
        {
            _update = info;
            UpdateButton.Visibility = Visibility.Visible;
            UpdateButton.Content = $"update {info.Tag}";
        });
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_update is not { } info)
        {
            return;
        }

        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "baixando…";
        UpdateStatusText.Visibility = Visibility.Visible;

        try
        {
            await Task.Run(() => UpdateInstaller.InstallAsync(info)).ConfigureAwait(true);

            // O .cmd de troca já foi lançado e está esperando este processo
            // morrer. Sai de verdade (não vai para a bandeja).
            ((App)Application.Current).QuitForUpdate();
        }
        catch
        {
            // Falhou o download/extração: restaura o pill para tentar de novo.
            UpdateStatusText.Visibility = Visibility.Collapsed;
            UpdateButton.Visibility = Visibility.Visible;
        }
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
    /// cortado pelo DWM. Reaplica a região a cada mudança de tamanho e mantém
    /// o conteúdo escalado proporcionalmente.
    /// </summary>
    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_hwnd != IntPtr.Zero)
        {
            DesktopFx.RoundCorners(_hwnd, 14);
        }

        if (!_isInitializingSize && e.PreviousSize is { Width: > 0 })
        {
            // Conteúdo acompanha a largura da janela (o usuário redimensionou
            // pela borda): escala = nova largura / largura padrão de design.
            ContentScale = Math.Clamp(ActualWidth / DefaultWidth, MinScale, MaxScale);
        }

        // Painel de letras cresce junto com a altura extra da janela.
        var lyricsHeight = Math.Max(80, ActualHeight - DefaultBaseHeight() - 36);
        LyricsScroll.Height = lyricsHeight;
        LyricsStatusText.Height = lyricsHeight;
    }

    /// <summary>Altura base do widget SEM letras (172 no padrão; escala com o conteúdo).</summary>
    private double DefaultBaseHeight() => 172 * ContentScale;

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

        // Ícone na capa acompanha o volume (aparece no hover)
        var pct = (int)Math.Round(volume * 100);
        VolumeHoverPct.Text = $"{pct}%";
        if (pct <= 0)
            VolumeHoverIcon.Text = "\uE74F"; // mudo
        else
            VolumeHoverIcon.Text = "\uE767"; // volume

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

    // Hover na capa: mostra o badge com ícone + % do volume do app tocando
    private void CoverFrame_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        var volume = VolumeMixer.Get(_media.AppProcessName) ?? 0f;
        var pct = (int)Math.Round(volume * 100);
        VolumeHoverPct.Text = $"{pct}%";
        VolumeHoverIcon.Text = pct <= 0 ? "\uE74F" : "\uE767";

        VolumeHoverBadge.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(120)));
    }

    private void CoverFrame_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        VolumeHoverBadge.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
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

        LyricsItems.ItemsSource = _lyrics; // itens são LyricLine (não só texto)
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

        // Karaokê por palavra na linha ativa (estilo Spicy Lyrics).
        UpdateActiveWordProgress();

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

    // ------------------------------------------------- seek pelas letras

    /// <summary>Clique numa linha: pula a música para o tempo dela no LRC.</summary>
    private async void LyricLine_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { DataContext: LyricLine clicked } ||
            _lyrics is not { Length: > 0 } lines)
        {
            return;
        }

        var accepted = await _tracker.SeekAsync(clicked.Time).ConfigureAwait(true);
        if (!accepted)
        {
            FlashLyricsSeekDenied();
            return;
        }

        // Seek aceito: reposiciona o relógio local na hora pedida para a UI
        // não continuar contando do lugar antigo até o próximo evento SMTC.
        _positionAtStamp = clicked.Time;
        _positionStamp = DateTime.UtcNow;
        RenderPosition();
        SyncLyrics();
    }

    /// <summary>Pisca o painel em vermelho quando o app de origem nega o seek.</summary>
    private void FlashLyricsSeekDenied()
    {
        var anim = new DoubleAnimation(0.35, TimeSpan.FromMilliseconds(120))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
        };
        LyricsScroll.BeginAnimation(UIElement.OpacityProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// Efeito spicy-lyrics: ativa em destaque, passadas apagadas, futuras
    /// visíveis. A linha ativa com marcações por palavra ganha o gradiente
    /// varredor (palavra cantada fica branca, a futura translúcida).
    /// </summary>
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

            if (!isActive || lines[i].Words is not { Count: >= 2 } words)
            {
                // Linha sem karaokê: cor sólida, sem Runs.
                if (text.Inlines.Count == 0 || text.Tag as string != "plain")
                {
                    text.Inlines.Clear();
                    text.Inlines.Add(new Run(lines[i].Text));
                    text.Tag = "plain";
                    text.TextDecorations = null;
                }
                text.Foreground = new SolidColorBrush(
                    isActive ? Colors.White
                    : isPast ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)   // passada: bem apagada
                             : Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF)); // futura: legível
                text.Effect = isActive
                    ? new DropShadowEffect { Color = Colors.White, Opacity = 0.55, BlurRadius = 9, ShadowDepth = 0 }
                    : null;
                continue;
            }

            BuildWordRuns(text, lines[i]);
            text.Effect = new DropShadowEffect { Color = Colors.White, Opacity = 0.55, BlurRadius = 9, ShadowDepth = 0 };
        }
    }

    /// <summary>Monta os Runs por palavra da linha ativa (uma vez por troca de linha).</summary>
    private void BuildWordRuns(TextBlock text, LyricLine line)
    {
        if (text.Tag as string == "words" && text.Inlines.Count == line.Words!.Count)
        {
            return; // já montada
        }

        _wordRuns = new List<(Run, SolidColorBrush)>(line.Words!.Count);
        text.Inlines.Clear();
        foreach (var w in line.Words!)
        {
            var brush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
            brush.Freeze();
            var run = new Run(w.Text) { Foreground = brush };
            text.Inlines.Add(run);
            _wordRuns.Add((run, brush));
        }
        text.Tag = "words";
        _activeLineWords = line.Words;
    }

    private List<(Run Run, SolidColorBrush Brush)>? _wordRuns;
    private IReadOnlyList<WordSpan>? _activeLineWords;

    /// <summary>
    /// O efeito Spicy: a linha ativa é pintada palavra a palavra conforme o
    /// tempo avança. Com marcações &lt;mm:ss.xx&gt; no LRC estendido o avanço é
    /// exato; sem elas, a progressão é proporcional à duração real da linha
    /// (start_ms/end_ms), como no Spicy Lyrics.
    /// </summary>
    private void UpdateActiveWordProgress()
    {
        if (_wordRuns is null || !_lyricsExpanded || _lyricsIndex < 0 || _lyrics is not { } lines)
        {
            return;
        }

        var position = CurrentPlaybackPosition();
        var line = lines[_lyricsIndex];
        var total = line.End - line.Time;
        var progress = total > TimeSpan.Zero
            ? Math.Clamp((position - line.Time).TotalMilliseconds / total.TotalMilliseconds, 0, 1)
            : 1.0;

        // Palavra "atual" pela progressão relativa na linha.
        var currentWordIndex = -1;
        if (line.Words is { Count: > 0 } words)
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (position >= words[i].Start)
                {
                    currentWordIndex = i;
                }
                else break;
            }
        }

        for (var i = 0; i < _wordRuns.Count; i++)
        {
            double opacity;
            if (currentWordIndex >= 0)
            {
                // Tem timestamps de palavra: cantadas = 1, atual acende, futuras apagadas.
                opacity = i < currentWordIndex ? 1.0
                        : i == currentWordIndex ? 1.0
                        : i == currentWordIndex + 1 ? 0.55
                        : 0.45;
            }
            else
            {
                // Sem timestamps por palavra: gradiente contínuo pela posição.
                // Cada palavra acende quando a "onda" de leitura chega nela.
                var wordFraction = (i + 0.5) / _wordRuns.Count;
                var delta = progress - wordFraction;
                opacity = delta >= 0 ? 1.0
                        : delta > -0.08 ? 0.75
                        : delta > -0.18 ? 0.55
                        : 0.45;
            }

            var (_, brush) = _wordRuns[i];
            var target = (byte)(255 * opacity);
            if (Math.Abs(brush.Color.A - target) > 4)
            {
                brush.Color = Color.FromArgb(target, 0xFF, 0xFF, 0xFF);
            }
        }
    }

    private TimeSpan CurrentPlaybackPosition()
    {
        var position = _media.IsPlaying
            ? _positionAtStamp + (DateTime.UtcNow - _positionStamp)
            : _positionAtStamp;
        return position;
    }

    /// <summary>Altura extra que o painel de letras adiciona à janela (escala com o conteúdo).</summary>
    private double LyricsPanelTargetHeight() => 236 * ContentScale;

    private void LyricsToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lyricsExpanded)
        {
            CollapseLyrics();
            return;
        }

        _lyricsExpanded = true;
        var targetHeight = Height + LyricsPanelTargetHeight();
        var heightAnim = new DoubleAnimation(Height, targetHeight, TimeSpan.FromMilliseconds(280))
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
        var targetHeight = Math.Max(DefaultHeight * ContentScale, Height - LyricsPanelTargetHeight());
        var heightAnim = new DoubleAnimation(Height, targetHeight, TimeSpan.FromMilliseconds(280))
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

        // Altura SEM o painel de letras: se fechou com letras abertas, a Height
        // atual inclui os ~236*scale do painel — salvar assim fazia o widget
        // nascer gigante no boot seguinte. Normaliza para a base antes de salvar.
        var savedHeight = _lyricsExpanded ? Height - LyricsPanelTargetHeight() : Height;
        settings.Width = Width;
        settings.Height = Math.Max(DefaultHeight * ContentScale, savedHeight);
        settings.Scale = ContentScale;
        settings.LyricsExpanded = _lyricsExpanded;
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

        // Tamanho personalizado pelo usuário (resize pela borda).
        if (settings is { Width: > 0, Height: > 0 })
        {
            var w = Math.Clamp(settings.Width, MinWidth, MaxWidth);
            var h = Math.Clamp(settings.Height, MinHeight, MaxHeight);
            if (Math.Abs(w - DefaultWidth) > 0.5 || Math.Abs(h - DefaultHeight) > 0.5)
            {
                Width = w;
                Height = h;
            }
        }

        if (settings.Scale > 0)
        {
            ContentScale = Math.Clamp(settings.Scale, MinScale, MaxScale);
        }

        // Reabre o painel de letras se estava aberto quando fechou. A Height
        // salva já é a base (sem painel) — a expansão soma por cima dela.
        if (settings.LyricsExpanded && AppSettings.Current.LyricsEnabled
                                   && settings is { Width: > 0, Height: > 0 })
        {
            Dispatcher.BeginInvoke(() =>
            {
                _lyricsExpanded = true;
                LyricsChevronRotate.Angle = 180;
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
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    /// <summary>Altura extra de letras proporcional à largura atual.</summary>
    private double LyricsExtraForWidth(double width) => width * 0.536; // (408-172)/440

    /// <summary>
    /// Escala do conteúdo interno: aplicada como LayoutTransform para tudo
    /// (capa, fontes, botões, paddings) crescer/diminuir junto com a janela.
    /// </summary>
    private double _contentScale = 1.0;
    private double ContentScale
    {
        get => _contentScale;
        set
        {
            var v = Math.Clamp(value, MinScale, MaxScale);
            if (Math.Abs(v - _contentScale) < 0.001)
            {
                return;
            }

            _contentScale = v;
            Shell.LayoutTransform = new ScaleTransform(v, v);
            UpdateLayout();
        }
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