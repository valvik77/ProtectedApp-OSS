param(
    [string]$InstallPath = (Join-Path $env:ProgramFiles 'ProtectedApp')
)

# La preparación no debe impedir una actualización: las extensiones del
# Explorador están versionadas y los restos bloqueados se limpian después o al
# reiniciar. La parada de procesos es, por tanto, estrictamente de mejor
# esfuerzo.
$ErrorActionPreference = 'Continue'
$serviceName = 'ProtectedAppGuardian'
$taskName = 'ProtectedApp Guardian Health Check'
$stateFolder = Join-Path $env:ProgramData 'ProtectedApp'
$maintenanceFile = Join-Path $stateFolder 'guardian-maintenance.flag'
$agentLeaseFile = Join-Path $stateFolder 'guardian-agent.json'
$shellDirectory = Join-Path $InstallPath 'Shell'
$appExecutable = Join-Path $InstallPath 'ProtectedApp.exe'

New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
Set-Content -LiteralPath $maintenanceFile -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding ASCII
# The installed agent will be stopped deliberately below. Forget the old
# observation while maintenance is active so the replacement service does
# not classify the short update window as a forced termination.
Remove-Item -LiteralPath $agentLeaseFile -Force -ErrorAction SilentlyContinue

# Ask the installed UI to close itself first so mounted vaults are written and
# unmounted before its binaries are replaced. Older releases do not understand
# this activation; the short forced fallback remains only for that case or a
# genuinely unresponsive UI.
if (Test-Path -LiteralPath $appExecutable) {
    Start-Process -FilePath $appExecutable -ArgumentList '--shutdown-for-update' -ErrorAction SilentlyContinue
    $shutdownDeadline = [DateTime]::UtcNow.AddSeconds(12)
    while ((Get-Process -Name 'ProtectedApp' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $shutdownDeadline) {
        Start-Sleep -Milliseconds 250
    }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($service) {
    try {
        Disable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
            $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
        }
    }
    catch { }
}

Get-Process -Name 'ProtectedApp' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# Las DLL de la extensión del Explorador están versionadas, por lo que una DLL
# antigua en uso no bloquea la copia de la nueva. Se intenta liberar únicamente
# el host COM propio; nunca se cierra Explorer durante una actualización.
$existingShellExtension = Get-ChildItem -LiteralPath $shellDirectory -Filter 'ProtectedApp.ShellExtension.*.dll' -File -ErrorAction SilentlyContinue |
    Select-Object -First 1
if ($existingShellExtension) {
    $appId = 'B22C3BE7-3ADC-4D51-A1E8-C7F72188F6E7'
    $extensionPaths = Get-ChildItem -LiteralPath $shellDirectory -Filter 'ProtectedApp.ShellExtension.*.dll' -File -ErrorAction SilentlyContinue |
        ForEach-Object { [IO.Path]::GetFullPath($_.FullName) }
    $hostIds = [Collections.Generic.HashSet[int]]::new()
    Get-CimInstance Win32_Process -Filter "Name='dllhost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($appId, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { $null = $hostIds.Add([int]$_.ProcessId) }
    foreach ($host in Get-Process -Name 'dllhost' -ErrorAction SilentlyContinue) {
        try {
            if ($host.Modules | Where-Object { $_.FileName -and $extensionPaths -contains [IO.Path]::GetFullPath($_.FileName) }) {
                $null = $hostIds.Add($host.Id)
            }
        }
        catch { }
    }
    foreach ($hostId in $hostIds) { Stop-Process -Id $hostId -Force -ErrorAction SilentlyContinue }
}

exit 0
