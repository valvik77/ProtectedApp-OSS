param(
    [string] $Subject = 'CN=ProtectedApp Development',
    [ValidateRange(1, 5)] [int] $ValidYears = 2,
    [switch] $TrustForCurrentUser
)

$ErrorActionPreference = 'Stop'
$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -FriendlyName 'ProtectedApp Development Code Signing' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -KeyAlgorithm RSA `
    -KeyLength 3072 `
    -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears($ValidYears)

if ($TrustForCurrentUser) {
    $temporaryCertificate = Join-Path ([System.IO.Path]::GetTempPath()) "ProtectedApp-$($certificate.Thumbprint).cer"
    try {
        Export-Certificate -Cert $certificate -FilePath $temporaryCertificate -Force | Out-Null
        Import-Certificate -FilePath $temporaryCertificate -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
        Import-Certificate -FilePath $temporaryCertificate -CertStoreLocation 'Cert:\CurrentUser\TrustedPublisher' | Out-Null
    }
    finally {
        Remove-Item -LiteralPath $temporaryCertificate -Force -ErrorAction SilentlyContinue
    }
    Write-Warning 'Este certificado solo es de desarrollo y ahora es de confianza para el usuario actual. No distribuyas binarios firmados con él.'
}

Write-Host "Certificado de desarrollo creado: $($certificate.Thumbprint)"
Write-Host "Compila con: .\Build-Installer.ps1 -SigningCertificateThumbprint $($certificate.Thumbprint) -AllowDevelopmentCertificate -RequireSignature"
