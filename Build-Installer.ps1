param(
    # Keep the Explorer extension DLL versioned.  Explorer can retain the
    # previous DLL in memory, so replacing a file with the same version would
    # make an otherwise valid upgrade fail with "Access denied".
    [Alias('Version')]
    [string]$InstallerVersion = '',
    [string]$SigningCertificateThumbprint = '',
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$SigningCertificateStoreLocation = 'CurrentUser',
    [string]$ArtifactSigningDlibPath = $env:PROTECTEDAPP_ARTIFACT_SIGNING_DLIB,
    [string]$ArtifactSigningMetadataPath = $env:PROTECTEDAPP_ARTIFACT_SIGNING_METADATA,
    [string]$TimestampUrl = $env:PROTECTEDAPP_TIMESTAMP_URL,
    [string]$PublishDirectoryName = '',
    [switch]$RequireSignature,
    [switch]$AllowUnsignedDevelopmentBuild,
    [switch]$AllowDevelopmentCertificate
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
# Inno Setup runs Windows PowerShell, even when this build runs under pwsh.
$installerPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
& $installerPowerShell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $projectRoot 'ProtectedApp.Guardian.Tests\Test-IntegrityTransaction.ps1')
if ($LASTEXITCODE -ne 0) { throw 'Los scripts de Guardian no superaron las pruebas en Windows PowerShell 5.1.' }
$artifactsFolder = Join-Path $projectRoot 'artifacts'
$installerFolder = Join-Path $artifactsFolder 'installer'
$toolsFolder = Join-Path $projectRoot '.tools'
$innoFolder = Join-Path $toolsFolder 'Inno7'
$iscc = Join-Path $innoFolder 'ISCC.exe'
$signingScript = Join-Path $projectRoot 'Signing\CodeSigning.ps1'
. $signingScript

$dokanyVersion = '2.3.1.1000'
$dokanyExpectedSha256 = '69FF8CB37BFEC3A75921C85FFD1C6370B50A9EC4ECEF2CF3A009D488DCBF5465'
$dokanyFolder = Join-Path $toolsFolder 'Dokany'
$dokanyMsi = Join-Path $dokanyFolder "Dokan_x64_$dokanyVersion.msi"
$dokanyUrl = "https://github.com/dokan-dev/dokany/releases/download/v$dokanyVersion/Dokan_x64.msi"

function Invoke-ResilientWebRequest
{
    param(
        [Parameter(Mandatory)][string]$Uri,
        [string]$OutFile,
        [ValidateRange(1, 10)][int]$MaximumAttempts = 4
    )

    $lastError = $null
    for ($attempt = 1; $attempt -le $MaximumAttempts; $attempt++) {
        $temporaryPath = if ($OutFile) { "$OutFile.download.$PID.$attempt" } else { $null }
        try {
            if ($OutFile) {
                Invoke-WebRequest -Uri $Uri -OutFile $temporaryPath -UseBasicParsing -TimeoutSec 90
                Move-Item -LiteralPath $temporaryPath -Destination $OutFile -Force
                return
            }

            return (Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 90).Content
        }
        catch {
            $lastError = $_
            if ($temporaryPath -and (Test-Path -LiteralPath $temporaryPath)) {
                Remove-Item -LiteralPath $temporaryPath -Force
            }

            if ($attempt -lt $MaximumAttempts) {
                $delaySeconds = [Math]::Min(30, 2 * [Math]::Pow(2, $attempt - 1))
                Write-Warning "No se pudo descargar $Uri (intento $attempt de $MaximumAttempts). Reintentando en $delaySeconds segundos..."
                Start-Sleep -Seconds $delaySeconds
            }
        }
    }

    throw "No se pudo descargar $Uri después de $MaximumAttempts intentos. $($lastError.Exception.Message)"
}

function Save-VerifiedLegalText
{
    param(
        [Parameter(Mandatory)][string]$Uri,
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        $content = Invoke-ResilientWebRequest -Uri $Uri
        $parent = Split-Path -Parent $Path
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        [IO.File]::WriteAllText($Path, $content, [Text.UTF8Encoding]::new($false))
    }
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedSha256) {
        throw "El texto legal descargado no coincide con la versión verificada: $Path"
    }
}

