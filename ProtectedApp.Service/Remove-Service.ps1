$ErrorActionPreference = 'Stop'
$serviceName = 'ProtectedAppGuardian'
$taskName = 'ProtectedApp Guardian Health Check'
$eventSource = 'ProtectedAppGuardian'
$stateFolder = Join-Path $env:ProgramData 'ProtectedApp'
$maintenanceFile = Join-Path $stateFolder 'guardian-maintenance.flag'
$installedServiceExe = Join-Path $stateFolder 'Service\ProtectedApp.Guardian.exe'
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $escapedScript = $PSCommandPath.Replace('"', '""')
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$escapedScript`"" -Wait
    exit
}

New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
Set-Content -LiteralPath $maintenanceFile -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding ASCII
$removeState = $false
try {
    # Restaurar primero evita dejar una carpeta bloqueada si la desinstalación
    # se interrumpe o si Guardian no puede recuperar sus permisos originales.
    if (Test-Path -LiteralPath $installedServiceExe) {
        & $installedServiceExe --restore-folders
        if ($LASTEXITCODE -ne 0) { throw 'No se pudieron restaurar los permisos originales de las carpetas protegidas.' }
    }

    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        if ($service.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15)) }
        sc.exe delete $serviceName | Out-Null
    }
    if (Test-Path -LiteralPath $installedServiceExe) {
        & $installedServiceExe --remove-gates
        if ($LASTEXITCODE -ne 0) { throw 'No se pudieron retirar las puertas preventivas de las aplicaciones protegidas.' }
    }
    if ([System.Diagnostics.EventLog]::SourceExists($eventSource)) {
        Remove-EventLog -Source $eventSource
    }
    $removeState = $true
}
finally {
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    if ($removeState) {
        Remove-Item -LiteralPath $stateFolder -Recurse -Force -ErrorAction SilentlyContinue
    }
}
Write-Host 'ProtectedApp Guardian, su tarea SYSTEM y sus datos de supervisión se han eliminado.' -ForegroundColor Green
