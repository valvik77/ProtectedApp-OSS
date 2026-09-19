param([Parameter(Mandatory)][string]$Version,[Parameter(Mandatory)][string]$Commit,[Parameter(Mandatory)][string]$InstallerSha256)
$template = Get-Content (Join-Path $PSScriptRoot '..\DEVELOPMENT-RELEASE-TEMPLATE.md') -Raw
$template.Replace('{VERSION}',$Version).Replace('{COMMIT}',$Commit).Replace('{INSTALLER_SHA256}',$InstallerSha256)
