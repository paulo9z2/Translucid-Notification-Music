using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Translucid.App;

/// <summary>
/// Ação disparada por um atalho global registrado.
/// </summary>
public enum HotkeyAction
{
    PlayPause,
    Next,
    Previous,
}

/// <summary>
/// Atalhos globais via user32 RegisterHotKey.
/// <para/>
/// Registra Ctrl+Alt+P (play/pause), Ctrl+Alt+N (next), Ctrl+Alt+B (previous)
/// com WM_HOTKEY capturado via HwndSource hook. Funciona mesmo sem foco no widget.
/// <para/>
/// Conflitos: RegisterHotKey retorna false + GetLastError 1409 (ERROR_HOTKEY_ALREADY_REGISTERED)
/// quando outro app já registrou o mesmo combo. Nesse caso logamos, expomos
/// <see cref="LastError"/> e mantemos os demais atalhos que conseguiram registrar —
/// nunca derruba o app.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyIdPlayPause = 0x9000;
    private const int HotkeyIdNext = 0x9001;
    private const int HotkeyIdPrev = 0x9002;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkP = 0x50; // P
    private const uint VkN = 0x4E; // N
    private const uint VkB = 0x42; // B (back/previous)

    private static uint Modifiers => ModControl | ModAlt | ModNoRepeat;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>True quando ao menos um atalho foi registrado com sucesso.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>Último erro de registro (ex.: conflito com outro app). Null quando OK.</summary>
    public string? LastError { get; private set; }

    /// <summary>Quantos dos 3 atalhos foram registrados (0..3).</summary>
    public int RegisteredCount { get; private set; }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>
    /// Registra os 3 atalhos no hwnd indicado. Deve ser chamado após OnSourceInitialized,
    /// quando o HwndSource já existe. Safe para chamar múltiplas vezes (re-registra).
    /// Retorna true se ao menos um atalho pegou; false se nenhum pegou (todos conflitaram).
    /// </summary>
    public bool TryRegister(IntPtr hwnd)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(HotkeyManager));

        // Re-entrada: limpa registro anterior antes de tentar de novo.
        UnregisterInternal();

        if (hwnd == IntPtr.Zero)
        {
            LastError = "HWND nulo — janela ainda não criada.";
            return false;
        }

        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        if (_source is null)
        {
            // Fallback: tenta criar HwndSource para hook (caso raro de chamada precoce).
            LastError = "HwndSource não encontrado para o HWND; tente chamar após OnSourceInitialized.";
            Debug.WriteLine($"[HotkeyManager] {LastError}");
            return false;
        }

        _source.AddHook(WndProc);

        var ok = 0;
        var errors = new List<string>(3);

        if (TryRegisterOne(HotkeyIdPlayPause, Modifiers, VkP, "Ctrl+Alt+P")) ok++;
        else errors.Add("Ctrl+Alt+P");

        if (TryRegisterOne(HotkeyIdNext, Modifiers, VkN, "Ctrl+Alt+N")) ok++;
        else errors.Add("Ctrl+Alt+N");

        if (TryRegisterOne(HotkeyIdPrev, Modifiers, VkB, "Ctrl+Alt+B")) ok++;
        else errors.Add("Ctrl+Alt+B");

        RegisteredCount = ok;
        IsRegistered = ok > 0;

        if (errors.Count > 0)
        {
            var msg = errors.Count == 3
                ? $"Nenhum atalho registrado — todos conflitaram: {string.Join(", ", errors)}."
                : $"Atalhos em conflito (não registrados): {string.Join(", ", errors)} — os demais funcionam.";
            LastError = msg;
            Debug.WriteLine($"[HotkeyManager] {msg} (err={Marshal.GetLastWin32Error()})");
            // Não dá throw; mantém os que pegaram.
        }
        else
        {
            LastError = null;
            Debug.WriteLine($"[HotkeyManager] 3/3 atalhos registrados (Ctrl+Alt+P/N/B).");
        }

        return IsRegistered;
    }

    private bool TryRegisterOne(int id, uint mods, uint vk, string label)
    {
        var ok = RegisterHotKey(_hwnd, id, mods, vk);
        if (!ok)
        {
            var err = Marshal.GetLastWin32Error();
            const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
            var detail = err == ERROR_HOTKEY_ALREADY_REGISTERED
                ? "já registrado por outro app"
                : $"erro Win32 {err}";
            Debug.WriteLine($"[HotkeyManager] Falha ao registrar {label} (id=0x{id:X}): {detail}");
        }
        return ok;
    }

    /// <summary>Remove todos os atalhos e o hook de janela.</summary>
    public void Unregister()
    {
        UnregisterInternal();
        LastError = null;
        RegisteredCount = 0;
        IsRegistered = false;
    }

    private void UnregisterInternal()
    {
        if (_source is not null)
        {
            try { _source.RemoveHook(WndProc); } catch { /* ignore */ }
            _source = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
            // Tenta desregistrar cada id; falha silenciosa se não estava registrado.
            TryUnregister(HotkeyIdPlayPause);
            TryUnregister(HotkeyIdNext);
            TryUnregister(HotkeyIdPrev);
        }

        _hwnd = IntPtr.Zero;
        IsRegistered = false;
        RegisteredCount = 0;
    }

    private void TryUnregister(int id)
    {
        try { UnregisterHotKey(_hwnd, id); } catch { /* ignore */ }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            var id = wParam.ToInt32();
            var action = id switch
            {
                HotkeyIdPlayPause => (HotkeyAction?)HotkeyAction.PlayPause,
                HotkeyIdNext => HotkeyAction.Next,
                HotkeyIdPrev => HotkeyAction.Previous,
                _ => null,
            };
            if (action.HasValue)
            {
                handled = true;
                try { Pressed?.Invoke(action.Value); }
                catch (Exception ex) { Debug.WriteLine($"[HotkeyManager] handler erro: {ex.Message}"); }
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterInternal();
        GC.SuppressFinalize(this);
    }
}
