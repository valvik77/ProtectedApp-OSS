using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Input;
using ProtectedApp.Services;
using WinRT.Interop;
using Windows.System;
using Windows.UI.Core;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private const int ImmediateLockHotkeyId = 0x5041;
    private const uint HotkeyControl = 0x0002;
    private const uint HotkeyAlt = 0x0001;
    private const uint HotkeyShift = 0x0004;
    private bool _immediateLockHotkeyRegistered;
    private uint _immediateLockModifiers;
    private uint _immediateLockVirtualKey;
    private int _immediateLockRunning;

    private async void ConfigureImmediateLockHotkey_Click(object sender, RoutedEventArgs e)
    {
        var capturedShortcut = _state.ImmediateLockHotkey ?? "Ctrl+Alt+L";
        var input = new TextBox
        {
            Header = "Combinación de teclas",
            Text = capturedShortcut,
            IsReadOnly = true,
            PlaceholderText = "Pulsa la combinación"
        };
        var details = new StackPanel { Width = 420, Spacing = 8 };
        details.Children.Add(new TextBlock
        {
            Text = "El atajo funciona aunque la ventana esté oculta. Solicita primero el cierre normal de las aplicaciones protegidas; tras 5 segundos, fuerza el cierre de las que sigan abiertas. Después desmonta bóvedas y bloquea Windows.",
            TextWrapping = TextWrapping.Wrap
        });
        details.Children.Add(input);
        details.Children.Add(new TextBlock
        {
            Text = "Haz clic en el campo y pulsa la combinación. Debe incluir Ctrl, Alt o Mayús y una letra, número o F1 a F12.",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Application.Current.Resources["MutedTextBrush"] as Microsoft.UI.Xaml.Media.Brush
        });
        var dialog = CreateDialog("Atajo de bloqueo inmediato", details, "Guardar", "Cancelar");
        dialog.SecondaryButtonText = "Desactivar";
        input.KeyDown += (_, args) =>
        {
            if (args.Key is VirtualKey.Control or VirtualKey.Menu or VirtualKey.Shift) return;
            var modifiers = GetPressedHotkeyModifiers();
            if (modifiers == 0) return;
            var candidate = BuildHotkeyText(modifiers, args.Key);
            if (candidate is null) return;
            capturedShortcut = candidate;
            input.Text = capturedShortcut;
            args.Handled = true;
        };
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        var choice = await dialog.ShowAsync();
        if (choice == ContentDialogResult.Secondary) capturedShortcut = string.Empty;
        else if (choice != ContentDialogResult.Primary) return;

        var requested = capturedShortcut;
        if (!TrySetImmediateLockHotkey(requested, out var error))
        {
            await ShowMessageAsync("No se pudo configurar el atajo", error);
            return;
        }

        _state.ImmediateLockHotkey = string.IsNullOrWhiteSpace(requested) ? null : NormalizeHotkey(requested);
        RefreshImmediateLockHotkeyStatus();
        await SaveAsync();
        AddActivity("ProtectedApp", string.IsNullOrWhiteSpace(_state.ImmediateLockHotkey)
            ? "Atajo de bloqueo inmediato desactivado"
            : $"Atajo de bloqueo inmediato configurado: {_state.ImmediateLockHotkey}");
    }

    private void RefreshImmediateLockHotkeyStatus()
    {
        if (ImmediateLockHotkeyStatusText is null) return;
        ImmediateLockHotkeyStatusText.Text = LocalizationService.T(string.IsNullOrWhiteSpace(_state.ImmediateLockHotkey)
            ? "Sin atajo configurado."
            : $"Atajo global: {_state.ImmediateLockHotkey}");
    }

    private void ApplyImmediateLockHotkey(bool showError)
    {
        if (TrySetImmediateLockHotkey(_state.ImmediateLockHotkey ?? string.Empty, out var error)) return;
        _state.ImmediateLockHotkey = null;
        if (showError) _ = ShowMessageAsync("Atajo no disponible", error);
        else AddActivity("ProtectedApp", $"Atajo de bloqueo inmediato desactivado: {error}");
    }

    private bool TrySetImmediateLockHotkey(string shortcut, out string error)
    {
        error = string.Empty;
        if (!TryParseHotkey(shortcut, out var modifiers, out var virtualKey, out var normalized, out error)) return false;

        var hwnd = WindowNative.GetWindowHandle(this);
        var hadRegistration = _immediateLockHotkeyRegistered;
        var previousModifiers = _immediateLockModifiers;
        var previousVirtualKey = _immediateLockVirtualKey;
        UnregisterImmediateLockHotkey();

        if (string.IsNullOrEmpty(normalized)) return true;
        if (RegisterHotKey(hwnd, ImmediateLockHotkeyId, modifiers, virtualKey))
        {
            _immediateLockHotkeyRegistered = true;
            _immediateLockModifiers = modifiers;
            _immediateLockVirtualKey = virtualKey;
            return true;
        }

        if (hadRegistration && RegisterHotKey(hwnd, ImmediateLockHotkeyId, previousModifiers, previousVirtualKey))
        {
            _immediateLockHotkeyRegistered = true;
            _immediateLockModifiers = previousModifiers;
            _immediateLockVirtualKey = previousVirtualKey;
        }
        error = "Windows no pudo registrar esta combinación; probablemente ya la usa otra aplicación.";
        return false;
    }

    private void UnregisterImmediateLockHotkey()
    {
        if (!_immediateLockHotkeyRegistered) return;
        UnregisterHotKey(WindowNative.GetWindowHandle(this), ImmediateLockHotkeyId);
        _immediateLockHotkeyRegistered = false;
    }

    private static string NormalizeHotkey(string shortcut)
    {
        TryParseHotkey(shortcut, out _, out _, out var normalized, out _);
        return normalized;
    }

    private static uint GetPressedHotkeyModifiers()
    {
        var modifiers = 0u;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= HotkeyControl;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= HotkeyAlt;
        if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down))
            modifiers |= HotkeyShift;
        return modifiers;
    }

    private static string? BuildHotkeyText(uint modifiers, VirtualKey key)
    {
        var code = (uint)key;
        var isAlphaNumeric = code is >= (uint)VirtualKey.Number0 and <= (uint)VirtualKey.Number9
            || code is >= (uint)VirtualKey.A and <= (uint)VirtualKey.Z;
        var isFunctionKey = code is >= (uint)VirtualKey.F1 and <= (uint)VirtualKey.F12;
        if (!isAlphaNumeric && !isFunctionKey) return null;

        var labels = new List<string>();
        if ((modifiers & HotkeyControl) != 0) labels.Add("Ctrl");
        if ((modifiers & HotkeyAlt) != 0) labels.Add("Alt");
        if ((modifiers & HotkeyShift) != 0) labels.Add("Mayús");
        labels.Add(isFunctionKey ? $"F{code - (uint)VirtualKey.F1 + 1}" : ((char)code).ToString());
        return string.Join("+", labels);
    }

    private static bool TryParseHotkey(string value, out uint modifiers, out uint virtualKey, out string normalized, out string error)
    {
        modifiers = 0;
        virtualKey = 0;
        normalized = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return true;

        string? key = null;
        foreach (var part in value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
                modifiers |= HotkeyControl;
            else if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= HotkeyAlt;
            else if (part.Equals("Mayús", StringComparison.OrdinalIgnoreCase) || part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= HotkeyShift;
            else if (key is null)
                key = part;
            else
            {
                error = "Indica una sola tecla junto a los modificadores.";
                return false;
            }
        }

        if (modifiers == 0 || string.IsNullOrWhiteSpace(key))
        {
            error = "Usa al menos Ctrl, Alt o Mayús y una tecla, por ejemplo Ctrl+Alt+L.";
            return false;
        }

        if (key.Length == 1 && char.IsLetterOrDigit(key[0]))
            virtualKey = char.ToUpperInvariant(key[0]);
        else if (key.Length is 2 or 3 && key.StartsWith("F", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(key[1..], out var functionKey) && functionKey is >= 1 and <= 12)
            virtualKey = (uint)(0x70 + functionKey - 1);
        else
        {
            error = "La tecla debe ser una letra, un número o F1 a F12.";
            return false;
        }

        var labels = new List<string>();
        if ((modifiers & HotkeyControl) != 0) labels.Add("Ctrl");
        if ((modifiers & HotkeyAlt) != 0) labels.Add("Alt");
        if ((modifiers & HotkeyShift) != 0) labels.Add("Mayús");
        labels.Add(key.Length == 1 ? char.ToUpperInvariant(key[0]).ToString() : key.ToUpperInvariant());
        normalized = string.Join("+", labels);
        return true;
    }

    private async Task ExecuteImmediateLockFromHotkeyAsync()
    {
        if (!_initialized || Interlocked.Exchange(ref _immediateLockRunning, 1) != 0) return;
        try
        {
            if (!_guardianManaged)
            {
                AddActivity("ProtectedApp", "Atajo de bloqueo inmediato ignorado: Guardian no está disponible");
                return;
            }

            var response = await _guardianClient.EmergencyLockAsync();
            if (!response.Success)
            {
                AddActivity("ProtectedApp", $"Atajo de bloqueo inmediato no confirmado: {response.Error ?? "error desconocido"}");
                return;
            }

            _monitor.RevokeAllAuthorizations();
            var lockedVaults = await LockAllVaultsAsync(showErrors: false);
            if (lockedVaults < 0) return;
            AddActivity("Bloqueo inmediato",
                $"Atajo aplicado; {response.GracefulCloseCount} aplicaciones cerradas normalmente, {response.ForcedTerminationCount} cierres forzados, {lockedVaults} bóvedas bloqueadas");
            await SaveAsync();
            HideToTray(showNotification: false, forceLock: true);
            LockWorkStation();
        }
        finally
        {
            Interlocked.Exchange(ref _immediateLockRunning, 0);
        }
    }
}
