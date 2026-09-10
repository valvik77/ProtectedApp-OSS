param(
    [Parameter(Mandatory)][string]$Version,
    [string]$AssetsPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'obj\project.assets.json'),
    [Parameter(Mandatory)][string]$OutputPath
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') {
    throw 'La versión debe tener el formato mayor.menor.revisión o mayor.menor.revisión.compilación.'
}
if (-not (Test-Path -LiteralPath $AssetsPath -PathType Leaf)) {
    throw "No se encontró el archivo de activos NuGet: $AssetsPath"
}

$assets = Get-Content -LiteralPath $AssetsPath -Raw | ConvertFrom-Json
$packages = @(
    $assets.libraries.PSObject.Properties |
        Where-Object { $_.Value.type -eq 'package' } |
        Sort-Object Name
)
if ($packages.Count -eq 0) {
    throw 'No se encontraron paquetes NuGet para incluir en el SBOM.'
}

$repositoryUrl = 'https://github.com/valvik77/ProtectedApp-OSS'
$commit = (& git -C (Split-Path -Parent $PSScriptRoot) rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[0-9a-f]{40}$') {
    throw 'No se pudo determinar el commit de origen para el SBOM.'
}

$spdxPackages = [Collections.Generic.List[object]]::new()
$spdxPackages.Add([ordered]@{
    SPDXID = 'SPDXRef-ProtectedApp'
    name = 'ProtectedApp'
    versionInfo = $Version
    downloadLocation = $repositoryUrl
    filesAnalyzed = $false
    licenseConcluded = 'GPL-3.0-only'
    licenseDeclared = 'GPL-3.0-only'
    supplier = 'Organization: ProtectedApp'
})

$relationships = [Collections.Generic.List[object]]::new()
$relationships.Add([ordered]@{
    spdxElementId = 'SPDXRef-DOCUMENT'
    relationshipType = 'DESCRIBES'
    relatedSpdxElement = 'SPDXRef-ProtectedApp'
})

$index = 0
foreach ($library in $packages) {
    $separator = $library.Name.LastIndexOf('/')
    if ($separator -le 0 -or $separator -eq $library.Name.Length - 1) {
        throw "Identificador de paquete NuGet inesperado: $($library.Name)"
    }

    $index++
    $packageName = $library.Name.Substring(0, $separator)
    $packageVersion = $library.Name.Substring($separator + 1)
    $packageId = "SPDXRef-NuGet-$index"
    $spdxPackages.Add([ordered]@{
        SPDXID = $packageId
        name = $packageName
        versionInfo = $packageVersion
        downloadLocation = "https://www.nuget.org/packages/$packageName/$packageVersion"
        filesAnalyzed = $false
        licenseConcluded = 'NOASSERTION'
        licenseDeclared = 'NOASSERTION'
        supplier = 'NOASSERTION'
        externalRefs = @([ordered]@{
            referenceCategory = 'PACKAGE-MANAGER'
            referenceType = 'purl'
            referenceLocator = "pkg:nuget/$packageName@$packageVersion"
        })
    })
    $relationships.Add([ordered]@{
        spdxElementId = 'SPDXRef-ProtectedApp'
        relationshipType = 'DEPENDS_ON'
        relatedSpdxElement = $packageId
    })
}

$document = [ordered]@{
    spdxVersion = 'SPDX-2.3'
    dataLicense = 'CC0-1.0'
    SPDXID = 'SPDXRef-DOCUMENT'
    name = "ProtectedApp-$Version"
    documentNamespace = "$repositoryUrl/sbom/$commit/$Version"
    creationInfo = [ordered]@{
        created = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        creators = @('Tool: ProtectedApp Scripts/New-ReleaseSbom.ps1')
    }
    packages = $spdxPackages
    relationships = $relationships
}

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}
[IO.File]::WriteAllText($OutputPath, ($document | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
Write-Host "SBOM SPDX creado: $OutputPath ($($packages.Count) paquetes NuGet)."
