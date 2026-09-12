using System.Collections.ObjectModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using ProtectedApp.Models;
using ProtectedApp.Services;
using WinRT.Interop;

namespace ProtectedApp;

public sealed partial class MainWindow : Window
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwShowNoActivate = 4;
    private const int SwRestore = 9;
    private static readonly UIntPtr MinimumWindowSizeSubclassId = new(0x50A1);
    private readonly StateStore _store = new();
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly SemaphoreSlim _unlockGate = new(1, 1);
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly SemaphoreSlim _guardianSyncRecoveryGate = new(1, 1);
    private readonly SemaphoreSlim _guardianInstallationGate = new(1, 1);
    private readonly SemaphoreSlim _folderAccessGate = new(1, 1);
    private readonly SemaphoreSlim _vaultOperationGate = new(1, 1);
    private readonly SemaphoreSlim _vaultSecurityEventGate = new(1, 1);
    private readonly DispatcherTimer _tamperTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _guardianTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _guardianHeartbeatTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly DispatcherTimer _activitySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private readonly DispatcherTimer _managementAutoLockTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _folderStatusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _vaultTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _vaultBackupTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private readonly GuardianClient _guardianClient = new();
    private readonly VaultService _vaultService = new();
    private readonly LocalAuthenticationThrottle _localAuthenticationThrottle = new();
    private readonly DiagnosticsService _diagnosticsService = new();
    private readonly ProcessMonitorService _monitor;
    private readonly TrayIconService _tray;
    private readonly WindowSubclassProc _minimumWindowSizeSubclass;
    private WatchdogService? _watchdog;
    private readonly AppWindow _appWindow;
    private AppState _state = new();
    private bool _initialized;
    private bool _sessionUnlocked;
    private bool _allowClose;
    private bool _updatingControls;
    private bool _guardianPollBusy;
    private bool _guardianHeartbeatBusy;
    private bool _guardianManaged;
    private bool _guardianPolicyRecoveryRequired;
    private bool _guardianUnavailableAlerted;
    private int _windowVisibilityGeneration;
    private bool _guardianSyncPending;
    private bool _guardianSyncWarningRecorded;
    private bool _guardianSyncPromptDeferred;
    private bool _handlingTamper;
    private bool _showRequestedWhileLoading;
    private bool _showRequestedWhileBusy;
    private bool _refreshingVaultBackupState;
    private int _vaultBackupRunning;
    private readonly Dictionary<Guid, string> _vaultBackupFailures = [];
    private readonly Dictionary<Guid, VaultScheduledBackupHealth> _vaultBackupHealthStates = [];
    private readonly Queue<PendingFolderRequest> _pendingFolderRequests = new();
    private readonly Queue<PendingVaultRequest> _pendingVaultRequests = new();
    private readonly Queue<string> _pendingVaultUnmountDrives = new();
    private readonly Dictionary<Guid, string> _timedSessionTokens = new();
    private readonly Dictionary<Guid, bool> _pendingRuleProtectionStates = new();
    private readonly HashSet<Guid> _suppressedRuleLaunches = new();
    private readonly Dictionary<Guid, uint> _lastReportedInputTicks = new();
    private DateTimeOffset _nextApplicationActivityReportUtc;
    private DateTimeOffset _nextGuardianStatusRefreshUtc;
    private int _showRequestBusy;
    private int _updateShutdownRequested;
    private int _openDialogCount;
    private UnlockWindow? _activeUnlockWindow;
    private NoticeWindow? _activeNoticeWindow;
    private string? _guardianToken;
    private string? _recentMasterPassword;
    private string _selectedNavigation = "dashboard";
    private string _selectedCategory = "all";
    private string _activityFilter = "recent";
    private DateTimeOffset _lastManagementActivityUtc = DateTimeOffset.UtcNow;
    private readonly List<ActivityEntry> _activityHistory = [];

    public ObservableCollection<ProtectedApplication> Applications { get; } = [];
    public ObservableCollection<ProtectedApplication> VisibleApps { get; } = [];
    public ObservableCollection<ProtectedFolder> Folders { get; } = [];
    public ObservableCollection<VaultContainer> Vaults { get; } = [];
    public ObservableCollection<VaultRecoveryItem> VaultRecoveryItems { get; } = [];
    public ObservableCollection<ActivityEntry> Activity { get; } = [];
    public ObservableCollection<DiagnosticResult> Diagnostics { get; } = [];

    public MainWindow()
    {
        try
        {
            InitializeComponent();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragRegion);
            var hwnd = WindowNative.GetWindowHandle(this);
            _minimumWindowSizeSubclass = EnforceMinimumWindowSize;
            SetWindowSubclass(hwnd, _minimumWindowSizeSubclass, MinimumWindowSizeSubclassId, UIntPtr.Zero);
            _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
            _appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "ProtectedApp.ico"));
            ConfigureTitleBar();
            Root.ActualThemeChanged += (_, _) =>
            {
                ConfigureTitleBar();
                SyncThemeToggle();
                SelectNavigation(_selectedNavigation);
                if (_initialized) RefreshVisibleActivity();
            };
            if (_appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(true, false);
                // El contenido se adapta con desplazamiento y la ventana puede
                // ampliarse para escalado de texto, lectores de pantalla y
                // pantallas pequeñas sin recortar controles.
                presenter.IsResizable = true;
                presenter.IsMaximizable = true;
            }
            _appWindow.Closing += AppWindow_Closing;

            _monitor = new ProcessMonitorService(() => Applications.ToArray());
            _monitor.AccessRequired += app => DispatcherQueue.TryEnqueue(() => _ = RequestApplicationAccessAsync(app));
            _monitor.MonitorEvent += (app, message) => DispatcherQueue.TryEnqueue(() => AddActivity(app.Name, message));

            _tray = new TrayIconService(hwnd);
            _tray.ShowRequested += () => DispatcherQueue.TryEnqueue(() => _ = ShowAndAuthenticateAsync());
            _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ExitWithAuthenticationAsync());
            _tray.VaultUnmountRequested += vaultId => DispatcherQueue.TryEnqueue(() => _ = UnmountVaultFromTrayAsync(vaultId));
            _tray.VaultOpenRequested += vaultId => DispatcherQueue.TryEnqueue(() => _ = OpenVaultFromTrayAsync(vaultId));
            _tray.SecurityEventReceived += Tray_SecurityEventReceived;
            _tamperTimer.Tick += TamperTimer_Tick;
            _guardianTimer.Tick += GuardianTimer_Tick;
            _guardianHeartbeatTimer.Tick += GuardianHeartbeatTimer_Tick;
            _activitySaveTimer.Tick += ActivitySaveTimer_Tick;
            _managementAutoLockTimer.Tick += ManagementAutoLockTimer_Tick;
            _folderStatusTimer.Tick += (_, _) =>
            {
                var now = DateTimeOffset.UtcNow;
                foreach (var folder in Folders)
                {
                    if (folder.IsEnabled && folder.UnlockedUntilUtc is { } until && until <= now)
                    {
                        folder.UnlockedUntilUtc = null;
                        FolderIconService.TryApply(folder);
                    }
                    folder.RefreshStatus();
                }
            };
            _vaultTimer.Tick += VaultTimer_Tick;
            _vaultBackupTimer.Tick += VaultBackupTimer_Tick;

            Activated += (_, _) =>
            {
                if (!_initialized) return;
                RefreshStats();
                RecordManagementActivity();
            };
            Closed += (_, _) =>
            {
                RemoveWindowSubclass(hwnd, _minimumWindowSizeSubclass, MinimumWindowSizeSubclassId);
                Cleanup();
            };
            Root.Loaded += Root_Loaded;
            Root.PointerMoved += (_, _) => RecordManagementActivity();
            Root.PointerPressed += (_, _) => RecordManagementActivity();
            Root.KeyDown += (_, _) => RecordManagementActivity();
            SelectNavigation("dashboard");
        }
        catch (Exception ex)
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "mainwindow-ctor.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
            throw;
        }
    }
}
