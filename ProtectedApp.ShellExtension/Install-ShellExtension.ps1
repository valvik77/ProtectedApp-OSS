param(
    [string]$AssemblyPath
)

# ProtectedApp no longer registers an in-process Explorer extension. Explorer
# replacements such as Directory Opus and tools that hook Explorer (Windhawk)
# can load icon overlays too, making a managed overlay an unacceptable source
# of instability. Context-menu actions are regular external commands and do
# not require a COM server in the shell process.
$ErrorActionPreference = 'SilentlyContinue'
$classId = '{6DCC2C90-2C9F-4C38-9075-7F2B1391C9B2}'
$overlayClassId = '{6BB2DA61-9C95-41A6-9F2D-35C3335D0F22}'
$vaultCommandClassId = '{42C771D7-2B8A-464C-97AB-BE23E892F0F9}'
$appId = '{B22C3BE7-3ADC-4D51-A1E8-C7F72188F6E7}'

Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\AppID\$appId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$classId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$overlayClassId" -Recurse -Force
Remove-Item -LiteralPath "Registry::HKEY_LOCAL_MACHINE\Software\Classes\CLSID\$vaultCommandClassId" -Recurse -Force
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' -Name $classId -Force
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Shell Extensions\Approved' -Name $overlayClassId -Force
Remove-Item -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Explorer\ShellIconOverlayIdentifiers\ProtectedApp' -Recurse -Force

# The former plain command represented NTFS folder locking. It must be
# removed as well, otherwise upgrades leave both contextual entries visible.
Remove-Item -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\Directory\shell\ProtectedApp.Unlock' -Recurse -Force
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\ProtectedApp.Vault\shell\ProtectedApp.Mount' -Name 'ExplorerCommandHandler' -Force
# Dokany PAVLT003 volumes are not exposed as normal WMI volumes. Explorer
# therefore cannot evaluate System.Volume.FileSystem reliably in AppliesTo,
# which hid the safe unmount command on the actual virtual drive.
Remove-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\Software\Classes\Drive\shell\ProtectedApp.UnmountVault' -Name 'AppliesTo' -Force

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
internal static class ProtectedAppShellRefresh
{
    [DllImport("shell32.dll")]
    internal static extern void SHChangeNotify(uint eventId, uint flags, IntPtr item1, IntPtr item2);
}
'@ -ErrorAction SilentlyContinue
[ProtectedAppShellRefresh]::SHChangeNotify(0x08000000, 0, [IntPtr]::Zero, [IntPtr]::Zero)
