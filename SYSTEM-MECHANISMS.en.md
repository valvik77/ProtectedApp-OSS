# System mechanisms and why they exist

> **English** | [Español](SYSTEM-MECHANISMS.md)

ProtectedApp uses several Windows mechanisms that security software and
reviewers rightly treat with suspicion: an Image File Execution Options (IFEO)
`Debugger` value, a Windows service, a `SYSTEM` scheduled task, process
termination, and access-control changes on folders. Each one is needed for what
the product promises. This page states, for every mechanism, what it does, why a
simpler alternative was not enough, how far it is limited, where the code is,
and how to see and remove it.

Everything here can be checked against the source. Where a claim is about the
absence of a behavior, the search that supports it is given.

## Summary

| Mechanism | Needed for | Scope limit | Removed by |
| --- | --- | --- | --- |
| IFEO `Debugger` gate | Ask for a password *before* a protected program runs | Only the exact paths the user enabled; fails closed | Removing the rule, or uninstalling |
| `ProtectedAppGuardian` service | Enforce rules while the window is closed | Local only; acts on configured rules | Uninstall |
| `SYSTEM` health task | Re-arm the gates if the service is stopped | Runs one bundled executable | Uninstall |
| Process termination | Lock protected programs when required | Only processes matching an enabled rule | n/a (behavior) |
| Folder ACL deny | Lock a protected folder | Only folders the user chose | Removing the rule, or uninstalling |
| Dokany driver | Editable encrypted vault drives | Third-party, optional at use time | Kept if shared; remove separately |
| Hidden PowerShell in the installer | Register and remove the components above | Bundled scripts from the install folder | n/a |

## 1. IFEO `Debugger` gate

