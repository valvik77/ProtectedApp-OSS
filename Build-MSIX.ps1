param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version = '1.4.53',
    [string]$SigningCertificateThumbprint = '',
    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$SigningCertificateStoreLocation = 'CurrentUser',
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowDevelopmentCertificate,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$artifactsFolder = Join-Path $projectRoot 'artifacts'
$publishFolder = Join-Path $artifactsFolder 'publish-x64'
$layoutFolder = Join-Path $artifactsFolder 'msix-layout'
$msixOutputDir = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { Join-Path $artifactsFolder 'msix' } else { $OutputDirectory }
$makeAppxPath = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 | Select-Object -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($makeAppxPath)) {
    throw 'No se encontró makeappx.exe. Instala el Windows SDK con las herramientas de empaquetado MSIX.'
}

$signToolPath = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 | Select-Object -ExpandProperty FullName
if ([string]::IsNullOrWhiteSpace($signToolPath)) {
    throw 'No se encontró signtool.exe. Instala el Windows SDK con las herramientas de firma.'
}

if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    if (-not [string]::IsNullOrWhiteSpace($env:PROTECTEDAPP_SIGNING_THUMBPRINT)) {
        $SigningCertificateThumbprint = $env:PROTECTEDAPP_SIGNING_THUMBPRINT
    }
    else {
        $autoValvikCertificate = Get-ChildItem "Cert:\$SigningCertificateStoreLocation\My" -ErrorAction SilentlyContinue |
            Where-Object { $_.HasPrivateKey -and $_.Subject -eq 'CN=Valvik' } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1

        if ($autoValvikCertificate) {
            $SigningCertificateThumbprint = $autoValvikCertificate.Thumbprint
            Write-Host "Certificado interno Valvik detectado automáticamente: $SigningCertificateThumbprint"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($SigningCertificateThumbprint)) {
    throw 'No se ha encontrado un certificado interno para firmar el MSIX. Usa -SigningCertificateThumbprint o instala el certificado Valvik en el almacén local.'
}

$normalizedThumbprint = ($SigningCertificateThumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
$certificatePath = "Cert:\$SigningCertificateStoreLocation\My\$normalizedThumbprint"
$certificate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
if ($null -eq $certificate) { throw "No se encontró el certificado $normalizedThumbprint en $SigningCertificateStoreLocation\My." }
if (-not $certificate.HasPrivateKey) { throw 'El certificado seleccionado no tiene clave privada.' }
if ($certificate.Subject -eq $certificate.Issuer -and -not $AllowDevelopmentCertificate) {
    throw 'El certificado actual es autofirmado y se usa solo para entorno interno. Repite la llamada con -AllowDevelopmentCertificate para forzar esta firma.'
}

New-Item -ItemType Directory -Path $artifactsFolder -Force | Out-Null
New-Item -ItemType Directory -Path $msixOutputDir -Force | Out-Null

if (Test-Path -LiteralPath $publishFolder) {
    Write-Host 'Se reutiliza la publicación existente de la build x64.'
}
else {
    Write-Host 'Publicando ProtectApp para x64 antes del empaquetado MSIX...'
    dotnet publish (Join-Path $projectRoot 'ProtectedApp.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Platform=x64 `
        -p:PublishDir="$publishFolder\" `
        -p:GuardianServicePublishDir="$publishFolder\Service\"
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish no se completó correctamente.' }
}

$requiredRuntimeFiles = @('ProtectedApp.exe', 'App.xbf', 'MainWindow.xbf', 'UnlockWindow.xbf', 'NoticeWindow.xbf', 'ProtectedApp.pri')
foreach ($requiredFile in $requiredRuntimeFiles) {
    $requiredPath = Join-Path $publishFolder $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "La publicación WinUI está incompleta. Falta: $requiredFile"
    }
}

if (Test-Path -LiteralPath $layoutFolder) {
    Remove-Item -LiteralPath $layoutFolder -Recurse -Force
}
New-Item -ItemType Directory -Path $layoutFolder -Force | Out-Null

Write-Host 'Copiando la salida publicada a la carpeta de layout MSIX...'
Copy-Item -Path (Join-Path $publishFolder '*') -Destination $layoutFolder -Recurse -Force

$assetsFolder = Join-Path $layoutFolder 'Assets'
New-Item -ItemType Directory -Path $assetsFolder -Force | Out-Null

$sourceBrandLogo = Join-Path $projectRoot 'Assets\BrandShield.png'
if (-not (Test-Path -LiteralPath $sourceBrandLogo -PathType Leaf)) {
    throw 'No se encontró el recurso BrandShield.png para la generación del paquete MSIX.'
}

$brandLogoBytes = [System.IO.File]::ReadAllBytes($sourceBrandLogo)
[System.IO.File]::WriteAllBytes((Join-Path $assetsFolder 'StoreLogo.png'), $brandLogoBytes)
[System.IO.File]::WriteAllBytes((Join-Path $assetsFolder 'Square150x150Logo.png'), $brandLogoBytes)
[System.IO.File]::WriteAllBytes((Join-Path $assetsFolder 'Square44x44Logo.png'), $brandLogoBytes)

$manifestVersion = $Version.Split('.')
if ($manifestVersion.Count -lt 4) {
    while ($manifestVersion.Count -lt 4) { $manifestVersion += '0' }
}
$manifestVersion = $manifestVersion[0..3] -join '.'

$manifestXml = @"
<?xml version="1.0" encoding="utf-8"?>
<Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
         xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
         xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
         IgnorableNamespaces="uap rescap">
  <Identity Name="ProtectedApp" Publisher="CN=Valvik" Version="$manifestVersion" ProcessorArchitecture="x64" />
  <Properties>
    <DisplayName>ProtectedApp</DisplayName>
    <PublisherDisplayName>Valvik</PublisherDisplayName>
    <Logo>Assets\StoreLogo.png</Logo>
    <Description>ProtectedApp</Description>
  </Properties>
  <Resources>
    <Resource Language="en-us" />
  </Resources>
  <Dependencies>
    <TargetDeviceFamily Name="Windows.Desktop" MinVersion="10.0.17763.0" MaxVersionTested="10.0.26100.0" />
  </Dependencies>
  <Capabilities>
    <rescap:Capability Name="runFullTrust" />
  </Capabilities>
  <Applications>
    <Application Id="App" Executable="ProtectedApp.exe" EntryPoint="Windows.FullTrustApplication">
      <uap:VisualElements
        DisplayName="ProtectedApp"
        Description="ProtectedApp"
        BackgroundColor="transparent"
        Square150x150Logo="Assets\Square150x150Logo.png"
        Square44x44Logo="Assets\Square44x44Logo.png" />
    </Application>
  </Applications>
</Package>
"@

$manifestPath = Join-Path $layoutFolder 'AppxManifest.xml'
[System.IO.File]::WriteAllText($manifestPath, $manifestXml, [System.Text.UTF8Encoding]::new($false))

$msixFileName = 'ProtectedApp-Interna-x64.msix'
$msixPath = Join-Path $msixOutputDir $msixFileName
if (Test-Path -LiteralPath $msixPath) { Remove-Item -LiteralPath $msixPath -Force }

Write-Host "Empaquetando MSIX: $msixPath"
& $makeAppxPath pack /d $layoutFolder /p $msixPath /o /nc
if ($LASTEXITCODE -ne 0) { throw 'makeappx no pudo crear el paquete MSIX.' }

Write-Host 'Firmando el paquete MSIX con el certificado interno actual...'
& $signToolPath sign /v /fd SHA256 /tr $TimestampUrl /td SHA256 /sha1 $normalizedThumbprint /sm $msixPath
if ($LASTEXITCODE -ne 0) { throw 'signtool no pudo firmar el MSIX.' }

Write-Host 'Verificando la firma del MSIX...'
& $signToolPath verify /pa /all /tw /v $msixPath
if ($LASTEXITCODE -ne 0) { throw 'La firma del paquete MSIX no ha sido verificada correctamente.' }

Write-Host ''
Write-Host "Paquete MSIX generado: $msixPath" -ForegroundColor Green
Write-Host "Certificado firmado: $($certificate.Subject) [$normalizedThumbprint]" -ForegroundColor Green
