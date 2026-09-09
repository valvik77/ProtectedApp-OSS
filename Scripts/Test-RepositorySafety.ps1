param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot

$trackedFiles = @(& git -C $projectRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'No se pudo enumerar los archivos controlados por Git para la auditoría.'
}

$forbiddenPathPattern = '(?i)(^|/)(secrets?/|\.env(?:\.|$)|[^/]+\.(pfx|p12|pem|key|snk|cer|crt)$)'
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
    if ($normalizedPath -match $forbiddenPathPattern) {
        $findings.Add("Archivo privado o secreto controlado por Git: $normalizedPath")
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

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw 'La auditoría de seguridad del repositorio detectó material que no debe publicarse.'
}

Write-Host 'Repository safety audit passed.'
