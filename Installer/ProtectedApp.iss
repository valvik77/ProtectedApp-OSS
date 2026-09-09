#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish-x64"
#endif
#ifndef DokanMsi
  #define DokanMsi "..\.tools\Dokany\Dokan_x64_2.3.1.1000.msi"
#endif
#ifndef MyOutputBaseFilename
  #define MyOutputBaseFilename "ProtectedApp-Setup-x64"
#endif

#define MyAppName "ProtectedApp"
#define MyAppExeName "ProtectedApp.exe"
#define MyPublisher "ProtectedApp"

[Setup]
AppId={{5F36D8C4-0E13-4ED5-9A28-5881F65735A4}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyPublisher}
DefaultDirName={autopf}\ProtectedApp
DefaultGroupName=ProtectedApp
DisableProgramGroupPage=yes
PrivilegesRequired=admin
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename={#MyOutputBaseFilename}
SetupIconFile=..\Assets\ProtectedApp.ico
WizardImageFile=..\Assets\WizardImage.bmp
WizardSmallImageFile=..\Assets\WizardSmallImage.bmp
UninstallDisplayIcon={app}\Assets\ProtectedApp.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern dynamic
WizardSizePercent=115
CloseApplications=no
RestartApplications=no
ChangesEnvironment=no
ChangesAssociations=yes
DisableWelcomePage=no
AllowNoIcons=yes
UsePreviousAppDir=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoProductName={#MyAppName}
VersionInfoDescription=Instalador de ProtectedApp
VersionInfoCompany={#MyPublisher}
LicenseFile=..\LICENSE

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
english.CreateDesktopShortcut=Create a desktop shortcut
spanish.CreateDesktopShortcut=Crear un acceso directo en el escritorio
english.Shortcuts=Shortcuts:
spanish.Shortcuts=Accesos directos:
english.EncryptFolder=Encrypt folder as vault
spanish.EncryptFolder=Cifrar carpeta como bóveda
english.VaultFile=ProtectedApp encrypted vault
spanish.VaultFile=Bóveda cifrada de ProtectedApp
english.MountUnmountVault=Mount/unmount vault
spanish.MountUnmountVault=Montar/Desmontar bóveda
english.UnmountVault=Unmount ProtectedApp vault
spanish.UnmountVault=Desmontar bóveda de ProtectedApp
english.DisableLegacyShell=Disabling the legacy Explorer extension...
spanish.DisableLegacyShell=Desactivando la extensión heredada del Explorador...
english.CleanShell=Cleaning obsolete Explorer components...
spanish.CleanShell=Limpiando extensiones antiguas del Explorador...
english.RestartExplorer=Restart Windows Explorer
spanish.RestartExplorer=Restaurar el Explorador de Windows
english.StartProtection=Start protection in the background
spanish.StartProtection=Iniciar la protección en segundo plano
english.CloseProtectedApp=Closing ProtectedApp...
spanish.CloseProtectedApp=Cerrando ProtectedApp...

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopShortcut}"; GroupDescription: "{cm:Shortcuts}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "Shell\ProtectedApp.ShellExtension*.dll"
Source: "Prepare-Installation.ps1"; Flags: dontcopy
Source: "{#DokanMsi}"; DestName: "Dokan_x64.msi"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\ProtectedApp"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\ProtectedApp"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "ProtectedApp"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\Directory\shell\ProtectedApp.Encrypt"; ValueType: string; ValueName: ""; ValueData: "{cm:EncryptFolder}"; Flags: deletekey uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Directory\shell\ProtectedApp.Encrypt"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Assets\ProtectedApp.ico,0"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\ProtectedApp.Encrypt"; ValueType: string; ValueName: "MultiSelectModel"; ValueData: "Single"
Root: HKLM; Subkey: "Software\Classes\Directory\shell\ProtectedApp.Encrypt\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --folder-action ""%1"""
Root: HKLM; Subkey: "Software\Classes\.pavault\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\VaultFile.ico,0"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\.pavault"; ValueType: string; ValueName: ""; ValueData: "ProtectedApp.Vault"; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault"; ValueType: string; ValueName: ""; ValueData: "{cm:VaultFile}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Assets\VaultFile.ico,0"
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\shell"; ValueType: string; ValueName: ""; ValueData: "open"
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\shell\ProtectedApp.Mount"; ValueType: string; ValueName: ""; ValueData: "{cm:MountUnmountVault}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\shell\ProtectedApp.Mount"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Assets\ProtectedApp.ico,0"
Root: HKLM; Subkey: "Software\Classes\ProtectedApp.Vault\shell\ProtectedApp.Mount\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --vault-action ""%1"""
Root: HKLM; Subkey: "Software\Classes\Drive\shell\ProtectedApp.UnmountVault"; ValueType: string; ValueName: ""; ValueData: "{cm:UnmountVault}"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Drive\shell\ProtectedApp.UnmountVault"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\Assets\ProtectedApp.ico,0"
Root: HKLM; Subkey: "Software\Classes\Drive\shell\ProtectedApp.UnmountVault\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" --unmount-vault-drive ""%1"""

[Run]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Shell\Install-ShellExtension.ps1"""; StatusMsg: "{cm:DisableLegacyShell}"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Shell\Cleanup-ShellExtensions.ps1"" -ShellDirectory ""{app}\Shell"" -RemoveAll"; StatusMsg: "{cm:CleanShell}"; Flags: runhidden waituntilterminated
Filename: "{sys}\explorer.exe"; Description: "{cm:RestartExplorer}"; Flags: nowait postinstall skipifsilent runasoriginaluser; Check: RestartExplorerAfterInstall
Filename: "{app}\{#MyAppExeName}"; Parameters: "--background --install-language ""{language}"""; Description: "{cm:StartProtection}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Shell\Remove-ShellExtension.ps1"" -AssemblyPath ""{app}\Shell\ProtectedApp.ShellExtension.{#MyAppVersion}.dll"""; RunOnceId: "RemoveShellExtension"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Shell\Cleanup-ShellExtensions.ps1"" -ShellDirectory ""{app}\Shell"" -RemoveAll"; RunOnceId: "CleanupShellExtensions"; Flags: runhidden waituntilterminated
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\Service\Remove-Service.ps1"""; RunOnceId: "RemoveGuardian"; Flags: runhidden waituntilterminated
Filename: "{sys}\taskkill.exe"; Parameters: "/F /T /IM ProtectedApp.exe"; RunOnceId: "StopProtectedApp"; Flags: runhidden waituntilterminated; StatusMsg: "{cm:CloseProtectedApp}"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\ProtectedApp"

[Code]
var
  MaintenanceMayBeActive: Boolean;
  ExplorerRestartRequired: Boolean;
  InstallationCompleted: Boolean;

function IsDokanyRuntimeCompatible(): Boolean;
var
  VersionMS, VersionLS: Cardinal;
  Major, Minor, Revision, Build: Cardinal;
begin
  Result := False;
  if not GetVersionNumbers(ExpandConstant('{sys}\dokan2.dll'), VersionMS, VersionLS) then
    Exit;

  Major := VersionMS shr 16;
  Minor := VersionMS and $FFFF;
  Revision := VersionLS shr 16;
  Build := VersionLS and $FFFF;
  Result := (Major > 2) or
    ((Major = 2) and ((Minor > 3) or
      ((Minor = 3) and ((Revision > 1) or
        ((Revision = 1) and (Build >= 1000))))));
end;

function InstallDokanyRuntime(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if IsDokanyRuntimeCompatible() then
    Exit;

  ExtractTemporaryFile('Dokan_x64.msi');
  if not Exec(
    ExpandConstant('{sys}\msiexec.exe'),
    '/i "' + ExpandConstant('{tmp}\Dokan_x64.msi') + '" /qn /norestart',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'No se pudo iniciar la instalación del componente de unidades virtuales Dokany.';
    Exit;
  end;

  if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result := 'Dokany no se pudo instalar (código ' + IntToStr(ResultCode) + ').';
    Exit;
  end;

  if ResultCode = 3010 then
    NeedsRestart := True;

  if not IsDokanyRuntimeCompatible() then
    Result := 'Dokany terminó la instalación, pero Windows no expone todavía el runtime requerido. Reinicia el equipo y repite la instalación.';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  MaintenanceMayBeActive := True;
  ExtractTemporaryFile('Prepare-Installation.ps1');
  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{tmp}\Prepare-Installation.ps1') + '" -InstallPath "' +
      ExpandConstant('{app}') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'No se pudo preparar ProtectedApp para la instalación.';
  end
  else if (ResultCode <> 0) and (ResultCode <> 3010) then
  begin
    Result := 'No se pudo liberar la extensión del Explorador de ProtectedApp. ' +
      'Cierra las ventanas del Explorador y vuelve a intentarlo.';
  end;

  ExplorerRestartRequired := ResultCode = 3010;

  if Result = '' then
    Result := InstallDokanyRuntime(NeedsRestart);
end;

function RestartExplorerAfterInstall(): Boolean;
begin
  Result := ExplorerRestartRequired;
end;

procedure UpdateGuardianServiceOrFail;
var
  ResultCode: Integer;
begin
  if not Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      ExpandConstant('{app}\Service\Update-Installed-Service.ps1') + '" -AppPath "' +
      ExpandConstant('{app}\ProtectedApp.exe') + '"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    MaintenanceMayBeActive := False;
    RaiseException('No se pudo iniciar la actualización del servicio Guardian. La instalación se detuvo para no dejar la protección en un estado incierto.');
  end;

  if ResultCode <> 0 then
  begin
    MaintenanceMayBeActive := False;
    RaiseException('Guardian no se pudo actualizar (código ' + IntToStr(ResultCode) + '). ' +
      'Consulta el detalle en ' + ExpandConstant('{commonappdata}\ProtectedApp\guardian-update-error.log') + '. ' +
      'No se puede confirmar la restauración desde este mensaje.');
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    UpdateGuardianServiceOrFail
  else if CurStep = ssDone then
  begin
    InstallationCompleted := True;
    MaintenanceMayBeActive := False;
  end;
end;

procedure DeinitializeSetup();
var
  ResultCode: Integer;
  RecoveryScript: String;
begin
  if (not InstallationCompleted) and ExplorerRestartRequired then
    Exec(ExpandConstant('{sys}\explorer.exe'), '', '', SW_SHOWNORMAL, ewNoWait, ResultCode);

  if not MaintenanceMayBeActive then
    Exit;

  RecoveryScript := ExpandConstant('{app}\Service\Update-Installed-Service.ps1');
  if FileExists(RecoveryScript) then
    Exec(
      ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
      '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
        RecoveryScript + '" -AppPath "' + ExpandConstant('{app}\ProtectedApp.exe') + '"',
      '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
  AppExe: String;
begin
  Result := False;
  AppExe := ExpandConstant('{app}\ProtectedApp.exe');

  if not FileExists(AppExe) then
  begin
    MsgBox('No se encuentra ProtectedApp.exe y no se puede validar la contraseña maestra.', mbError, MB_OK);
    Exit;
  end;

  if not Exec(AppExe, '--authorize-uninstall', ExpandConstant('{app}'),
    SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    MsgBox('No se pudo abrir la autorización de desinstalación.', mbError, MB_OK);
    Exit;
  end;

  if ResultCode <> 0 then
  begin
    MsgBox('Desinstalación cancelada. La contraseña maestra no fue validada.', mbInformation, MB_OK);
    Exit;
  end;

  Result := True;
end;
