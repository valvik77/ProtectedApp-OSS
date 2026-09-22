# Changelog

All notable changes to ProtectedApp are documented here. Entries are grouped by
the version or review candidate that contains them. An **unsigned review
candidate** is not a public release and must not be distributed as one; see
[RELEASING.en.md](RELEASING.en.md) for the release and signing policy.

> [Español](CHANGELOG.es.md)

## Unreleased

### Security

- Added a deterministic corruption-and-recovery regression corpus for PAVLT003/4
  vaults: damaged headers, encrypted indexes, file chunks, truncated containers,
  delta journals and encrypted backups must be rejected without altering the
  original vault. The tests also make the intended lazy verification boundary
  explicit: the envelope is authenticated when opening a vault and each file
  block is authenticated before it is read. An interrupted virtual write is
  also required to preserve the previous container byte-for-byte and remove its
  incomplete temporary file. Virtual entries with traversal or absolute paths
  must be rejected before they can replace an existing vault. Case-insensitive
  duplicate virtual paths are now rejected before a temporary container is
  created.

## 1.4.211 — 2026-09-22 (development pre-release)

### Fixed

- A protected `.py` or `.bat` script started with a relative name (`python
  backup.py`, `.\backup.py`) was not recognized and ran without a password.
  Guardian, its independent emergency-recovery supervisor, and the app's
  monitor and inactivity tracking now resolve relative names against the
  directory the process started in, read from the process itself (including
  32-bit processes), and also recognize the `\\?\` path prefix. The command
  line is split with Windows' own parser (`CommandLineToArgvW`), so a
  backslash-escaped quote such as `python -X\" backup.py` can no longer hide the
  script, and a script inside a shell string (`cmd /c "python backup.py"`) is
  recognized. Existing 8.3 short paths on local disks are also normalized to
  their long Windows path before comparison; a network path is never looked up
  (that would make the SYSTEM service contact whatever server a caller names),
  so it is compared as written. Launching as `python -m module`, or from a shell
  string that first changes directory, is still not recognized.
- After a protected `.py` script was approved, its own arguments were dropped
  and it started in the script's folder. It now keeps the arguments that follow
  the script, harmless interpreter options (`-u`, `-X`, `-W`, `py -3.11`) and
  the original working directory. Options that execute other code (`-c`, `-m`)
  and shell wrappers are still never replayed, so approving one script cannot
  authorize a different payload.
- An interpreter launch intercepted for a user with no ProtectedApp
  configuration is no longer refused for lack of a policy; it now passes
  through like any other script.

### Security

- The SYSTEM service no longer reads the file system, or the network, for a
  path that a caller controls. Path.GetFullPath expands 8.3 aliases whenever a
  path contains "~", and File.Exists, Directory.Exists and directory listing
  are all file-system reads; for a path on a share each one blocked for about
  21 s when the server did not answer, and would authenticate to it with the
  computer account when it did. A process started from a share, a Gate started
  with a crafted argument, or a working directory on a share was enough. Process
  image paths, the caller's identity checks on the pipe, the paths the Gate
  sends, and the interpreter and working-directory checks now normalize text
  only, and probe the disk solely for a fixed local drive.
- Guardian no longer starts a program in the interactive session on behalf of a
  caller that is not that session's user (a service, another account, or a "run
  as" launch). Before, the program was started with the session user's token,
  running the caller's command under a different identity.

### Changed

- The Guardian pipe clients (the protection gate and the management app) now
  connect at the Identification impersonation level instead of Impersonation.
  Guardian only needs to know who is calling, so it can still read the caller's
  identity but can no longer act as that user, even if another process answers
  on the pipe in place of the service.
- The installer and uninstaller start the four legacy Explorer-extension
  cleanup scripts only when registry entries or files of that removed extension
  remain. A clean installation, and its removal, no longer launch those hidden
  PowerShell processes.

### Documentation

- Added [SYSTEM-MECHANISMS.en.md](SYSTEM-MECHANISMS.en.md), which explains why
  each sensitive Windows mechanism (IFEO gate, service, `SYSTEM` task, process
  termination, folder locking, Dokany, installer scripts) is needed, how far it
  is limited, and how to inspect and remove it, including the effect a protected
  Python script has on other scripts run by the same interpreter.

## 1.4.210 — 2026-09-21 (development pre-release)

### Fixed

- State persistence now serializes concurrent saves, uses unique temporary
  files, and flushes them before atomic replacement, preventing colliding
  writes from corrupting the encrypted local configuration.
- The source version now matches the current development pre-release, so
  standalone local builds do not begin from an obsolete version reference.
- IPC client and Guardian session lookups now release process handles promptly,
  and malformed IPC responses are handled as controlled availability errors.
- Guardian now rejects malformed IPC JSON deterministically, and bootstrap
  secret comparison clears its temporary byte copies after use.
- Launch and crash diagnostics (`launch.log`, `crash.log`,
  `launch-exception.log`) are now strictly size-bounded: no file exceeds
  256 KiB. An entry that would not fit rotates the file to a single `.1`
  companion first, and a single entry larger than the limit is truncated with
  a marker, instead of growing for the life of the installation.
