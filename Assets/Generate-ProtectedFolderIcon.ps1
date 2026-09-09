param(
    [string]$Output = (Join-Path $PSScriptRoot 'ProtectedFolderIcon.ico')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ProtectedAppNativeIcons
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(
        string fileName,
        int iconIndex,
        int iconWidth,
        int iconHeight,
        IntPtr[] icons,
        uint[] iconIds,
        uint iconCount,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);
}
'@

$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = [Collections.Generic.List[byte[]]]::new()
$shellIconLibrary = Join-Path $env:WINDIR 'System32\imageres.dll'

function Add-LockedBadge([Drawing.Graphics]$Graphics, [int]$Size) {
    $badgeSize = [Math]::Max(7, [int][Math]::Round($Size * 0.42))
    $inset = [Math]::Max(0, [int][Math]::Round($Size * 0.025))
    $x = $Size - $badgeSize - $inset
    $y = $Size - $badgeSize - $inset
    $brush = [Drawing.SolidBrush]::new([Drawing.Color]::FromArgb(255, 0, 122, 204))
    $font = [Drawing.Font]::new('Segoe Fluent Icons', [Math]::Max(7, $badgeSize * 0.70),
        [Drawing.FontStyle]::Regular, [Drawing.GraphicsUnit]::Pixel)
    $format = [Drawing.StringFormat]::new()
    try {
        $format.Alignment = [Drawing.StringAlignment]::Center
        $format.LineAlignment = [Drawing.StringAlignment]::Center
        $Graphics.FillEllipse($brush, [Drawing.Rectangle]::new($x, $y, $badgeSize, $badgeSize))
        $Graphics.DrawString([string][char]0xE72E, $font, [Drawing.Brushes]::White,
            [Drawing.RectangleF]::new($x, $y - ($badgeSize * 0.02), $badgeSize, $badgeSize), $format)
    }
    finally {
        $format.Dispose()
        $font.Dispose()
        $brush.Dispose()
    }
}

try {
    foreach ($size in $sizes) {
        $handles = New-Object IntPtr[] 1
        $iconIds = New-Object uint32[] 1
        if ([ProtectedAppNativeIcons]::PrivateExtractIcons($shellIconLibrary, 3, $size, $size, $handles, $iconIds, 1, 0) -ne 1 -or $handles[0] -eq [IntPtr]::Zero) {
            throw "No se pudo extraer el icono nativo de carpeta de Windows para $size px."
        }

        $folderIcon = $null
        $folderBitmap = $null
        $bitmap = $null
        $graphics = $null
        try {
            $folderIcon = [Drawing.Icon]::FromHandle($handles[0])
            $folderBitmap = $folderIcon.ToBitmap()
            $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($folderBitmap, [Drawing.Rectangle]::new(0, 0, $size, $size))

            Add-LockedBadge $graphics $size

            $stream = [IO.MemoryStream]::new()
            try {
                $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                $frames.Add($stream.ToArray())
            }
            finally { $stream.Dispose() }

            if ($size -eq 256) {
                $previewPath = [IO.Path]::ChangeExtension([IO.Path]::GetFullPath($Output), '.png')
                $bitmap.Save($previewPath, [Drawing.Imaging.ImageFormat]::Png)
            }
        }
        finally {
            if ($graphics) { $graphics.Dispose() }
            if ($bitmap) { $bitmap.Dispose() }
            if ($folderBitmap) { $folderBitmap.Dispose() }
            if ($folderIcon) { $folderIcon.Dispose() }
            [void][ProtectedAppNativeIcons]::DestroyIcon($handles[0])
        }
    }
}
finally { }

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
finally {
    $writer.Dispose()
    $file.Dispose()
}

Write-Output $outputPath
