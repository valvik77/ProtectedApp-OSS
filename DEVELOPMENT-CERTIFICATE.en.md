# Public certificate for development and test builds

> [Español](DEVELOPMENT-CERTIFICATE.md)

## Essential warning

This certificate is **self-signed, is not backed by a public certificate
authority, and is not a qualified certificate**. Installing it deliberately
changes Windows trust for the current user. Use it only to evaluate ProtectedApp
development builds on a test computer, virtual machine, or recoverable
environment.

It does not establish that a build is safe, stable, or an official release. It
does not prevent Smart App Control or a WDAC policy from rejecting the file. Do
not install it on managed devices without authorization or on systems where a
trust-store change is unacceptable.

The private key is **not included on GitHub or in the `.cer` file**. The public
certificate cannot sign software without that key. However, while you trust the
certificate, Windows may trust any executable signed with the corresponding
private key. Remove it when testing ends.

## Identity you must verify

- File: `Signing/Public/ProtectedApp-Development-Test.cer`
- Subject and issuer: `CN=Valvik ProtectedApp Development`
- Purpose: code signing (`1.3.6.1.5.5.7.3.3`)
- Exact file SHA-256:
  `AFD8A0ADD54130355D878CFEC7E3590119A9FCB536FDD45EC8DC797F82E65C39`
- X.509 SHA-1 thumbprint displayed by Windows:
  `4AD1F2E988F4EFD130DF66A02442B470927B433C`
- Validity: August 21, 2026 through August 21, 2028

SHA-1 is shown only because Windows uses it as the certificate identifier; file
integrity is checked with SHA-256.

## 1. Verify before trusting

Clone the official repository and run from its root:

```powershell
.\Signing\Test-PublicDevelopmentCertificate.ps1
```

The output must say `VALID DEVELOPMENT/TEST CERTIFICATE - NOT PUBLICLY TRUSTED`,
show `HasPrivateKey: False`, and exactly match the fingerprints above. If
anything differs, do not install the certificate or run the setup.

## 2. Deliberately install for the current user

Do not use `LocalMachine`. The following stores limit the trust change to the
current user. Read the warning above before running:

```powershell
$certificate = Resolve-Path .\Signing\Public\ProtectedApp-Development-Test.cer
Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\Root
Import-Certificate -FilePath $certificate -CertStoreLocation Cert:\CurrentUser\TrustedPublisher
```

Windows will display or apply the trust change. The ProtectedApp installer must
never perform this operation silently.

## 3. Verify every installer

First compare the SHA-256 published specifically for that build, then verify its
signature after installing the certificate:

```powershell
$setup = '.\ProtectedApp-Setup-x64-VERSION.exe'
Get-FileHash -LiteralPath $setup -Algorithm SHA256
$signature = Get-AuthenticodeSignature -LiteralPath $setup
$signature | Format-List Status,StatusMessage,SignerCertificate,TimeStamperCertificate
if ($signature.Status -ne 'Valid' -or
    $signature.SignerCertificate.Thumbprint -ne '4AD1F2E988F4EFD130DF66A02442B470927B433C') {
    throw 'The installer is not signed by the expected test certificate.'
}
```

A valid signature proves only that the file has not changed since it was signed
by whoever controls that key. Also confirm that it came from
`https://github.com/valvik77/ProtectedApp-OSS` and that its version, commit, and
hash match the build notes.

## 4. Remove trust after testing

Run these commands only for the specified thumbprint:

```powershell
$thumbprint = '4AD1F2E988F4EFD130DF66A02442B470927B433C'
Remove-Item -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$thumbprint" -ErrorAction SilentlyContinue
Remove-Item -LiteralPath "Cert:\CurrentUser\Root\$thumbprint" -ErrorAction SilentlyContinue
```

Installed applications are not automatically removed, but their signatures will
no longer be trusted through this certificate.

## Compromise, replacement, or expiry

If the repository publishes a compromise notice, stop running new builds,
remove the certificate immediately with the commands above, and wait for
instructions. A self-signed certificate has no public OCSP/CRL revocation
service comparable to a commercial authority.

A replacement or renewal has a different thumbprint and **must never be accepted
automatically**. ProtectedApp requires reinstallation when the signing identity
changes. Save and lock vaults and retain verified backups before uninstalling or
changing channels.

This test channel is provisional. Stable public releases require a publicly
recognized signing identity; this certificate is not intended to replace one.
