param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ProtectedApp.exe')
)

$ErrorActionPreference = 'Stop'
$serviceName = 'ProtectedAppGuardian'
$taskName = 'ProtectedApp Guardian Health Check'
$stateFolder = Join-Path $env:ProgramData 'ProtectedApp'
$serviceFolder = Join-Path $stateFolder 'Service'
$installedServiceExe = Join-Path $serviceFolder 'ProtectedApp.Guardian.exe'
$sourceServiceExe = Join-Path $PSScriptRoot 'ProtectedApp.Guardian.exe'
$sourceGateExe = Join-Path $PSScriptRoot 'ProtectedApp.Gate.exe'
$installedGateExe = Join-Path $stateFolder 'ProtectedApp.Gate.exe'
$maintenanceFile = Join-Path $stateFolder 'guardian-maintenance.flag'
$integrityFile = Join-Path $stateFolder 'guardian-integrity.json'
$recoveryFolder = Join-Path $stateFolder 'Recovery'
$signerIdentityFile = Join-Path $stateFolder 'Policy\guardian-signer.thumbprint'
$agentIdentityFile = Join-Path $stateFolder 'Policy\guardian-agent-identity.json'
$agentLeaseFile = Join-Path $stateFolder 'guardian-agent.json'
$updateErrorFile = Join-Path $stateFolder 'guardian-update-error.log'
$transactionFolder = Join-Path $serviceFolder ('.update-' + [Guid]::NewGuid().ToString('N'))
$stagedServiceExe = Join-Path $transactionFolder 'ProtectedApp.Guardian.exe.new'
$stagedGateExe = Join-Path $transactionFolder 'ProtectedApp.Gate.exe.new'
$snapshot = $null
$replacementStarted = $false
$rollbackFailed = $false

trap {
    try {
        New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
        $message = "{0:O}`r`n{1}`r`n{2}" -f [DateTimeOffset]::UtcNow, $_.Exception.Message, $_.ScriptStackTrace
        Set-Content -LiteralPath $updateErrorFile -Value $message -Encoding UTF8
    }
    catch { }
    exit 1
}

. (Join-Path $PSScriptRoot 'Integrity-Transaction.ps1')

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if (-not $service) {
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    exit 0
}

if (-not (Test-Path -LiteralPath $sourceServiceExe)) { throw "No se encuentra $sourceServiceExe" }
if (-not (Test-Path -LiteralPath $sourceGateExe)) { throw "No se encuentra $sourceGateExe" }

New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
Set-Content -LiteralPath $maintenanceFile -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding ASCII
Remove-Item -LiteralPath $agentLeaseFile -Force -ErrorAction SilentlyContinue
try {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    $service = Get-Service -Name $serviceName
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
    }

    # La tarea SYSTEM de salud ejecuta el mismo binario con --health-watch.
    # Detener la tarea no garantiza que su proceso haya terminado antes de
    # copiar el ejecutable. Durante mantenimiento es seguro finalizar ambas
    # instancias para no dejar Guardian ejecutando una versión anterior.
    Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    $guardianExitDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $guardianExitDeadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue) {
        throw 'Guardian sigue en ejecución y bloquea la actualización del servicio.'
    }

    # Primero se copian y verifican ambos binarios en un directorio privado.
    # El servicio instalado no se toca hasta disponer de una vuelta atrás.
    New-Item -ItemType Directory -Path $serviceFolder -Force | Out-Null
    $snapshot = @(Save-GuardianTransaction $stateFolder $transactionFolder)
    $previousSignerIdentity = Read-GuardianSignerIdentity $signerIdentityFile
    Copy-Item -LiteralPath $sourceServiceExe -Destination $stagedServiceExe -Force
    Copy-Item -LiteralPath $sourceGateExe -Destination $stagedGateExe -Force
    if ((Get-FileHash -LiteralPath $sourceServiceExe -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $stagedServiceExe -Algorithm SHA256).Hash) {
        throw 'La copia temporal de Guardian no superó la verificación.'
    }
    if ((Get-FileHash -LiteralPath $sourceGateExe -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $stagedGateExe -Algorithm SHA256).Hash) {
        throw 'La copia temporal de Gate no superó la verificación.'
    }
    $replacementStarted = $true
    Copy-Item -LiteralPath $stagedServiceExe -Destination $installedServiceExe -Force
    Copy-Item -LiteralPath $stagedGateExe -Destination $installedGateExe -Force

    $guardianIdentity = Set-GuardianSignerIdentity $previousSignerIdentity $installedServiceExe $installedGateExe $signerIdentityFile

    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')
    $gateAcl = New-Object System.Security.AccessControl.FileSecurity
    $gateAcl.SetAccessRuleProtection($true, $false)
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $allow)))
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $allow)))
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $allow)))
    Set-Acl -LiteralPath $installedGateExe -AclObject $gateAcl
    New-GuardianIntegrityBaseline $installedServiceExe $installedGateExe $stateFolder $signerIdentityFile

    $resolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
    $appSignature = Get-AuthenticodeSignature -LiteralPath $resolvedAppPath
    $appIdentity = (([string]$appSignature.SignerCertificate.Thumbprint) -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
    if ($appSignature.Status -ne 'Valid' -or $appIdentity.Length -ne 40 -or $appIdentity -ne $guardianIdentity) {
        throw 'ProtectedApp debe conservar una firma válida del mismo editor que Guardian.'
    }
    $agentIdentity = [ordered]@{
        Version = 1
        Path = $resolvedAppPath
        Sha256 = (Get-FileHash -LiteralPath $resolvedAppPath -Algorithm SHA256).Hash.ToUpperInvariant()
        SignerThumbprint = $appIdentity
    } | ConvertTo-Json -Compress
    Set-Content -LiteralPath $agentIdentityFile -Value $agentIdentity -Encoding UTF8
    $binaryPath = '"{0}" --app "{1}"' -f $installedServiceExe, $resolvedAppPath
    sc.exe config $serviceName binPath= $binaryPath start= auto | Out-Null
    sc.exe failure $serviceName reset= 86400 actions= restart/0/restart/1000/restart/5000 | Out-Null
    sc.exe failureflag $serviceName 1 | Out-Null
    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))

    # Running only confirms the SCM state. Guardian still needs a brief window
    # to recreate the per-user background agent. Keep maintenance active until
    # that process is visible, otherwise the persisted anti-tamper observation
    # can turn an authorized update into a Windows session lock.
    $agentDeadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $agentReady = @(Get-Process -Name 'ProtectedApp' -ErrorAction SilentlyContinue).Count -gt 0
        if (-not $agentReady) { Start-Sleep -Milliseconds 200 }
    } while (-not $agentReady -and [DateTime]::UtcNow -lt $agentDeadline)

    if ($agentReady) { Start-Sleep -Milliseconds 750 }
}
catch {
    $operationError = $_
    if ($replacementStarted) {
        try {
            Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            $current = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($current -and $current.Status -ne 'Stopped') {
                Stop-Service -Name $serviceName -Force
                $current.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15))
            }
            Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction Stop
            Restore-GuardianTransaction $snapshot
        }
        catch {
            $rollbackFailed = $true
            throw "Falló la actualización y no se pudo completar el rollback. Se conserva $transactionFolder. $($_.Exception.Message)"
        }
    }
    throw $operationError
}
finally {
    if (-not $rollbackFailed) { Remove-GuardianTransaction $stateFolder $transactionFolder }
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    if ($rollbackFailed) {
        Disable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null
    }
    else {
        Enable-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue | Out-Null
        $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($currentService -and $currentService.Status -ne 'Running') {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        }
        Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
}
