param(
    [string] $CertificatePath = (Join-Path $PSScriptRoot 'Public\ProtectedApp-Development-Test.cer')
)

$ErrorActionPreference = 'Stop'
$expectedSha256 = 'AFD8A0ADD54130355D878CFEC7E3590119A9FCB536FDD45EC8DC797F82E65C39'
$expectedThumbprint = '4AD1F2E988F4EFD130DF66A02442B470927B433C'
$expectedSubject = 'CN=Valvik ProtectedApp Development'
$codeSigningOid = '1.3.6.1.5.5.7.3.3'
$resolvedPath = (Resolve-Path -LiteralPath $CertificatePath -ErrorAction Stop).Path

if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($resolvedPath) -ne
    [Security.Cryptography.X509Certificates.X509ContentType]::Cert) {
    throw 'El archivo no es un certificado X.509 público sin clave privada.'
}

$raw = [IO.File]::ReadAllBytes($resolvedPath)
$sha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($raw))
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($raw)
try {
    $hasCodeSigningUsage = $certificate.Extensions |
        Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq $codeSigningOid }
    if ($certificate.HasPrivateKey) { throw 'El archivo contiene una clave privada y no debe utilizarse.' }
    if ($sha256 -ne $expectedSha256) { throw "SHA-256 inesperado: $sha256" }
    if ($certificate.Thumbprint -ne $expectedThumbprint) { throw "Huella X.509 inesperada: $($certificate.Thumbprint)" }
    if ($certificate.Subject -ne $expectedSubject -or $certificate.Issuer -ne $expectedSubject) {
        throw "Identidad inesperada: $($certificate.Subject) / $($certificate.Issuer)"
    }
    if (-not $hasCodeSigningUsage) { throw 'El certificado no está limitado al uso de firma de código.' }
    if ($certificate.NotAfter -ne [datetime]'2028-08-21T15:54:33') {
        throw "Caducidad inesperada: $($certificate.NotAfter.ToString('O'))"
    }

    [pscustomobject]@{
        Result = 'VALID DEVELOPMENT/TEST CERTIFICATE - NOT PUBLICLY TRUSTED'
        Subject = $certificate.Subject
        SHA256 = $sha256
        X509ThumbprintSHA1 = $certificate.Thumbprint
        ValidFrom = $certificate.NotBefore
        ValidUntil = $certificate.NotAfter
        HasPrivateKey = $certificate.HasPrivateKey
    } | Format-List
}
finally {
    $certificate.Dispose()
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($raw)
}
