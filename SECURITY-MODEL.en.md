# Security model

> **English** | [Español](SECURITY-MODEL.md)

## Purpose

ProtectedApp helps a Windows user password-protect selected desktop applications
and keep files in encrypted vaults. Guardian enforces the configured rules even
when the management window is hidden.

## Explicit limits

ProtectedApp is not an antivirus, a vulnerability scanner, or a tool designed
to identify, exploit, or bypass security controls. It is also not a boundary
against a local administrator, `SYSTEM`, malware already running with equivalent
privileges, or physical access to the computer. It complements—not replaces—
BitLocker, Secure Boot, Windows accounts, backups, and App Control/WDAC policies.

Application protection controls access from a normal Windows session. Locking can
close protected processes, so unsaved work in an external application can be
lost. The product warns before lock actions and provides removal through the
official installer.

## System changes

Installation can register the Guardian Windows service, a recovery task running
as `SYSTEM`, optional Explorer integrations, and the Dokany runtime required for
vault virtual drives. The application diagnostics verify these components, their
paths, and their permissions. The official uninstaller removes ProtectedApp's
own components; Dokany is retained when it may be shared with other software.

## Data and network

Vaults use authenticated encryption. A vault password does not turn the computer
into an isolated environment or recover data exposed before the vault was
locked. The local application state and optional webhook secrets receive Windows
DPAPI protection appropriate to the component that uses them. Optionally,
**TPM configuration protection** encrypts local state with a random key wrapped
by a non-exportable RSA key in that user's TPM on that computer. It does not
modify vaults or their passwords. It is not a migration or recovery mechanism:
export a configuration backup before resetting the TPM, reinstalling Windows,
or changing the motherboard. After any of those operations, restoring the
backup or setting up ProtectedApp again may be required.

ProtectedApp includes no telemetry. Possible network traffic is limited to the
HTTPS tamper-alert webhook explicitly configured by the operator and the OCSP/
CRL queries Windows may make while checking revocation for the Authenticode
signature of a manually selected installer. The application does not download or
install updates automatically.

## Release integrity

Official releases are built by GitHub Actions, pass builds, tests, dependency and
repository audits, and are published with hashes. Public signing keys are never
stored in the repository or development computers. See the [Code signing
policy](CODE-SIGNING-POLICY.en.md) and [SIGNING.en.md](SIGNING.en.md) for signing and
approval details.

## Reporting issues

Report vulnerabilities according to [SECURITY.en.md](SECURITY.en.md), not in
public issues containing exploitable details or secrets.