**What it does.** For each enabled protected `.exe`, the Guardian service writes
a subkey under
`HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\<image>\`.
When Windows starts that program, it starts `ProtectedApp.Gate.exe` instead. The
gate reports the blocked attempt to Guardian over a local pipe and exits; the
ProtectedApp agent then asks for the password. **The gate never starts the
original program.** Only Guardian does, after the password is accepted, and it
starts it in the user's own session with that user's own token (see section 4).

**Why not something simpler.** Windows offers no supported user-mode hook that
means "ask before this program runs". Polling for new processes and killing them
lets the program run, and possibly read or change data, before it is stopped.
IFEO is a long-standing Windows mechanism that intercepts the launch itself. It
is also used by legitimate tools, but a malware analyst will recognize the
technique, so its limits matter:

- **Path-filtered, not name-wide.** Each rule sets `UseFilter=1` on the image and
  a child key with `FilterFullPath` set to one full path. Another program with the
  same file name in a different folder is not intercepted
  ([ExecutionGateManager.cs](ProtectedApp.Service/ExecutionGateManager.cs)).
- **Marked and reversible.** Every child key carries `ProtectedAppManaged=1`.
  Cleanup removes only marked entries. If `UseFilter` already had a value, it is
  saved as `ProtectedAppPreviousUseFilter` and restored afterward.
- **Temporary authorization.** After a correct password, the `Debugger` value is
  removed so the launch can proceed, and restored once the program is no longer
  running (never sooner than about 8 seconds after authorization).
- **Fails closed.** If Guardian cannot be reached within 2 seconds, the gate
  shows a notice and the program does not start
  ([ProtectedApp.Gate/Program.cs](ProtectedApp.Gate/Program.cs)).
- **Nothing else.** The gate only connects to Guardian's local pipe. It does not
  touch the network or other processes.

**Python scripts.** A `.py` script is not an executable, so the interpreter and
launcher executables (`py.exe`, `pyw.exe`, `python.exe`, `pythonw.exe`) found in
the standard locations and near the script are gated the same way, and the
intercepted command line is forwarded to Guardian.

*Effect on other scripts.* While at least one `.py` rule is enabled, **every**
launch of those interpreters is intercepted, including scripts unrelated to
ProtectedApp. When the command line does not reference a protected script,
Guardian lifts that gate entry for the interpreter and starts the same
interpreter again, with the original arguments and working directory, so the
script runs normally
([GuardianEnforcer.cs](ProtectedApp.Service/GuardianEnforcer.cs)). Because
Guardian makes that second launch in the user's session, the new process is not a
child of the original caller, starts from a fresh copy of the user's environment,
does not inherit the caller's console or redirected input and output, and its
exit code is not returned to the caller. A script that relies on any of these
(output piped in a terminal, or a virtual environment activated in the calling
shell) may behave differently. Guardian restarts the interpreter only when the
caller is the user of the interactive session; a launch from a service, another
account or `runas` is blocked with a notice, because restarting it would run the
caller's command under a different identity. Disabling or removing the `.py`
rules ends the interception.

*What counts as running a protected script.* A script is recognized by its full
path or by a relative name resolved against the directory the process started in
(`python backup.py`, `.\backup.py`), including inside a shell string such as
`cmd /c "python backup.py"`. The command line is split with Windows' own parser
(`CommandLineToArgvW`), so quoting and backslash escapes cannot hide the script
([WindowsCommandLine.cs](Shared/WindowsCommandLine.cs)). Existing 8.3 short
paths on local disks are normalized to their long path before comparison; a
network path is never looked up, because the SYSTEM service would contact
whichever server a caller names, so it is compared as written. It is not recognized
when launched as `python -m module`, or from a shell string that first changes
directory: resolving either would require interpreting Python imports or the
`cmd`/PowerShell language and could identify a different file. After the user approves,
Guardian starts again only the script, the arguments that follow it, and harmless
interpreter options (`-u`, `-O`, `-X`, `-W`, `py` version selectors such as
`-3.11`), in the original working directory. Options that execute other code
(`-c`, `-m`, `-i`) and shell wrappers are discarded, so approving one script
never authorizes a different payload
([ProtectedTarget.cs](Shared/ProtectedTarget.cs)).

**Directory Opus.** If Directory Opus is installed, Guardian removes the
Page Heap diagnostic flags from `Image File Execution Options\dopus.exe`. Page
Heap is a Windows debugging option, not a protection, and with it enabled Opus is
terminated by verifier stops when it browses Dokany drives. Only the Page Heap
flag bit and `PageHeapFlags` are removed; nothing is added
([DirectoryOpusCompatibility.cs](ProtectedApp.Service/DirectoryOpusCompatibility.cs)).

## 2. Guardian service and `SYSTEM` health task

**Service.** `ProtectedAppGuardian` starts automatically so that rules apply
before anyone opens the window, and so that a normal user process cannot end the
protection by closing the interface. It is installed by
[Install-Service.ps1](ProtectedApp.Service/Install-Service.ps1) with automatic
restart on failure.

**Health task.** `ProtectedApp Guardian Health Check` runs as `SYSTEM` at boot
and every minute, and executes one command: the installed
`ProtectedApp.Guardian.exe --health-watch`. It checks the integrity of the
Guardian binary and its configuration, re-arms the IFEO gates, and restarts the
service if it was stopped or tampered with. If it finds Guardian stopped or
unrecoverable, it may lock the Windows session, **at most once per boot**
([GuardianHealthCheck.cs](ProtectedApp.Service/GuardianHealthCheck.cs)).

**Why.** Without it, stopping the service would silently remove the protection.

**Protection of its own files.** `%ProgramData%\ProtectedApp` is writable only by
`SYSTEM` and Administrators; standard users can read and run. Installation
records the SHA-256 and signer of the management application, and Guardian
accepts privileged requests only from that identity.

**Limit.** This is not a boundary against a local administrator or `SYSTEM`. An
administrator can stop the service or edit the registry. That is stated in the
[security model](SECURITY-MODEL.en.md), and the task exists to make silent
tampering visible and to restore the configured state, not to defeat an
administrator.

## 3. Local pipe

Guardian listens on a local named pipe. The access list grants `SYSTEM` and the
service identity full control, grants Authenticated Users read/write, and
**explicitly denies the Network SID**, so it is not reachable from other
machines. Authorization does not rest on the pipe list alone: sessions receive
tokens bound to the user SID, the calling process ID, and its start time, and
they expire. Clients connect at the *Identification* impersonation level: the
service can read who is calling, but cannot act as that user, even if another
process were to answer on the pipe in its place
([GuardianProtocol.cs](Shared/GuardianProtocol.cs)). Management operations
(changing the policy, locking everything,
configuring the webhook) additionally require the caller to be the recorded
ProtectedApp executable
([GuardianIpcServer.cs](ProtectedApp.Service/GuardianIpcServer.cs)). Failed
password attempts are throttled.

## 4. Starting a program in the user's session

A service runs in session 0 and cannot show a program on the user's desktop. To
start an authorized program, Guardian obtains the active user's token
(`WTSQueryUserToken`), duplicates it, and calls `CreateProcessAsUser` on the
interactive desktop. The program therefore runs with **the user's own
privileges, not as `SYSTEM` and not elevated**
([InteractiveProcessLauncher.cs](ProtectedApp.Service/InteractiveProcessLauncher.cs)).
The executable is the path the user configured in the rule.

## 5. Process termination

When a lock policy applies, or when a protected program runs without
authorization, Guardian ends it. A process is a target only if its executable
path equals an enabled rule, or, for a script rule, if it is a known interpreter
whose command line references that script. On an immediate lock, Guardian first
asks windows to close normally and waits a grace period; it forces termination
only for those that did not close. The product warns before lock actions because
unsaved work in a closed program can be lost
([GuardianEnforcer.cs](ProtectedApp.Service/GuardianEnforcer.cs)). It does not
terminate processes that match no rule.

## 6. Folder locking

Protected folders are locked by adding a deny entry to the folder's access-control
list. Before changing it, Guardian saves the original list, and it restores it
when the rule is removed. A folder that already contains a protected application,
or that nests inside or around another protected folder, is refused. Read access
to attributes is preserved so Explorer and Directory Opus can resolve icons.
Uninstalling **first runs `--restore-folders` and stops if it fails**, so an
interrupted removal does not leave a folder locked
([FolderProtectionService.cs](ProtectedApp.Service/FolderProtectionService.cs),
[Remove-Service.ps1](ProtectedApp.Service/Remove-Service.ps1)).

## 7. Vault drives (Dokany)

Editable vaults appear as virtual drives through [Dokany](https://dokan-dev.github.io/),
an open-source third-party file-system driver. The installer bundles its MSI and
installs it silently only when a compatible runtime is missing. ProtectedApp
itself contains no kernel driver: the repository holds no compiled binaries,
and its own components are user-mode. Because Dokany may be shared with other
software, the uninstaller keeps it.

## 8. Installer behavior

- The installer and uninstaller run bundled PowerShell scripts with
  `-NoProfile -NonInteractive -ExecutionPolicy Bypass`, hidden, from the
  installation folder. `-ExecutionPolicy Bypass` applies only to these
  invocations; it does not change the machine's policy. The scripts are in the
  repository: [Install-Service.ps1](ProtectedApp.Service/Install-Service.ps1),
  [Remove-Service.ps1](ProtectedApp.Service/Remove-Service.ps1),
  [Cleanup-ShellExtensions.ps1](ProtectedApp.ShellExtension/Cleanup-ShellExtensions.ps1),
  [Remove-ShellExtension.ps1](ProtectedApp.ShellExtension/Remove-ShellExtension.ps1),
  and [Install-ShellExtension.ps1](ProtectedApp.ShellExtension/Install-ShellExtension.ps1).
  Despite its name, the last one no longer installs anything: it removes
  registrations left by older versions. **These four legacy-cleanup scripts
  start only if registry entries or files of the old extension are found**, so a
  clean installation and its removal do not run them. What always runs is
  [Prepare-Installation.ps1](Installer/Prepare-Installation.ps1) (closes the app
  and stops the service before an update),
  [Update-Installed-Service.ps1](ProtectedApp.Service/Update-Installed-Service.ps1)
  (registers or updates the service), and `Remove-Service.ps1` when uninstalling.
- Explorer integration is a set of static context-menu commands under
  `HKLM\Software\Classes` (folder encrypt, `.pavault` open and mount, drive
  unmount), each running `ProtectedApp.exe` with a flag. **No in-process shell
  extension or COM server is registered**; the installer excludes the legacy DLL.
- Start with Windows uses the current user's `Run` key and starts silently.
- **Uninstalling requires the master password** (`--authorize-uninstall`). It
  then restores folders, removes the task, service, IFEO gates, event source, and
  `%ProgramData%\ProtectedApp`, and deletes `%LocalAppData%\ProtectedApp`.

## What ProtectedApp does not do

Each item can be re-checked by searching the source:

- **No code injection.** No use of `WriteProcessMemory`, `CreateRemoteThread`,
  `VirtualAllocEx`, `QueueUserAPC`, or `SetWindowsHookEx`.
- **No keylogging or global hooks.** The only keyboard API is `RegisterHotKey`,
  for the immediate-lock shortcut the user sets in Settings.
- **No hidden persistence.** The persistent components are those listed on this
  page.
- **No telemetry, remote code, or automatic updates.** Network use is limited to
  the optional tamper-alert webhook described in the [security
  model](SECURITY-MODEL.en.md). Updates are installed only from a file the user
  selects.
- **No credential collection.** Passwords are checked locally (stored as
  PBKDF2-SHA256 hashes) and never leave the computer. The only network feature is
  the optional webhook, whose payload contains no passwords or paths.

## See it, and remove it

Read-only checks in an elevated PowerShell:

```powershell
# IFEO entries created by ProtectedApp (also check the WOW6432Node view)
Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options' |
  ForEach-Object { Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue } |
  Where-Object { (Get-ItemProperty $_.PSPath).ProtectedAppManaged -eq 1 } |
  ForEach-Object { Get-ItemProperty $_.PSPath | Select-Object FilterFullPath, Debugger }

Get-Service ProtectedAppGuardian
schtasks /Query /TN "ProtectedApp Guardian Health Check" /V /FO LIST
```

To remove everything, use the official uninstaller from Windows Settings and
enter the master password. Do not delete the registry entries by hand while
folders are locked; the uninstaller restores their permissions first.

## Reporting a false positive or a concern

If antivirus software flags a build, please open an issue with the scanner name,
the detection name, and the file's SHA-256, so it can be checked against the
published release hashes and reported to the vendor. For a security concern, use
the private process in [SECURITY.en.md](SECURITY.en.md).
