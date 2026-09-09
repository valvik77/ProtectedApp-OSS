param(
    [Parameter(Mandatory = $true)]
    [string]$ShellDirectory,
    [string]$KeepAssemblyPath = '',
    [switch]$RemoveAll
)

# Explorer may retain an older COM extension DLL after an update.  Never make
# an installation fail because of that: delete unused versions now and queue
# only locked, orphaned versions for deletion at the next Windows restart.
$ErrorActionPreference = 'SilentlyContinue'
$resolvedDirectory = [IO.Path]::GetFullPath($ShellDirectory)
if (-not (Test-Path -LiteralPath $resolvedDirectory -PathType Container)) { exit 0 }

$keepPath = if ([string]::IsNullOrWhiteSpace($KeepAssemblyPath)) { '' } else { [IO.Path]::GetFullPath($KeepAssemblyPath) }

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ProtectedAppPendingDelete
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool MoveFileEx(string existingFileName, string newFileName, int flags);
}
'@ -ErrorAction SilentlyContinue

# Older installers used the unversioned name. It must be treated exactly like
# the versioned files, otherwise it survives every upgrade indefinitely.
$extensionFiles = Get-ChildItem -LiteralPath $resolvedDirectory -File |
    Where-Object {
        $_.Name -eq 'ProtectedApp.ShellExtension.dll' -or
        $_.Name -match '^ProtectedApp\.ShellExtension\.\d+(\.\d+){2,3}\.dll$'
    }

$extensionFiles |
    ForEach-Object {
        $candidate = $_.FullName
        if (-not $RemoveAll -and $keepPath -and
            [string]::Equals($candidate, $keepPath, [StringComparison]::OrdinalIgnoreCase)) {
            return
        }

        Remove-Item -LiteralPath $candidate -Force -ErrorAction SilentlyContinue
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { return }

        # MOVEFILE_DELAY_UNTIL_REBOOT deletes a locked orphan without touching
        # the active extension or requiring Explorer to be closed.
        [ProtectedAppPendingDelete]::MoveFileEx($candidate, $null, 0x4) | Out-Null
    }

# PDB files are never loaded by Explorer and can be removed immediately. They
# were shipped by legacy builds next to the old extension DLL.
Get-ChildItem -LiteralPath $resolvedDirectory -File |
    Where-Object {
        $_.Name -eq 'ProtectedApp.ShellExtension.pdb' -or
        $_.Name -match '^ProtectedApp\.ShellExtension\.\d+(\.\d+){2,3}\.pdb$'
    } |
    Remove-Item -Force -ErrorAction SilentlyContinue
