# ProtectedApp

> **English** | [Español](README.es.md)

> [!WARNING]
> **Development/test certificate — not a publicly trusted release certificate.**
> This repository publishes only the public part of its self-signed development
> certificate; it contains no private key. Installing it deliberately changes
> the trust settings of the current Windows user and should be done only on a
> test machine or virtual machine that you control. It does not make a build an
> official release, and Smart App Control, WDAC, antivirus software, or
> organizational policies may still block it. Never install this certificate in
> the machine-wide (`LocalMachine`) stores or accept a certificate whose SHA-256
> and thumbprint do not exactly match the documented values. Read and follow the
> complete [verification, current-user installation, build verification, risk,
> expiry, replacement, and removal instructions](DEVELOPMENT-CERTIFICATE.en.md)
> **before installing or running any development-signed build**.

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
- Can create scheduled, password-encrypted configuration backups with a chosen
  folder and retention count.
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

1. After a new interactive installation, leave **Start ProtectedApp** selected:
   the app opens master-password setup automatically. Otherwise, open it from
   Start, its shortcut, or the tray icon. An upgrade stays in the background.
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
write. Normal edits use an authenticated encrypted differential journal with
only changed blocks and metadata; very large edits automatically use the
compatible encrypted full journal. Interrupted sessions can be recovered or
inspected read-only at the next unlock. Do not synchronize an open vault work directory;
synchronize the closed `.pavault` file instead.

An optional per-vault TPM mode requires both the vault password and a
non-exportable key in the current computer's TPM. It is deliberately off by
default. Before enabling it, ProtectedApp retains a password-only recovery copy
next to the vault as `name.pavault.tpm-recovery.pavault`. Keep that file safely:
resetting the TPM, reinstalling Windows, or replacing the motherboard makes the
TPM-bound primary vault unavailable.

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
Development/test builds can use the repository's strictly public certificate
only after reading its [explicit trust, verification, and removal
instructions](DEVELOPMENT-CERTIFICATE.en.md). It is self-signed and is not a
stable-release or Smart App Control trust mechanism.
Do not reuse an installer version: Explorer can retain a versioned shell-
extension DLL and reject an otherwise valid replacement. The unsigned-build
switch is intentionally explicit. An unsigned or development-signed build is
not an official public release and Windows security policies may block it.

## Releases, signatures, and verification

The release process, checksums, and publication instructions are documented in
[RELEASING.en.md](RELEASING.en.md). Clearly marked development/test prereleases
may be available, signed locally with the self-signed certificate described
above. They are not official stable releases, are not publicly trusted, and
must not be confused with the unsigned internal review candidates from GitHub
Actions. No publicly trusted stable binary release is available yet.

The [changelog](CHANGELOG.md) tracks user-visible changes, security hardening,
and release-candidate status.

The project's [Code signing policy](CODE-SIGNING-POLICY.en.md) describes signing
roles, privacy, release approval, and the intended SignPath integration.

## Privacy

ProtectedApp does not include telemetry. The optional tamper-alert webhook is
configured explicitly by the installer or operator. Its JSON carries a random
event ID, a stable random installation ID, UTC timestamp, event code, a stable
incident category, severity, and a safe summary; it never includes paths,
user names, passwords, vault contents, or raw local error details. When a user
manually selects a local update, Windows may contact certificate-revocation
services (OCSP or CRL) while validating its Authenticode signature.

## Security reports and contributions

Please read [SECURITY.en.md](SECURITY.en.md) before reporting a vulnerability. Use
GitHub's private vulnerability reporting rather than a public issue for security
reports. Contribution guidance is available in [CONTRIBUTING.en.md](CONTRIBUTING.en.md).

## License

ProtectedApp is free software under [GPL-3.0-or-later](LICENSE). Third-party
notices are available in [Legal/THIRD-PARTY-NOTICES-en.txt](Legal/THIRD-PARTY-NOTICES-en.txt).
