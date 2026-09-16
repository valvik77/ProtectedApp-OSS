param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

$trackedFiles = @(& git -C $projectRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo enumerar los archivos controlados por Git para la auditoría.'
}

$forbiddenPathPattern = '(?i)(^|/)(secrets?/|\.env(?:\.|$)|[^/]+\.(secret|pfx|p12|pem|key|snk|cer|crt)$)'
$allowedPublicCertificatePath = 'Signing/Public/ProtectedApp-Development-Test.cer'
$allowedPublicCertificateSha256 = 'AFD8A0ADD54130355D878CFEC7E3590119A9FCB536FDD45EC8DC797F82E65C39'
$allowedPublicCertificateThumbprint = '4AD1F2E988F4EFD130DF66A02442B470927B433C'
$privateKeyPattern = '-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----'
$tokenPatterns = @(
    'gh[pousr]_[A-Za-z0-9_]{20,}',
    'github_pat_[A-Za-z0-9_]{20,}',
    'AKIA[0-9A-Z]{16}'
)
$textExtensions = @('.cs', '.csproj', '.json', '.md', '.ps1', '.props', '.sln', '.targets', '.txt', '.xaml', '.xml', '.yml', '.yaml', '.iss')
$findings = [Collections.Generic.List[string]]::new()

foreach ($relativePath in $trackedFiles) {
    $normalizedPath = $relativePath.Replace('\', '/')
    # .env.example is the sole tracked configuration template allowed by .gitignore.
    $isAllowedExample = $normalizedPath -eq '.env.example'
    $isAllowedPublicCertificate = $normalizedPath -eq $allowedPublicCertificatePath
    if (-not $isAllowedExample -and -not $isAllowedPublicCertificate -and
        $normalizedPath -match $forbiddenPathPattern) {
        $findings.Add("Archivo privado o secreto controlado por Git: $normalizedPath")
        continue
    }

    if ($isAllowedPublicCertificate) {
        $fullPath = Join-Path $projectRoot $relativePath
        try {
            if ([Security.Cryptography.X509Certificates.X509Certificate2]::GetCertContentType($fullPath) -ne
                [Security.Cryptography.X509Certificates.X509ContentType]::Cert) {
                throw 'El archivo no contiene exclusivamente un certificado X.509 público.'
            }
            $rawCertificate = [IO.File]::ReadAllBytes($fullPath)
            $certificateHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($rawCertificate))
            $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($rawCertificate)
            try {
                $codeSigningOid = '1.3.6.1.5.5.7.3.3'
                $hasCodeSigningUsage = $certificate.Extensions |
                    Where-Object { $_ -is [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
                    ForEach-Object { $_.EnhancedKeyUsages } |
                    Where-Object { $_.Value -eq $codeSigningOid }
                if ($certificate.HasPrivateKey -or
                    $certificateHash -ne $allowedPublicCertificateSha256 -or
                    $certificate.Thumbprint -ne $allowedPublicCertificateThumbprint -or
                    $certificate.Subject -ne 'CN=Valvik ProtectedApp Development' -or
                    $certificate.Issuer -ne $certificate.Subject -or
                    -not $hasCodeSigningUsage) {
                    throw 'La identidad, finalidad o huella no coincide con el certificado público aprobado.'
                }
            }
            finally { $certificate.Dispose() }
        }
        catch {
            $findings.Add("Certificado público de prueba no válido: $($_.Exception.Message)")
        }
        continue
    }

    if ([IO.Path]::GetExtension($normalizedPath).ToLowerInvariant() -notin $textExtensions) {
        continue
    }

    $fullPath = Join-Path $projectRoot $relativePath
    $content = [IO.File]::ReadAllText($fullPath)
    if ($content -match $privateKeyPattern) {
        $findings.Add("Material de clave privada detectado en: $normalizedPath")
    }
    foreach ($pattern in $tokenPatterns) {
        if ($content -match $pattern) {
            $findings.Add("Posible token de acceso detectado en: $normalizedPath")
            break
        }
    }
}

if ($allowedPublicCertificatePath -notin $trackedFiles) {
    $findings.Add("Falta el certificado público de prueba controlado: $allowedPublicCertificatePath")
}

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw 'La auditoría de seguridad del repositorio detectó material que no debe publicarse.'
}

Write-Host 'Repository safety audit passed.'
