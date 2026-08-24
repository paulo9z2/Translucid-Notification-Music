using System.Windows;

namespace Translucid.App;

/// <summary>
/// Aplica o icone na bandeja (icones ocultos) — o widget nao fica na taskbar.
/// </summary>
public partial class App : System.Windows.Application
{
    public App()
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    private System.Windows.Forms.NotifyIcon? _tray;
    private bool _balloonShown;

    /// <summary>True quando o usuario realmente quer sair (menu "Sair" da bandeja).</summary>
    public bool IsQuitting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "")
            ?? System.Drawing.SystemIcons.Application;

        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Translucid Notification Music",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Mostrar widget", null, (_, _) => ShowWindow(true));
        menu.Items.Add("Esconder widget", null, (_, _) => ShowWindow(false));
        menu.Items.Add("Configurações…", null, (_, _) => OpenSettings());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => Quit());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowWindow(true);
    }

    /// <summary>Notifica uma vez que o widget foi para a bandeja.</summary>
    public void NotifyHidden()
    {
        if (_balloonShown || _tray is null)
        {
            return;
        }

        _balloonShown = true;
        _tray.ShowBalloonTip(
            2500, "Translucid",
            "O widget continua rodando aqui na barra de sistema.",
            System.Windows.Forms.ToolTipIcon.Info);
    }

    private void ShowWindow(bool show)
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is null)
            {
                return;
            }

            if (show)
            {
                MainWindow.Show();
                MainWindow.WindowState = WindowState.Normal;
                MainWindow.Activate();
            }
            else
            {
                MainWindow.Hide();
            }
        });
    }

    public void OpenSettings()
    {
        Dispatcher.Invoke(SettingsWindow.ShowOrFocus);
    }

    private void Quit()
    {
        IsQuitting = true;
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}