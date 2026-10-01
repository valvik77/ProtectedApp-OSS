using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;
using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class NoticeWindow : Window
{
    private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly AppWindow _appWindow;
    private readonly IntPtr _hwnd;
    private readonly DispatcherTimer? _autoCloseTimer;
    private bool _hasBeenActivated;
    private bool _actionRequested;
    // The extension selector needs one more row than a plain notice.
    private const int Width = 450;
    private int _height = 220;

    /// <summary>
    /// The minutes chosen in the extension selector, or null when the notice had
    /// none. Only meaningful once <see cref="ShowAsync"/> has returned true.
    /// </summary>
    public int? SelectedExtensionMinutes { get; private set; }

    public NoticeWindow(string applicationName, string message, bool isError = false,
        string? actionLabel = null, string? subtitle = null, TimeSpan? autoCloseAfter = null,
        IReadOnlyList<int>? extensionChoices = null, int? defaultExtensionMinutes = null)
    {
        InitializeComponent();
        LocalizationService.LanguageChanged += RefreshLanguage;
        Closed += (_, _) => LocalizationService.LanguageChanged -= RefreshLanguage;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(HeaderDragArea);
        AppNameText.Text = LocalizationService.T(AppNameText, "Text", applicationName);
        SubtitleText.Text = LocalizationService.T(SubtitleText, "Text", subtitle ?? (isError ? "No se pudo iniciar" : "Ejecución bloqueada"));
        ToolTipService.SetToolTip(AppNameText, LocalizationService.T(AppNameText, "ToolTip", applicationName));
        MessageText.Text = LocalizationService.T(MessageText, "Text", message);
        if (!string.IsNullOrWhiteSpace(actionLabel))
        {
            ActionButton.Content = LocalizationService.T(ActionButton, "Content", actionLabel);
            ActionButton.Visibility = Visibility.Visible;
            AcceptButton.Content = LocalizationService.T(AcceptButton, "Content", "No ampliar");
        }

        if (extensionChoices is { Count: > 0 } && !string.IsNullOrWhiteSpace(actionLabel))
        {
            var selected = defaultExtensionMinutes ?? extensionChoices[0];
            foreach (var minutes in extensionChoices)
            {
                ExtensionMinutesCombo.Items.Add(new ComboBoxItem
                {
                    Content = LocalizationService.T(FormatExtensionChoice(minutes)),
                    Tag = minutes
                });
                if (minutes == selected) ExtensionMinutesCombo.SelectedIndex = ExtensionMinutesCombo.Items.Count - 1;
            }
            if (ExtensionMinutesCombo.SelectedIndex < 0) ExtensionMinutesCombo.SelectedIndex = 0;
            SelectedExtensionMinutes = (int)((ComboBoxItem)ExtensionMinutesCombo.SelectedItem).Tag;
            ExtensionLabel.Text = LocalizationService.T(ExtensionLabel, "Text", "Ampliar esta vez");
            ExtensionPanel.Visibility = Visibility.Visible;
            _height = 268;
        }

        _hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        _appWindow.Resize(new Windows.Graphics.SizeInt32(Width, _height));
        _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProtectedApp.ico"));
        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
            presenter.SetBorderAndTitleBar(true, false);
        }
        if (autoCloseAfter is { } delay && delay > TimeSpan.Zero)
        {
            _autoCloseTimer = new DispatcherTimer { Interval = delay };
            _autoCloseTimer.Tick += (_, _) =>
            {
                _autoCloseTimer.Stop();
                _completion.TrySetResult(false);
                Close();
            };
        }
        _appWindow.Closing += (_, _) =>
        {
            _autoCloseTimer?.Stop();
            _completion.TrySetResult(_actionRequested);
        };
        CenterWindow();
        Root.Loaded += (_, _) =>
        {
            LocalizationService.ApplyTo(Root);
            BringToForeground();
        };
    }

    public Task<bool> ShowAsync()
    {
        BringToForeground();
        _autoCloseTimer?.Start();
        return _completion.Task;
    }

    public void BringToForeground()
    {
        // As with UnlockWindow, showing the native AppWindow before WinUI has
        // loaded can leave an empty black host instead of the notice content.
        if (!_hasBeenActivated)
        {
            _hasBeenActivated = true;
            Activate();
            return;
        }
        _appWindow.Show(true);
        Activate();
        ShowWindow(_hwnd, SwRestore);
        ForceForegroundWindow();
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.High, () =>
        {
            ForceForegroundWindow();
            AcceptButton.Focus(FocusState.Programmatic);
        });
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _completion.TrySetResult(false);
        Close();
    }

    private void RefreshLanguage() => DispatcherQueue.TryEnqueue(() => LocalizationService.ApplyTo(Root));

    private void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        _actionRequested = true;
        // Resolve before closing the native window. Depending on window
        // activation, Closing can be delivered after the caller has already
        // moved on, causing the timed-session action to be lost.
        _completion.TrySetResult(true);
        Close();
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey.Enter or VirtualKey.Escape)) return;
        e.Handled = true;
        _completion.TrySetResult(false);
        Close();
    }

    private void ForceForegroundWindow()
    {
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetWindowPos(_hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
            BringWindowToTop(_hwnd);
            SetForegroundWindow(_hwnd);
            SetActiveWindow(_hwnd);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void CenterWindow()
    {
        var area = GetActiveDisplayArea();
        _appWindow.Move(new Windows.Graphics.PointInt32(
            area.WorkArea.X + (area.WorkArea.Width - Width) / 2,
            area.WorkArea.Y + (area.WorkArea.Height - _height) / 2));
    }

    private void ExtensionMinutesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExtensionMinutesCombo.SelectedItem is ComboBoxItem { Tag: int minutes })
            SelectedExtensionMinutes = minutes;
    }

    /// <summary>Renders a choice the way the rest of the app writes durations.</summary>
    private static string FormatExtensionChoice(int minutes) => minutes switch
    {
        60 => "1 hora",
        120 => "2 horas",
        _ => $"{minutes} min"
    };

    private static DisplayArea GetActiveDisplayArea()
    {
        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && GetWindowRect(foreground, out var bounds))
        {
            var center = new Windows.Graphics.PointInt32(
                bounds.Left + (bounds.Right - bounds.Left) / 2,
                bounds.Top + (bounds.Bottom - bounds.Top) / 2);
            return DisplayArea.GetFromPoint(center, DisplayAreaFallback.Primary);
        }
        return DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(0, 0), DisplayAreaFallback.Primary);
    }

    private const int SwRestore = 9;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopMost = new(-1);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
