param(
    [string]$Source = (Join-Path $PSScriptRoot 'BrandShield.png'),
    [string]$Output = (Join-Path $PSScriptRoot 'VaultFile.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
$shield = [Drawing.Image]::FromFile([IO.Path]::GetFullPath($Source))

function Draw-VaultDrive([Drawing.Graphics]$Graphics, [int]$Size) {
    # High-resolution vector artwork: a real storage device with depth,
    # front panel, bay line and status LEDs. Unlike a shell icon, every ICO
    # frame is rendered at its own native size.
    $left = $Size * 0.09; $right = $Size * 0.91
    $top = $Size * 0.29; $bottom = $Size * 0.75
    $front = [Drawing.RectangleF]::new($left, $top, $right - $left, $bottom - $top)
    $shadow = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(65, 0, 0, 0))
    $body = [Drawing.Drawing2D.LinearGradientBrush]::new($front,
        [Drawing.Color]::FromArgb(255, 224, 230, 238), [Drawing.Color]::FromArgb(255, 91, 104, 119), 90)
    $outline = [Drawing.Pen]::new([Drawing.Color]::FromArgb(255, 50, 62, 74), [Math]::Max(1, $Size * 0.018))
    $panel = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(205, 51, 65, 78))
    $slotPen = [Drawing.Pen]::new([Drawing.Color]::FromArgb(225, 25, 35, 45), [Math]::Max(1, $Size * 0.025))
    $highlight = [Drawing.Pen]::new([Drawing.Color]::FromArgb(180, 255, 255, 255), [Math]::Max(1, $Size * 0.012))
    $green = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 78, 220, 116))
    try {
        $Graphics.FillRectangle($shadow, $left, $top + ($Size * 0.045), $right - $left, $bottom - $top)
        $Graphics.FillRectangle($body, $front)
        $Graphics.DrawRectangle($outline, $left, $top, $right - $left, $bottom - $top)

        $topFace = [Drawing.PointF[]]@(
            [Drawing.PointF]::new($left, $top), [Drawing.PointF]::new($left + ($Size * 0.11), $Size * 0.19),
            [Drawing.PointF]::new($right - ($Size * 0.11), $Size * 0.19), [Drawing.PointF]::new($right, $top)
        )
        $topBrush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 244, 247, 251))
        try { $Graphics.FillPolygon($topBrush, $topFace); $Graphics.DrawPolygon($outline, $topFace) }
        finally { $topBrush.Dispose() }

        $Graphics.FillRectangle($panel, $left + ($Size * 0.055), $Size * 0.50, ($right - $left) - ($Size * 0.11), $Size * 0.15)
        $Graphics.DrawLine($slotPen, $left + ($Size * 0.12), $Size * 0.57, $left + ($Size * 0.53), $Size * 0.57)
        $Graphics.DrawLine($highlight, $left + ($Size * 0.08), $top + ($Size * 0.04), $right - ($Size * 0.08), $top + ($Size * 0.04))
        # Keep the status LED in the unobstructed left side of the front panel.
        $Graphics.FillEllipse($green, $left + ($Size * 0.10), $Size * 0.535, $Size * 0.06, $Size * 0.06)
    }
    finally {
        $green.Dispose(); $highlight.Dispose(); $slotPen.Dispose(); $panel.Dispose(); $outline.Dispose(); $body.Dispose(); $shadow.Dispose()
    }
}

try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            Draw-VaultDrive $graphics $size

            $shieldSize = $size * 0.47
            $shieldX = $size - $shieldSize - ($size * 0.03)
            $shieldY = $size - $shieldSize - ($size * 0.03)
            $graphics.DrawImage($shield, [Drawing.RectangleF]::new($shieldX, $shieldY, $shieldSize, $shieldSize))

            $stream = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            }
            finally { $stream.Dispose() }
        }
        finally {
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $shield.Dispose() }

$outputPath = [IO.Path]::GetFullPath($Output)
$file = [IO.File]::Open($outputPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
$writer = [IO.BinaryWriter]::new($file)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$frames.Count)
    $offset = 6 + (16 * $frames.Count)
    for ($index = 0; $index -lt $frames.Count; $index++) {
        $size = $sizes[$index]
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally { $writer.Dispose(); $file.Dispose() }

Write-Output $outputPath
