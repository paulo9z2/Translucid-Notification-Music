using System.Runtime.InteropServices;

namespace Translucid.Core;

public static class DesktopFx
{
    private const int WcaAccentPolicy = 19;
    private const int AccentAcrylicBlurBehind = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(
        int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndBottom = new(1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    // Valores do DWMWCP_ / DWMSBT_
    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_TRANSIENTWINDOW = 2; // acrylic/blur de flyout do Win11

    /// <summary>
    /// Backdrop nativo do Windows 11 (DWM): o proprio sistema desenha o blur e
    /// ARREDONDA os cantos da janela. Retorna true se o DWM aceitou.
    /// </summary>
    public static bool TryCornerRounding(IntPtr hwnd)
    {
        try
        {
            int corner = DWMWCP_ROUND;
            return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;

    /// <summary>
    /// Recorta a janela em retangulo arredondado. O sistema assume a posse da
    /// regiao depois de SetWindowRgn (nao precisa delete manual).
    /// </summary>
    public static void RoundCorners(IntPtr hwnd, int radius)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return;
        }

        var hRgn = CreateRoundRectRgn(
            0, 0, rect.Right - rect.Left + 1, rect.Bottom - rect.Top + 1,
            radius * 2, radius * 2);

        if (hRgn != IntPtr.Zero)
        {
            SetWindowRgn(hwnd, hRgn, true);
        }
    }

    /// <summary>
    /// Aplica o efeito Acrylic do Windows 10/11 na janela indicada.
    /// Retorna true se o DWM aceitou o efeito.
    /// </summary>
    public static bool EnableAcrylic(IntPtr hwnd, int tintArgb = unchecked((int)0x88000000))
    {
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentAcrylicBlurBehind,
                AccentFlags = 0x20,
                GradientColor = tintArgb,
                AnimationId = 0,
            };

            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = Marshal.AllocHGlobal(Marshal.SizeOf<AccentPolicy>()),
                SizeOfData = Marshal.SizeOf<AccentPolicy>(),
            };

            try
            {
                Marshal.StructureToPtr(accent, data.Data, false);
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(data.Data);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tira a janela do Alt+Tab (janela "tool").</summary>
    public static void HideFromAltTab(IntPtr hwnd)
    {
        var style = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, style | (int)WS_EX_TOOLWINDOW);
    }

    /// <summary>
    /// Coloca a janela abaixo de todas as outras do sistema (HWND_BOTTOM) e
    /// impede que ela se ative. Para manter o widget "sempre atrás", chame
    /// isso periodicamente (o Windows re-ordena quando outras janelas mudam).
    /// </summary>
    public static void PlaceBelowWindows(IntPtr hwnd)
    {
        try
        {
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoActivate);
        }
        catch
        {
            // janela já destruída ou sem dono? ignora
        }
    }
}