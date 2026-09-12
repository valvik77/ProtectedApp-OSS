using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class UnlockWindow : Window
{
    private readonly Func<string, Task<UnlockAttemptResult>> _verifyPassword;
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _retryTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private bool _resultSet;
    private bool _verifying;
    private bool _hasBeenActivated;
    private bool _unlockContentLoaded;
    private DateTimeOffset _retryUntilUtc;
    private string _retryMessage = string.Empty;

    private readonly Func<Task<UnlockAttemptResult>>? _verifyHello;
    private readonly bool _allowWindowsHello;

    public UnlockWindow(string title, string subtitle, Func<string, bool> verifyPassword, bool allowWindowsHello = false)
        : this(title, subtitle, password => Task.FromResult(
            verifyPassword(password) ? UnlockAttemptResult.Accepted : UnlockAttemptResult.Incorrect),
            allowWindowsHello, null)
    {
    }

    public UnlockWindow(string title, string subtitle, Func<string, Task<bool>> verifyPassword, bool allowWindowsHello = false)
        : this(title, subtitle, async password =>
            await verifyPassword(password) ? UnlockAttemptResult.Accepted : UnlockAttemptResult.Incorrect,
            allowWindowsHello, null)
    {
    }

    public UnlockWindow(string title, string subtitle, Func<string, UnlockAttemptResult> verifyPassword, bool allowWindowsHello = false)
        : this(title, subtitle, password => Task.FromResult(verifyPassword(password)), allowWindowsHello, null)
    {
    }

    public UnlockWindow(string title, string subtitle, Func<string, Task<UnlockAttemptResult>> verifyPassword,
        bool allowWindowsHello = false, Func<Task<UnlockAttemptResult>>? verifyHello = null)
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += RefreshLanguage;
        Closed += (_, _) => LocalizationService.LanguageChanged -= RefreshLanguage;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderDragArea);
        AppNameText.Text = LocalizationService.T(AppNameText, "Text", title);
        SubtitleText.Text = LocalizationService.T(SubtitleText, "Text", subtitle);
        ToolTipService.SetToolTip(AppNameText, LocalizationService.T(AppNameText, "ToolTip", title));
        ToolTipService.SetToolTip(SubtitleText, LocalizationService.T(SubtitleText, "ToolTip", subtitle));
        _verifyPassword = verifyPassword;
        _verifyHello = verifyHello;
        _allowWindowsHello = allowWindowsHello;
        _retryTimer.Tick += RetryTimer_Tick;

        var targetHeight = allowWindowsHello ? WindowHeightWithHello : WindowHeight;
        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        var dpi = GetDpiForWindow(_hwnd);
        var scale = (dpi > 0 ? dpi : 96f) / 96f;
        var scaledWidth = (int)(WindowWidth * scale);
        var scaledHeight = (int)(targetHeight * scale);
        _appWindow.Resize(new Windows.Graphics.SizeInt32(scaledWidth, scaledHeight));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProtectedApp.ico"));
        if (allowWindowsHello)
        {
            WindowsHelloButton.Visibility = Visibility.Visible;
        }
        _appWindow.Closing += (_, _) =>
        {
            _retryTimer.Stop();
            Complete(false);
        };
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        ConfigureTitleBar();
        Root.ActualThemeChanged += (_, _) => ConfigureTitleBar();
        Root.Loaded += (_, _) =>
        {
            LocalizationService.ApplyTo(Root);
            _unlockContentLoaded = true;
            UpdateCapsLockHint();
            BringToForeground();
        };
        CenterWindow(targetHeight);
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
                QueuePasswordFocus();
        };
    }

    public Task<bool> ShowAsync()
    {
        BringToForeground();
        return _completion.Task;
    }

    public void BringToForeground()
    {
        // Calling AppWindow.Show before WinUI has raised Loaded can expose an
        // empty native host. This was especially visible when a vault was
        // reopened from Explorer after being unmounted: only a black rectangle
        // appeared and the password UI never rendered.
        if (!_hasBeenActivated)
        {
            _hasBeenActivated = true;
            Activate();
            if (_unlockContentLoaded) QueuePasswordFocus();
            return;
        }
        _appWindow.Show(true);
        Activate();
        ShowWindow(_hwnd, SwRestore);
        ForceForegroundWindow();
        QueuePasswordFocus();
    }

    private void RefreshLanguage() => DispatcherQueue.TryEnqueue(() => LocalizationService.ApplyTo(Root));

    private void QueuePasswordFocus()
    {
        if (!_unlockContentLoaded) return;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            _appWindow.Show(true);
            ShowWindow(_hwnd, SwRestore);
            ForceForegroundWindow();
            PasswordInput.Focus(FocusState.Programmatic);

            // A second dispatcher turn guarantees the PasswordBox has an
            // active native host after Explorer/tray activations.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High,
                () => PasswordInput.Focus(FocusState.Programmatic));
        });
    }

    private void ForceForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero
            ? 0
            : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0
            && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
            SetActiveWindow(_hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private async void WindowsHelloButton_Click(object sender, RoutedEventArgs e)
    {
        if (_verifying) return;
        _verifying = true;
        WindowsHelloButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        UnlockButton.IsEnabled = false;
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            var prompt = $"{LocalizationService.T("Autoriza el desbloqueo de")} {AppNameText.Text}";
            var verified = await WindowsHelloService.VerifyUserConsentAsync(prompt);
            if (verified)
            {
                if (_verifyHello is not null)
                {
                    var helloResult = await _verifyHello();
                    if (helloResult.Success)
                    {
                        Complete(true);
                        Close();
                        return;
                    }
                    ErrorText.Text = LocalizationService.T(helloResult.Error ?? "Autorización no concedida.");
                    ErrorText.Visibility = Visibility.Visible;
                }
                else
                {
                    Complete(true);
                    Close();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            ErrorText.Text = LocalizationService.UserFacingError(ex);
            ErrorText.Visibility = Visibility.Visible;
        }
        finally
        {
            _verifying = false;
            WindowsHelloButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            UnlockButton.IsEnabled = true;
            PasswordInput.Focus(FocusState.Programmatic);
        }
    }

    private async void UnlockButton_Click(object sender, RoutedEventArgs e) => await TryUnlockAsync();

    private async void PasswordInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        UpdateCapsLockHint();
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await TryUnlockAsync();
    }

    private void PasswordInput_KeyUp(object sender, KeyRoutedEventArgs e) => UpdateCapsLockHint();

    private void PasswordInput_GotFocus(object sender, RoutedEventArgs e) => UpdateCapsLockHint();

    private void UpdateCapsLockHint() =>
        CapsLockHint.Visibility = (GetKeyState(VkCapsLock) & 1) != 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void RevealPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        var reveal = RevealPasswordButton.IsChecked == true;
        PasswordInput.PasswordRevealMode = reveal
            ? PasswordRevealMode.Visible
            : PasswordRevealMode.Hidden;
        AutomationProperties.SetName(RevealPasswordButton, LocalizationService.T(reveal ? "Ocultar contraseña" : "Mostrar contraseña"));
        ToolTipService.SetToolTip(RevealPasswordButton, LocalizationService.T(reveal ? "Ocultar contraseña" : "Mostrar contraseña"));
        PasswordInput.Focus(FocusState.Programmatic);
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        if (_verifying) return;
        CancelUnlock();
    }

    private async Task TryUnlockAsync()
    {
        if (_verifying) return;
        _verifying = true;
        var password = PasswordInput.Password;
        PasswordInput.IsEnabled = false;
        RevealPasswordButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        UnlockButton.IsEnabled = false;
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        UnlockAttemptResult result;
        try { result = await _verifyPassword(password); }
        catch { result = new UnlockAttemptResult(false, LocalizationService.T("No se pudo verificar la contraseña.")); }
        if (!result.Success)
        {
            CancelButton.IsEnabled = true;
            _verifying = false;
            if (result.RetryAfterSeconds > 0)
            {
                PasswordInput.Password = string.Empty;
                _retryUntilUtc = DateTimeOffset.UtcNow.AddSeconds(result.RetryAfterSeconds);
                _retryMessage = string.IsNullOrWhiteSpace(result.Error)
                    ? LocalizationService.T("Demasiados intentos.")
                    : LocalizationService.T(result.Error.Trim());
                UpdateRetryCountdown();
                _retryTimer.Start();
            }
            else
            {
                ErrorText.Text = string.IsNullOrWhiteSpace(result.Error)
                    ? LocalizationService.T("No se pudo verificar la contraseña.")
                    : LocalizationService.T(result.Error);
                ErrorText.Visibility = Visibility.Visible;
                SetCredentialControlsEnabled(true);
                PasswordInput.SelectAll();
                PasswordInput.Focus(FocusState.Programmatic);
            }
            return;
        }
        ErrorText.Text = string.Empty;
        ErrorText.Visibility = Visibility.Collapsed;
        Complete(true);
        Close();
    }

    private void RetryTimer_Tick(object? sender, object e) => UpdateRetryCountdown();

    private void UpdateRetryCountdown()
    {
        var remaining = (int)Math.Ceiling((_retryUntilUtc - DateTimeOffset.UtcNow).TotalSeconds);
        if (remaining <= 0)
        {
            _retryTimer.Stop();
            ErrorText.Text = string.Empty;
            ErrorText.Visibility = Visibility.Collapsed;
            SetCredentialControlsEnabled(true);
            PasswordInput.Focus(FocusState.Programmatic);
            return;
        }

        SetCredentialControlsEnabled(false);
        var separator = _retryMessage.EndsWith('.') ? " " : ". ";
        ErrorText.Text = LocalizationService.IsEnglish
            ? $"{_retryMessage}{separator}Try again in {remaining} s."
            : $"{_retryMessage}{separator}Vuelve a intentarlo en {remaining} s.";
        ErrorText.Visibility = Visibility.Visible;
    }

    private void SetCredentialControlsEnabled(bool enabled)
    {
        PasswordInput.IsEnabled = enabled;
        RevealPasswordButton.IsEnabled = enabled;
        UnlockButton.IsEnabled = enabled;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
        => CancelUnlock();

    private void CancelUnlock()
    {
        if (_verifying) return;
        Complete(false);
        Close();
    }

    private void Complete(bool result)
    {
        if (_resultSet) return;
        _resultSet = true;
        _completion.TrySetResult(result);
    }

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;
        var light = Root.ActualTheme == ElementTheme.Light;
        var color = light
            ? Windows.UI.Color.FromArgb(255, 248, 248, 248)
            : Windows.UI.Color.FromArgb(255, 15, 20, 28);
        var foreground = light
            ? Windows.UI.Color.FromArgb(255, 31, 31, 31)
            : Windows.UI.Color.FromArgb(255, 222, 226, 238);
        _appWindow.TitleBar.BackgroundColor = color;
        _appWindow.TitleBar.ForegroundColor = foreground;
        _appWindow.TitleBar.ButtonBackgroundColor = color;
        _appWindow.TitleBar.ButtonForegroundColor = foreground;
        _appWindow.TitleBar.ButtonHoverBackgroundColor = light
            ? Windows.UI.Color.FromArgb(255, 229, 229, 229)
            : Windows.UI.Color.FromArgb(255, 48, 53, 62);
        _appWindow.TitleBar.ButtonPressedBackgroundColor = light
            ? Windows.UI.Color.FromArgb(255, 204, 204, 204)
            : Windows.UI.Color.FromArgb(255, 62, 72, 81);
    }

    private void CenterWindow(int height = WindowHeight)
    {
        var area = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var dpi = GetDpiForWindow(_hwnd);
        var scale = (dpi > 0 ? dpi : 96f) / 96f;
        var scaledWidth = (int)(WindowWidth * scale);
        var scaledHeight = (int)(height * scale);
        var x = area.WorkArea.X + (area.WorkArea.Width - scaledWidth) / 2;
        var y = area.WorkArea.Y + (area.WorkArea.Height - scaledHeight) / 2;
        _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private const int WindowWidth = 450;
    private const int WindowHeight = 238;
    private const int WindowHeightWithHello = 283;
    private const int SwRestore = 9;
    private const int VkCapsLock = 0x14;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetActiveWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
