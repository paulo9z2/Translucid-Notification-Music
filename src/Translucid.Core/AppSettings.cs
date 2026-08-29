using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Translucid.Core;

/// <summary>
/// Configurações persistentes do widget: posição, travas e preferências,
/// salvas em %LOCALAPPDATA%\Translucid\ui.json. O autostart vive no registro
/// (HKCU Run), igual aos apps normais do Windows.
/// Multi-monitor: salva o DeviceName do monitor onde o widget estava e um
/// dicionário de posições por tela. Ao carregar, valida se o monitor ainda
/// existe e faz clamp ao WorkArea para o widget nunca sumir.
/// </summary>
public sealed class AppSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Translucid";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Translucid", "ui.json");

    public double Left { get; set; }
    public double Top { get; set; }

    /// <summary>Largura personalizada pelo usuário (0 = usar padrão 440).</summary>
    public double Width { get; set; }

    /// <summary>Altura base (com letras fechadas) personalizada (0 = padrão 172).</summary>
    public double Height { get; set; }

    /// <summary>Fator de escala da interface (1.0 = padrão). Ajustado junto com o resize.</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Painel de letras estava aberto quando fechou (restaurado no boot).</summary>
    public bool LyricsExpanded { get; set; }

    public bool Locked { get; set; } = true;
    public bool LyricsEnabled { get; set; }

    /// <summary>Quando true, libera o resize estendido (opção B): até 1200×800 com escala 2.5×. Quando false, "dorme" nos limites padrão 900×600 / 1.6×. Persistido em ui.json.</summary>
    public bool ExtendedResizeEnabled { get; set; } = false;

    /// <summary>Ativa os atalhos globais (Ctrl+Alt+P/N/B) via RegisterHotKey.</summary>
    public bool HotkeysEnabled { get; set; } = true;

    /// <summary>
    /// Ativado: o widget fica sempre atrás de todas as janelas (camada de
    /// fundo). Desativado: comportamento normal de uma janela.
    /// </summary>
    public bool AlwaysOnBottom { get; set; } = true;

    /// <summary>
    /// DeviceName do monitor onde o widget foi salvo por último
    /// (ex: \\.\DISPLAY1). Usado para restaurar na tela correta e para
    /// detectar quando o monitor foi desconectado.
    /// </summary>
    public string? MonitorDeviceName { get; set; }

    /// <summary>
    /// Posição salva por monitor. Chave = DeviceName; valor = bounds salvos.
    /// Mantém memória de onde o widget estava em cada tela, então ao
    /// reconectar um monitor ele volta para o mesmo lugar.
    /// </summary>
    public Dictionary<string, MonitorPlacement>? MonitorPositions { get; set; }

    /// <summary>Posição de janela associada a um monitor específico.</summary>
    public sealed class MonitorPlacement
    {
        public double Left { get; set; }
        public double Top { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    /// <summary>True quando já existia um json salvo com posição.</summary>
    public bool HasSavedPosition { get; private set; }

    public event Action? Changed;

    public static AppSettings Current { get; } = Load();

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null)
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this));
            Changed?.Invoke();
        }
        catch
        {
            // nunca deixa um erro de persistência derrubar o widget
        }
    }

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
            {
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(RunValueName) is not null)
            {
                key.DeleteValue(RunValueName, false);
            }
        }
        catch
        {
            // sem acesso ao registro? segue a vida
        }
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath));
                if (settings is not null)
                {
                    settings.HasSavedPosition = settings.Width > 0 || settings.Height > 0 || settings.Left != 0 || settings.Top != 0;
                    settings.MonitorPositions ??= new Dictionary<string, MonitorPlacement>();
                    return settings;
                }
            }
        }
        catch
        {
            // corrompeu? ignora e usa o padrão
        }

        return new AppSettings
        {
            MonitorPositions = new Dictionary<string, MonitorPlacement>()
        };
    }
}
