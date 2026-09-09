param()
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot)
$license = (Get-Content LICENSE -Raw).Replace("`r`n", "`n").Trim()
if (-not $license.StartsWith('GNU GENERAL PUBLIC LICENSE') -or
    $license -match 'Set-PSReadLineOption|Controlador no válido') {
    throw 'LICENSE contains unexpected content.'
}
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($license)))
if ($digest -ne '3743F7A4AB5132F7DBEB88E68097657AB8374E74B46B6402EFBE80CCFB1B6488') {
    throw 'LICENSE differs from the verified GPL reference.'
}
$output = & dotnet list ProtectedApp.sln package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Dependency audit failed.' }
$report = ($output -join "`n") | ConvertFrom-Json
foreach ($project in $report.projects) {
    foreach ($framework in $project.frameworks) {
        foreach ($package in @($framework.topLevelPackages) + @($framework.transitivePackages)) {
            if ($package.vulnerabilities.Count -gt 0) {
                throw "Vulnerable dependency: $($package.id) $($package.resolvedVersion)"
            }
        }
    }
}
Write-Host 'License and dependency checks passed.'
