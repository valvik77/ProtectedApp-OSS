param(
    [string]$AppPath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'ProtectedApp.exe'),
    [string]$BootstrapSecretFile
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Integrity-Transaction.ps1')
$serviceName = 'ProtectedAppGuardian'
$taskName = 'ProtectedApp Guardian Health Check'
$eventSource = 'ProtectedAppGuardian'
$serviceExe = Join-Path $PSScriptRoot 'ProtectedApp.Guardian.exe'
$gateExe = Join-Path $PSScriptRoot 'ProtectedApp.Gate.exe'
$stateFolder = Join-Path $env:ProgramData 'ProtectedApp'
$serviceFolder = Join-Path $stateFolder 'Service'
$policyFolder = Join-Path $stateFolder 'Policy'
$installedServiceExe = Join-Path $serviceFolder 'ProtectedApp.Guardian.exe'
$installedGateExe = Join-Path $stateFolder 'ProtectedApp.Gate.exe'
$maintenanceFile = Join-Path $stateFolder 'guardian-maintenance.flag'
$integrityFile = Join-Path $stateFolder 'guardian-integrity.json'
$recoveryFolder = Join-Path $stateFolder 'Recovery'
$signerIdentityFile = Join-Path $policyFolder 'guardian-signer.thumbprint'
$agentIdentityFile = Join-Path $policyFolder 'guardian-agent-identity.json'
$versionFile = Join-Path $stateFolder 'guardian-protection.version'
$installErrorFile = Join-Path $stateFolder 'guardian-install-error.log'
$transactionFolder = Join-Path $serviceFolder ('.update-' + [Guid]::NewGuid().ToString('N'))
$snapshot = $null
$replacementStarted = $false
$rollbackFailed = $false

trap {
    try {
        New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
        $message = "{0:O}`r`n{1}`r`n{2}" -f [DateTimeOffset]::UtcNow, $_.Exception.Message, $_.ScriptStackTrace
        Set-Content -LiteralPath $installErrorFile -Value $message -Encoding UTF8
    }
    catch { }
    exit 1
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $escapedScript = $PSCommandPath.Replace('"', '""')
    $escapedApp = $AppPath.Replace('"', '""')
    $escapedSecretFile = ([string]$BootstrapSecretFile).Replace('"', '""')
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$escapedScript`" -AppPath `"$escapedApp`" -BootstrapSecretFile `"$escapedSecretFile`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    exit
}

if (-not (Test-Path -LiteralPath $serviceExe)) { throw "No se encuentra $serviceExe" }
if (-not (Test-Path -LiteralPath $gateExe)) { throw "No se encuentra $gateExe" }
if (-not (Test-Path -LiteralPath $AppPath)) { throw "No se encuentra $AppPath" }
if ([string]::IsNullOrWhiteSpace($BootstrapSecretFile) -or -not (Test-Path -LiteralPath $BootstrapSecretFile)) { throw 'La instalación debe iniciarse desde ProtectedApp para crear la política de forma segura.' }
$BootstrapSecret = (Get-Content -LiteralPath $BootstrapSecretFile -Raw -Encoding ASCII).Trim()
Remove-Item -LiteralPath $BootstrapSecretFile -Force -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($BootstrapSecret)) { throw 'El secreto de arranque no es válido.' }

New-Item -ItemType Directory -Path $stateFolder -Force | Out-Null
Remove-Item -LiteralPath $installErrorFile -Force -ErrorAction SilentlyContinue

# Una reparación no puede borrar la política ni la copia de ACL antes de
# comprobar que el nuevo Guardian puede arrancar. Esos archivos permiten
# recuperar carpetas protegidas si una actualización se interrumpe. La
# política se sustituye de forma transaccional durante BootstrapPolicy.
$staleGuardianFiles = @(
    'guardian-agent.json',
    'guardian-running.json',
    'guardian-tamper.json'
)
foreach ($staleGuardianFile in $staleGuardianFiles) {
    Remove-Item -LiteralPath (Join-Path $stateFolder $staleGuardianFile) -Force -ErrorAction SilentlyContinue
}

