param(
    [string]$OutputDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Draw-FluentShield([Drawing.Graphics]$Graphics, [single]$Size, [single]$X, [single]$Y) {
    $shield = [Drawing.Drawing2D.GraphicsPath]::new()
    $shield.AddBezier($X + ($Size * .50), $Y + ($Size * .04), $X + ($Size * .61), $Y + ($Size * .08), $X + ($Size * .74), $Y + ($Size * .10), $X + ($Size * .84), $Y + ($Size * .17))
    $shield.AddLine($X + ($Size * .84), $Y + ($Size * .17), $X + ($Size * .84), $Y + ($Size * .48))
    $shield.AddBezier($X + ($Size * .84), $Y + ($Size * .48), $X + ($Size * .84), $Y + ($Size * .68), $X + ($Size * .69), $Y + ($Size * .84), $X + ($Size * .50), $Y + ($Size * .96))
    $shield.AddBezier($X + ($Size * .50), $Y + ($Size * .96), $X + ($Size * .31), $Y + ($Size * .84), $X + ($Size * .16), $Y + ($Size * .68), $X + ($Size * .16), $Y + ($Size * .48))
    $shield.AddLine($X + ($Size * .16), $Y + ($Size * .48), $X + ($Size * .16), $Y + ($Size * .17))
    $shield.AddBezier($X + ($Size * .16), $Y + ($Size * .17), $X + ($Size * .26), $Y + ($Size * .10), $X + ($Size * .39), $Y + ($Size * .08), $X + ($Size * .50), $Y + ($Size * .04))
    $shield.CloseFigure()

    $fill = [Drawing.Drawing2D.LinearGradientBrush]::new(
        [Drawing.RectangleF]::new($X, $Y, $Size, $Size),
        [Drawing.Color]::FromArgb(255, 28, 151, 234), [Drawing.Color]::FromArgb(255, 0, 78, 145), 90)
    $edge = [Drawing.Pen]::new([Drawing.Color]::FromArgb(225, 118, 204, 255), [Math]::Max(1, $Size * .028))
    $body = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(245, 255, 255, 255))
    $line = [Drawing.Pen]::new([Drawing.Color]::FromArgb(245, 255, 255, 255), [Math]::Max(1.5, $Size * .075))
    try {
        $Graphics.FillPath($fill, $shield)
        $Graphics.DrawPath($edge, $shield)

        $lockWidth = $Size * .38; $lockHeight = $Size * .28
        $lockX = $X + (($Size - $lockWidth) / 2); $lockY = $Y + ($Size * .43)
        $Graphics.DrawArc($line, $lockX + ($lockWidth * .18), $lockY - ($Size * .20), $lockWidth * .64, $Size * .31, 180, 180)
        $Graphics.FillRectangle($body, $lockX, $lockY, $lockWidth, $lockHeight)
        $keyhole = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 0, 86, 153))
        try {
            $Graphics.FillEllipse($keyhole, $lockX + ($lockWidth * .40), $lockY + ($lockHeight * .26), $lockWidth * .20, $lockWidth * .20)
            $Graphics.FillRectangle($keyhole, $lockX + ($lockWidth * .46), $lockY + ($lockHeight * .42), $lockWidth * .08, $lockHeight * .26)
        }
        finally { $keyhole.Dispose() }
    }
    finally { $line.Dispose(); $body.Dispose(); $edge.Dispose(); $fill.Dispose(); $shield.Dispose() }
}

function New-BrandBitmap([int]$Size) {
    $bitmap = [Drawing.Bitmap]::new($Size, $Size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.Clear([Drawing.Color]::Transparent)
        Draw-FluentShield $graphics ($Size * .90) ($Size * .05) ($Size * .02)
        return $bitmap
    }
    finally { $graphics.Dispose() }
}

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
foreach ($size in $sizes) {
    $bitmap = New-BrandBitmap $size
    try {
        $stream = [IO.MemoryStream]::new()
        try { $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png); $frames.Add($stream.ToArray()) }
        finally { $stream.Dispose() }
    }
    finally { $bitmap.Dispose() }
}

$brandPng = Join-Path $OutputDirectory 'BrandShield.png'
$large = New-BrandBitmap 512
try { $large.Save($brandPng, [Drawing.Imaging.ImageFormat]::Png) }
finally { $large.Dispose() }

$iconPath = Join-Path $OutputDirectory 'ProtectedApp.ico'
$file = [IO.File]::Open($iconPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size })); $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length); $writer.Write([uint32]$offset); $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally { $writer.Dispose(); $file.Dispose() }

function Save-Wizard([string]$Path, [int]$Width, [int]$Height, [int]$LogoSize) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height, [Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(31, 35, 45))
        $logo = New-BrandBitmap $LogoSize
        try { $graphics.DrawImage($logo, ($Width - $LogoSize) / 2, ($Height - $LogoSize) / 2, $LogoSize, $LogoSize) }
        finally { $logo.Dispose() }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Bmp)
    }
    finally { $graphics.Dispose(); $bitmap.Dispose() }
}

Save-Wizard (Join-Path $OutputDirectory 'WizardImage.bmp') 328 628 248
Save-Wizard (Join-Path $OutputDirectory 'WizardSmallImage.bmp') 110 110 84

Write-Output $brandPng
Write-Output $iconPath
