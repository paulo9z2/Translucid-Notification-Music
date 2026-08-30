using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Translucid.Core;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using TextBox = System.Windows.Controls.TextBox;
using WpfColorConverter = System.Windows.Media.ColorConverter;

using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Translucid.App;

/// <summary>Janelinha de preferências aberta pelo ícone da bandeja.</summary>
public partial class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private bool _initializing = true;
    private bool _hotkeysExpanded;
    private const string HotkeyPlaceholder = "Pressione um atalho…";

    private string? _pauseBeforeEdit;
    private string? _nextBeforeEdit;
    private string? _prevBeforeEdit;

    public SettingsWindow()
    {
        InitializeComponent();
        AutoStartToggle.IsChecked = AppSettings.IsAutoStartEnabled();
        BottomToggle.IsChecked = AppSettings.Current.AlwaysOnBottom;
        LyricsToggle.IsChecked = AppSettings.Current.LyricsEnabled;
        HotkeysToggle.IsChecked = AppSettings.Current.HotkeysEnabled;
        ExtendedResizeToggle.IsChecked = AppSettings.Current.ExtendedResizeEnabled;
        SpicetifyBridgeToggle.IsChecked = AppSettings.Current.SpicetifyBridgeEnabled;

        // Inicializa Expander de atalhos (code-behind preparado para hotkeys customizáveis)
        InitializeHotkeysExpander();

        Closed += (_, _) => _instance = null;
        _initializing = false;
    }

    /// <summary>Inicializa estado visual do expander + TextBoxes com AppSettings HotkeyPause/Next/Prev.</summary>
    private void InitializeHotkeysExpander()
    {
        _hotkeysExpanded = false;
        // Garante estado colapsado inicial (XAML já vem collapsed, mas reforça)
        if (HotkeysExpanderPanel != null)
        {
            HotkeysExpanderPanel.Visibility = Visibility.Collapsed;
            HotkeysExpanderPanel.Opacity = 0;
        }
        if (ExpanderContentScale != null)
        {
            ExpanderContentScale.ScaleY = 0;
        }
        if (ChevronRotate != null)
        {
            ChevronRotate.Angle = 0;
        }

        // Carrega valores persistidos (futuro: AppSettings HotkeyPause/Next/Prev)
        LoadHotkeyBoxes();
        SyncHotkeysExpanderEnabled();
    }

    private void LoadHotkeyBoxes()
    {
        SetHotkeyBoxValue(HotkeyPauseBox, ClearPauseButton, AppSettings.Current.HotkeyPause);
        SetHotkeyBoxValue(HotkeyNextBox, ClearNextButton, AppSettings.Current.HotkeyNext);
        SetHotkeyBoxValue(HotkeyPrevBox, ClearPrevButton, AppSettings.Current.HotkeyPrev);
    }

    private void SetHotkeyBoxValue(TextBox? box, Button? clearBtn, string? value)
    {
        if (box == null) return;
        if (string.IsNullOrWhiteSpace(value))
        {
            box.Text = HotkeyPlaceholder;
            box.Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#B0FFFFFF")!);
            box.FontStyle = FontStyles.Italic;
            if (clearBtn != null) clearBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            box.Text = value;
            box.Foreground = new SolidColorBrush(Colors.White);
            box.FontStyle = FontStyles.Normal;
            if (clearBtn != null) clearBtn.Visibility = Visibility.Visible;
        }
    }

    private void SyncHotkeysExpanderEnabled()
    {
        var enabled = HotkeysToggle?.IsChecked == true;
        if (HotkeysExpanderRoot != null)
        {
            HotkeysExpanderRoot.IsEnabled = enabled;
            HotkeysExpanderRoot.Opacity = enabled ? 1.0 : 0.45;
            if (enabled)
            {
                HotkeysExpanderRoot.ToolTip = null;
                ExpanderHeader.ToolTip = "Personalizar atalhos";
            }
            else
            {
                HotkeysExpanderRoot.ToolTip = "Ative 'Atalhos globais' para personalizar";
                ExpanderHeader.ToolTip = "Ative 'Atalhos globais' para personalizar";
            }
        }
        if (ExpanderHeader != null)
        {
            ExpanderHeader.IsEnabled = enabled;
            System.Windows.Automation.AutomationProperties.SetName(ExpanderHeader,
                enabled ? "Personalizar atalhos, colapsado" : "Personalizar atalhos, desabilitado");
        }
        // Se desabilitou e estava expandido, recolhe com animação
        if (!enabled && _hotkeysExpanded)
        {
            _hotkeysExpanded = false;
            AnimateHotkeysExpander(false);
        }
    }

    /// <summary>Abre a janela (uma única instância), perto do widget.</summary>
    public static void ShowOrFocus()
    {
        if (_instance is { } existing && existing.IsVisible)
        {
            existing.Activate();
            return;
        }

        var window = new SettingsWindow();
        _instance = window;

        if (Application.Current.MainWindow is { IsVisible: true } main)
        {
            var area = SystemParameters.WorkArea;
            window.Left = Math.Clamp(main.Left + 48, area.Left, area.Right - window.Width);
            window.Top = Math.Clamp(main.Top + 48, area.Top, area.Bottom - window.Height);
        }

        window.Show();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        DesktopFx.TryCornerRounding(hwnd);
        // Fundo SEMPRE presente (translúcido): sem ele a janela não captura
        // cliques nas áreas vazias e o arraste não funciona.
        Shell.Background = new SolidColorBrush(Color.FromArgb(0x28, 0x0A, 0x0B, 0x0D));
        if (!DesktopFx.EnableAcrylic(hwnd))
        {
            Shell.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0B, 0x0D));
        }

        DesktopFx.HideFromAltTab(hwnd);
        DesktopFx.RoundCorners(hwnd, 14);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject { } source &&
            FindAncestor<System.Windows.Controls.Button>(source) is not null)
        {
            return;
        }
        // Evita arrastar quando clica no header do expander ou nos campos de hotkey
        if (e.OriginalSource is DependencyObject src2)
        {
            var tb = FindAncestor<TextBox>(src2);
            if (tb == HotkeyPauseBox || tb == HotkeyNextBox || tb == HotkeyPrevBox)
                return;
            var hdr = FindAncestor<Border>(src2);
            if (hdr == ExpanderHeader || hdr == HotkeysExpanderRoot || hdr == HotkeysExpanderPanel)
            {
                // deixa o toggle do expander tratar; não arrasta
                if (hdr == ExpanderHeader) return;
            }
        }
        DragMove();
    }

    private void CloseSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ===== Toggles existentes =====
    private void AutoStartToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.SetAutoStart(toggle.IsChecked == true);
        }
    }

    private void LyricsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.LyricsEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void BottomToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.AlwaysOnBottom = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void HotkeysToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.HotkeysEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
            SyncHotkeysExpanderEnabled();
        }
    }

    private void ExtendedResizeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.ExtendedResizeEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    private void SpicetifyBridgeToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (sender is System.Windows.Controls.Primitives.ToggleButton toggle)
        {
            AppSettings.Current.SpicetifyBridgeEnabled = toggle.IsChecked == true;
            AppSettings.Current.Save();
        }
    }

    // ===== Expander Atalhos Globais — animação 280ms chevron 0<->180 + ScaleY/Opacity =====

    /// <summary>Toggle do expander de atalhos com animação 280ms. Vinculado ao Border ExpanderHeader via MouseLeftButtonUp.</summary>
    private void ToggleHotkeysExpander_Click(object sender, MouseButtonEventArgs e)
    {
        // Ignora se desabilitado pelo toggle pai
        if (HotkeysToggle?.IsChecked != true)
        {
            e.Handled = true;
            return;
        }
        e.Handled = true;
        _hotkeysExpanded = !_hotkeysExpanded;
        AnimateHotkeysExpander(_hotkeysExpanded);
    }

    /// <summary>Overload para chamada via Button.Click ou programática (RoutedEventArgs). Mantido para compatibilidade.</summary>
    private void ToggleHotkeysExpander_Click(object sender, RoutedEventArgs e)
    {
        if (HotkeysToggle?.IsChecked != true) return;
        _hotkeysExpanded = !_hotkeysExpanded;
        AnimateHotkeysExpander(_hotkeysExpanded);
    }

    /// <summary>Alias para XAML que referencia ExpanderHeader_Click (compat com spec docs). Encaminha para ToggleHotkeysExpander_Click.</summary>
    private void ExpanderHeader_Click(object sender, MouseButtonEventArgs e) => ToggleHotkeysExpander_Click(sender, e);
    private void ExpanderHeader_Click(object sender, RoutedEventArgs e) => ToggleHotkeysExpander_Click(sender, e);

    private void ExpanderHeader_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Space)
        {
            e.Handled = true;
            _hotkeysExpanded = !_hotkeysExpanded;
            AnimateHotkeysExpander(_hotkeysExpanded);
        }
        else if (e.Key == Key.Escape && _hotkeysExpanded)
        {
            e.Handled = true;
            _hotkeysExpanded = false;
            AnimateHotkeysExpander(false);
        }
    }

    private void AnimateHotkeysExpander(bool expand)
    {
        // Fallback FindName caso XAML ainda não tenha gerado campos (build sem expander)
        var panel = HotkeysExpanderPanel ?? FindName("HotkeysExpanderPanel") as Border ?? FindName("ExpanderContent") as Border;
        var chevron = ChevronRotate ?? FindName("ChevronRotate") as RotateTransform;
        var scale = ExpanderContentScale ?? FindName("ExpanderContentScale") as ScaleTransform;
        var header = ExpanderHeader ?? FindName("ExpanderHeader") as Border;

        var duration = TimeSpan.FromMilliseconds(280); // 0:0:0.28 spec §5
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        if (header != null)
        {
            System.Windows.Automation.AutomationProperties.SetName(header,
                expand ? "Personalizar atalhos, expandido" : "Personalizar atalhos, colapsado");
        }

        if (panel == null || chevron == null || scale == null)
        {
            if (panel != null)
                panel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (expand) panel.Visibility = Visibility.Visible;

        // Storyboard com 3 DoubleAnimations: Opacity (0→1), ScaleY (0→1), Chevron Angle (0→180) — spec §5.1 §5.2
        var sb = new Storyboard();
        sb.Duration = new Duration(duration);

        var opacityAnim = new DoubleAnimation
        {
            From = expand ? 0 : 1,
            To = expand ? 1 : 0,
            Duration = new Duration(duration),
            EasingFunction = ease
        };
        Storyboard.SetTarget(opacityAnim, panel);
        Storyboard.SetTargetProperty(opacityAnim, new PropertyPath(UIElement.OpacityProperty));
        sb.Children.Add(opacityAnim);

        var scaleAnim = new DoubleAnimation
        {
            From = expand ? 0 : 1,
            To = expand ? 1 : 0,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scaleAnim, scale);
        Storyboard.SetTargetProperty(scaleAnim, new PropertyPath(ScaleTransform.ScaleYProperty));
        sb.Children.Add(scaleAnim);

        var chevronAnim = new DoubleAnimation
        {
            From = expand ? 0 : 180,
            To = expand ? 180 : 0,
            Duration = new Duration(duration),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(chevronAnim, chevron);
        Storyboard.SetTargetProperty(chevronAnim, new PropertyPath(RotateTransform.AngleProperty));
        sb.Children.Add(chevronAnim);

        // Borda sutil quando expandido — token #1AFFFFFF
        if (header != null)
            header.BorderBrush = expand
                ? new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#1AFFFFFF")!)
                : new SolidColorBrush(Colors.Transparent);

        sb.Completed += (_, _) =>
        {
            if (!expand && !_hotkeysExpanded)
                panel.Visibility = Visibility.Collapsed;
        };
        sb.Begin();
    }

    // ===== Captura de atalhos — PreviewKeyDown =====

    private void HotkeyPause_PreviewKeyDown(object sender, KeyEventArgs e) => HandleHotkeyPreview(HotkeyPauseBox, e, "Pause");
    private void HotkeyNext_PreviewKeyDown(object sender, KeyEventArgs e) => HandleHotkeyPreview(HotkeyNextBox, e, "Next");
    private void HotkeyPrev_PreviewKeyDown(object sender, KeyEventArgs e) => HandleHotkeyPreview(HotkeyPrevBox, e, "Prev");

    /// <summary>Handler genérico compatível com XAML PreviewKeyDown direto (sem slot).</summary>
    private void HotkeyBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is TextBox tb)
        {
            var slot = tb == HotkeyPauseBox ? "Pause" : tb == HotkeyNextBox ? "Next" : "Prev";
            HandleHotkeyPreview(tb, e, slot);
        }
    }

    private void HandleHotkeyPreview(TextBox? targetBox, KeyEventArgs e, string slot)
    {
        if (targetBox == null)
        {
            e.Handled = true;
            return;
        }

        // Permitir navegação Tab / Shift+Tab
        if (e.Key == Key.Tab)
        {
            e.Handled = false;
            return;
        }

        // Esc cancela captura e restaura valor anterior
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            var prev = slot switch { "Pause" => _pauseBeforeEdit, "Next" => _nextBeforeEdit, "Prev" => _prevBeforeEdit, _ => null };
            RestoreHotkeyBox(targetBox, prev);
            // Tira foco do campo
            Keyboard.ClearFocus();
            if (ExpanderHeader != null) ExpanderHeader.Focus();
            return;
        }

        // Enter confirma e sai do campo
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Keyboard.ClearFocus();
            if (ExpanderHeader != null) ExpanderHeader.Focus();
            return;
        }

        // Back/Delete limpa o atalho (equivale ao botão × E711)
        if (e.Key == Key.Back || e.Key == Key.Delete)
        {
            e.Handled = true;
            ClearHotkeyBox(targetBox, slot);
            return;
        }

        // Ignora pressionamento isolado de modificador (Ctrl/Shift/Alt/Win)
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (IsModifierKey(key))
        {
            e.Handled = true;
            return;
        }

        var formatted = FormatHotkey(Keyboard.Modifiers, key);
        if (formatted == null)
        {
            e.Handled = true;
            return;
        }

        e.Handled = true;

        // Valida duplicata entre os 3 campos (feedback visual opcional)
        if (IsHotkeyDuplicate(formatted, slot))
        {
            // Indica conflito: borda salmão temporária (opcional, conforme spec)
            targetBox.ToolTip = "Já em uso por outro atalho";
            // Mantém texto anterior e não salva duplicata; mas permite sobrescrever visualmente
            // Vamos ainda aplicar mas com tooltip de conflito
        }
        else
        {
            targetBox.ToolTip = "Clique e pressione o novo atalho";
        }

        targetBox.Text = formatted;
        targetBox.Foreground = new SolidColorBrush(Colors.White);
        targetBox.FontStyle = FontStyles.Normal;

        UpdateClearButtonVisibility(targetBox, true);
        UpdateBoxFocusVisual(targetBox, true);

        // Salva temporariamente + integração futura AppSettings HotkeyPause/Next/Prev
        PersistHotkey(slot, formatted);
    }

    private static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string? FormatHotkey(ModifierKeys mods, Key key)
    {
        // Filtra tecla inválida (DeadChar etc)
        if (key == Key.None) return null;

        var parts = new List<string>(4);
        if ((mods & ModifierKeys.Control) == ModifierKeys.Control) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) == ModifierKeys.Alt) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) == ModifierKeys.Shift) parts.Add("Shift");
        if ((mods & ModifierKeys.Windows) == ModifierKeys.Windows) parts.Add("Win");

        // Exige ao menos um modificador para atalho global confiável (evita letra solta)
        if (parts.Count == 0) return null;

        string keyText;
        if (key >= Key.A && key <= Key.Z) keyText = key.ToString();
        else if (key >= Key.D0 && key <= Key.D9) keyText = key.ToString()[1].ToString(); // D1 -> "1"
        else if (key >= Key.NumPad0 && key <= Key.NumPad9) keyText = (key - Key.NumPad0).ToString();
        else if (key >= Key.F1 && key <= Key.F12) keyText = key.ToString();
        else if (key >= Key.F13 && key <= Key.F24) keyText = key.ToString();
        else keyText = key switch
        {
            Key.OemPlus or Key.Add => "+",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Space => "Space",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Up => "Up",
            Key.Down => "Down",
            _ => key.ToString()
        };

        parts.Add(keyText);
        return string.Join(" + ", parts);
    }

    private bool IsHotkeyDuplicate(string formatted, string currentSlot)
    {
        var existing = new[]
        {
            (Slot: "Pause", Value: GetBoxRealValue(HotkeyPauseBox)),
            (Slot: "Next", Value: GetBoxRealValue(HotkeyNextBox)),
            (Slot: "Prev", Value: GetBoxRealValue(HotkeyPrevBox)),
        };
        foreach (var (slot, val) in existing)
        {
            if (slot == currentSlot) continue;
            if (!string.IsNullOrEmpty(val) && string.Equals(val, formatted, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private void ValidateHotkeyDuplicates()
    {
        // Opcional: destacar conflito com borda #4DFF8A6B. Implementação leve: apenas tooltip.
        // Pode expandir para borda salmão se desejar.
    }

    private void PersistHotkey(string slot, string formatted)
    {
        switch (slot)
        {
            case "Pause": AppSettings.Current.HotkeyPause = formatted; break;
            case "Next": AppSettings.Current.HotkeyNext = formatted; break;
            case "Prev": AppSettings.Current.HotkeyPrev = formatted; break;
        }
        AppSettings.Current.Save();
    }

    private void PersistHotkeyFromBox(TextBox tb, string? value)
    {
        if (tb == HotkeyPauseBox) AppSettings.Current.HotkeyPause = value;
        else if (tb == HotkeyNextBox) AppSettings.Current.HotkeyNext = value;
        else if (tb == HotkeyPrevBox) AppSettings.Current.HotkeyPrev = value;
    }

    private static string? GetBoxRealValue(TextBox? tb)
    {
        if (tb == null) return null;
        var t = tb.Text;
        if (t == HotkeyPlaceholder) return null;
        if (string.IsNullOrWhiteSpace(t)) return null;
        return t;
    }

    private void RestoreHotkeyBox(TextBox? tb, string? prev)
    {
        if (tb == null) return;
        if (string.IsNullOrWhiteSpace(prev))
        {
            tb.Text = HotkeyPlaceholder;
            tb.Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#B0FFFFFF")!);
            tb.FontStyle = FontStyles.Italic;
            UpdateClearButtonVisibility(tb, false);
        }
        else
        {
            tb.Text = prev;
            tb.Foreground = new SolidColorBrush(Colors.White);
            tb.FontStyle = FontStyles.Normal;
            UpdateClearButtonVisibility(tb, true);
        }
        UpdateBoxFocusVisual(tb, false);
    }

    private void ClearHotkeyBox(TextBox? tb, string slot)
    {
        if (tb == null) return;
        tb.Text = HotkeyPlaceholder;
        tb.Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#B0FFFFFF")!);
        tb.FontStyle = FontStyles.Italic;
        UpdateClearButtonVisibility(tb, false);
        UpdateBoxFocusVisual(tb, false);
        PersistHotkey(slot, null!);
        // Salva null/vazio para indicar sem atalho
        if (slot == "Pause") AppSettings.Current.HotkeyPause = null;
        else if (slot == "Next") AppSettings.Current.HotkeyNext = null;
        else if (slot == "Prev") AppSettings.Current.HotkeyPrev = null;
        AppSettings.Current.Save();
    }

    // ===== GotFocus / LostFocus para placeholder "Pressione um atalho…" =====

    private void HotkeyBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Guarda valor anterior para Esc restaurar
        if (tb == HotkeyPauseBox) _pauseBeforeEdit = GetBoxRealValue(tb);
        else if (tb == HotkeyNextBox) _nextBeforeEdit = GetBoxRealValue(tb);
        else if (tb == HotkeyPrevBox) _prevBeforeEdit = GetBoxRealValue(tb);

        if (tb.Text == HotkeyPlaceholder)
        {
            tb.Text = string.Empty;
            tb.Foreground = new SolidColorBrush(Colors.White);
            tb.FontStyle = FontStyles.Normal;
        }
        UpdateBoxFocusVisual(tb, true);
        // Mostra hint via caret placeholder? Mantém texto vazio até PreviewKeyDown preencher
    }

    private void HotkeyBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;

        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            tb.Text = HotkeyPlaceholder;
            tb.Foreground = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#B0FFFFFF")!);
            tb.FontStyle = FontStyles.Italic;
            UpdateClearButtonVisibility(tb, false);
            PersistHotkeyFromBox(tb, null);
            // Não salva automaticamente aqui para evitar gravar placeholder; já tratado em Clear
        }
        else if (tb.Text != HotkeyPlaceholder)
        {
            tb.Foreground = new SolidColorBrush(Colors.White);
            tb.FontStyle = FontStyles.Normal;
            UpdateClearButtonVisibility(tb, true);
            PersistHotkeyFromBox(tb, tb.Text);
            AppSettings.Current.Save();
        }
        UpdateBoxFocusVisual(tb, false);
    }

    private void UpdateClearButtonVisibility(TextBox tb, bool hasValue)
    {
        Button? btn = tb == HotkeyPauseBox ? ClearPauseButton : tb == HotkeyNextBox ? ClearNextButton : tb == HotkeyPrevBox ? ClearPrevButton : null;
        if (btn == null)
        {
            // fallback via FindName (E711)
            var name = tb == HotkeyPauseBox ? "ClearPauseButton" : tb == HotkeyNextBox ? "ClearNextButton" : "ClearPrevButton";
            btn = FindName(name) as Button;
        }
        if (btn != null)
            btn.Visibility = hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBoxFocusVisual(TextBox tb, bool focused)
    {
        // Altera borda do parent Border para #5CB8FF quando focado (capturando)
        if (tb.Parent is Border bd)
        {
            if (focused)
            {
                bd.BorderBrush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#5CB8FF")!);
                bd.BorderThickness = new Thickness(1.2);
            }
            else
            {
                bd.BorderBrush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#26FFFFFF")!);
                bd.BorderThickness = new Thickness(1);
            }
        }
        else
        {
            // Tenta achar Border via visual tree se Parent for Grid content presenter
            var parent = VisualTreeHelper.GetParent(tb) as Border;
            if (parent != null)
            {
                parent.BorderBrush = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString(focused ? "#5CB8FF" : "#26FFFFFF")!);
                parent.BorderThickness = new Thickness(focused ? 1.2 : 1);
            }
        }
    }

    // ===== Handlers de limpar (E711) =====
    private void ClearPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearHotkeyBox(HotkeyPauseBox, "Pause");
        // Foca header para feedback
        ExpanderHeader?.Focus();
    }

    private void ClearNext_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearHotkeyBox(HotkeyNextBox, "Next");
        ExpanderHeader?.Focus();
    }

    private void ClearPrev_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ClearHotkeyBox(HotkeyPrevBox, "Prev");
        ExpanderHeader?.Focus();
    }

    // Compat wrappers para spec que pode chamar ClearPause_Click via E711 genérico
    private void ClearHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            if (btn == ClearPauseButton) ClearPause_Click(sender, e);
            else if (btn == ClearNextButton) ClearNext_Click(sender, e);
            else if (btn == ClearPrevButton) ClearPrev_Click(sender, e);
        }
    }
}
