# Releasing ProtectedApp

> **English** | [Español](RELEASING.md)

Signed public releases are produced from a reviewed `main` commit and an
immutable `vM.m.r` (or `vM.m.r.c`) tag. The tag starts the **Release candidate**
workflow, which builds, tests, audits, and creates an unsigned candidate for
review. It also generates an SPDX SBOM, a SHA-256 checksum file, and GitHub
provenance attestations.

The workflow does not publish a GitHub Release automatically. Until SignPath is
configured, there is no public binary release of ProtectedApp. Its unsigned
artifacts are internal review candidates only and must not be published or
distributed. After SignPath Foundation approves the project, signing must be
limited to `main` and version tags, use origin verification, and require the
manual approval described in the [Code signing policy](CODE-SIGNING-POLICY.md).

## Signed release sequence

1. Merge reviewed changes into `main` and confirm that CI, CodeQL, and dependency
   auditing are green.
2. Create and push a `vM.m.r` tag on that commit. Never reuse or move a tag
   already used for a published version.
3. Review the artifacts, checksums, SBOM, and GitHub Actions attestation. Request
   signing only from that verified workflow.
4. After manual signing approval, check Authenticode, its timestamp, and the
   expected signer.
5. Create the GitHub Release from [the template](.github/RELEASE_TEMPLATE.md),
   attach only the signed installer, SBOM, and checksum file, and link this guide
   and the signing policy.

Do not attach certificates, DLibs, signing metadata, PFX files, keys, tokens, or
unsigned candidates to a signed public release.

## Verifying a download

```powershell
Get-FileHash .\ProtectedApp-Setup-x64-M.m.r.exe -Algorithm SHA256
Get-AuthenticodeSignature .\ProtectedApp-Setup-x64-M.m.r.exe | Format-List
```

The hash must match the `.sha256` file from the same release and the signature
must be valid. Verify attestations with GitHub CLI:

```powershell
gh attestation verify .\ProtectedApp-Setup-x64-M.m.r.exe --owner valvik77
```

## Maintainer accounts

Anyone with GitHub write access or SignPath signing-approval rights must use
MFA. A passkey or FIDO2 security key is recommended; store recovery codes away
from the development computer.
