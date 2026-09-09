using ProtectedApp.Services;

namespace ProtectedApp;

public sealed partial class MainWindow
{
    private async Task SaveAsync(bool synchronizeGuardian = true)
    {
        var recoverSynchronization = false;
        await _saveGate.WaitAsync();
        try
        {
            _state.Applications = Applications.ToList();
            _state.Folders = [];
            _state.Vaults = Vaults.ToList();
            _state.ActivityHistory = _activityHistory.ToList();
            await _store.SaveAsync(_state);
            if (synchronizeGuardian && _guardianManaged && _guardianToken is not null)
            {
                if (await _guardianClient.SyncPolicyAsync(_state, _guardianToken))
                    ClearGuardianSynchronizationPending(reportRecovery: false);
                else
                    MarkGuardianSynchronizationPending();
            }
            else if (synchronizeGuardian && GuardianServiceDetector.IsInstalled())
                MarkGuardianSynchronizationPending();

            recoverSynchronization = synchronizeGuardian && _guardianSyncPending
                && !_guardianSyncPromptDeferred
                && _sessionUnlocked
                && !_handlingTamper;
        }
        finally { _saveGate.Release(); }
        if (recoverSynchronization) _ = RecoverGuardianSynchronizationAsync();
    }
}
