# Releasing ProtectedApp

> **English** | [Español](RELEASING.md)

Stable public releases are produced from a reviewed `main` commit and an
immutable `vM.m.r` (or `vM.m.r.c`) tag. The tag starts the **Release candidate**
workflow, which builds, tests, audits, and creates an unsigned candidate for
review. It also generates an SPDX SBOM, a SHA-256 checksum file for that
unsigned candidate, and GitHub provenance attestations for the unsigned
candidate.

The workflow does not publish a GitHub Release automatically. Until SignPath is
configured, there is no stable public binary release of ProtectedApp. Its unsigned
artifacts are internal review candidates only and must not be published or
distributed. After SignPath Foundation approves the project, signing must be
limited to `main` and version tags, use origin verification, and require the
manual approval described in the [Code signing policy](CODE-SIGNING-POLICY.en.md).

## Development/test prereleases

Exceptionally, a `dev-vM.m.r` GitHub prerelease may be published after local
signing with the self-signed development certificate whose **public part** is
in [Signing/Public](Signing/Public/README.md). It is not an official release,
is not publicly trusted, and does not inherit the unsigned candidate's GitHub
Actions attestation. The author must build from the clean tagged commit, run
tests and audits, verify the expected signature and thumbprint, and generate
the SHA-256 **after** signing. The prerelease must state the exact commit and
local-build origin, provide the hash, and link to the public certificate and its
[instructions and risks](DEVELOPMENT-CERTIFICATE.en.md). Never attach a private
key, PFX, or unsigned candidate. Downloaders must verify the hash and signature
before running the installer and consciously decide whether to trust the
certificate for their current user; Smart App Control/WDAC may still block it.

## Signed release sequence

1. Merge reviewed changes into `main` and confirm that CI, CodeQL, and dependency
   auditing are green.
2. Create and push a `vM.m.r` tag on that commit. Never reuse or move a tag
   already used for a published version.
3. Review the unsigned candidate, its checksum, SBOM, and GitHub Actions
   attestation. Request signing only from that verified workflow.
4. After manual signing approval, check Authenticode, its timestamp, and the
   expected signer.
5. Generate a new SHA-256 checksum for the signed installer. Authenticode
   signing changes its bytes, so never reuse the unsigned candidate's checksum
   or attestation for the signed file.
6. Create the GitHub Release from [the template](.github/RELEASE_TEMPLATE.md),
   attach only the signed installer, SBOM, and newly generated checksum file,
   and link this guide and the signing policy.

Do not attach certificates, DLibs, signing metadata, PFX files, keys, tokens, or
unsigned candidates to a signed public release.

Generate the checksum after signing, using the exact installer that will be
attached to the release:

```powershell
$installer = '.\ProtectedApp-Setup-x64-M.m.r.exe'
$hash = Get-FileHash -LiteralPath $installer -Algorithm SHA256
"{0} *{1}" -f $hash.Hash, (Split-Path -Leaf $installer) |
  Set-Content -LiteralPath '.\ProtectedApp-M.m.r.sha256' -Encoding utf8NoBOM
```

## Verifying a download

```powershell
Get-FileHash .\ProtectedApp-Setup-x64-M.m.r.exe -Algorithm SHA256
Get-AuthenticodeSignature .\ProtectedApp-Setup-x64-M.m.r.exe | Format-List
```

The hash must match the `.sha256` file from the same public release and the
signature must be valid. The provenance attestation in the release-candidate
workflow applies only to its unsigned internal candidate, not the later signed
installer. When reviewing that internal candidate with GitHub CLI, restrict the
identity to this repository:

```powershell
gh attestation verify .\ProtectedApp-Setup-x64-M.m.r.exe --repo valvik77/ProtectedApp-OSS
```

## Maintainer accounts

Anyone with GitHub write access or SignPath signing-approval rights must use
MFA. A passkey or FIDO2 security key is recommended; store recovery codes away
from the development computer.