function Copy-DistributionLegalFiles
{
    param([Parameter(Mandatory)][string]$Destination)

    $sourceLegal = Join-Path $projectRoot 'Legal'
    $projectLicense = Join-Path $projectRoot 'LICENSE'
    if (-not (Test-Path -LiteralPath $projectLicense -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $sourceLegal 'THIRD-PARTY-NOTICES.txt') -PathType Leaf)) {
        throw 'Faltan la licencia GPL o los avisos de terceros requeridos para distribuir ProtectedApp.'
    }
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $normalizedProjectLicense = ([IO.File]::ReadAllText($projectLicense)).Replace("`r`n", "`n").Trim()
    $projectLicenseDigest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalizedProjectLicense)))
    if ($projectLicenseDigest -ne '3743F7A4AB5132F7DBEB88E68097657AB8374E74B46B6402EFBE80CCFB1B6488') {
        throw 'La licencia GPL incluida no coincide con el texto verificado.'
    }
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $Destination 'LICENSE') -Force
    Copy-Item -LiteralPath (Join-Path $sourceLegal 'THIRD-PARTY-NOTICES.txt') -Destination $Destination -Force

    $dokanyLicenses = Join-Path $dokanyFolder 'Licenses'
    Save-VerifiedLegalText -Uri 'https://raw.githubusercontent.com/dokan-dev/dokany/v2.3.1.1000/license.lgpl.txt' `
        -Path (Join-Path $dokanyLicenses 'LGPL-3.0.txt') `
        -ExpectedSha256 'A853C2FFEC17057872340EEE242AE4D96CBF2B520AE27D903E1B2FEF1A5F9D1C'
    # Dokany is GPL-3.0-or-later. Reuse the GPL-3.0 text already reviewed and
    # shipped at the project root so an installer build never depends on GNU's
    # website being reachable.
    Copy-Item -LiteralPath $projectLicense -Destination (Join-Path $dokanyLicenses 'GPL-3.0.txt') -Force
    Save-VerifiedLegalText -Uri 'https://raw.githubusercontent.com/dokan-dev/dokany/v2.3.1.1000/license.mit.txt' `
        -Path (Join-Path $dokanyLicenses 'MIT.txt') `
        -ExpectedSha256 '7C7007FC460A096242DF72182DA1158536E62B170DEA738D338BB6EBB394B27C'
    Save-VerifiedLegalText -Uri 'https://raw.githubusercontent.com/dokan-dev/dokan-dotnet/e67b7afd6fa3d97ed38c88393d496ab3b1356021/license.txt' `
        -Path (Join-Path $dokanyLicenses 'DokanNet-MIT.txt') `
        -ExpectedSha256 '2481E64DA7CAAE6558A6B99C70575C8F32DD342C556052B4D9CE995BE530649A'

    $dokanyDestination = Join-Path $Destination 'Licenses\Dokany'
    $dokanNetDestination = Join-Path $Destination 'Licenses\DokanNet'
    New-Item -ItemType Directory -Path $dokanyDestination, $dokanNetDestination -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $dokanyLicenses 'LGPL-3.0.txt') -Destination $dokanyDestination -Force
    Copy-Item -LiteralPath (Join-Path $dokanyLicenses 'GPL-3.0.txt') -Destination $dokanyDestination -Force
    Copy-Item -LiteralPath (Join-Path $dokanyLicenses 'MIT.txt') -Destination $dokanyDestination -Force
    Copy-Item -LiteralPath (Join-Path $dokanyLicenses 'DokanNet-MIT.txt') -Destination $dokanNetDestination -Force

    $assetsPath = Join-Path $projectRoot 'obj\project.assets.json'
    if (-not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw 'No se encontró project.assets.json para recopilar las licencias NuGet.'
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $nugetOutput = & dotnet nuget locals global-packages --list
    if ($LASTEXITCODE -ne 0 -or ($nugetOutput -join "`n") -notmatch '(?im)^global-packages:\s*(.+)$') {
        throw 'No se pudo determinar la carpeta global de paquetes NuGet.'
    }
    $globalPackages = $Matches[1].Trim()
    $nugetDestination = Join-Path $Destination 'Licenses\NuGet'
    New-Item -ItemType Directory -Path $nugetDestination -Force | Out-Null
    $copiedLegalFiles = 0
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package' -or $library.Name -notmatch '^(.+)/([^/]+)$') { continue }
        $packageId = $Matches[1]
        $packageVersion = $Matches[2]
        $packageFolder = Join-Path (Join-Path $globalPackages $packageId.ToLowerInvariant()) $packageVersion.ToLowerInvariant()
        if (-not (Test-Path -LiteralPath $packageFolder -PathType Container)) { continue }
        $legalFiles = Get-ChildItem -LiteralPath $packageFolder -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '^(license|notice|third[-_. ]?party)' }
        if (-not $legalFiles) { continue }
        $safePackageName = ($packageId + '-' + $packageVersion) -replace '[^A-Za-z0-9._-]', '_'
        $packageDestination = Join-Path $nugetDestination $safePackageName
        New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
        foreach ($legalFile in $legalFiles) {
            if ((Get-Content -LiteralPath $legalFile.FullName -Raw -ErrorAction SilentlyContinue) -match '(?i)WINDOWS APP SDK ENGINEERING PREVIEW') {
                throw "El paquete $packageId $packageVersion contiene términos Engineering Preview incompatibles con la distribución oficial."
            }
            Copy-Item -LiteralPath $legalFile.FullName -Destination $packageDestination -Force
            $copiedLegalFiles++
        }
    }

    # Self-contained publishing adds the .NET runtime pack outside the normal
    # project.assets.json library list. Resolve its exact version from the
    # generated deps file so its license and third-party notices are retained.
    $depsPath = Join-Path $publishFolder 'ProtectedApp.deps.json'
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw 'No se encontró ProtectedApp.deps.json para identificar el runtime .NET distribuido.'
    }
    $deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $runtimePack = $null
    foreach ($target in $deps.targets.PSObject.Properties) {
        $runtimePack = $target.Value.PSObject.Properties.Name |
            Where-Object { $_ -match '^runtimepack\.Microsoft\.NETCore\.App\.Runtime\.win-x64/([^/]+)$' } |
            Select-Object -First 1
        if ($runtimePack) { break }
    }
    if (-not $runtimePack -or $runtimePack -notmatch '^runtimepack\.(.+)/([^/]+)$') {
        throw 'No se pudo identificar la versión del runtime .NET autocontenido.'
    }
    $runtimePackageId = $Matches[1]
    $runtimePackageVersion = $Matches[2]
    $runtimePackageFolder = Join-Path (Join-Path $globalPackages $runtimePackageId.ToLowerInvariant()) $runtimePackageVersion.ToLowerInvariant()
    $runtimeLegalFiles = Get-ChildItem -LiteralPath $runtimePackageFolder -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^(license|notice|third[-_. ]?party)' }
    if (-not $runtimeLegalFiles) {
        throw "No se encontraron los avisos del runtime .NET $runtimePackageVersion."
    }
    $runtimeDestination = Join-Path $nugetDestination "$runtimePackageId-$runtimePackageVersion"
    New-Item -ItemType Directory -Path $runtimeDestination -Force | Out-Null
    Copy-Item -LiteralPath $runtimeLegalFiles.FullName -Destination $runtimeDestination -Force
    $copiedLegalFiles += $runtimeLegalFiles.Count

    if ($copiedLegalFiles -eq 0) {
        throw 'No se copió ningún aviso legal desde los paquetes NuGet; se cancela la distribución.'
    }
}

