# Copyright (c) 2026 AJACE Holdings LLC. Licensed under the MIT License. See LICENSE in the repository root.

<#
.SYNOPSIS
    Draws the Display Toggle icon (a monitor with an on/off switch) and saves it as a multi-size .ico.
#>
param([Parameter(Mandatory = $true)][string]$Path)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($r * 2, [Math]::Min($w, $h))
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    $p
}

function New-Brush([int]$r, [int]$g, [int]$b) {
    New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, $r, $g, $b))
}

# Drawn on a 256x256 canvas and scaled down for each size.
function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.ScaleTransform($size / 256.0, $size / 256.0)

    $stand = New-Brush 0x70 0x70 0x70
    $g.FillRectangle($stand, 112, 190, 32, 30)
    $g.FillPath($stand, (New-RoundRect 64 212 128 22 11))

    $g.FillPath((New-Brush 0x2B 0x2B 0x2B), (New-RoundRect 6 26 244 172 24))

    $screenRect = New-Object System.Drawing.RectangleF 20, 40, 216, 144
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $screenRect,
        ([System.Drawing.Color]::FromArgb(255, 0x4C, 0xC2, 0xFF)),
        ([System.Drawing.Color]::FromArgb(255, 0x00, 0x5F, 0xB8)), 45.0
    $g.FillPath($grad, (New-RoundRect 20 40 216 144 12))

    # On/off switch in the middle of the screen
    $g.FillPath([System.Drawing.Brushes]::White, (New-RoundRect 58 80 140 64 32))
    $g.FillEllipse((New-Brush 0x00 0x5F 0xB8), 138, 88, 48, 48)

    $g.Dispose()
    $bmp
}

# Classic DIB icon entry (BITMAPINFOHEADER + BGRA rows bottom-up + empty AND mask).
function Get-DibBytes([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter $ms
    $maskStride = [int][Math]::Ceiling($n / 32.0) * 4
    $bw.Write([UInt32]40); $bw.Write([Int32]$n); $bw.Write([Int32]($n * 2))
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
    $bw.Write([UInt32]($n * $n * 4 + $maskStride * $n))
    $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskStride * $n)))
    $bw.Flush()
    , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = New-IconBitmap $s
    if ($s -ge 256) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        , $ms.ToArray()
    }
    else {
        , (Get-DibBytes $bmp)
    }
    $bmp.Dispose()
}

$dir = Split-Path -Parent $Path
if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($Path)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$images[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
