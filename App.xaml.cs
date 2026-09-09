using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using ProtectedApp.Services;

namespace ProtectedApp;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    private AppInstance? _currentInstance;
    private static Mutex? _singleInstanceMutex;

    public App()
    {
        UnhandledException += (s, e) =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"),
                    $"{DateTimeOffset.Now:O} [XamlUnhandledException]{Environment.NewLine}{e.Message}{Environment.NewLine}{e.Exception}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
            // A partially initialized WinUI tree cannot service Guardian
            // requests safely. Exit cleanly instead of leaving Windows Error
            // Reporting to create a dump on every recovery attempt.
            e.Handled = true;
            Environment.Exit(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "crash.log"),
                    $"{DateTimeOffset.Now:O} [AppDomainUnhandledException]{Environment.NewLine}{e.ExceptionObject}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }
        };
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            LogDiagnostic("OnLaunched started");
            VaultActivationRequest.EnsureFileAssociation();
            var commandLine = Environment.GetCommandLineArgs();
            LogDiagnostic($"Command line: {string.Join("|", commandLine)}");
            var requestedFolderAction = FolderActivationRequest.TryGetFolderActionPath(commandLine);
            var requestedFolder = requestedFolderAction ?? FolderActivationRequest.TryGetFolderPath(commandLine);
            var requestedVaultAction = VaultActivationRequest.TryGetVaultActionPath(commandLine);
            var requestedVault = requestedVaultAction ?? VaultActivationRequest.TryGetVaultPath(commandLine);
            var requestedVaultUnmountDrive = VaultActivationRequest.TryGetVaultUnmountDrive(commandLine);
            var updateShutdownRequested = commandLine.Any(argument =>
                argument.Equals("--shutdown-for-update", StringComparison.OrdinalIgnoreCase));
            var silentLaunch = AppLaunchMode.IsSilent(commandLine);
            LogDiagnostic($"Silent launch: {silentLaunch}, Folder: {requestedFolder}, Vault: {requestedVault}");
            if (commandLine.Any(argument =>
                    argument.Equals("--authorize-uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                LogDiagnostic("Running uninstall authorization");
                await RunUninstallAuthorizationAsync();
                return;
            }

            if (WatchdogService.IsWatchdogMode(commandLine))
            {
                LogDiagnostic("Running watchdog mode");
                _ = Task.Run(() =>
                {
                    WatchdogService.RunWatchdogMode(commandLine);
                    Environment.Exit(0);
                });
                return;
            }

            if (commandLine.Any(argument => argument.Equals("--vault-rw-probe", StringComparison.OrdinalIgnoreCase)))
            {
                var probeRoot = Path.Combine(Path.GetTempPath(), "ProtectedApp-VaultProbe-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await VaultFormatV3.RunVirtualWriteProbeAsync(probeRoot);
                    await VaultService.RunReadWriteMountProbeAsync(probeRoot);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Vault RW probe failed: {ex}");
                    Environment.Exit(1);
                }
                return;
            }

            if (commandLine.Any(argument => argument.Equals("--vault-backup-probe", StringComparison.OrdinalIgnoreCase)))
            {
                var probeRoot = Path.Combine(Path.GetTempPath(), "ProtectedApp-ScheduledBackupProbe-" + Guid.NewGuid().ToString("N"));
                try
                {
                    await VaultService.RunScheduledBackupProbeAsync(probeRoot);
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Scheduled backup probe failed: {ex}");
                    Environment.Exit(1);
                }
                return;
            }

            if (commandLine.Any(argument => argument.Equals("--startup-regression-probe", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    AppLaunchMode.RunRegressionProbe();
                    WindowsSecurityMessageRouter.RunRegressionProbe();
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Startup regression probe failed: {ex}");
                    Environment.Exit(1);
                }
                return;
            }

            if (commandLine.Any(argument => argument.Equals("--startup-probe", StringComparison.OrdinalIgnoreCase)))
            {
                // This probe validates the independent notification surface.
                // Do not construct MainWindow: it owns the tray icon and a
                // failed probe must never leave a second background instance.
                try
                {
                    _ = new NoticeWindow("Comprobación", "Recursos de interfaz cargados correctamente.");
                    Environment.Exit(0);
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Startup probe failed: {ex}");
                    Environment.Exit(1);
                }
                return;
            }

            var instanceKey = Environment.GetEnvironmentVariable("PROTECTEDAPP_INSTANCE_KEY");
            var mutexName = string.IsNullOrWhiteSpace(instanceKey)
                ? @"Local\ProtectedApp.SingleInstance.Mutex"
                : $@"Local\ProtectedApp.{instanceKey}.Mutex";

            bool isPrimary;
            try
            {
                _singleInstanceMutex = new Mutex(true, mutexName, out isPrimary);
            }
            catch
            {
                isPrimary = true;
            }

            LogDiagnostic($"Primary instance: {isPrimary}");
            var currentInstance = AppInstance.GetCurrent();
            if (!isPrimary)
            {
                LogDiagnostic("Not primary - redirecting to existing instance");
                var primaryInstance = AppInstance.FindOrRegisterForKey(string.IsNullOrWhiteSpace(instanceKey)
                    ? "ProtectedApp.Primary"
                    : instanceKey);
                if (silentLaunch && requestedFolder is null && requestedVault is null
                    && requestedVaultUnmountDrive is null)
                {
                    Exit();
                    return;
                }
                if (!primaryInstance.IsCurrent)
                {
                    await primaryInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
                }
                else
                {
                    TrayIconService.SignalExistingInstance();
                }
                Exit();
                return;
            }

            AppInstance.FindOrRegisterForKey(string.IsNullOrWhiteSpace(instanceKey)
                ? "ProtectedApp.Primary"
                : instanceKey);

            _currentInstance = currentInstance;
            LogDiagnostic("Creating MainWindow");
            try
            {
                MainWindow = new MainWindow();
                LogDiagnostic("MainWindow created successfully");
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Failed to create MainWindow: {ex}");
                throw;
            }

            if (requestedFolder is not null)
                MainWindow.RequestFolderFromActivation(requestedFolder, requestedFolderAction is not null);
            if (requestedVault is not null)
                MainWindow.RequestVaultFromActivation(requestedVault, requestedVaultAction is not null);
            if (requestedVaultUnmountDrive is not null)
                MainWindow.RequestVaultUnmountFromActivation(requestedVaultUnmountDrive);
            if (updateShutdownRequested)
                MainWindow.RequestUpdateShutdown();
            _currentInstance.Activated += (_, activation) =>
            {
                LogDiagnostic("Instance activation received");
                var launchArgs = activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                    ? launch.Arguments
                    : string.Empty;
                LogDiagnostic($"Activation arguments: {launchArgs}");

                var folderActionPath = FolderActivationRequest.TryGetFolderActionPath(launchArgs);
                var folderPath = folderActionPath ?? FolderActivationRequest.TryGetFolderPath(launchArgs);
                var vaultActionPath = VaultActivationRequest.TryGetVaultActionPath(launchArgs);
                var vaultPath = vaultActionPath ?? VaultActivationRequest.TryGetVaultPath(launchArgs);
                var vaultUnmountDrive = VaultActivationRequest.TryGetVaultUnmountDrive(launchArgs);
                var updateShutdownRequested = launchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Any(argument => argument.Equals("--shutdown-for-update", StringComparison.OrdinalIgnoreCase));
                var isInteractiveActivation = !AppLaunchMode.IsSilent(launchArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries));

                MainWindow.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (updateShutdownRequested)
                        {
                            LogDiagnostic("Update shutdown requested");
                            MainWindow.RequestUpdateShutdown();
                        }
                        else if (folderPath is not null)
                        {
                            LogDiagnostic("Folder activation");
                            MainWindow.RequestFolderFromActivation(folderPath, folderActionPath is not null);
                        }
                        else if (vaultPath is not null)
                        {
                            LogDiagnostic("Vault activation");
                            MainWindow.RequestVaultFromActivation(vaultPath, vaultActionPath is not null);
                        }
                        else if (vaultUnmountDrive is not null)
                        {
                            LogDiagnostic("Vault drive unmount activation");
                            MainWindow.RequestVaultUnmountFromActivation(vaultUnmountDrive);
                        }
                        else if (isInteractiveActivation)
                        {
                            LogDiagnostic("Interactive activation - requesting authenticated access");
                            MainWindow.RequestShowFromActivation();
                        }
                        else
                        {
                            LogDiagnostic("Silent activation - keeping current state");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogDiagnostic($"Error handling activation: {ex.Message}");
                    }
                });
            };
            if (silentLaunch || requestedVault is not null || requestedVaultUnmountDrive is not null)
            {
                LogDiagnostic("Silent launch - hiding immediately");
                MainWindow.HideImmediatelyForSilentLaunch();
            }
            else
            {
                LogDiagnostic("Interactive launch - showing window");
                MainWindow.ShowMainWindow();
            }
            LogDiagnostic("OnLaunched completed successfully");
        }
        catch (Exception ex)
        {
            LogDiagnostic($"OnLaunched exception: {ex}");
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, "launch-exception.log"),
                    $"{DateTimeOffset.Now:O}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
            catch { }

            // A half-initialized process has neither a usable window nor a
            // tray icon. Leaving it alive makes Guardian believe its
            // interactive agent is healthy and prevents recovery.
            Environment.Exit(1);
        }
    }

    private static void LogDiagnostic(string message)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "launch.log"),
                $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task RunUninstallAuthorizationAsync()
    {
        var store = new StateStore();
        var state = await store.LoadAsync();
        var guardian = new GuardianClient();
        var guardianInstalled = GuardianServiceDetector.IsInstalled();
        var guardianStatus = await guardian.GetStatusAsync();
        var guardianAvailable = guardianStatus.Success && guardianStatus.PolicyConfigured;

        // A freshly installed copy that has never created credentials must remain
        // removable. If Guardian or a local credential exists, fail closed.
        if (string.IsNullOrWhiteSpace(state.MasterPasswordHash) && !guardianAvailable)
        {
            Environment.Exit(guardianInstalled ? 1 : 0);
            return;
        }

        var unlockWindow = new UnlockWindow(
            "Desinstalar ProtectedApp",
            "Introduce tu contraseña maestra para autorizar la desinstalación",
            async password =>
            {
                if (guardianAvailable)
                {
                    var response = await guardian.AuthenticateMasterDetailedAsync(password);
                    if (!response.Success || string.IsNullOrWhiteSpace(response.Token))
                        return UnlockAttemptResult.FromGuardian(response);
                    var preparation = await guardian.PrepareUninstallAsync(response.Token);
                    return UnlockAttemptResult.FromGuardian(preparation);
                }
                if (!PasswordService.Verify(password, state.MasterPasswordHash, state.MasterPasswordSalt))
                    return UnlockAttemptResult.Incorrect;
                return UnlockAttemptResult.Accepted;
            });

        var authorized = await unlockWindow.ShowAsync();
        Environment.Exit(authorized ? 0 : 1);
    }
}
