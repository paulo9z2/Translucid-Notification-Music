using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Translucid.Core;

namespace Translucid.App;

/// <summary>
/// Janela de progresso do update: card escuro com barra pill azul (#5CB8FF),
/// velocidade e bytes — tema igual ao widget (acrylic + borda translúcida).
/// </summary>
public partial class UpdateProgressWindow : Window
{
    private readonly DispatcherTimer _speedTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(120),
    };
    private UpdateProgress? _last;
    private bool _indeterminateRunning;

    public UpdateProgressWindow()
    {
        InitializeComponent();
        _speedTimer.Tick += (_, _) => Render(_last);
        _speedTimer.Start();

        // pulso indeterminado (enquanto o total é desconhecido)
        var pulse = new DoubleAnimation(-60, 380, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        PulseBlock.BeginAnimation(Canvas.LeftProperty, pulse);
        _indeterminateRunning = true;
    }

    /// <summary>Chamado de qualquer thread; faz o marshal e atualiza suave.</summary>
    public void Report(UpdateProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            _last = progress;
            Render(progress);
        });
    }

    private void Render(UpdateProgress? p)
    {
        if (p is null)
        {
            return;
        }

        TitleText.Text = p.Stage switch
        {
            UpdateProgressStage.Verifying => "Verificando integridade",
            UpdateProgressStage.Installing => "Preparando instalação",
            _ => "Baixando atualização",
        };
        TitleIcon.Text = p.Stage switch
        {
            UpdateProgressStage.Verifying => "\uE72E", // escudo
            UpdateProgressStage.Installing => "\uE9F5", // engrenagem
            _ => "\uE896", // seta de download
        };

        var hasTotal = p.TotalBytes > 0;

        if (p.Stage == UpdateProgressStage.Indeterminate || !hasTotal)
        {
            // trilho vazio + pulso deslizante
            if (!_indeterminateRunning)
            {
                BarFill.Width = 0;
                IndeterminateHost.Visibility = Visibility.Visible;
                var pulse = new DoubleAnimation(-60, 380, TimeSpan.FromMilliseconds(1100))
                {
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                PulseBlock.BeginAnimation(Canvas.LeftProperty, pulse);
                _indeterminateRunning = true;
            }
        }
        else
        {
            if (_indeterminateRunning)
            {
                IndeterminateHost.Visibility = Visibility.Collapsed;
                PulseBlock.BeginAnimation(Canvas.LeftProperty, null);
                _indeterminateRunning = false;
            }
            var pct = Math.Clamp(p.BytesReceived / (double)p.TotalBytes, 0, 1);
            var width = (BarTrack.ActualWidth > 0 ? BarTrack.ActualWidth : 330) * pct;
            BarFill.BeginAnimation(WidthProperty,
                new DoubleAnimation(width, TimeSpan.FromMilliseconds(120)));
        }

        SpeedText.Text = p.Stage == UpdateProgressStage.Downloading && p.BytesPerSecond > 0
            ? $"{p.BytesPerSecond / (1024 * 1024):0.0} MB/s"
            : p.Message;

        BytesText.Text = hasTotal
            ? $"{p.BytesReceived / (1024.0 * 1024):0.0} MB / {p.TotalBytes / (1024.0 * 1024):0.0} MB"
            : "";
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();
}
