using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProtectedApp.Services;
using ProtectedApp.Shared;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async Task<bool> EnsureFolderManagementAvailableAsync(string authenticationTitle)
    {
        if (!_guardianManaged || !GuardianServiceDetector.IsFullyConfigured())
        {
            await ShowMessageAsync("Guardian es necesario", "La protección de carpetas requiere que el servicio Guardian esté instalado, actualizado y en ejecución.");
            return false;
        }
        return _guardianToken is not null || await VerifyMasterAsync(authenticationTitle);
    }

    private async Task<GuardianResponse> PersistFolderPolicyAsync()
    {
        await _saveGate.WaitAsync();
        try
        {
            _state.Applications = Applications.ToList();
            _state.Folders = [];
            _state.ActivityHistory = _activityHistory.ToList();
            var response = await _guardianClient.SyncPolicyDetailedAsync(_state, _guardianToken ?? string.Empty);
            if (!response.Success) return response;
            await _store.SaveAsync(_state);
            ClearGuardianSynchronizationPending(reportRecovery: false);
            return response;
        }
        finally { _saveGate.Release(); }
    }

    private static NumberBox CreateFolderMinutesBox(int value) => new()
    {
        Header = "Tiempo de desbloqueo (minutos)", Value = Math.Clamp(value, 1, 10_080), Minimum = 1, Maximum = 10_080,
        SmallChange = 1, LargeChange = 5, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline
    };

    private static bool TryReadFolderMinutes(NumberBox box, out int minutes, out string error)
    {
        minutes = double.IsNaN(box.Value) ? 0 : (int)Math.Round(box.Value);
        error = minutes is < 1 or > 10_080 ? "El tiempo debe estar entre 1 y 10080 minutos." : string.Empty;
        return error.Length == 0;
    }

    private TextBlock CreateDialogErrorText() => new()
    {
        Foreground = ThemeBrush(Windows.UI.Color.FromArgb(255, 255, 85, 85), Windows.UI.Color.FromArgb(255, 248, 81, 73)),
        FontSize = 11, TextWrapping = TextWrapping.Wrap
    };
}