function Get-NextInstallerVersion
{
    # Version 1.4.57 is the last fixed version. Every unattended local build
    # advances the third component, so Explorer never has to replace a DLL it
    # may still have loaded from a previous installation.
    $highestBuild = 57
    $knownLocations = @(
        (Join-Path $env:ProgramFiles 'ProtectedApp\Shell'),
        $installerFolder
    )
    foreach ($location in $knownLocations)
    {
        if (-not (Test-Path -LiteralPath $location -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $location -File -ErrorAction SilentlyContinue)
        {
            $candidate = if ($file.Name -match '^ProtectedApp\.ShellExtension\.(\d+\.\d+\.\d+(?:\.\d+)?)\.dll$')
            {
                $Matches[1]
            }
            elseif ($file.Name -match '^ProtectedApp-Setup-x64(?:-\d+\.\d+\.\d+(?:\.\d+)?)?\.exe$')
            {
                $file.VersionInfo.FileVersion.Trim()
            }
            else { $null }
            if ($candidate -match '^1\.4\.(\d+)(?:\.\d+)?$')
            {
                $highestBuild = [Math]::Max($highestBuild, [int]$Matches[1])
            }
        }
    }
    return "1.4.$($highestBuild + 1)"
}

if ([string]::IsNullOrWhiteSpace($InstallerVersion))
{
    $InstallerVersion = Get-NextInstallerVersion
    Write-Host "Versión de instalador asignada automáticamente: $InstallerVersion"
}
elseif ($InstallerVersion -notmatch '^\d+\.\d+\.\d+(\.\d+)?$')
{
    throw 'La versión del instalador debe tener el formato mayor.menor.revisión o mayor.menor.revisión.compilación.'
}

if ([string]::IsNullOrWhiteSpace($PublishDirectoryName))
{
    # A running probe or a manually started development build can retain CLR
    # files in its publish folder. Use a fresh folder for every build rather
    # than deleting files that another ProtectedApp process may have loaded.
    $PublishDirectoryName = "publish-x64-$InstallerVersion-$([Guid]::NewGuid().ToString('N'))"
}
elseif ($PublishDirectoryName -notmatch '^[A-Za-z0-9._-]+$')
{
    throw 'El nombre de la carpeta de publicación contiene caracteres no permitidos.'
}
$publishFolder = Join-Path $artifactsFolder $PublishDirectoryName

New-Item -ItemType Directory -Path $dokanyFolder -Force | Out-Null
if (-not (Test-Path -LiteralPath $dokanyMsi -PathType Leaf)) {
    Write-Host "Descargando Dokany $dokanyVersion desde la publicación oficial..."
    Invoke-ResilientWebRequest -Uri $dokanyUrl -OutFile $dokanyMsi
}

$dokanyHash = (Get-FileHash -LiteralPath $dokanyMsi -Algorithm SHA256).Hash
if ($dokanyHash -ne $dokanyExpectedSha256) {
    throw "El SHA-256 del runtime Dokany no coincide con la publicación oficial. Esperado: $dokanyExpectedSha256; obtenido: $dokanyHash"
}
$dokanySignature = Get-AuthenticodeSignature -LiteralPath $dokanyMsi
if ($dokanySignature.Status -ne 'Valid' -or $dokanySignature.SignerCertificate.Subject -notlike 'CN=LEOSAC,*') {
    throw 'La firma Authenticode del runtime Dokany no es válida o no pertenece a LEOSAC.'
}
Write-Host "Runtime Dokany verificado: $dokanyVersion [$dokanyHash]"

if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    if (-not [string]::IsNullOrWhiteSpace($env:PROTECTEDAPP_SIGNING_THUMBPRINT)) {
        $SigningCertificateThumbprint = $env:PROTECTEDAPP_SIGNING_THUMBPRINT
    }
    else {
        $autoValvikCertificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
            Where-Object {
                $_.HasPrivateKey -and
                $_.Subject -eq 'CN=Valvik ProtectedApp Development'
            } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        # Compatibilidad con el certificado de desarrollo anterior.
        if (-not $autoValvikCertificate) {
            $autoValvikCertificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
                Where-Object {
                    $_.HasPrivateKey -and
                    $_.Subject -eq 'CN=Valvik'
                } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1
        }

        if ($autoValvikCertificate) {
            $SigningCertificateThumbprint = $autoValvikCertificate.Thumbprint
            Write-Host "Certificado Valvik detectado automáticamente: $SigningCertificateThumbprint"
        }
    }
}

