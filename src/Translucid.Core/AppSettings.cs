using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Translucid.Core;

/// <summary>
/// Configurações persistentes do widget: posição, travas e preferências,
/// salvas em %LOCALAPPDATA%\Translucid\ui.json. O autostart vive no registro
/// (HKCU Run), igual aos apps normais do Windows.
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
    public bool Locked { get; set; } = true;
    public bool LyricsEnabled { get; set; }

    /// <summary>
    /// Ativado: o widget fica sempre atrás de todas as janelas (camada de
    /// fundo). Desativado: comportamento normal de uma janela.
    /// </summary>
    public bool AlwaysOnBottom { get; set; } = true;

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
                    settings.HasSavedPosition = true;
                    return settings;
                }
            }
        }
        catch
        {
            // corrompeu? ignora e usa o padrão
        }

        return new AppSettings();
    }
}