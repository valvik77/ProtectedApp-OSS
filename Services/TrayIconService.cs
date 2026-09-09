using System.Runtime.InteropServices;

namespace ProtectedApp.Services;

public sealed class TrayIconService : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint TrayMessage = WmApp + 17;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NinBalloonUserClick = 0x0405;
    private const uint WmCommand = 0x0111;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NifInfo = 0x00000010;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const int ShowCommand = 1001;
    private const int ExitCommand = 1002;
    private const int FirstUnmountVaultCommand = 1100;
    private const int FirstOpenVaultCommand = 1200;

    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly SubclassProc _subclassProc;
    private NotifyIconData _data;
    private readonly bool _sessionNotificationsRegistered;
    private readonly object _mountedVaultsSync = new();
    private IReadOnlyList<(Guid Id, string Name)> _mountedVaults = [];
    private IReadOnlyList<(Guid Id, string Name)> _recentVaults = [];
    private readonly Dictionary<int, Guid> _unmountCommands = [];
    private readonly Dictionary<int, Guid> _openCommands = [];
    public bool IsVisible { get; private set; }
    private static readonly uint ActivateMessage = RegisterWindowMessage("ProtectedApp.Activate.v1");
    private static readonly uint TaskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action<Guid>? VaultUnmountRequested;
    public event Action<Guid>? VaultOpenRequested;
    public event Action<VaultSecurityTrigger>? SecurityEventReceived;

    public TrayIconService(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _icon = LoadImage(IntPtr.Zero, Path.Combine(AppContext.BaseDirectory, "Assets", "ProtectedApp.ico"), ImageIcon, 0, 0, LrLoadFromFile);
        _subclassProc = WindowSubclass;
        SetWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero, UIntPtr.Zero);
        _sessionNotificationsRegistered = WTSRegisterSessionNotification(_hwnd, 0);
        _data = new NotifyIconData
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = TrayMessage,
            hIcon = _icon,
            szTip = LocalizationService.T("ProtectedApp — protección activa")
        };
    }

    public void SetVisible(bool visible)
    {
        if (visible == IsVisible) return;
        if (visible)
        {
            _data.uFlags = NifMessage | NifIcon | NifTip;
            IsVisible = ShellNotifyIcon(NimAdd, ref _data);
            return;
        }

        ShellNotifyIcon(NimDelete, ref _data);
        IsVisible = false;
    }

    public void ShowBalloon(string title, string message)
    {
        if (!IsVisible) return;
        _data.uFlags = NifInfo;
        _data.szInfoTitle = LocalizationService.T(title);
        _data.szInfo = LocalizationService.T(message);
        _data.dwInfoFlags = 1;
        ShellNotifyIcon(NimModify, ref _data);
    }

    public void RefreshLocalizedText()
    {
        _data.szTip = LocalizationService.T("ProtectedApp — protección activa");
        if (IsVisible)
        {
            _data.uFlags = NifTip;
            ShellNotifyIcon(NimModify, ref _data);
        }
    }

    public void DismissBalloon()
    {
        if (!IsVisible) return;
        // An empty NIF_INFO update instructs the shell to withdraw the
        // currently displayed balloon instead of leaving a stale warning.
        _data.uFlags = NifInfo;
        _data.szInfoTitle = string.Empty;
        _data.szInfo = string.Empty;
        _data.dwInfoFlags = 0;
        ShellNotifyIcon(NimModify, ref _data);
    }

    public void SetMountedVaults(IEnumerable<(Guid Id, string Name)> vaults)
    {
        lock (_mountedVaultsSync)
        {
            _mountedVaults = vaults
                .Where(vault => vault.Id != Guid.Empty)
                .Select(vault => (Id: vault.Id, Name: string.IsNullOrWhiteSpace(vault.Name) ? LocalizationService.T("Bóveda sin nombre") : vault.Name.Trim()))
                .OrderBy(vault => vault.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
    }

    public void SetRecentVaults(IEnumerable<(Guid Id, string Name)> vaults)
    {
        lock (_mountedVaultsSync)
        {
            _recentVaults = vaults
                .Where(vault => vault.Id != Guid.Empty)
                .GroupBy(vault => vault.Id)
                .Select(group => group.First())
                .Select(vault => (Id: vault.Id,
                    Name: string.IsNullOrWhiteSpace(vault.Name) ? LocalizationService.T("Bóveda sin nombre") : vault.Name.Trim()))
                .Take(5)
                .ToArray();
        }
    }

    public void Dispose()
    {
        if (IsVisible) ShellNotifyIcon(NimDelete, ref _data);
        IsVisible = false;
        if (_sessionNotificationsRegistered) WTSUnRegisterSessionNotification(_hwnd);
        RemoveWindowSubclass(_hwnd, _subclassProc, UIntPtr.Zero);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }

    public static void SignalExistingInstance() =>
        PostMessage(new IntPtr(0xFFFF), ActivateMessage, IntPtr.Zero, IntPtr.Zero);

    private IntPtr WindowSubclass(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        // Explorer discards notification icons when it restarts. Re-add ours
        // so a hidden ProtectedApp never becomes inaccessible.
        if (message == TaskbarCreatedMessage)
        {
            IsVisible = false;
            SetVisible(true);
            return IntPtr.Zero;
        }
        if (WindowsSecurityMessageRouter.TryResolve(message, unchecked((nuint)wParam.ToInt64()), out var trigger))
            SecurityEventReceived?.Invoke(trigger);
        if (message == ActivateMessage)
        {
            ShowRequested?.Invoke();
            return IntPtr.Zero;
        }
        if (message == TrayMessage)
        {
            var mouseMessage = unchecked((uint)lParam.ToInt64());
            if (mouseMessage is WmLButtonUp or WmLButtonDblClk or 0x0201 /* WM_LBUTTONDOWN */
                or NinBalloonUserClick)
                ShowRequested?.Invoke();
            if (mouseMessage == WmRButtonUp) ShowContextMenu();
            return IntPtr.Zero;
        }
        if (message == WmCommand)
        {
            var command = wParam.ToInt32() & 0xFFFF;
            if (command == ShowCommand) ShowRequested?.Invoke();
            if (command == ExitCommand) ExitRequested?.Invoke();
            Guid vaultId;
            Guid recentVaultId;
            lock (_mountedVaultsSync)
            {
                _unmountCommands.TryGetValue(command, out vaultId);
                _openCommands.TryGetValue(command, out recentVaultId);
            }
            if (vaultId != Guid.Empty) VaultUnmountRequested?.Invoke(vaultId);
            if (recentVaultId != Guid.Empty) VaultOpenRequested?.Invoke(recentVaultId);
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString, ShowCommand, LocalizationService.T("Abrir ProtectedApp"));
        lock (_mountedVaultsSync)
        {
            _unmountCommands.Clear();
            _openCommands.Clear();
            if (_mountedVaults.Count > 0)
            {
                AppendMenu(menu, MfSeparator, 0, string.Empty);
                for (var index = 0; index < _mountedVaults.Count; index++)
                {
                    var command = FirstUnmountVaultCommand + index;
                    _unmountCommands[command] = _mountedVaults[index].Id;
                    AppendMenu(menu, MfString, command, $"{LocalizationService.T("Desmontar:")} {_mountedVaults[index].Name}");
                }
            }
            var recent = _recentVaults.Where(vault => !_mountedVaults.Any(mounted => mounted.Id == vault.Id)).ToArray();
            if (recent.Length > 0)
            {
                AppendMenu(menu, MfSeparator, 0, string.Empty);
                for (var index = 0; index < recent.Length; index++)
                {
                    var command = FirstOpenVaultCommand + index;
                    _openCommands[command] = recent[index].Id;
                    AppendMenu(menu, MfString, command, $"{LocalizationService.T("Abrir bóveda:")} {recent[index].Name}");
                }
            }
        }
        AppendMenu(menu, MfSeparator, 0, string.Empty);
        AppendMenu(menu, MfString, ExitCommand, LocalizationService.T("Bloquear y ocultar"));
        GetCursorPos(out var point);
        SetForegroundWindow(_hwnd);
        TrackPopupMenu(menu, TpmRightButton, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
        DestroyMenu(menu);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("comctl32.dll")] private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll")] private static extern bool RemoveWindowSubclass(IntPtr hwnd, SubclassProc proc, UIntPtr id);
    [DllImport("comctl32.dll")] private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string text);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, uint flags);
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}