$usesArtifactSigning = -not [string]::IsNullOrWhiteSpace($ArtifactSigningDlibPath) -or
    -not [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath)
$usesCertificateStore = -not [string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)
if ($usesArtifactSigning -and $usesCertificateStore) {
    throw 'Selecciona un único método: certificado del almacén o Microsoft Artifact Signing.'
}
if ($usesArtifactSigning -and
    ([string]::IsNullOrWhiteSpace($ArtifactSigningDlibPath) -or [string]::IsNullOrWhiteSpace($ArtifactSigningMetadataPath))) {
    throw 'Artifact Signing necesita tanto la DLL de integración como el archivo de metadatos.'
}
$signingMode = if ($usesArtifactSigning) { 'ArtifactSigning' } elseif ($usesCertificateStore) { 'CertificateStore' } else { $null }
if ($RequireSignature -and $AllowUnsignedDevelopmentBuild) {
    throw '-RequireSignature y -AllowUnsignedDevelopmentBuild son opciones incompatibles.'
}
if ($null -eq $signingMode -and -not $AllowUnsignedDevelopmentBuild) {
    throw 'No se configuró una identidad de firma. Indica un certificado o Microsoft Artifact Signing; usa -AllowUnsignedDevelopmentBuild únicamente para pruebas locales.'
}
$signTool = $null
$normalizedSigningThumbprint = $null
if ($null -ne $signingMode) {
    $signTool = Resolve-ProtectedAppSignTool
    if ($signingMode -eq 'CertificateStore') {
        $allowDevelopmentCertificate = $AllowDevelopmentCertificate
        if (-not $allowDevelopmentCertificate) {
            try {
                $candidate = Get-Item -LiteralPath "Cert:\$SigningCertificateStoreLocation\My\$($SigningCertificateThumbprint.Trim())" -ErrorAction Stop
                $allowDevelopmentCertificate = $candidate.Subject -eq $candidate.Issuer
            }
            catch { }
        }

        $certificate = Get-ProtectedAppSigningCertificate `
            -Thumbprint $SigningCertificateThumbprint `
            -StoreLocation $SigningCertificateStoreLocation `
            -AllowDevelopmentCertificate:$allowDevelopmentCertificate
        $normalizedSigningThumbprint = $certificate.Thumbprint
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { $TimestampUrl = 'http://timestamp.digicert.com' }
        if ($allowDevelopmentCertificate -and -not $AllowDevelopmentCertificate) {
            Write-Warning 'Se usó un certificado autofirmado para una build firmada en desarrollo local; no debe publicarse como versión de producción.'
        }
        Write-Host "Firma configurada: $($certificate.Subject) [$normalizedSigningThumbprint]"
    }
    else {
        $ArtifactSigningDlibPath = [System.IO.Path]::GetFullPath($ArtifactSigningDlibPath)
        $ArtifactSigningMetadataPath = [System.IO.Path]::GetFullPath($ArtifactSigningMetadataPath)
        if (-not (Test-Path -LiteralPath $ArtifactSigningDlibPath -PathType Leaf)) { throw "No existe la DLL de Artifact Signing: $ArtifactSigningDlibPath" }
        if (-not (Test-Path -LiteralPath $ArtifactSigningMetadataPath -PathType Leaf)) { throw "No existe el archivo de metadatos de Artifact Signing: $ArtifactSigningMetadataPath" }
        if ([string]::IsNullOrWhiteSpace($TimestampUrl)) { $TimestampUrl = 'http://timestamp.acs.microsoft.com' }
        Write-Host 'Firma configurada: Microsoft Artifact Signing'
    }
}
else {
    Write-Warning 'Compilación de desarrollo autorizada expresamente: el instalador se generará SIN FIRMA y no debe distribuirse.'
}

New-Item -ItemType Directory -Path $artifactsFolder -Force | Out-Null
New-Item -ItemType Directory -Path $installerFolder -Force | Out-Null

# Never publish over a previous WinUI output. Loose XBF files are consumed at
# runtime and mixing an old App.xbf with a new MainWindow.xbf makes the agent
# crash before it can create the tray icon or display an unlock request.
$artifactsFullPath = [System.IO.Path]::GetFullPath($artifactsFolder).TrimEnd([System.IO.Path]::DirectorySeparatorChar)
$publishFullPath = [System.IO.Path]::GetFullPath($publishFolder)
$expectedPrefix = $artifactsFullPath + [System.IO.Path]::DirectorySeparatorChar
if (-not $publishFullPath.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta de publicación queda fuera de artifacts: $publishFullPath"
}
if (Test-Path -LiteralPath $publishFullPath) {
    Remove-Item -LiteralPath $publishFullPath -Recurse -Force
}
New-Item -ItemType Directory -Path $publishFullPath -Force | Out-Null

Write-Host "Publicando ProtectedApp y Guardian (versión $InstallerVersion)..."
$assemblyVersionParts = $InstallerVersion.Split('.')
$assemblyVersion = if ($assemblyVersionParts.Count -eq 3) { "$InstallerVersion.0" } else { $InstallerVersion }
dotnet publish (Join-Path $projectRoot 'ProtectedApp.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Platform=x64 `
    -p:Version=$InstallerVersion `
    -p:AssemblyVersion=$assemblyVersion `
    -p:FileVersion=$assemblyVersion `
    -p:PublishDir="$publishFolder\" `
    -p:GuardianServicePublishDir="$publishFolder\Service\"
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish no se completó correctamente.' }

Write-Host 'Recopilando la licencia GPL y licencias de todos los paquetes distribuidos...'
Copy-DistributionLegalFiles -Destination (Join-Path $publishFolder 'Legal')

$testProjects = @(
    (Join-Path $projectRoot 'ProtectedApp.Guardian.Tests\ProtectedApp.Guardian.Tests.csproj'),
    (Join-Path $projectRoot 'ProtectedApp.Vault.Tests\ProtectedApp.Vault.Tests.csproj')
)
foreach ($testProject in $testProjects) {
    if (-not (Test-Path -LiteralPath $testProject)) {
        throw "No se encontró el proyecto de pruebas requerido: $testProject"
    }
    Write-Host "Ejecutando pruebas: $(Split-Path $testProject -Leaf)..."
    dotnet test $testProject -c Release -p:Platform=x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw "Las pruebas no se completaron correctamente: $testProject" }
}

function Test-ShellExtensionCleanup
{
    $cleanupScript = Join-Path $projectRoot 'ProtectedApp.ShellExtension\Cleanup-ShellExtensions.ps1'
    $testDirectory = Join-Path ([IO.Path]::GetTempPath()) ("ProtectedApp-ShellCleanup-" + [Guid]::NewGuid().ToString('N'))
    try {
        New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
        @(
            'ProtectedApp.ShellExtension.dll',
            'ProtectedApp.ShellExtension.1.4.111.dll',
            'ProtectedApp.ShellExtension.1.4.111.0.dll',
            'ProtectedApp.ShellExtension.pdb',
            'ProtectedApp.ShellExtension.1.4.111.pdb'
        ) | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $testDirectory $_), [byte[]](0)) }
        [IO.File]::WriteAllBytes((Join-Path $testDirectory 'Unrelated.dll'), [byte[]](0))

        & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $cleanupScript `
            -ShellDirectory $testDirectory -RemoveAll
        if ($LASTEXITCODE -ne 0) { throw 'La limpieza de extensiones devolvió un error.' }

        $remainingLegacyFiles = Get-ChildItem -LiteralPath $testDirectory -File |
            Where-Object { $_.Name -match '^ProtectedApp\.ShellExtension(?:\.\d+(\.\d+){2,3})?\.(dll|pdb)$' }
        if ($remainingLegacyFiles) {
            throw "La limpieza dejó archivos heredados: $($remainingLegacyFiles.Name -join ', ')"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $testDirectory 'Unrelated.dll'))) {
            throw 'La limpieza de extensiones eliminó un archivo ajeno.'
        }
    }
    finally {
        if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Recurse -Force }
    }
}

Write-Host 'Verificando limpieza de DLL heredadas del Explorador...'
Test-ShellExtensionCleanup

$shellPublishFolder = Join-Path $publishFolder 'Shell'
New-Item -ItemType Directory -Path $shellPublishFolder -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProtectedApp.ShellExtension\Install-ShellExtension.ps1') -Destination $shellPublishFolder -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProtectedApp.ShellExtension\Remove-ShellExtension.ps1') -Destination $shellPublishFolder -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'ProtectedApp.ShellExtension\Cleanup-ShellExtensions.ps1') -Destination $shellPublishFolder -Force

$requiredRuntimeFiles = @('ProtectedApp.exe', 'App.xbf', 'MainWindow.xbf', 'UnlockWindow.xbf', 'NoticeWindow.xbf', 'ProtectedApp.pri')
foreach ($requiredFile in $requiredRuntimeFiles) {
    $requiredPath = Join-Path $publishFolder $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "La publicación WinUI está incompleta. Falta: $requiredFile"
    }
}

Write-Host 'Verificando el arranque real de los recursos WinUI...'
$previousInstanceKey = $env:PROTECTEDAPP_INSTANCE_KEY
$env:PROTECTEDAPP_INSTANCE_KEY = "InstallerProbe_$PID"
try {
    $startupProbe = Start-Process `
        -FilePath (Join-Path $publishFolder 'ProtectedApp.exe') `
        -ArgumentList '--startup-probe' `
        -WorkingDirectory $publishFolder `
        -Wait `
        -PassThru
    if ($startupProbe.ExitCode -ne 0) {
        throw "La comprobación de arranque WinUI terminó con el código $($startupProbe.ExitCode)."
    }

    Write-Host 'Verificando copias programadas y recuperación de bóvedas...'
    $vaultBackupProbe = Start-Process `
        -FilePath (Join-Path $publishFolder 'ProtectedApp.exe') `
        -ArgumentList '--vault-backup-probe' `
        -WorkingDirectory $publishFolder `
        -Wait `
        -PassThru
    if ($vaultBackupProbe.ExitCode -ne 0) {
        throw "La comprobación de copias programadas terminó con el código $($vaultBackupProbe.ExitCode)."
    }

    Write-Host 'Verificando el arranque silencioso y los eventos de sesión...'
    $startupRegressionProbe = Start-Process `
        -FilePath (Join-Path $publishFolder 'ProtectedApp.exe') `
        -ArgumentList '--startup-regression-probe' `
        -WorkingDirectory $publishFolder `
        -Wait `
        -PassThru
    if ($startupRegressionProbe.ExitCode -ne 0) {
        throw "La comprobación de arranque y sesión terminó con el código $($startupRegressionProbe.ExitCode)."
    }
}
finally {
    if ($null -eq $previousInstanceKey) {
        Remove-Item Env:PROTECTEDAPP_INSTANCE_KEY -ErrorAction SilentlyContinue
    }
    else {
        $env:PROTECTEDAPP_INSTANCE_KEY = $previousInstanceKey
    }
}

