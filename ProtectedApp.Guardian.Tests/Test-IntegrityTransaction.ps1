$ErrorActionPreference = 'Stop'
foreach ($script in Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..\ProtectedApp.Service') -Filter '*.ps1') {
    $tokens = $null
    $parseErrors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
}
. (Join-Path $PSScriptRoot '..\ProtectedApp.Service\Integrity-Transaction.ps1')
# Exercise file transactions without elevation; production ACL setup is not mocked in the installer.
function Set-Acl { param($LiteralPath, $AclObject) }
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('ProtectedApp-transaction-' + [Guid]::NewGuid().ToString('N'))
try {
    # Inject failures after each of the eight files could have changed.
    foreach ($stage in 1..8) {
        $state = Join-Path $testRoot "stage-$stage"
        $transaction = Join-Path $state 'Service\.update-test'
        New-Item -ItemType Directory -Path (Join-Path $state 'Service') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $state 'Policy') -Force | Out-Null
        New-Item -ItemType Directory -Path (Join-Path $state 'Recovery') -Force | Out-Null
        foreach ($file in @('guardian-integrity.json', 'Policy\guardian-signer.thumbprint',
            'Recovery\ProtectedApp.Guardian.exe', 'Recovery\ProtectedApp.Gate.exe',
            'guardian-protection.version', 'Service\ProtectedApp.Guardian.exe', 'ProtectedApp.Gate.exe')) {
            [IO.File]::WriteAllText((Join-Path $state $file), "previous-$file")
        }
        $entries = @(Save-GuardianTransaction $state $transaction)
        for ($index = 0; $index -lt $stage; $index++) {
            [IO.File]::WriteAllText($entries[$index].Target, 'partial-new-installation')
        }
        Restore-GuardianTransaction $entries
        foreach ($entry in $entries) {
            if ($entry.Exists) {
                if ((Get-FileHash -LiteralPath $entry.Target).Hash -ne $entry.Hash) { throw "Rollback incompleto en etapa $stage" }
            }
            elseif (Test-Path -LiteralPath $entry.Target) { throw 'El rollback creó confianza que no existía antes.' }
        }
        Remove-GuardianTransaction $state $transaction
    }
    # A first installation failure must leave no baseline, identity or binaries.
    $state = Join-Path $testRoot 'fresh'
    $transaction = Join-Path $state 'Service\.update-test'
    $entries = @(Save-GuardianTransaction $state $transaction)
    foreach ($entry in $entries) {
        New-Item -ItemType Directory -Path (Split-Path $entry.Target -Parent) -Force | Out-Null
        [IO.File]::WriteAllText($entry.Target, 'new')
    }
    Restore-GuardianTransaction $entries
    if (@($entries | Where-Object { Test-Path -LiteralPath $_.Target }).Count) { throw 'Instalación fallida conserva archivos nuevos.' }
    Remove-GuardianTransaction $state $transaction

    # A damaged rollback copy must be retained and rejected before any restore.
    $state = Join-Path $testRoot 'damaged'
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $state 'guardian-integrity.json'), 'original')
    $transaction = Join-Path $state 'Service\.update-test'
    $entries = @(Save-GuardianTransaction $state $transaction)
    [IO.File]::WriteAllText($entries[0].Backup, 'tampered')
    $rejected = $false
    try { Restore-GuardianTransaction $entries } catch { $rejected = $true }
    if (-not $rejected -or -not (Test-Path -LiteralPath $transaction)) { throw 'Se aceptó o eliminó una copia alterada.' }
    if ([IO.File]::ReadAllText($entries[0].Target) -ne 'original') { throw 'Se modificó el destino antes de validar el respaldo.' }

    $state = Join-Path $testRoot 'baseline'
    $guardian = Join-Path $state 'Service\ProtectedApp.Guardian.exe'
    $gate = Join-Path $state 'ProtectedApp.Gate.exe'
    New-Item -ItemType Directory -Path (Split-Path $guardian -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($guardian, 'guardian-new')
    [IO.File]::WriteAllText($gate, 'gate-new')
    $identity = 'A' * 40
    $baselineIdentityPath = Join-Path $state 'Policy\guardian-signer.thumbprint'
    New-Item -ItemType Directory -Path (Split-Path $baselineIdentityPath -Parent) -Force | Out-Null
    [IO.File]::WriteAllText($baselineIdentityPath, $identity)
    New-GuardianIntegrityBaseline $guardian $gate $state $baselineIdentityPath
    $manifest = Get-Content -LiteralPath (Join-Path $state 'guardian-integrity.json') -Raw | ConvertFrom-Json
    if ($manifest.Version -ne 1 -or $manifest.Files.Count -ne 2) { throw 'La línea base autorizada no se creó correctamente.' }
    foreach ($file in $manifest.Files) {
        $recovery = Join-Path (Join-Path $state 'Recovery') $file.Name
        if ((Get-FileHash -LiteralPath $recovery).Hash -ne $file.Sha256) { throw 'La recuperación no coincide con el manifiesto.' }
    }
    $identityPath = Join-Path $state 'signer.thumbprint'
    [IO.File]::WriteAllText($identityPath, $identity)
    if ((Read-GuardianSignerIdentity $identityPath) -ne $identity) { throw 'No se pudo leer la identidad instalada.' }
    [IO.File]::WriteAllText($identityPath, 'invalid')
    $rejected = $false
    try { Read-GuardianSignerIdentity $identityPath | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Se acepto una identidad invalida.' }

    # Fake certificates test policy decisions without modifying trust stores.
    function Get-AuthenticodeSignature {
        param($LiteralPath)
        return $script:testSignature
    }
    $script:testSignature = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Thumbprint = $identity } }
    Set-GuardianSignerIdentity $identity $guardian $gate $identityPath | Out-Null
    if ((Read-GuardianSignerIdentity $identityPath) -ne $identity) { throw 'No se conservo el firmante.' }
    foreach ($signature in @(
        [pscustomobject]@{ Status = 'NotSigned'; SignerCertificate = $null },
        [pscustomobject]@{ Status = 'HashMismatch'; SignerCertificate = [pscustomobject]@{ Thumbprint = $identity } },
        [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Thumbprint = ('B' * 40) } })) {
        $script:testSignature = $signature
        $rejected = $false
        try { Set-GuardianSignerIdentity $identity $guardian $gate $identityPath | Out-Null } catch { $rejected = $true }
        if (-not $rejected -or (Read-GuardianSignerIdentity $identityPath) -ne $identity) {
            throw 'La comprobacion de firma no preservo la confianza anterior.'
        }
    }
    New-GuardianIntegrityBaseline $guardian $gate $state $identityPath
    $manifest = Get-Content -LiteralPath (Join-Path $state 'guardian-integrity.json') -Raw | ConvertFrom-Json
    if ($manifest.SignerThumbprint -ne $identity) { throw 'El manifiesto perdio el firmante esperado.' }
    Write-Output 'Correctos: sintaxis, 10 escenarios de rollback, linea base e identidad de firma.'
}
finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ((Split-Path $resolved -Parent) -ne [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') -or
        (Split-Path $resolved -Leaf) -notlike 'ProtectedApp-transaction-*') { throw 'Ruta temporal inesperada.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