Set-Content -LiteralPath $maintenanceFile -Value ([DateTimeOffset]::UtcNow.ToString('O')) -Encoding ASCII
try {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

    $existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force; $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(15)) }
    }

    # Una instalación anterior puede haber eliminado el registro del servicio
    # mientras su proceso seguía vivo. Esos procesos huérfanos bloquean el
    # ejecutable de Guardian e impiden sustituirlo durante la reparación.
    Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue
    $guardianExitDeadline = [DateTime]::UtcNow.AddSeconds(10)
    while ((Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue) -and [DateTime]::UtcNow -lt $guardianExitDeadline) {
        Start-Sleep -Milliseconds 200
    }
    if (Get-Process -Name 'ProtectedApp.Guardian' -ErrorAction SilentlyContinue) {
        throw 'Un proceso anterior de Guardian sigue activo y bloquea su actualización.'
    }

    if (-not [System.Diagnostics.EventLog]::SourceExists($eventSource)) {
        New-EventLog -LogName Application -Source $eventSource
    }

    New-Item -ItemType Directory -Path $serviceFolder -Force | Out-Null
    New-Item -ItemType Directory -Path $policyFolder -Force | Out-Null
    $snapshot = @(Save-GuardianTransaction $stateFolder $transactionFolder)
    $previousSignerIdentity = Read-GuardianSignerIdentity $signerIdentityFile
    $replacementStarted = $true
    Copy-Item -LiteralPath $serviceExe -Destination $installedServiceExe -Force
    Copy-Item -LiteralPath $gateExe -Destination $installedGateExe -Force

    $guardianIdentity = Set-GuardianSignerIdentity $previousSignerIdentity $installedServiceExe $installedGateExe $signerIdentityFile

    $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $none = [System.Security.AccessControl.PropagationFlags]::None
    $allow = [System.Security.AccessControl.AccessControlType]::Allow
    $system = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-18')
    $admins = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-11')

    $rootAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $rootAcl.SetAccessRuleProtection($true, $false)
    $rootAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $inherit, $none, $allow)))
    $rootAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $inherit, $none, $allow)))
    $rootAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $inherit, $none, $allow)))
    Set-Acl -LiteralPath $stateFolder -AclObject $rootAcl

    $privateAcl = New-Object System.Security.AccessControl.DirectorySecurity
    $privateAcl.SetAccessRuleProtection($true, $false)
    $privateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $inherit, $none, $allow)))
    $privateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $inherit, $none, $allow)))
    Set-Acl -LiteralPath $serviceFolder -AclObject $privateAcl
    Set-Acl -LiteralPath $policyFolder -AclObject $privateAcl
    New-Item -ItemType Directory -Path $recoveryFolder -Force | Out-Null
    Set-Acl -LiteralPath $recoveryFolder -AclObject $privateAcl

    $gateAcl = New-Object System.Security.AccessControl.FileSecurity
    $gateAcl.SetAccessRuleProtection($true, $false)
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($system, 'FullControl', $allow)))
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($admins, 'FullControl', $allow)))
    $gateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', $allow)))
    Set-Acl -LiteralPath $installedGateExe -AclObject $gateAcl
    New-GuardianIntegrityBaseline $installedServiceExe $installedGateExe $stateFolder $signerIdentityFile
    Remove-Item -LiteralPath $versionFile -Force -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($BootstrapSecret)) {
        Set-Content -LiteralPath (Join-Path $policyFolder 'bootstrap.secret') -Value $BootstrapSecret -Encoding ASCII
    }

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
    if ($existing) {
        sc.exe config $serviceName binPath= $binaryPath start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'No se pudo configurar Guardian.' }
    }
    else {
        New-Service -Name $serviceName -BinaryPathName $binaryPath -DisplayName 'ProtectedApp Guardian' -Description 'Mantiene el agente ProtectedApp activo en la sesión interactiva.' -StartupType Automatic | Out-Null
    }
    sc.exe failure $serviceName reset= 86400 actions= restart/0/restart/1000/restart/5000 | Out-Null
    sc.exe failureflag $serviceName 1 | Out-Null

    $healthArguments = '--health-watch --app "{0}"' -f $resolvedAppPath
    $action = New-ScheduledTaskAction -Execute $installedServiceExe -Argument $healthArguments
    $startupTrigger = New-ScheduledTaskTrigger -AtStartup
    $recurringTrigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($startupTrigger, $recurringTrigger) -Principal $principal -Settings $settings -Description 'Rearma las puertas, bloquea la sesión y reinicia ProtectedApp Guardian si se detiene.' -Force | Out-Null

    Start-Service -Name $serviceName
    (Get-Service -Name $serviceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(15))
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
            throw "Falló la instalación y no se pudo completar el rollback. Se conserva $transactionFolder. $($_.Exception.Message)"
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
    elseif ($existing) {
        Start-Service -Name $serviceName -ErrorAction SilentlyContinue
        Start-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
}
Start-ScheduledTask -TaskName $taskName
Write-Host 'ProtectedApp Guardian y su supervisor SYSTEM de 250 ms están instalados y en ejecución.' -ForegroundColor Green
