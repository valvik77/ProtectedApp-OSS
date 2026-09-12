using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtectedApp.Models;
using ProtectedApp.Services;
using ProtectedApp.Shared;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async void GuardianHeartbeatTimer_Tick(object? sender, object e)
    {
        if (!_initialized || _guardianHeartbeatBusy || !GuardianServiceDetector.IsFullyConfigured()) return;
        _guardianHeartbeatBusy = true;
        try { await _guardianClient.ReportAgentHeartbeatAsync(); }
        finally { _guardianHeartbeatBusy = false; }
    }

    private async void GuardianTimer_Tick(object? sender, object e)
    {
        if (!_initialized || _guardianPollBusy) return;
        _guardianPollBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            if (now >= _nextGuardianStatusRefreshUtc)
            {
                var status = GuardianServiceDetector.IsFullyConfigured()
                    ? await _guardianClient.GetStatusAsync()
                    : new GuardianResponse();
                _guardianManaged = status.Success && status.PolicyConfigured;
                _nextGuardianStatusRefreshUtc = now.AddSeconds(_guardianManaged ? 2 : 1);
                if (_guardianManaged)
                {
                    StatusText.Text = LocalizationService.T("Activa");
                    StatusDot.Fill = ThemeBrush(
                        Windows.UI.Color.FromArgb(255, 80, 250, 123),
                        Windows.UI.Color.FromArgb(255, 46, 160, 67));
                }
                else
                {
                    _monitor.IsPaused = true;
                    StatusText.Text = LocalizationService.T("Guardian no disponible");
                    StatusDot.Fill = ThemeBrush(
                        Windows.UI.Color.FromArgb(255, 255, 153, 164),
                        Windows.UI.Color.FromArgb(255, 209, 52, 56));
                    if (!_guardianUnavailableAlerted)
                    {
                        _guardianUnavailableAlerted = true;
                        AddActivity("Guardian", "Alerta: Guardian no está disponible; la protección administrada necesita reparación");
                        if (_state.SecurityAlertsEnabled)
                        _tray.ShowBalloon("Alerta de seguridad", "Guardian no está disponible. Repara el servicio para restaurar la protección.");
                    }
                }
                RefreshDashboard();
            }
            if (!_guardianManaged) return;

            _guardianUnavailableAlerted = false;
            await ReportForegroundApplicationActivityAsync();
            _monitor.IsPaused = true;
            _watchdog?.Dispose();
            _watchdog = null;
            if (_guardianSyncPending && !_guardianSyncPromptDeferred && _sessionUnlocked && !_handlingTamper)
                _ = RecoverGuardianSynchronizationAsync();
            var pending = await _guardianClient.ClaimPendingAsync();
            if (pending is null) return;
            var app = Applications.FirstOrDefault(candidate => candidate.Id == pending.RuleId)
                ?? Applications.FirstOrDefault(candidate => PathsEqual(candidate.Path, pending.Path));
            if (app is null)
            {
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                return;
            }
            if (_pendingRuleProtectionStates.TryGetValue(app.Id, out var pendingEnabled)
                && !pendingEnabled)
            {
                if (string.Equals(pending.Kind, GuardianProtocol.PendingAuthentication, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pending.Kind, GuardianProtocol.PendingBlocked, StringComparison.OrdinalIgnoreCase))
                    _suppressedRuleLaunches.Add(app.Id);
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                return;
            }
            if (!app.IsEnabled)
            {
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                return;
            }
            if (string.Equals(pending.Kind, GuardianProtocol.PendingGracefulClose, StringComparison.OrdinalIgnoreCase))
            {
                RequestGracefulClose(pending.ProcessIds);
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                AddActivity(app.Name, "Cierre normal solicitado; guarda los cambios pendientes si la aplicación lo requiere");
                return;
            }
            if (string.Equals(pending.Kind, GuardianProtocol.PendingNotice, StringComparison.OrdinalIgnoreCase))
            {
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                if (!_state.CloseWarningNotificationsEnabled) return;
                var message = string.IsNullOrWhiteSpace(pending.Message)
                    ? $"{app.Name} se cerrará automáticamente en menos de un minuto."
                    : pending.Message;
                AddActivity(app.Name, message);
                _tray.ShowBalloon("Cierre automático", message);
                var inactivityNotice = app.ForceCloseAfterMinutes == 0 && app.ForceCloseAfterInactivityMinutes > 0;
                var extensionMinutes = Math.Max(1, inactivityNotice
                    ? app.ForceCloseAfterInactivityMinutes
                    : app.ForceCloseAfterMinutes);
                var extend = await ShowNoticeWindowAsync(app.Name, message, false,
                    $"Extender {extensionMinutes} min", "Cierre automático", TimeSpan.FromSeconds(55));
                if (extend)
                {
                    _timedSessionTokens.TryGetValue(app.Id, out var timedSessionToken);
                    if (string.IsNullOrWhiteSpace(timedSessionToken)
                        && !await VerifyMasterAsync($"Extender el tiempo de {app.Name}"))
                    {
                        AddActivity(app.Name, "Ampliación del cierre automático cancelada");
                        return;
                    }

                    var response = inactivityNotice
                        ? await _guardianClient.ExtendInactiveSessionAsync(app.Id, timedSessionToken, _guardianToken)
                        : await _guardianClient.ExtendTimedSessionAsync(app.Id, timedSessionToken, _guardianToken);
                    if (!response.Success
                        && response.Error?.Contains("autorización", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        _timedSessionTokens.Remove(app.Id);
                        if (await VerifyMasterAsync($"Extender el tiempo de {app.Name}"))
                        {
                            response = inactivityNotice
                                ? await _guardianClient.ExtendInactiveSessionAsync(app.Id, null, _guardianToken)
                                : await _guardianClient.ExtendTimedSessionAsync(app.Id, null, _guardianToken);
                        }
                    }
                    if (response.Success && !string.IsNullOrWhiteSpace(response.TimedSessionToken))
                        _timedSessionTokens[app.Id] = response.TimedSessionToken;
                    if (response.Success)
                        _tray.DismissBalloon();
                    AddActivity(app.Name, response.Success
                        ? inactivityNotice
                            ? $"Cierre por inactividad reiniciado {extensionMinutes} min"
                            : $"Cierre automático ampliado {extensionMinutes} min"
                        : $"No se pudo ampliar el cierre automático: {response.Error}");
                    if (!response.Success)
                        _tray.ShowBalloon("No se pudo ampliar", app.Name);
                }
                return;
            }
            app.BlockCount++;
            if (!string.Equals(pending.Kind, GuardianProtocol.PendingAuthentication, StringComparison.OrdinalIgnoreCase))
            {
                await _guardianClient.DismissPendingAsync(pending.RuleId);
                var message = string.IsNullOrWhiteSpace(pending.Message)
                    ? "Guardian bloqueó la ejecución de forma segura."
                    : pending.Message;
                AddActivity(app.Name, pending.Kind == GuardianProtocol.PendingBlocked
                    ? $"Ejecución denegada: {message}"
                    : $"Error de inicio: {message}");
                await ShowNoticeWindowAsync(app.Name, message,
                    pending.Kind == GuardianProtocol.PendingError);
                return;
            }
            AddActivity(app.Name, "Ejecución interceptada por Guardian (SYSTEM)");
            await RequestApplicationAccessAsync(app, guardianManaged: true);
        }
        finally { _guardianPollBusy = false; }
    }

    private static void RequestGracefulClose(IEnumerable<int> processIds)
    {
        var candidateIds = ExpandProcessTree(processIds);
        if (candidateIds.Count == 0) return;

        var visibleWindows = new List<(IntPtr Handle, int ProcessId)>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || GetWindowThreadProcessId(window, out var processId) == 0)
                return true;
            var id = checked((int)processId);
            if (candidateIds.Contains(id)) visibleWindows.Add((window, id));
            return true;
        }, IntPtr.Zero);

        var visibleProcessIds = visibleWindows.Select(item => item.ProcessId).ToHashSet();
        var parentByProcess = GetParentProcesses(candidateIds);
        var leafWindowProcesses = visibleProcessIds
            .Where(processId => !visibleProcessIds.Any(other => other != processId
                && IsAncestor(processId, other, parentByProcess)))
            .ToHashSet();

        var closeRequested = false;
        foreach (var window in visibleWindows.Where(item => leafWindowProcesses.Contains(item.ProcessId)))
            closeRequested |= PostMessage(window.Handle, WindowMessageClose, IntPtr.Zero, IntPtr.Zero);

        if (!closeRequested)
        {
            foreach (var processId in candidateIds)
            {
                try
                {
                    using var process = Process.GetProcessById(processId);
                    if (!process.HasExited && process.SessionId == Process.GetCurrentProcess().SessionId)
                        process.CloseMainWindow();
                }
                catch { }
            }
        }

        _ = FocusCloseConfirmationAsync(candidateIds, visibleWindows.Select(item => item.Handle).ToHashSet());
    }

    private static async Task FocusCloseConfirmationAsync(HashSet<int> initialProcessIds,
        IReadOnlySet<IntPtr> initialWindows)
    {
        var candidateIds = initialProcessIds;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            await Task.Delay(100);
            candidateIds = ExpandProcessTree(candidateIds);
            IntPtr newWindow = IntPtr.Zero;
            EnumWindows((window, _) =>
            {
                if (newWindow != IntPtr.Zero || initialWindows.Contains(window) || !IsWindowVisible(window)
                    || GetWindowThreadProcessId(window, out var processId) == 0
                    || !candidateIds.Contains(checked((int)processId)))
                    return true;
                newWindow = window;
                return false;
            }, IntPtr.Zero);
            if (newWindow == IntPtr.Zero) continue;
            ActivateOrFlashWindow(newWindow);
            return;
        }
    }

    private static void ActivateOrFlashWindow(IntPtr window)
    {
        ShowWindow(window, SwRestore);
        var foreground = GetForegroundWindow();
        var currentThread = GetCurrentThreadId();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }

        if (GetForegroundWindow() == window) return;
        var flash = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = window,
            Flags = FlashWindowTray | FlashWindowTimerNoForeground,
            Count = 5
        };
        FlashWindowEx(ref flash);
    }

    private static HashSet<int> ExpandProcessTree(IEnumerable<int> processIds)
    {
        var candidates = processIds.Where(id => id > 0).ToHashSet();
        if (candidates.Count == 0) return candidates;
        var parentByProcess = GetParentProcesses();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var process in parentByProcess)
                if (!candidates.Contains(process.Key) && candidates.Contains(process.Value))
                    changed |= candidates.Add(process.Key);
        }
        return candidates;
    }

    private static Dictionary<int, int> GetParentProcesses(IReadOnlySet<int>? filter = null)
    {
        var result = new Dictionary<int, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId, SessionId FROM Win32_Process");
            using var processes = searcher.Get();
            var currentSession = Process.GetCurrentProcess().SessionId;
            foreach (ManagementObject process in processes)
            {
                if (Convert.ToInt32(process["SessionId"]) != currentSession) continue;
                var processId = Convert.ToInt32(process["ProcessId"]);
                if (filter is not null && !filter.Contains(processId)) continue;
                result[processId] = Convert.ToInt32(process["ParentProcessId"]);
            }
        }
        catch { }
        return result;
    }

    private static bool IsAncestor(int ancestor, int processId, IReadOnlyDictionary<int, int> parentByProcess)
    {
        var visited = new HashSet<int>();
        while (parentByProcess.TryGetValue(processId, out var parent) && parent > 0 && visited.Add(parent))
        {
            if (parent == ancestor) return true;
            processId = parent;
        }
        return false;
    }

    private async void TamperTimer_Tick(object? sender, object e)
    {
        if (!_initialized || _handlingTamper || !TamperSignalService.TryPeekBatch(out var signals)) return;
        await ApplyTamperResponseAsync(signals);
    }

    private async Task ApplyTamperResponseAsync(TamperSignalBatchInfo batch)
    {
        if (batch.Signals.Count == 0) return;
        _handlingTamper = true;
        _sessionUnlocked = false;
        _monitor.RevokeAllAuthorizations();
        PauseIcon.Glyph = "\ue034";
        StatusText.Text = LocalizationService.T("Activa");
        StatusDot.Fill = ThemeBrush(
            Windows.UI.Color.FromArgb(255, 80, 250, 123),
            Windows.UI.Color.FromArgb(255, 46, 160, 67));
        PauseButton.SetValue(ToolTipService.ToolTipProperty, "Pausar protección");
        foreach (var group in batch.Signals.GroupBy(signal => signal.Reason, StringComparer.CurrentCultureIgnoreCase))
        {
            var suffix = group.Count() > 1 ? $" ({group.Count()} incidentes agrupados)" : string.Empty;
            AddActivity("Guardian", $"Manipulación detectada: {group.Key}{suffix}");
        }
        if (_state.SecurityAlertsEnabled)
            _tray.ShowBalloon("Alerta de seguridad", "Se detectó una manipulación de Guardian. Se han revocado los accesos protegidos.");
        try
        {
            if (await LockAllVaultsAsync(showErrors: false) < 0)
                AddActivity("ProtectedApp", "No se pudieron cerrar todas las bóvedas durante la respuesta antimanipulación; se conservó su carpeta de trabajo");
            await SaveAsync();
            TamperSignalService.Acknowledge(batch.LastId);
        }
        finally
        {
            HideToTray(showNotification: false);
            LockWorkStation();
            _handlingTamper = false;
        }
    }

    private void StartLocalWatchdog()
    {
        _watchdog?.Dispose();
        _watchdog = new WatchdogService();
        _watchdog.Start();
    }

    private async Task<bool> ShowNoticeWindowAsync(string applicationName, string message, bool isError,
        string? actionLabel = null, string? subtitle = null, TimeSpan? autoCloseAfter = null)
    {
        var noticeWindow = new NoticeWindow(applicationName, message, isError, actionLabel, subtitle, autoCloseAfter);
        _activeNoticeWindow = noticeWindow;
        try
        {
            return await noticeWindow.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_activeNoticeWindow, noticeWindow))
                _activeNoticeWindow = null;
        }
    }

    private void RefreshGuardianStatus()
    {
        var running = GuardianServiceDetector.IsRunning();
        var installed = GuardianServiceDetector.IsInstalled();
        var managed = _guardianManaged;
        GuardianStatusText.Text = LocalizationService.T(managed
            ? "Activo · reglas aplicadas por Guardian como SYSTEM"
            : running
            ? "Activo · pendiente de actualizar el motor de protección"
            : installed
            ? "Instalado pero detenido · pendiente de recuperación"
            : "No instalado · protección no disponible");
        GuardianStatusDot.Fill = running
            ? ThemeBrush(Windows.UI.Color.FromArgb(255, 80, 250, 123), Windows.UI.Color.FromArgb(255, 46, 160, 67))
            : ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 118, 118, 118));
        GuardianServiceButton.Content = LocalizationService.T(managed ? "Servicio activo" : installed ? "Reparar servicio" : "Instalar servicio");
        GuardianServiceButton.IsEnabled = !managed;
    }

    private async void GuardianServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (!await VerifyMasterAsync("Autorizar cambio del servicio")) return;
        await SetGuardianInstalledAsync(true);
    }

    private async Task<bool> SetGuardianInstalledAsync(bool install)
    {
        await _guardianInstallationGate.WaitAsync();
        try { return await SetGuardianInstalledCoreAsync(install); }
        finally { _guardianInstallationGate.Release(); }
    }

    private async Task ReportForegroundApplicationActivityAsync()
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextApplicationActivityReportUtc) return;
        _nextApplicationActivityReportUtc = now.AddMilliseconds(750);
        var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info)) return;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || GetWindowThreadProcessId(foreground, out var processId) == 0) return;

        try
        {
            using var process = Process.GetProcessById(checked((int)processId));
            if (process.HasExited || process.SessionId != Process.GetCurrentProcess().SessionId) return;
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return;
            var app = Applications.FirstOrDefault(candidate => candidate.IsEnabled
                && candidate.ForceCloseAfterMinutes == 0
                && candidate.ForceCloseAfterInactivityMinutes > 0
                && ForegroundProcessMatches(candidate, process, path));
            if (app is null || (_lastReportedInputTicks.TryGetValue(app.Id, out var previousTick)
                && previousTick == info.dwTime)) return;

            var response = await _guardianClient.ReportApplicationActivityAsync(app.Id);
            if (!response.Success) return;
            if (!string.IsNullOrWhiteSpace(response.TimedSessionToken))
                _timedSessionTokens[app.Id] = response.TimedSessionToken;
            _lastReportedInputTicks[app.Id] = info.dwTime;
            _nextApplicationActivityReportUtc = now.AddSeconds(1);
        }
        catch { }
    }

    private static bool ForegroundProcessMatches(ProtectedApplication app, Process process, string processPath)
    {
        if (!ProtectedTarget.IsScript(app.Path)) return PathsEqual(app.Path, processPath);
        if (!ProtectedTarget.IsPotentialScriptHost(processPath)) return false;
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                var commandLine = Convert.ToString(item["CommandLine"]);
                return !string.IsNullOrWhiteSpace(commandLine)
                    && ProtectedTarget.CommandLineReferences(commandLine, app.Path);
            }
        }
        catch { }
        return false;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint WindowMessageClose = 0x0010;
    private const uint FlashWindowTray = 0x00000002;
    private const uint FlashWindowTimerNoForeground = 0x0000000C;
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo lastInputInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    private async Task<bool> SetGuardianInstalledCoreAsync(bool install)
    {
        var disabledFolders = new List<ProtectedFolder>();
        if (install)
        {
            disabledFolders = GetGuardianIncompatibleFolders();
            if (disabledFolders.Count > 0)
            {
                var details = string.Join("\n", disabledFolders.Select(folder => $"• {folder.Name}: {folder.Path}"));
                var dialog = CreateDialog("Carpetas no compatibles con Guardian", new TextBlock
                {
                    Text = "Guardian no puede aplicar permisos NTFS de forma segura sobre carpetas de OneDrive, enlaces o carpetas redirigidas. " +
                           "Para reparar el servicio se desactivará su protección y se restaurarán sus permisos originales.\n\n" + details,
                    TextWrapping = TextWrapping.Wrap
                }, "Desactivar y reparar", "Cancelar");
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return false;

                foreach (var folder in disabledFolders)
                {
                    folder.IsEnabled = false;
                    folder.UnlockedUntilUtc = null;
                    FolderIconService.TryRestore(folder);
                }
            }
        }

        var script = Path.Combine(AppContext.BaseDirectory, "Service", install ? "Install-Service.ps1" : "Remove-Service.ps1");
        if (!File.Exists(script))
        {
            await ShowMessageAsync("Servicio no disponible", "Los archivos del servicio no están junto a esta publicación. Vuelve a publicar ProtectedApp y copia toda la carpeta.");
            return false;
        }

        GuardianServiceButton.IsEnabled = false;
        GuardianStatusText.Text = LocalizationService.T(install ? "Solicitando instalación…" : "Solicitando desinstalación…");
        var bootstrapSecret = install ? Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)) : null;
        var bootstrapSecretFile = bootstrapSecret is null ? null : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ProtectedApp",
            $"bootstrap-{Guid.NewGuid():N}.secret");
        try
        {
            if (bootstrapSecretFile is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(bootstrapSecretFile)!);
                File.WriteAllText(bootstrapSecretFile, bootstrapSecret!, System.Text.Encoding.ASCII);
            }
            var startInfo = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Normal
            };
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(script);
            if (install && bootstrapSecretFile is not null)
            {
                startInfo.ArgumentList.Add("-BootstrapSecretFile");
                startInfo.ArgumentList.Add(bootstrapSecretFile);
            }
            using var process = Process.Start(startInfo);
            if (process is null) return false;
            await process.WaitForExitAsync();
            var installerExitCode = process.ExitCode;
            await Task.Delay(800);

            var running = GuardianServiceDetector.IsRunning();
            if (install && running)
            {
                _watchdog?.Dispose();
                _watchdog = null;
                _state.Applications = Applications.ToList();
                _state.Folders = Folders.ToList();
                for (var attempt = 0; attempt < 10 && !await _guardianClient.IsAvailableAsync(); attempt++)
                    await Task.Delay(300);
                var bootstrap = await _guardianClient.BootstrapPolicyDetailedAsync(_state, bootstrapSecret ?? string.Empty);
                if (!bootstrap.Success)
                {
                    _guardianManaged = false;
                    _monitor.IsPaused = true;
                    RefreshGuardianStatus();
                    await ShowMessageAsync("No se pudo reparar el servicio",
                        bootstrap.Error ?? "Guardian rechazó la política de protección. Revisa las carpetas protegidas y vuelve a intentarlo.");
                    return false;
                }
                if (_recentMasterPassword is not null)
                    _guardianToken = await _guardianClient.AuthenticateMasterAsync(_recentMasterPassword);
                if (_guardianToken is not null)
                {
                    foreach (var folder in disabledFolders)
                    {
                        var restore = await _guardianClient.RestoreOrphanedFolderLockAsync(folder.Path, _guardianToken);
                        if (!restore.Success)
                            throw new InvalidOperationException(restore.Error ?? $"Guardian no pudo restaurar los permisos de {folder.Name}.");
                    }
                }
                if (_guardianToken is not null)
                {
                    if (await _guardianClient.SyncPolicyAsync(_state, _guardianToken))
                        ClearGuardianSynchronizationPending(reportRecovery: true);
                    else
                        MarkGuardianSynchronizationPending();
                }
                _recentMasterPassword = null;
                var guardianStatus = await _guardianClient.GetStatusAsync();
                _guardianManaged = guardianStatus.Success && guardianStatus.PolicyConfigured;
                _monitor.IsPaused = true;
                if (_guardianManaged && disabledFolders.Count > 0)
                {
                    await SaveAsync();
                    foreach (var folder in disabledFolders)
                        AddActivity(folder.Name, "Protección desactivada: Guardian no admite carpetas sincronizadas o redirigidas");
                    RefreshFolders();
                }
            }
            RefreshGuardianStatus();
            var succeeded = install ? running && _guardianManaged : !running;
            if (succeeded) return true;
            var installationDetail = install ? ReadGuardianInstallationError() : null;
            await ShowMessageAsync("No se pudo cambiar el servicio", install
                ? installationDetail ?? (installerExitCode != 0
                    ? $"El instalador de Guardian terminó con el código {installerExitCode}. Vuelve a intentarlo; el próximo intento mostrará el detalle si Windows rechaza algún paso."
                    : "El servicio arrancó, pero Guardian no confirmó una política protegida. La protección permanece deshabilitada hasta que se repare el servicio.")
                : "Windows no confirmó la eliminación del servicio. Comprueba el aviso de UAC y vuelve a intentarlo.");
            return false;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            RefreshGuardianStatus();
            return false;
        }
        catch (Exception ex)
        {
            RefreshGuardianStatus();
            await ShowMessageAsync("No se pudo cambiar el servicio", LocalizationService.UserFacingError(ex));
            return false;
        }
        finally
        {
            if (bootstrapSecretFile is not null) try { File.Delete(bootstrapSecretFile); } catch { }
            RefreshGuardianStatus();
        }
    }

    private List<ProtectedFolder> GetGuardianIncompatibleFolders()
    {
        var incompatible = new List<ProtectedFolder>();
        foreach (var folder in Folders.Where(folder => folder.IsEnabled))
        {
            if (IsInsideOneDrive(folder.Path))
            {
                incompatible.Add(folder);
                continue;
            }
            try
            {
                if (File.GetAttributes(folder.Path).HasFlag(FileAttributes.ReparsePoint))
                    incompatible.Add(folder);
            }
            catch (Exception)
            {
                // Guardian will provide the specific validation error if the path cannot be inspected here.
            }
        }
        return incompatible;
    }

    private static bool IsInsideOneDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var roots = new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }
                .Select(Environment.GetEnvironmentVariable)
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => Path.GetFullPath(root!).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return roots.Any(root => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static string? ReadGuardianInstallationError()
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ProtectedApp", "guardian-install-error.log");
            if (!File.Exists(path)) return null;
            var error = File.ReadAllLines(path).Skip(1).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
            return string.IsNullOrWhiteSpace(error) ? null : $"Guardian no se pudo instalar: {error}";
        }
        catch { return null; }
    }

    private void MarkGuardianSynchronizationPending()
    {
        var newlyPending = !_guardianSyncPending;
        _guardianSyncPending = true;
        _guardianToken = null;
        if (newlyPending) _guardianSyncPromptDeferred = false;
        if (_guardianSyncWarningRecorded) return;
        _guardianSyncWarningRecorded = true;
        AddActivity("Guardian",
            "Sincronización de política pendiente: Guardian se reinició o la autorización dejó de ser válida; se reintentará automáticamente");
    }

    private void ClearGuardianSynchronizationPending(bool reportRecovery)
    {
        var wasPending = _guardianSyncPending;
        _guardianSyncPending = false;
        _guardianSyncWarningRecorded = false;
        _guardianSyncPromptDeferred = false;
        if (reportRecovery && wasPending)
            AddActivity("Guardian", "Política sincronizada automáticamente después de recuperar la conexión");
    }

    private async Task SynchronizeGuardianPolicyAfterAuthenticationAsync()
    {
        if (_guardianToken is null) return;
        _state.Applications = Applications.ToList();
        if (await _guardianClient.SyncPolicyAsync(_state, _guardianToken))
            ClearGuardianSynchronizationPending(reportRecovery: true);
        else
            MarkGuardianSynchronizationPending();
    }

    private async Task RecoverGuardianSynchronizationAsync()
    {
        if (!await _guardianSyncRecoveryGate.WaitAsync(0)) return;
        try
        {
            if (!_guardianSyncPending || _guardianSyncPromptDeferred || !_sessionUnlocked || _handlingTamper) return;

            GuardianResponse? status = null;
            for (var attempt = 0; attempt < 20 && _sessionUnlocked && !_handlingTamper; attempt++)
            {
                if (GuardianServiceDetector.IsFullyConfigured())
                {
                    status = await _guardianClient.GetStatusAsync();
                    if (status.Success && status.PolicyConfigured) break;
                }
                await Task.Delay(500);
            }

            if (status?.Success != true || !status.PolicyConfigured || !_sessionUnlocked || _handlingTamper)
            {
                _guardianSyncPromptDeferred = true;
                return;
            }

            _guardianManaged = true;
            _monitor.IsPaused = true;
            var verified = await VerifyMasterAsync("Reconectar con Guardian");
            if (!verified || _guardianSyncPending)
                _guardianSyncPromptDeferred = true;
        }
        finally { _guardianSyncRecoveryGate.Release(); }
    }

    private async Task<bool> RecoverLocalStateFromGuardianAsync(string? authorizedToken = null)
    {
        while (true)
        {
            GuardianResponse? response = null;
            if (!string.IsNullOrWhiteSpace(authorizedToken))
            {
                response = await _guardianClient.RecoverPolicyAsync(token: authorizedToken);
                authorizedToken = null;
            }
            else
            {
                var unlockWindow = new UnlockWindow(
                    "Recuperar configuración protegida",
                    "Introduce la contraseña maestra para restaurar las reglas desde Guardian",
                    async password =>
                    {
                        response = await _guardianClient.RecoverPolicyAsync(password);
                        RecordGuardianAuthenticationActivity("ProtectedApp", response,
                            "Contraseña maestra incorrecta al recuperar la configuración");
                        return UnlockAttemptResult.FromGuardian(response);
                    });
                if (!await ShowUnlockWindowAsync(unlockWindow)) return false;
            }
            if (response?.Success != true || response.Policy is null)
            {
                await ShowMessageAsync("No se pudo recuperar", LocalizationService.UserFacingMessage(response?.Error, "Guardian no devolvió la política protegida."));
                return false;
            }

            _guardianToken = response.Token;
            _state.MasterPasswordHash = response.Policy.MasterPasswordHash;
            _state.MasterPasswordSalt = response.Policy.MasterPasswordSalt;
            _state.PollIntervalMilliseconds = response.Policy.ScanIntervalMilliseconds;
            _monitor.IntervalMilliseconds = _state.PollIntervalMilliseconds;
            SelectPollInterval(_state.PollIntervalMilliseconds);
            Applications.Clear();
            var recoveredApplications = new List<ProtectedApplication>();
            foreach (var rule in response.Policy.Rules.OrderBy(rule => rule.Name))
            {
                var app = new ProtectedApplication
                {
                    Id = rule.Id,
                    Name = rule.Name,
                    Path = rule.Path,
                    Category = string.IsNullOrWhiteSpace(rule.Category) ? "General" : rule.Category,
                    IsEnabled = rule.IsEnabled,
                    PasswordHash = rule.PasswordHash,
                    PasswordSalt = rule.PasswordSalt,
                    UnlockGraceMinutes = rule.UnlockGraceMinutes,
                    ForceCloseAfterMinutes = rule.ForceCloseAfterMinutes,
                    ForceCloseAfterInactivityMinutes = rule.ForceCloseAfterInactivityMinutes,
                    ScheduleEnabled = rule.ScheduleEnabled,
                    ScheduleDays = rule.ScheduleDays,
                    ScheduleStartMinutes = rule.ScheduleStartMinutes,
                    ScheduleEndMinutes = rule.ScheduleEndMinutes,
                    BlockOutsideSchedule = rule.BlockOutsideSchedule
                };
                recoveredApplications.Add(app);
            }
            await PrepareProtectedApplicationIconsAsync(recoveredApplications);
            foreach (var app in recoveredApplications)
            {
                app.PropertyChanged += Rule_PropertyChanged;
                Applications.Add(app);
            }
            foreach (var existing in Folders) existing.PropertyChanged -= Folder_PropertyChanged;
            Folders.Clear();
            foreach (var rule in response.Policy.FolderRules.OrderBy(rule => rule.Name))
            {
                var folder = new ProtectedFolder
                {
                    Id = rule.Id,
                    Name = rule.Name,
                    Path = rule.Path,
                    IsEnabled = rule.IsEnabled,
                    PasswordHash = rule.PasswordHash,
                    PasswordSalt = rule.PasswordSalt,
                    UnlockMinutes = rule.UnlockMinutes
                };
                folder.PropertyChanged += Folder_PropertyChanged;
                Folders.Add(folder);
            }
            _sessionUnlocked = true;
            _guardianPolicyRecoveryRequired = false;
            RecordManagementActivity();
            await SaveAsync();
            RefreshVisibleApps();
            RefreshFolders();
            AddActivity("Guardian", "Configuración local restaurada desde la política protegida");
            return true;
        }
    }
}
