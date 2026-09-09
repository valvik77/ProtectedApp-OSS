param(
    [Parameter(Mandatory = $true)]
    [string]$AssemblyPath
)

$ErrorActionPreference = 'SilentlyContinue'
$classId = '{6DCC2C90-2C9F-4C38-9075-7F2B1391C9B2}'
$overlayClassId = '{6BB2DA61-9C95-41A6-9F2D-35C3335D0F22}'
$vaultCommandClassId = '{42C771D7-2B8A-464C-97AB-BE23E892F0F9}'
$appId = '{B22C3BE7-3ADC-4D51-A1E8-C7F72188F6E7}'
$regAsm = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)) `
    'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
$resolvedAssembly = [IO.Path]::GetFullPath($AssemblyPath)

function Stop-ProtectedAppShellHost {
    param([string]$ResolvedAssembly, [string]$AppId)

    $normalizedAppId = $AppId.Trim('{}')
    $processIds = [Collections.Generic.HashSet[int]]::new()
    Get-CimInstance Win32_Process -Filter "Name='dllhost.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($normalizedAppId, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { $null = $processIds.Add([int]$_.ProcessId) }
    foreach ($process in Get-Process -Name 'dllhost' -ErrorAction SilentlyContinue) {
        try {
            if ($process.Modules | Where-Object {
                    $_.FileName -and [string]::Equals([IO.Path]::GetFullPath($_.FileName), $ResolvedAssembly,
                        [StringComparison]::OrdinalIgnoreCase)
                }) { $null = $processIds.Add($process.Id) }
        }
        catch { }
    }
    foreach ($processId in $processIds) {
        Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
    }
}

if (Test-Path -LiteralPath $regAsm -PathType Leaf) {
    & $regAsm $resolvedAssembly /nologo /unregister | Out-Null
}
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\AppID\$appId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$classId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$overlayClassId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$vaultCommandClassId" -Recurse -Force
Remove-Item -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\Directory\shell\ProtectedApp.Unlock' -Recurse -Force
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' -Name $classId -Force
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' -Name $overlayClassId -Force
Remove-Item -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\ProtectedApp' -Recurse -Force
Stop-ProtectedAppShellHost -ResolvedAssembly $resolvedAssembly -AppId $appId
