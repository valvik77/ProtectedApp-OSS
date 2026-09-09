function Resolve-ProtectedAppSignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $command) { return $command.Source }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -LiteralPath $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
    if ($null -eq $candidate) {
        throw 'No se encontró signtool.exe. Instala el Windows SDK con las herramientas de firma.'
    }
    return $candidate.FullName
}

function Get-ProtectedAppSigningCertificate {
    param(
        [Parameter(Mandatory)] [string] $Thumbprint,
        [ValidateSet('CurrentUser', 'LocalMachine')] [string] $StoreLocation = 'CurrentUser',
        [switch] $AllowDevelopmentCertificate
    )

    $normalizedThumbprint = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalizedThumbprint.Length -ne 40) {
        throw 'La huella del certificado de firma debe contener 40 caracteres hexadecimales SHA-1.'
    }
    $certificatePath = "Cert:\$StoreLocation\My\$normalizedThumbprint"
    $certificate = Get-Item -LiteralPath $certificatePath -ErrorAction SilentlyContinue
    if ($null -eq $certificate) {
        throw "No se encontró el certificado $normalizedThumbprint en $StoreLocation\My."
    }
    if (-not $certificate.HasPrivateKey) {
        throw 'El certificado seleccionado no dispone de una clave privada accesible.'
    }
    $codeSigningOid = '1.3.6.1.5.5.7.3.3'
    $supportsCodeSigning = $certificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        ForEach-Object { $_.EnhancedKeyUsages } |
        Where-Object { $_.Value -eq $codeSigningOid }
    if ($null -eq $supportsCodeSigning) {
        throw 'El certificado no incluye el uso mejorado Code Signing.'
    }
    $now = Get-Date
    if ($certificate.NotBefore -gt $now -or $certificate.NotAfter -le $now) {
        throw "El certificado no es válido en este momento ($($certificate.NotBefore)-$($certificate.NotAfter))."
    }
    if (-not $AllowDevelopmentCertificate -and $certificate.Subject -eq $certificate.Issuer) {
        throw 'Los certificados autofirmados solo se admiten con -AllowDevelopmentCertificate y nunca deben publicarse como una versión de producción.'
    }
    return $certificate
}

function Test-ProtectedAppAuthenticodeSignature {
    param(
        [Parameter(Mandatory)] [string] $SignToolPath,
        [Parameter(Mandatory)] [string] $Path,
        [string] $ExpectedThumbprint
    )

    & $SignToolPath verify /pa /all /tw /q $Path
    if ($LASTEXITCODE -ne 0) {
        throw "La firma Authenticode o su sello de tiempo no son válidos: $Path"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
        throw "Windows no confía en la firma de '$Path': $($signature.StatusMessage)"
    }
    if ($null -eq $signature.TimeStamperCertificate) {
        throw "El archivo no contiene un sello de tiempo verificable: $Path"
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedThumbprint) -and
        $signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint) {
        throw "El firmante de '$Path' no coincide con el certificado seleccionado."
    }
    return $signature
}

function Invoke-ProtectedAppCodeSigning {
    param(
        [Parameter(Mandatory)] [ValidateSet('CertificateStore', 'ArtifactSigning')] [string] $Mode,
        [Parameter(Mandatory)] [string] $SignToolPath,
        [Parameter(Mandatory)] [string[]] $Paths,
        [Parameter(Mandatory)] [string] $TimestampUrl,
        [string] $CertificateThumbprint,
        [ValidateSet('CurrentUser', 'LocalMachine')] [string] $CertificateStoreLocation = 'CurrentUser',
        [string] $ArtifactSigningDlibPath,
        [string] $ArtifactSigningMetadataPath
    )

    foreach ($path in $Paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "No se puede firmar porque falta el archivo: $path"
        }
        $arguments = @('sign', '/v', '/fd', 'SHA256', '/tr', $TimestampUrl, '/td', 'SHA256', '/d', 'ProtectedApp')
        if ($Mode -eq 'CertificateStore') {
            $arguments += @('/sha1', $CertificateThumbprint, '/s', 'My')
            if ($CertificateStoreLocation -eq 'LocalMachine') { $arguments += '/sm' }
        }
        else {
            $arguments += @('/dlib', $ArtifactSigningDlibPath, '/dmdf', $ArtifactSigningMetadataPath)
        }
        $arguments += $path
        & $SignToolPath @arguments
        if ($LASTEXITCODE -ne 0) { throw "SignTool no pudo firmar: $path" }
        $expectedThumbprint = if ($Mode -eq 'CertificateStore') { $CertificateThumbprint } else { $null }
        $null = Test-ProtectedAppAuthenticodeSignature -SignToolPath $SignToolPath -Path $path -ExpectedThumbprint $expectedThumbprint
    }
}