if ($null -ne $signingMode) {
    Write-Host 'Firmando los binarios propios antes de empaquetarlos...'
    $firstPartyBinaries = @(
        (Join-Path $publishFolder 'ProtectedApp.exe'),
        (Join-Path $publishFolder 'ProtectedApp.dll'),
        (Join-Path $publishFolder 'Service\ProtectedApp.Guardian.exe'),
        (Join-Path $publishFolder 'Service\ProtectedApp.Gate.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf }

    if ($firstPartyBinaries.Count -eq 0) {
        throw 'No se encontró ningún binario propio para firmar en la publicación actual.'
    }

    Invoke-ProtectedAppCodeSigning `
        -Mode $signingMode `
        -SignToolPath $signTool `
        -Paths $firstPartyBinaries `
        -TimestampUrl $TimestampUrl `
        -CertificateThumbprint $normalizedSigningThumbprint `
        -CertificateStoreLocation $SigningCertificateStoreLocation `
        -ArtifactSigningDlibPath $ArtifactSigningDlibPath `
        -ArtifactSigningMetadataPath $ArtifactSigningMetadataPath
}

if (-not (Test-Path -LiteralPath $iscc)) {
    New-Item -ItemType Directory -Path $toolsFolder -Force | Out-Null
    $bootstrapper = Join-Path $toolsFolder 'innosetup-7.1.0-x64.exe'
    if (-not (Test-Path -LiteralPath $bootstrapper)) {
        Write-Host 'Descargando el compilador oficial Inno Setup 7.1.0 x64...'
        Invoke-ResilientWebRequest `
            -Uri 'https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe' `
            -OutFile $bootstrapper
    }

    $signature = Get-AuthenticodeSignature -LiteralPath $bootstrapper
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*Pyrsys B.V.*') {
        throw 'La firma digital del instalador de Inno Setup no es válida o no pertenece a Pyrsys B.V.'
    }

    Write-Host 'Preparando el compilador local de Inno Setup...'
    $compilerInstall = Start-Process -FilePath $bootstrapper -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$innoFolder`""
    )
    if ($compilerInstall.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $iscc)) {
        throw 'No se pudo preparar el compilador de Inno Setup.'
    }
}

$setupBaseName = "ProtectedApp-Setup-x64-$InstallerVersion"
Write-Host "Creando $setupBaseName.exe..."
& $iscc "/DMyAppVersion=$InstallerVersion" "/DMyOutputBaseFilename=$setupBaseName" "/DPublishDir=$publishFolder" "/DDokanMsi=$dokanyMsi" (Join-Path $projectRoot 'Installer\ProtectedApp.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup no pudo generar el instalador.' }

$setupPath = Join-Path $installerFolder "$setupBaseName.exe"
if (-not (Test-Path -LiteralPath $setupPath)) { throw 'No se encontró el instalador generado.' }
$generatedVersion = (Get-Item -LiteralPath $setupPath).VersionInfo.FileVersion.Trim()
$setupMetadata = (Get-Item -LiteralPath $setupPath).VersionInfo
if ($setupMetadata.ProductName.Trim() -ne 'ProtectedApp' -or
    $setupMetadata.FileDescription.Trim() -ne 'Instalador de ProtectedApp') {
    throw 'Los metadatos del instalador no coinciden con los exigidos por el actualizador.'
}
if ($generatedVersion -ne $InstallerVersion) {
    throw "El instalador se generó con la versión $generatedVersion en lugar de $InstallerVersion. Se cancela la entrega para no reutilizar una extensión del Explorador ya cargada."
}
if ($null -ne $signingMode) {
    Write-Host 'Firmando y verificando el instalador...'
    Invoke-ProtectedAppCodeSigning `
        -Mode $signingMode `
        -SignToolPath $signTool `
        -Paths @($setupPath) `
        -TimestampUrl $TimestampUrl `
        -CertificateThumbprint $normalizedSigningThumbprint `
        -CertificateStoreLocation $SigningCertificateStoreLocation `
        -ArtifactSigningDlibPath $ArtifactSigningDlibPath `
        -ArtifactSigningMetadataPath $ArtifactSigningMetadataPath
}
$stream = [System.IO.File]::OpenRead($setupPath)
try {
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try { $hash = [System.BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '') }
    finally { $sha256.Dispose() }
}
finally { $stream.Dispose() }
Write-Host ''
Write-Host "Instalador creado: $setupPath" -ForegroundColor Green
Write-Host "SHA-256: $hash"
if ($null -ne $signingMode) {
    $installerSignature = Get-AuthenticodeSignature -LiteralPath $setupPath
    Write-Host "Firma válida: $($installerSignature.SignerCertificate.Subject)" -ForegroundColor Green
    Write-Host "Huella: $($installerSignature.SignerCertificate.Thumbprint)"
    Write-Host "Sello de tiempo: $($installerSignature.TimeStamperCertificate.Subject)"
}