- Fifteen dialogs and labels (duplicate-file and duplicate-vault notices,
  permanent-deletion confirmation, uninstaller and remote-alert messages, and
  others) were shown in Spanish while the English interface was selected. The
  permanent-deletion prompt is now localized when the dialog is built, from the
  same code that validates the answer, and it always accepts the original
  Spanish word as well, so an English user can never be asked for a word that
  is rejected.
- The English message for a too-short new vault password now states the
  enforced 12-character minimum instead of resolving to no translation.
- The tamper-alert webhook filter now also rejects multicast, reserved,
  benchmarking (`198.18.0.0/15`), documentation, and IPv6 addresses that embed
  an IPv4 target (`::/96`, NAT64), in addition to loopback and private ranges.
- The project website no longer stops working when the browser blocks
  `localStorage`, and ignores an unrecognized stored language.

### Changed

- Vault command-line activation now uses one bounded Windows argument parser
  for open, contextual action, and drive-unmount requests.
- Updated the test SDK to 18.10.1 for the Guardian and vault test suites.
- Updated Microsoft Windows App SDK to 2.5.1 after successful x64 build,
  Guardian/vault test, and visual compatibility validation.
- Removed 206 superseded duplicate entries and four obsolete password-length
  entries from the English localization catalogue. The effective catalogue was
  verified unchanged apart from the corrections listed above.

## 1.4.206 — 2026-09-19 (development pre-release)

### Fixed

- The installer now keeps the ProtectedApp shield artwork in both light and
  Windows dark mode instead of falling back to Inno Setup's generic artwork.
- Local installer version selection now also considers development pre-release
  tags, preventing a new installer from receiving an older version number.

## 1.4.205 — 2026-09-19 (development pre-release)

### Fixed

- Double-clicking a `.pavault` file now explicitly opens that vault without
  first unlocking the ProtectedApp management panel. The separate contextual
  mount/unmount command remains unchanged.

## 1.4.204 — 2026-09-19 (development pre-release)

### Added

- Published the public-key-only development/test certificate with pinned
  identity and SHA-256 verification, explicit current-user trust instructions,
  removal and compromise procedures, and repository checks that reject any
  substituted certificate or private key material.
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

- Master passwords now require at least 8 characters. Passwords for vaults,
  encrypted backups, and app-specific credentials remain at least 12
  characters because they directly protect encrypted or portable data.
- Accelerated virtual-vault work: repeated reads reuse an authenticated
  in-memory block cache and a session file handle, file and chunk lookups are
  indexed, and clearly incompressible blocks skip unnecessary compression.
- Further reduced virtual-vault allocation and copy overhead: Explorer and
  editable-overlay reads now decrypt directly into their destination buffers,
  cached chunks are copied directly without a full temporary clone, and the
  first edit of an existing block avoids a second 64 KiB plaintext buffer.
- Improved responsiveness of large virtual folders and final vault commits.
  Stable directory listings are reused until a change invalidates them; final
  consolidation reuses modified blocks only after the virtual drive has fully
  stopped, while recovery journals retain independent snapshots.
- Sequential final consolidation no longer fills the interactive plaintext
  cache with blocks that will not be read again, reducing memory pressure
  while preserving authenticated caching for normal Explorer use.
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
- Reworked the new protected-application editor into a two-column layout on
  wide windows, avoiding unnecessary vertical scrolling.

### Fixed

- A one-minute inactivity or timed-close limit now warns 15 seconds before
  closing rather than immediately at launch or after an extension.
- Dismissing an inactivity warning no longer turns the pointer movement needed
  to reach the dialog into an unintended activity extension; the choice is now
  labelled **Do not extend**.
- The protected-app picker now discovers desktop programs from App Paths and
  Start-menu shortcuts as well as uninstall entries, while excluding Dokan's
  internal command-line tool.
- Made the protected-app picker responsive on narrow and high-DPI windows so
  its search field and rows no longer clip at the right edge.
- Ignore inaccessible Start-menu subdirectories while discovering applications
  so a protected folder cannot close ProtectedApp.
- Suppress only immediate helper-process relaunches after automatic closing,
  preventing a timeout from opening an unsolicited password prompt. New
  authorized launches start with fresh timer UI state.
- Keep password retry delays visible and enforced when a password window is
  closed and reopened, instead of appearing to reset the lockout.
- The setup now opens master-password creation on a first installation; upgrades keep the panel in the background. Post-install activation also works when a ProtectedApp instance is already running.
- Guardian service installation, update, and removal now manage the SYSTEM health task through the local Task Scheduler API instead of CIM, which can deny access in Windows Sandbox.
- Report a failed Guardian installer before attempting policy bootstrap against a still-running service; this preserves the real installation error instead of misleadingly reporting an invalid authorization secret.
- Skip redundant Guardian service reconfiguration during repair when its binary path and automatic start are already correct; retain the actual Windows error if reconfiguration is needed and fails.
- Suppress the premature "Guardian unavailable" security alert while first-run master-password setup and automatic Guardian installation are still in progress.
- Install Guardian after the first interactive activation of an instance started silently by the setup; that activation does not rerun the startup-only installation path.
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
