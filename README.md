# ProtectedApp

> **English** | [Español](README.es.md)

ProtectedApp is an open-source WinUI 3 application for Windows that password-
protects selected desktop applications and provides encrypted vaults for
sensitive files. Guardian monitors configured processes, requires authorization
before they can run, and starts them again after the password is accepted.

## What it does

- Protects selected `.exe`, `.bat`, and `.py` applications with either the
  master password or an application-specific password.
- Supports temporary authorization, automatic lock/close policies, and weekly
  schedules.
- Creates `.pavault` encrypted vaults with AES-256-GCM and PBKDF2-SHA256.
- Opens vaults read-only or as editable Dokany virtual drives. Editable work is
  journaled and recovered in encrypted form after an interruption.
- Creates encrypted settings backups and keeps encrypted prior vault copies for
  recovery.
- Uses the Guardian Windows service to enforce configured rules even when the
  management window is hidden.

## Important security limits

ProtectedApp helps protect applications and vault contents during a normal
Windows session. It is not a boundary against a local administrator, `SYSTEM`,
malware already running with elevated privileges, or physical access to an
unlocked device. Use it alongside BitLocker, Secure Boot, Windows accounts,
backups, and—where appropriate—tested App Control for Business/WDAC policies.

The complete scope, system changes, network behavior, and limitations are in
the [security model](SECURITY-MODEL.en.md).

## Getting started

1. Open ProtectedApp interactively from Start, its shortcut, or the tray icon.
2. Create the master password. It is required to manage rules, restore backups,
   and uninstall safely.
3. In **Settings**, confirm that the **Guardian service** is running.
4. Under **Protected applications**, choose **Add** and select an application
   or script to protect.
5. Under **Encrypted vaults**, create a `.pavault` vault, or convert a folder
   after first making sure you retain the original folder until the vault has
   been verified.

Automatic startup is intentionally silent: it does not show the management
window or request the master password. The password is requested only after an
interactive open or an attempt to run a protected resource.

## Vaults and recovery

Vault data is encrypted and authenticated in independently protected blocks.
Changing a vault password creates a new data key and re-encrypts its files;
previous encrypted backups retain their previous password. A vault supports up
to 1 GB of content, 20,000 entries, and 100,000 blocks.

When editing a vault, use **Save and lock vault** when finished. ProtectedApp
validates the vault and atomically replaces it only after a successful encrypted
write. Interrupted editable sessions can offer recovery of the valid encrypted
journal at the next unlock. Do not synchronize an open vault work directory;
synchronize the closed `.pavault` file instead.

## Build from source

Build on Windows with the required .NET SDK, Windows App SDK, and Dokany support
for virtual-drive tests:

```powershell
dotnet restore
dotnet build ProtectedApp.sln -c Release -warnaserror
dotnet test ProtectedApp.Guardian.Tests\ProtectedApp.Guardian.Tests.csproj -c Release --no-restore
dotnet test ProtectedApp.Vault.Tests\ProtectedApp.Vault.Tests.csproj -c Release --no-restore
```

To create a local development installer:

```powershell
.\Build-Installer.ps1 -AllowUnsignedDevelopmentBuild
```

The script assigns a new installer version automatically for each local build.
Do not reuse an installer version: Explorer can retain a versioned shell-
extension DLL and reject an otherwise valid replacement. The unsigned-build
switch is intentionally explicit. An unsigned or development-signed build is
not an official public release and Windows security policies may block it.

## Releases, signatures, and verification

The release process, checksums, SBOM, provenance, and publication instructions
are documented in [RELEASING.en.md](RELEASING.en.md). No public ProtectedApp
binary release is available until its signing process has been configured and
approved. Unsigned workflow artifacts are internal review candidates and must
not be downloaded or distributed as releases.

The project's [Code signing policy](CODE-SIGNING-POLICY.en.md) describes signing
roles, privacy, release approval, and the intended SignPath integration.

## Privacy

ProtectedApp does not include telemetry. The optional tamper-alert webhook is
configured explicitly by the installer or operator. When a user manually
selects a local update, Windows may contact certificate-revocation services
(OCSP or CRL) while validating its Authenticode signature.

## Security reports and contributions

Please read [SECURITY.en.md](SECURITY.en.md) before reporting a vulnerability. Use
GitHub's private vulnerability reporting rather than a public issue for security
reports. Contribution guidance is available in [CONTRIBUTING.en.md](CONTRIBUTING.en.md).

## License

ProtectedApp is free software under [GPL-3.0-or-later](LICENSE). Third-party
notices are available in [Legal/THIRD-PARTY-NOTICES.en.txt](Legal/THIRD-PARTY-NOTICES.en.txt).
