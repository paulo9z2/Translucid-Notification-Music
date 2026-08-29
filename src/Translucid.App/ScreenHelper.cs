using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;

namespace Translucid.App;

/// <summary>
/// Utilitário multi-monitor consciente: resolve em qual Screen o widget está,
/// salva DeviceName e garante clamp ao WorkArea (área sem a taskbar) para
/// o widget nunca sumir ao desconectar um monitor.
/// Usa System.Windows.Forms.Screen (disponível via UseWindowsForms=true).
/// Converte pixels (WinForms) ↔ DIPs (WPF) via CompositionTarget.TransformFromDevice
/// ou fallback via VisualTreeHelper.GetDpi / GetDpiForWindow.
/// </summary>
internal static class ScreenHelper
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    // ------------------------------------------------------------ helpers de DPI

    private static double GetDpiScale(Window? window)
    {
        if (window is null) return 1.0;
        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                var dpi = GetDpiForWindow(helper.Handle);
                if (dpi != 0) return dpi / 96.0;
            }
        }
        catch { }

        try
        {
            var dpi = VisualTreeHelper.GetDpi(window);
            if (dpi.DpiScaleX > 0) return dpi.DpiScaleX;
        }
        catch { }

        try
        {
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
            {
                // TransformFromDevice já entrega a razão exata pixels->DIPs
                // Para descobrir a escala, invertemos: um DIP em device são scale pixels.
                var m = source.CompositionTarget.TransformToDevice;
                if (m.M11 > 0) return m.M11;
            }
        }
        catch { }

        return 1.0;
    }

    private static WpfRect PixelsToDips(Rectangle pixelRect, Window? window)
    {
        var scale = GetDpiScale(window);
        if (Math.Abs(scale - 1.0) < 0.001)
            return new WpfRect(pixelRect.X, pixelRect.Y, pixelRect.Width, pixelRect.Height);

        return new WpfRect(
            pixelRect.X / scale,
            pixelRect.Y / scale,
            pixelRect.Width / scale,
            pixelRect.Height / scale);
    }

    private static WpfRect PixelsToDips(Rectangle pixelRect, double scale)
    {
        if (Math.Abs(scale - 1.0) < 0.001)
            return new WpfRect(pixelRect.X, pixelRect.Y, pixelRect.Width, pixelRect.Height);
        return new WpfRect(
            pixelRect.X / scale,
            pixelRect.Y / scale,
            pixelRect.Width / scale,
            pixelRect.Height / scale);
    }

    // ------------------------------------------------------------ descoberta de Screen

    public static System.Windows.Forms.Screen? FindByDeviceName(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return null;
        foreach (var s in System.Windows.Forms.Screen.AllScreens)
        {
            if (string.Equals(s.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                return s;
        }
        return null;
    }

    public static bool IsDeviceAvailable(string? deviceName)
        => FindByDeviceName(deviceName) is not null;

    public static string? GetDeviceNameForWindow(Window window)
    {
        try
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                var screen = System.Windows.Forms.Screen.FromHandle(helper.Handle);
                return screen.DeviceName;
            }
        }
        catch { }

        // fallback: pelo centro da janela
        try
        {
            var centerX = window.Left + (double.IsNaN(window.Width) || window.Width <= 0 ? window.ActualWidth : window.Width) / 2.0;
            var centerY = window.Top + (double.IsNaN(window.Height) || window.Height <= 0 ? window.ActualHeight : window.Height) / 2.0;
            return GetDeviceNameForPoint(centerX, centerY, window);
        }
        catch { return null; }
    }

    public static string? GetDeviceNameForPoint(double dipX, double dipY, Window? window = null)
    {
        try
        {
            var scale = GetDpiScale(window);
            var pixelX = (int)Math.Round(dipX * scale);
            var pixelY = (int)Math.Round(dipY * scale);
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(pixelX, pixelY));
            return screen.DeviceName;
        }
        catch { return null; }
    }

    private static System.Windows.Forms.Screen GetBestScreen(string? preferredDeviceName, double left, double top, double width, double height, Window? window)
    {
        // 1) Preferido explícito (último salvo)
        if (!string.IsNullOrWhiteSpace(preferredDeviceName))
        {
            var ps = FindByDeviceName(preferredDeviceName);
            if (ps != null) return ps;
        }

        // 2) Screen que contém o centro da janela (virtual screen, suporta coords negativas)
        try
        {
            var scale = GetDpiScale(window);
            var cx = (int)Math.Round((left + width / 2.0) * scale);
            var cy = (int)Math.Round((top + height / 2.0) * scale);
            return System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy));
        }
        catch { }

        // 3) Screen que mais intersecta o rect da janela
        try
        {
            var scale = GetDpiScale(window);
            var rect = new Rectangle(
                (int)Math.Round(left * scale),
                (int)Math.Round(top * scale),
                (int)Math.Max(1, Math.Round(width * scale)),
                (int)Math.Max(1, Math.Round(height * scale)));
            return System.Windows.Forms.Screen.FromRectangle(rect);
        }
        catch { }

        // 4) Primário
        return System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
    }

    public static WpfRect GetWorkAreaInDips(System.Windows.Forms.Screen screen, Window? window)
    {
        var pixelArea = screen.WorkingArea;
        return PixelsToDips(pixelArea, window);
    }

    public static WpfRect GetPrimaryWorkArea(Window? window)
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen;
        if (primary != null) return GetWorkAreaInDips(primary, window);
        // fallback WPF
        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// Clampa (left,top) para que a janela de tamanho width×height fique
    /// totalmente dentro do WorkArea do monitor correto.
    /// Se width/height forem maiores que o WorkArea, alinha no topo-esquerda do WorkArea.
    /// </summary>
    public static WpfPoint GetClampedPosition(double left, double top, double width, double height, Window? window, string? preferredDeviceName)
    {
        if (width <= 0) width = 440;
        if (height <= 0) height = 172;

        var screen = GetBestScreen(preferredDeviceName, left, top, width, height, window);
        var area = GetWorkAreaInDips(screen, window);

        // se screen ainda não é confiável (area vazia), usa SystemParameters
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0)
            area = SystemParameters.WorkArea;

        double clampedLeft;
        double clampedTop;

        if (width >= area.Width)
            clampedLeft = area.Left;
        else
            clampedLeft = Math.Clamp(left, area.Left, area.Right - width);

        if (height >= area.Height)
            clampedTop = area.Top;
        else
            clampedTop = Math.Clamp(top, area.Top, area.Bottom - height);

        return new WpfPoint(clampedLeft, clampedTop);
    }

    public static WpfPoint GetClampedPosition(double left, double top, double width, double height, string? preferredDeviceName)
        => GetClampedPosition(left, top, width, height, null, preferredDeviceName);

    /// <summary>
    /// Garante que a janela esteja visível: se estiver totalmente fora de todas
    /// as telas (ex: monitor desconectado), move para o WorkArea do monitor
    /// preferido ou primário. Retorna true se moveu.
    /// </summary>
    public static bool EnsureWindowIsVisible(Window window, string? preferredDeviceName = null)
    {
        if (window is null) return false;
        try
        {
            double w = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
            double h = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
            if (double.IsNaN(w) || w <= 0) w = 440;
            if (double.IsNaN(h) || h <= 0) h = 172;

            // Verifica se pelo menos parte da janela está visível em qualquer tela
            if (IsRectVisibleOnAnyScreen(window.Left, window.Top, w, h, window))
            {
                // Mesmo visível, garante que não ficou espremida além da WorkArea (taskbar)
                // — faz clamp suave mas só se estiver fora do WorkArea do seu screen
                var clamped = GetClampedPosition(window.Left, window.Top, w, h, window, preferredDeviceName ?? GetDeviceNameForWindow(window));
                bool needsMove = Math.Abs(clamped.X - window.Left) > 0.5 || Math.Abs(clamped.Y - window.Top) > 0.5;
                if (needsMove)
                {
                    window.Left = clamped.X;
                    window.Top = clamped.Y;
                    return true;
                }
                return false;
            }

            var target = GetClampedPosition(window.Left, window.Top, w, h, window, preferredDeviceName);
            window.Left = target.X;
            window.Top = target.Y;
            return true;
        }
        catch { return false; }
    }

    public static bool IsRectVisibleOnAnyScreen(double left, double top, double width, double height, Window? window = null)
    {
        try
        {
            var scale = GetDpiScale(window);
            // testa interseção com WorkingArea de cada screen (em DIPs)
            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var area = PixelsToDips(s.WorkingArea, scale);
                var winRect = new WpfRect(left, top, width, height);
                winRect.Intersect(area);
                if (!winRect.IsEmpty && winRect.Width > 40 && winRect.Height > 40)
                    return true;
                // também considera interseção com Bounds (mesmo que esteja sob taskbar, ainda é visível)
                var bounds = PixelsToDips(s.Bounds, scale);
                var winBounds = new WpfRect(left, top, width, height);
                winBounds.Intersect(bounds);
                if (!winBounds.IsEmpty && winBounds.Width > 40 && winBounds.Height > 40)
                    return true;
            }
            return false;
        }
        catch { return true; }
    }
}
