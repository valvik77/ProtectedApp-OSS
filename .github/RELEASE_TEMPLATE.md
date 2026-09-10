# ProtectedApp {VERSION}

> This release is signed only after the configured SignPath origin verification
> and manual approval succeed. Do not publish an unsigned review candidate.

## Changes

- Describe user-visible changes, fixes, and security-impacting changes.

## Verification

- Source commit: `{COMMIT}`
- GitHub Actions build: `{BUILD_URL}`
- SHA-256: see `ProtectedApp-{VERSION}.sha256`.
- SBOM: `ProtectedApp-{VERSION}.spdx.json`.
- Artifact attestation: verify with `gh attestation verify` before publishing.

## Installation and removal

See the installation and uninstall sections in [README.md](../README.md).

## Code signing policy

[Code signing policy](../CODE-SIGNING-POLICY.md). Free code signing provided
by SignPath.io, certificate by SignPath Foundation, applies only once the
Foundation approves the project and the release has completed its signing flow.
