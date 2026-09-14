# Changelog

All notable changes to ProtectedApp are documented here. Entries are grouped by
the version or review candidate that contains them. An **unsigned review
candidate** is not a public release and must not be distributed as one; see
[RELEASING.en.md](RELEASING.en.md) for the release and signing policy.

> [Español](CHANGELOG.es.md)

## Unreleased

### Added

- Optional TPM protection for the local ProtectedApp configuration and rules.
  It protects local rules, preferences, and activity history with a
  non-exportable key on the current computer.
- Optional TPM second factor for individual vaults. A TPM-bound vault requires
  its password and the current computer's TPM; enabling it creates a
  password-only recovery copy next to the vault.
- Scheduled, password-encrypted `.pabackup` configuration backups, with a
  selected destination, daily or weekly frequency, retention, and a manual
  **Create now** action.
- Safe incident category, severity, and summary fields in optional tamper
  webhook events, without exposing local paths, credentials, or raw errors.

### Changed

- Accelerated virtual-vault work: repeated reads reuse an authenticated
  in-memory block cache and a session file handle, file and chunk lookups are
  indexed, and clearly incompressible blocks skip unnecessary compression.
- Reused the authenticated vault session for its immediate virtual mount,
  avoiding a second password derivation and TPM operation for normal opens.
- Coalesced ordinary Explorer handle-close journal requests. Explicit file
  flushes and final vault locking still wait for a durable encrypted journal.
- Replaced routine full-container edit journals with an authenticated,
  encrypted differential journal containing only changed 64 KiB blocks and
  metadata. Oversized overlays automatically retain the compatible full-journal
  fallback, and interrupted journals can be inspected read-only before commit.
- Indexed direct children for editable virtual folders, avoiding a full-vault
  scan whenever Explorer lists a directory.
- Buffered writes to temporary encrypted containers and still flush them to
  physical storage immediately before verification and atomic replacement.
- Clarified TPM wording throughout the settings UI and security documentation:
  configuration TPM protection safeguards local configuration and protected-app
  rules; it does not change vault encryption unless TPM is enabled per vault.
- Moved expensive TPM state operations off the UI thread to keep configuration
  changes responsive.
- Kept full installer probes out of ordinary pull-request and push CI; they now
  run only for tagged review candidates.

### Fixed

- Prevented UI deadlocks while converting a vault to TPM protection and while
  running the installer vault-backup verification probe.
- Automatic local installer versions now consider the checked-in version,
  release tags, installed binaries, and existing installers, preventing
  accidental downgrade builds or reuse of a published shell-extension version.

## 1.4.186 — 2026-09-13 (unsigned review candidate)

### Added

- Automatic encrypted configuration backups with recovery guidance.
- Optional TPM protection for vaults and for local configuration state.
- More actionable, privacy-preserving tamper webhook payloads.

### Changed

- Redesigned the WinUI interface around the ShieldLock-inspired workspace:
  navigation, dashboard cards, typography, icons, dialogs, editors, vault rows,
  and responsive settings surfaces.
- Added translucent acrylic styling to navigation and dialog overlays, and
  refined focus, hover, selection, and contrast states.
- Made protected-application editor dialogs wider, centered, and less prone to
  clipped action icons or unnecessary vertical scrolling.
- Made local development installers use a distinct automatically generated
  version, including support for an explicitly requested local signing flow.

### Fixed

- Corrected several dialog layout, centering, clipping, and action-icon sizing
  problems introduced during the interface redesign.
- Prevented an installer verification hang caused by a UI synchronization
  context during vault reads.
- Reduced ordinary CI runner load by reserving installer probes for tagged
  review candidates.

## Initial open-source release — 2026-09-10

### Added

- Public GPL-3.0-or-later source repository, build instructions, contribution
  guidance, security policy, signing policy, release process, SBOM/provenance
  workflow, and repository safety checks.
- English documentation alongside the original Spanish documentation.

### Security

- Hardened installer validation, update trust checks, configuration protection,
  recovery handling, and repository secret controls.
- Documented the security model, privacy boundaries, optional webhook behavior,
  and code-signing responsibilities.
