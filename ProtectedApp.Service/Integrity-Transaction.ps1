# Snapshot bytes, including absence, before replacing any installed component.
# Rollback must never calculate a new trusted baseline from installed binaries.
function Save-GuardianTransaction {
    param([string]$StateFolder, [string]$TransactionFolder)
    New-Item -ItemType Directory -Path $TransactionFolder -Force | Out-Null
    $acl = New-Object System.Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $identity, 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $TransactionFolder -AclObject $acl
    $entries = @()
    foreach ($relative in @('guardian-integrity.json', 'Policy\guardian-signer.thumbprint',
        'Recovery\ProtectedApp.Guardian.exe', 'Recovery\ProtectedApp.Gate.exe',
        'guardian-protection.version', 'Policy\bootstrap.secret', 'Policy\guardian-agent-identity.json',
        'Service\ProtectedApp.Guardian.exe', 'ProtectedApp.Gate.exe')) {
        $target = Join-Path $StateFolder $relative
        $backup = Join-Path $TransactionFolder ($entries.Count.ToString() + '.previous')
        $exists = Test-Path -LiteralPath $target -PathType Leaf
        $hash = $null
        if ($exists) {
            $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            Copy-Item -LiteralPath $target -Destination $backup -Force
            if ((Get-FileHash -LiteralPath $backup -Algorithm SHA256).Hash -ne $hash) {
                throw "No se pudo verificar la copia previa de $relative"
            }
        }
        $entries += [pscustomobject]@{ Target = $target; Backup = $backup; Exists = $exists; Hash = $hash }
    }
    $entries | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $TransactionFolder 'snapshot.json') -Encoding UTF8
    return $entries
}

function Restore-GuardianTransaction {
    param([object[]]$Entries)
    # Validate all saved bytes before restoring even the first file.
    foreach ($entry in $Entries) {
        if ($entry.Exists -and (Get-FileHash -LiteralPath $entry.Backup -Algorithm SHA256).Hash -ne $entry.Hash) {
            throw 'El respaldo de la transacción cambió; se conserva para recuperación manual.'
        }
    }
    foreach ($entry in $Entries) {
        if ($entry.Exists) {
            New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $entry.Backup -Destination $entry.Target -Force
            if ((Get-FileHash -LiteralPath $entry.Target -Algorithm SHA256).Hash -ne $entry.Hash) {
                throw 'No se pudo restaurar exactamente el estado anterior.'
            }
        }
        elseif (Test-Path -LiteralPath $entry.Target) {
            Remove-Item -LiteralPath $entry.Target -Force
        }
    }
}

function Remove-GuardianTransaction {
    param([string]$StateFolder, [string]$TransactionFolder)
    $parent = [IO.Path]::GetFullPath((Join-Path $StateFolder 'Service')).TrimEnd('\')
    $target = [IO.Path]::GetFullPath($TransactionFolder).TrimEnd('\')
    if ((Split-Path $target -Parent) -ne $parent -or (Split-Path $target -Leaf) -notlike '.update-*') {
        throw 'La ruta de limpieza de la transacción no es válida.'
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}

function Read-GuardianSignerIdentity {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $thumbprint = ((Get-Content -LiteralPath $Path -Raw -ErrorAction Stop) -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
    if ($thumbprint.Length -eq 40) { return $thumbprint }
    throw 'La identidad de firma instalada no es válida.'
}

function Set-GuardianSignerIdentity {
    param([string]$PreviousIdentity, [string]$GuardianPath, [string]$GatePath, [string]$IdentityPath)
    $guardianSignature = Get-AuthenticodeSignature -LiteralPath $GuardianPath
    $gateSignature = Get-AuthenticodeSignature -LiteralPath $GatePath
    $guardianIdentity = (([string]$guardianSignature.SignerCertificate.Thumbprint) -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
    $gateIdentity = (([string]$gateSignature.SignerCertificate.Thumbprint) -replace '[^0-9a-fA-F]', '').ToUpperInvariant()
    $signedPair = $guardianSignature.Status -eq 'Valid' -and $gateSignature.Status -eq 'Valid' -and
        $guardianIdentity.Length -eq 40 -and $guardianIdentity -eq $gateIdentity

    if (-not $signedPair) {
        throw 'Guardian y Gate deben tener una firma Authenticode válida de la misma identidad.'
    }
    if (-not [string]::IsNullOrWhiteSpace($PreviousIdentity)) {
        if ($guardianIdentity -ne $PreviousIdentity) { throw 'La actualización está firmada por una identidad distinta a la instalada.' }
    }
    Set-Content -LiteralPath $IdentityPath -Value $guardianIdentity -Encoding ASCII
    return $guardianIdentity
}

function New-GuardianIntegrityBaseline {
    param([string]$GuardianPath, [string]$GatePath, [string]$StateFolder, [string]$SignerIdentityPath)
    if (-not (Test-Path -LiteralPath $GuardianPath -PathType Leaf)) { throw 'Falta el binario Guardian para crear la línea base.' }
    if (-not (Test-Path -LiteralPath $GatePath -PathType Leaf)) { throw 'Falta el binario Gate para crear la línea base.' }
    $signerIdentity = Read-GuardianSignerIdentity $SignerIdentityPath
    if ([string]::IsNullOrWhiteSpace($signerIdentity)) {
        throw 'No se puede crear la línea base sin una identidad de firma válida.'
    }

    $recoveryFolder = Join-Path $StateFolder 'Recovery'
    $manifestPath = Join-Path $StateFolder 'guardian-integrity.json'
    New-Item -ItemType Directory -Path $recoveryFolder -Force | Out-Null
    $files = @()
    foreach ($source in @(
        [pscustomobject]@{ Name = 'ProtectedApp.Guardian.exe'; Path = $GuardianPath },
        [pscustomobject]@{ Name = 'ProtectedApp.Gate.exe'; Path = $GatePath })) {
        $destination = Join-Path $recoveryFolder $source.Name
        $temporary = Join-Path $recoveryFolder ('.' + $source.Name + '.' + [Guid]::NewGuid().ToString('N') + '.tmp')
        try {
            Copy-Item -LiteralPath $source.Path -Destination $temporary -Force
            $sourceHash = (Get-FileHash -LiteralPath $source.Path -Algorithm SHA256).Hash
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $sourceHash) {
                throw "La copia de recuperación de $($source.Name) no superó la verificación."
            }
            Move-Item -LiteralPath $temporary -Destination $destination -Force
            $files += [ordered]@{ Name = $source.Name; Sha256 = $sourceHash }
        }
        finally {
            Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue
        }
    }
    $manifest = [ordered]@{
        Version = 1
        Files = $files
        SignerThumbprint = $signerIdentity
    }
    $manifestTemporary = $manifestPath + '.tmp-' + [Guid]::NewGuid().ToString('N')
    try {
        $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestTemporary -Encoding UTF8
        Move-Item -LiteralPath $manifestTemporary -Destination $manifestPath -Force
    }
    finally {
        Remove-Item -LiteralPath $manifestTemporary -Force -ErrorAction SilentlyContinue
    }
}
