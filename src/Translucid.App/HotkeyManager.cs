using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Translucid.Core;

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
/// Lê dinamicamente <see cref="AppSettings.HotkeyPause"/> / Next / Prev
/// (formato "Ctrl + Alt + P") via <see cref="ParseHotkey"/> / <see cref="TryParseHotkey"/>.
/// Fallback para Ctrl+Alt+P/N/B quando o parse falha. String nula/vazia = slot desabilitado.
/// <para/>
/// Conflitos: RegisterHotKey retorna false + GetLastError 1409 (ERROR_HOTKEY_ALREADY_REGISTERED)
/// quando outro app já registrou o mesmo combo. Nesse caso logamos, expomos
/// <see cref="LastError"/> e mantemos os demais atalhos — nunca derruba o app.
/// <para/>
/// Thread-safety: todo estado mutável é protegido por lock(_sync). <see cref="Reload"/>
/// pode ser chamado de qualquer thread (ex.: AppSettings.Changed).
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyIdPlayPause = 0x9000;
    private const int HotkeyIdNext = 0x9001;
    private const int HotkeyIdPrev = 0x9002;

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const uint VkP = 0x50; // P
    private const uint VkN = 0x4E; // N
    private const uint VkB = 0x42; // B (back/previous)

    private const string DefaultPauseLabel = "Ctrl + Alt + P";
    private const string DefaultNextLabel = "Ctrl + Alt + N";
    private const string DefaultPrevLabel = "Ctrl + Alt + B";

    private static uint DefaultPauseMods => ModControl | ModAlt;
    private static uint DefaultNextMods => ModControl | ModAlt;
    private static uint DefaultPrevMods => ModControl | ModAlt;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly object _sync = new();
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _disposed;

    /// <summary>True quando ao menos um atalho foi registrado com sucesso.</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>Último erro de registro (ex.: conflito com outro app). Null quando OK.</summary>
    public string? LastError { get; private set; }

    /// <summary>Quantos dos atalhos foram registrados (0..3).</summary>
    public int RegisteredCount { get; private set; }

    public event Action<HotkeyAction>? Pressed;

    // ---------------------------------------------------------------- Parse

    /// <summary>
    /// Tenta converter string "Ctrl + Alt + P" em (MOD_*, VK).
    /// Aceita Ctrl/Control, Alt, Shift, Win/Windows/Super/Meta + Key.
    /// Key: A-Z, 0-9, F1-F24, Space, Insert, Delete, Home, End, PageUp, PageDown,
    /// Left/Right/Up/Down, +, -, ,, ., Numpad0-9 etc.
    /// Retorna false se vazio/invalido.
    /// Modifiers retornados NÃO incluem MOD_NOREPEAT — caller adiciona.
    /// </summary>
    public static bool TryParseHotkey(string? text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmedOverall = text.Trim();
        if (trimmedOverall.EndsWith("+") && !trimmedOverall.EndsWith("++"))
        {
            var prefix = trimmedOverall.Substring(0, trimmedOverall.Length - 1).TrimEnd();
            if (prefix.EndsWith("+"))
                prefix = prefix.Substring(0, prefix.Length - 1).TrimEnd();
            if (!string.IsNullOrWhiteSpace(prefix))
            {
                var modParts = prefix.Split('+');
                uint modsTmp = 0;
                var okMods = true;
                foreach (var mp in modParts)
                {
                    var tm = mp.Trim();
                    if (string.IsNullOrEmpty(tm)) continue;
                    if (tm.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || tm.Equals("Control", StringComparison.OrdinalIgnoreCase)) modsTmp |= ModControl;
                    else if (tm.Equals("Alt", StringComparison.OrdinalIgnoreCase)) modsTmp |= ModAlt;
                    else if (tm.Equals("Shift", StringComparison.OrdinalIgnoreCase)) modsTmp |= ModShift;
                    else if (tm.Equals("Win", StringComparison.OrdinalIgnoreCase) || tm.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                             || tm.Equals("Super", StringComparison.OrdinalIgnoreCase) || tm.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                             || tm.Equals("LWin", StringComparison.OrdinalIgnoreCase) || tm.Equals("RWin", StringComparison.OrdinalIgnoreCase)) modsTmp |= ModWin;
                    else { okMods = false; break; }
                }
                if (okMods && modsTmp != 0)
                {
                    modifiers = modsTmp;
                    vk = 0xBB;
                    return true;
                }
            }
        }

        var rawParts = text.Split('+');
        var parts = new List<string>(4);
        foreach (var p in rawParts)
        {
            var t = p.Trim();
            if (!string.IsNullOrEmpty(t))
                parts.Add(t);
        }
        if (parts.Count == 0)
            return false;

        var keyToken = parts[^1];
        var modTokens = parts.Take(parts.Count - 1).ToArray();

        if (modTokens.Length == 0)
            return false;

        uint mods = 0;
        foreach (var m in modTokens)
        {
            if (m.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || m.Equals("Control", StringComparison.OrdinalIgnoreCase))
                mods |= ModControl;
            else if (m.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                mods |= ModAlt;
            else if (m.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                mods |= ModShift;
            else if (m.Equals("Win", StringComparison.OrdinalIgnoreCase) || m.Equals("Windows", StringComparison.OrdinalIgnoreCase)
                     || m.Equals("Super", StringComparison.OrdinalIgnoreCase) || m.Equals("Meta", StringComparison.OrdinalIgnoreCase)
                     || m.Equals("LWin", StringComparison.OrdinalIgnoreCase) || m.Equals("RWin", StringComparison.OrdinalIgnoreCase))
                mods |= ModWin;
            else
                return false;
        }

        if (mods == 0)
            return false;

        if (!TryMapKey(keyToken, out var vkVal))
            return false;

        modifiers = mods;
        vk = vkVal;
        return true;
    }

    /// <summary>
    /// Alias para <see cref="TryParseHotkey"/> retornando tupla nullable.
    /// Null quando parse falha.
    /// </summary>
    public static (uint Modifiers, uint Vk)? ParseHotkey(string? text)
    {
        if (TryParseHotkey(text, out var mods, out var vk))
            return (mods, vk);
        return null;
    }

    /// <summary>
    /// Overload compatível com código que espera `ParseHotkey(string, out mods, out vk)` bool.
    /// </summary>
    public static bool ParseHotkey(string? text, out uint modifiers, out uint vk)
        => TryParseHotkey(text, out modifiers, out vk);

    private static bool TryMapKey(string token, out uint vk)
    {
        vk = 0;
        if (string.IsNullOrWhiteSpace(token))
            return false;
        var t = token.Trim();

        if (t.Length == 1)
        {
            var ch = t[0];
            if ((ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z'))
            {
                vk = (uint)char.ToUpperInvariant(ch);
                return true;
            }
            if (ch >= '0' && ch <= '9')
            {
                vk = (uint)ch;
                return true;
            }
            switch (ch)
            {
                case '+': vk = 0xBB; return true;
                case '-': vk = 0xBD; return true;
                case ',': vk = 0xBC; return true;
                case '.': vk = 0xBE; return true;
                case '*': vk = 0x6A; return true;
                case '/': vk = 0x6F; return true;
            }
        }

        if (t.Length == 2 && (t[0] == 'D' || t[0] == 'd') && char.IsDigit(t[1]))
        {
            vk = (uint)t[1];
            return true;
        }

        if ((t.Length >= 2 && t.Length <= 3) && (t[0] == 'F' || t[0] == 'f'))
        {
            if (int.TryParse(t.Substring(1), out var n) && n >= 1 && n <= 24)
            {
                vk = (uint)(0x70 + n - 1);
                return true;
            }
        }

        var lower = t.ToLowerInvariant();
        switch (lower)
        {
            case "space": vk = 0x20; return true;
            case "enter": case "return": vk = 0x0D; return true;
            case "esc": case "escape": vk = 0x1B; return true;
            case "tab": vk = 0x09; return true;
            case "backspace": case "back": vk = 0x08; return true;
            case "insert": case "ins": vk = 0x2D; return true;
            case "delete": case "del": vk = 0x2E; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup": case "pgup": case "prior": vk = 0x21; return true;
            case "pagedown": case "pgdn": vk = 0x22; return true;
            case "left": case "arrowleft": vk = 0x25; return true;
            case "up": case "arrowup": vk = 0x26; return true;
            case "right": case "arrowright": vk = 0x27; return true;
            case "down": case "arrowdown": vk = 0x28; return true;
            case "plus": case "oemplus": case "add": vk = 0xBB; return true;
            case "minus": case "oemminus": case "subtract": vk = 0xBD; return true;
            case "comma": case "oemcomma": vk = 0xBC; return true;
            case "period": case "oemperiod": vk = 0xBE; return true;
            case "multiply": vk = 0x6A; return true;
            case "divide": vk = 0x6F; return true;
            case "decimal": vk = 0x6E; return true;
            case "numlock": vk = 0x90; return true;
            case "scroll": vk = 0x91; return true;
            case "pause": vk = 0x13; return true;
            case "capslock": vk = 0x14; return true;
            case "numpad0": vk = 0x60; return true;
            case "numpad1": vk = 0x61; return true;
            case "numpad2": vk = 0x62; return true;
            case "numpad3": vk = 0x63; return true;
            case "numpad4": vk = 0x64; return true;
            case "numpad5": vk = 0x65; return true;
            case "numpad6": vk = 0x66; return true;
            case "numpad7": vk = 0x67; return true;
            case "numpad8": vk = 0x68; return true;
            case "numpad9": vk = 0x69; return true;
            case "semicolon": case ";": vk = 0xBA; return true;
            case "colon": case ":": vk = 0xBA; return true;
            case "slash": case "oem2": vk = 0xBF; return true;
            case "tilde": case "`": case "oem3": vk = 0xC0; return true;
            case "openbracket": case "[": case "oem4": vk = 0xDB; return true;
            case "backslash": case "\\": case "oem5": vk = 0xDC; return true;
            case "closebracket": case "]": case "oem6": vk = 0xDD; return true;
            case "quote": case "'": case "oem7": vk = 0xDE; return true;
        }

        return false;
    }

    // ---------------------------------------------------------------- TryRegister

    /// <summary>
    /// Registra os atalhos no hwnd indicado lendo <see cref="AppSettings"/> dinamicamente.
    /// Deve ser chamado após OnSourceInitialized, quando o HwndSource já existe.
    /// Thread-safe; safe para chamar múltiplas vezes (re-registra).
    /// Retorna true se ao menos um atalho pegou; false se nenhum pegou.
    /// </summary>
    public bool TryRegister(IntPtr hwnd)
    {
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(HotkeyManager));
            return TryRegisterCore(hwnd);
        }
    }

    private bool TryRegisterCore(IntPtr hwnd)
    {
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
            LastError = "HwndSource não encontrado para o HWND; tente chamar após OnSourceInitialized.";
            Debug.WriteLine($"[HotkeyManager] {LastError}");
            _hwnd = IntPtr.Zero;
            return false;
        }

        AddHookSafe(_source);

        var ok = 0;
        var errors = new List<string>(3);
        var attempted = 0;

        var pause = ResolveHotkey(AppSettings.Current.HotkeyPause, DefaultPauseMods, VkP, DefaultPauseLabel);
        var next = ResolveHotkey(AppSettings.Current.HotkeyNext, DefaultNextMods, VkN, DefaultNextLabel);
        var prev = ResolveHotkey(AppSettings.Current.HotkeyPrev, DefaultPrevMods, VkB, DefaultPrevLabel);

        if (pause is { } p)
        {
            attempted++;
            if (TryRegisterOne(HotkeyIdPlayPause, p.Mods, p.Vk, p.Label)) ok++;
            else errors.Add(p.Label);
        }
        if (next is { } n)
        {
            attempted++;
            if (TryRegisterOne(HotkeyIdNext, n.Mods, n.Vk, n.Label)) ok++;
            else errors.Add(n.Label);
        }
        if (prev is { } pr)
        {
            attempted++;
            if (TryRegisterOne(HotkeyIdPrev, pr.Mods, pr.Vk, pr.Label)) ok++;
            else errors.Add(pr.Label);
        }

        RegisteredCount = ok;
        IsRegistered = ok > 0;

        if (attempted == 0)
        {
            LastError = "Nenhum atalho configurado.";
            Debug.WriteLine($"[HotkeyManager] {LastError}");
            return false;
        }

        if (errors.Count > 0)
        {
            var msg = ok == 0
                ? $"Nenhum atalho registrado — todos conflitaram: {string.Join(", ", errors)}."
                : $"Atalhos em conflito (não registrados): {string.Join(", ", errors)} — os demais funcionam.";
            LastError = msg;
            Debug.WriteLine($"[HotkeyManager] {msg} (err={Marshal.GetLastWin32Error()})");
        }
        else
        {
            LastError = null;
            Debug.WriteLine($"[HotkeyManager] {ok}/{attempted} atalhos registrados.");
        }

        return IsRegistered;
    }

    private (uint Mods, uint Vk, string Label)? ResolveHotkey(string? configured, uint fallbackMods, uint fallbackVk, string fallbackLabel)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return null;

        if (TryParseHotkey(configured, out var mods, out var vk))
        {
            mods |= ModNoRepeat;
            return (mods, vk, configured);
        }

        Debug.WriteLine($"[HotkeyManager] parse falhou para '{configured}' — fallback {fallbackLabel}");
        return (fallbackMods | ModNoRepeat, fallbackVk, fallbackLabel);
    }

    private bool TryRegisterOne(int id, uint mods, uint vk, string label)
    {
        try
        {
            var ok = RegisterHotKey(_hwnd, id, mods, vk);
            if (!ok)
            {
                var err = Marshal.GetLastWin32Error();
                const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;
                var detail = err == ERROR_HOTKEY_ALREADY_REGISTERED
                    ? "já registrado por outro app (1409)"
                    : $"erro Win32 {err}";
                Debug.WriteLine($"[HotkeyManager] Falha ao registrar {label} (id=0x{id:X}): {detail}");
            }
            return ok;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HotkeyManager] exceção ao registrar {label}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Re-registra hotkeys lendo <see cref="AppSettings"/> atuais.
    /// Chamado em <c>MainWindow.OnSettingsChanged</c> quando o usuário altera
    /// HotkeyPause/Next/Prev ou liga/desliga HotkeysEnabled.
    /// Thread-safe; no-op se ainda sem HWND ou já disposed.
    /// Retorna true se ao menos um atalho ficou registrado.
    /// </summary>
    public bool Reload()
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (_hwnd == IntPtr.Zero) return false;

            if (!AppSettings.Current.HotkeysEnabled)
            {
                UnregisterInternal();
                LastError = null;
                RegisteredCount = 0;
                IsRegistered = false;
                Debug.WriteLine("[HotkeyManager] Reload: HotkeysEnabled=false — desregistrado.");
                return false;
            }

            var hwnd = _hwnd;
            return TryRegisterCore(hwnd);
        }
    }

    /// <summary>Remove todos os atalhos e o hook de janela. Thread-safe.</summary>
    public void Unregister()
    {
        lock (_sync)
        {
            UnregisterInternal();
            LastError = null;
            RegisteredCount = 0;
            IsRegistered = false;
        }
    }

    private void UnregisterInternal()
    {
        if (_source is not null)
        {
            try { RemoveHookSafe(_source); } catch { /* ignore */ }
            _source = null;
        }

        if (_hwnd != IntPtr.Zero)
        {
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

    private void AddHookSafe(HwndSource source)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.Invoke(() => source.AddHook(WndProc));
            else
                source.AddHook(WndProc);
        }
        catch
        {
            try { source.AddHook(WndProc); } catch { }
        }
    }

    private void RemoveHookSafe(HwndSource source)
    {
        try
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp != null && !disp.CheckAccess())
                disp.Invoke(() => source.RemoveHook(WndProc));
            else
                source.RemoveHook(WndProc);
        }
        catch
        {
            try { source.RemoveHook(WndProc); } catch { }
        }
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
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            UnregisterInternal();
        }
        GC.SuppressFinalize(this);
    }
}
