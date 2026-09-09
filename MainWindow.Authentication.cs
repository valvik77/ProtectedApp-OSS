using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtectedApp.Models;
using ProtectedApp.Services;
using ProtectedApp.Shared;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async Task CreateMasterPasswordAsync(string title, string subtitle)
    {
        await _dialogGate.WaitAsync();
        try
        {
            while (true)
            {
                var password = new PasswordBox { PlaceholderText = "Mínimo 12 caracteres", PasswordRevealMode = PasswordRevealMode.Peek };
                var confirmation = new PasswordBox { PlaceholderText = "Repite la contraseña", PasswordRevealMode = PasswordRevealMode.Peek };
                var error = new TextBlock { Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)), FontSize = 12, TextWrapping = TextWrapping.Wrap };
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(new TextBlock { Text = subtitle, Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 98, 114, 164), Windows.UI.Color.FromArgb(255, 59, 59, 59)), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(password);
                panel.Children.Add(confirmation);
                panel.Children.Add(error);
                var dialog = CreateDialog(title, panel, "Guardar", null);
                var valid = false;
                dialog.PrimaryButtonClick += (_, args) =>
                {
                    if (password.Password.Length < PasswordService.MinimumPasswordLength) { error.Text = LocalizationService.T("La contraseña debe tener al menos 12 caracteres."); args.Cancel = true; return; }
                    if (password.Password != confirmation.Password) { error.Text = LocalizationService.T("Las contraseñas no coinciden."); args.Cancel = true; return; }
                    valid = true;
                };
                await dialog.ShowAsync();
                if (!valid) continue;
                var result = PasswordService.Hash(password.Password);
                _state.MasterPasswordHash = result.Hash;
                _state.MasterPasswordSalt = result.Salt;
                _monitor.RevokeAllAuthorizations();
                _recentMasterPassword = password.Password;
                await SaveAsync();
                return;
            }
        }
        finally { _dialogGate.Release(); }
    }

    private async Task<bool> VerifyMasterAsync(string title)
    {
        if (string.IsNullOrWhiteSpace(_state.MasterPasswordHash)) return false;
        await _dialogGate.WaitAsync();
        try
        {
            var wasVisible = _appWindow.IsVisible;
            var guardianManaged = _guardianManaged;
            if (guardianManaged && !await _guardianClient.IsAvailableAsync())
            {
                await ShowMessageAsync("Guardian no disponible", "La autenticación se ha cerrado de forma segura porque el motor protegido no respondió.");
                return false;
            }
            bool verified;
            string? verifiedPassword = null;
            // Guardian-issued tokens are bound to this process. Windows Hello
            // can only be offered as an alternative while such an
            // authorization is still valid; otherwise it would unlock the
            // panel without authorizing any Guardian operation.
            var allowWindowsHello = _state.UseWindowsHello
                && (!guardianManaged || !string.IsNullOrWhiteSpace(_guardianToken));
            try
            {
                UnlockWindow unlockWindow;
                if (guardianManaged)
                {
                    unlockWindow = new UnlockWindow(
                        title,
                        "Introduce tu contraseña maestra para continuar",
                        async password =>
                        {
                            var response = await _guardianClient.AuthenticateMasterDetailedAsync(password);
                            _guardianToken = response.Success ? response.Token : null;
                            if (response.Success) verifiedPassword = password;
                            RecordGuardianAuthenticationActivity("ProtectedApp", response,
                                "Contraseña maestra incorrecta");
                            return UnlockAttemptResult.FromGuardian(response);
                        },
                        allowWindowsHello);
                }
                else
                {
                    unlockWindow = new UnlockWindow(
                        title,
                        "Introduce tu contraseña maestra para continuar",
                        password =>
                        {
                            var valid = PasswordService.Verify(password, _state.MasterPasswordHash, _state.MasterPasswordSalt);
                            if (valid) verifiedPassword = password;
                            var result = _localAuthenticationThrottle.Verify("master", valid, "Contraseña maestra incorrecta.");
                            RecordLocalAuthenticationActivity("ProtectedApp", result,
                                "Contraseña maestra incorrecta");
                            return result.Attempt;
                        },
                        allowWindowsHello);
                }
                verified = await ShowUnlockWindowAsync(unlockWindow);
            }
            finally
            {
                if (wasVisible)
                    RestoreMainWindowFocus();
            }
            if (verified)
            {
                // Existing credentials used the previous PBKDF2 work factor.
                // Upgrade them only after a successful proof of knowledge; the
                // authenticated Guardian token then propagates the new record.
                if (verifiedPassword is not null && PasswordService.NeedsRehash(_state.MasterPasswordSalt))
                {
                    var upgraded = PasswordService.Hash(verifiedPassword);
                    _state.MasterPasswordHash = upgraded.Hash;
                    _state.MasterPasswordSalt = upgraded.Salt;
                    await SaveAsync();
                }
                _recentMasterPassword = guardianManaged ? null : verifiedPassword;
                _sessionUnlocked = true;
                RecordManagementActivity();
                if (guardianManaged && _guardianToken is not null)
                {
                    if (_guardianPolicyRecoveryRequired)
                        await RecoverLocalStateFromGuardianAsync(_guardianToken);
                    else
                        await SynchronizeGuardianPolicyAfterAuthenticationAsync();
                }
                else if (_guardianSyncPending && GuardianServiceDetector.IsInstalled())
                    _ = RecoverGuardianSynchronizationAsync();
            }
            else if (!wasVisible)
            {
                HideToTray(showNotification: false);
            }
            return verified;
        }
        finally { _dialogGate.Release(); }
    }

    private async Task RequestApplicationAccessAsync(ProtectedApplication app, bool guardianManaged = false)
    {
        await _unlockGate.WaitAsync();
        try
        {
            GuardianResponse? guardianResponse = null;
            UnlockWindow unlockWindow;
            if (guardianManaged)
            {
                unlockWindow = new UnlockWindow(app.Name, "Introduce la contraseña para continuar", async password =>
                {
                    guardianResponse = await _guardianClient.AuthorizeAndLaunchAsync(app.Id, password);
                    RecordGuardianAuthenticationActivity(app.Name, guardianResponse,
                        "Contraseña incorrecta al intentar desbloquear la aplicación");
                    return UnlockAttemptResult.FromGuardian(guardianResponse, acceptPasswordWithoutLaunch: true);
                },
                _state.UseWindowsHello,
                async () =>
                {
                    guardianResponse = await _guardianClient.AuthorizeAndLaunchAsync(app.Id, null, _guardianToken);
                    return UnlockAttemptResult.FromGuardian(guardianResponse, acceptPasswordWithoutLaunch: true);
                });
            }
            else
            {
                unlockWindow = new UnlockWindow(app.Name, "Introduce la contraseña para continuar", password =>
                {
                    var valid = string.IsNullOrWhiteSpace(app.PasswordHash)
                        ? PasswordService.Verify(password, _state.MasterPasswordHash, _state.MasterPasswordSalt)
                        : PasswordService.Verify(password, app.PasswordHash, app.PasswordSalt);
                    var result = _localAuthenticationThrottle.Verify($"rule:{app.Id:N}", valid,
                        "Contraseña incorrecta.");
                    RecordLocalAuthenticationActivity(app.Name, result,
                        "Contraseña incorrecta al intentar desbloquear la aplicación");
                    return result.Attempt;
                },
                _state.UseWindowsHello);
            }
            var verified = await ShowUnlockWindowAsync(unlockWindow);
            _monitor.Resolve(app);
            if (verified)
            {
                if (guardianManaged)
                {
                    if (guardianResponse?.Success == true)
                    {
                        if (!string.IsNullOrWhiteSpace(guardianResponse.TimedSessionToken))
                            _timedSessionTokens[app.Id] = guardianResponse.TimedSessionToken;
                        AddActivity(app.Name, "Acceso autorizado por Guardian; aplicación iniciada");
                    }
                    else
                    {
                        AddActivity(app.Name, "Guardian rechazó el inicio.");
                        await ShowMessageAsync("No se pudo iniciar", LocalizationService.UserFacingMessage(guardianResponse?.Error, "Guardian rechazó la autorización."));
                    }
                    await SaveAsync();
                    return;
                }
                try
                {
                    _monitor.PrepareAuthorizedLaunch(app);
                    var launch = ProtectedTarget.GetLaunchCommand(app.Path);
                    var process = Process.Start(new ProcessStartInfo(launch.Executable, launch.Arguments)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(app.Path)
                    });
                    if (process is not null) _monitor.AllowProcess(process, app.Path);
                    AddActivity(app.Name, "Acceso autorizado; aplicación iniciada");
                }
                catch (Exception)
                {
                    _monitor.CancelAuthorizedLaunch(app);
                    AddActivity(app.Name, "No se pudo iniciar la aplicación.");
                    _tray.ShowBalloon("No se pudo iniciar", app.Name);
                }
            }
            else
            {
                if (guardianManaged) await _guardianClient.DismissPendingAsync(app.Id);
                AddActivity(app.Name, "Acceso cancelado");
            }
            await SaveAsync();
        }
        finally { _unlockGate.Release(); }
    }

    private async Task<bool> ShowUnlockWindowAsync(UnlockWindow unlockWindow)
    {
        _activeUnlockWindow = unlockWindow;
        try
        {
            return await unlockWindow.ShowAsync();
        }
        finally
        {
            if (ReferenceEquals(_activeUnlockWindow, unlockWindow))
                _activeUnlockWindow = null;
        }
    }

    private void RecordGuardianAuthenticationActivity(string source, GuardianResponse response, string rejectionMessage)
    {
        if (response.LockoutEnded)
            AddActivity(source, "Bloqueo temporal por intentos fallidos finalizado");
        if (response.PasswordRejected)
            AddActivity(source, rejectionMessage);
        if (response.LockoutStarted)
            AddActivity(source,
                $"Bloqueo temporal iniciado durante {response.RetryAfterSeconds} s tras {response.FailureCount} intentos fallidos");
    }

    private void RecordLocalAuthenticationActivity(string source, LocalThrottleResult result, string rejectionMessage)
    {
        if (result.LockoutEnded)
            AddActivity(source, "Bloqueo temporal por intentos fallidos finalizado");
        if (result.PasswordRejected)
            AddActivity(source, rejectionMessage);
        if (result.LockoutStarted)
            AddActivity(source,
                $"Bloqueo temporal iniciado durante {result.Attempt.RetryAfterSeconds} s tras {result.Attempt.FailureCount} intentos fallidos");
    }
}
