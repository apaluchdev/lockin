<#
.SYNOPSIS
  Renders the LockIn icon (gold padlock on a dark rounded square) to
  src/LockIn.App/Assets/lockin.ico (16-256 px) and lockin.png (256 px).
  Run with Windows PowerShell 5.1 (uses System.Drawing).
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\src\LockIn.App\Assets'
New-Item -ItemType Directory -Force $assets | Out-Null

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    $gold = [System.Drawing.Color]::FromArgb(255, 200, 170, 110)
    $goldLight = [System.Drawing.Color]::FromArgb(255, 240, 214, 140)
    $dark = [System.Drawing.Color]::FromArgb(255, 11, 17, 24)

    # Background tile
    $bg = New-RoundedRect 4 4 248 248 52
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)), (New-Object System.Drawing.PointF(0, 256)),
        [System.Drawing.Color]::FromArgb(255, 30, 44, 60), $dark)
    $g.FillPath($bgBrush, $bg)
    if ($size -ge 32) {
        $ring = New-Object System.Drawing.Pen($gold, 8)
        $g.DrawPath($ring, $bg)
    }

    # Shackle
    $shackle = New-Object System.Drawing.Pen($gold, 24)
    $shackle.StartCap = 'Flat'; $shackle.EndCap = 'Flat'
    $g.DrawArc($shackle, 82, 44, 92, 100, 180, 180)
    $g.DrawLine($shackle, 82, 94, 82, 124)
    $g.DrawLine($shackle, 174, 94, 174, 124)

    # Body
    $body = New-RoundedRect 52 112 152 104 20
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 112)), (New-Object System.Drawing.PointF(0, 216)), $goldLight, $gold)
    $g.FillPath($bodyBrush, $body)

    # Keyhole
    $hole = New-Object System.Drawing.SolidBrush($dark)
    $g.FillEllipse($hole, 112, 136, 32, 32)
    $g.FillRectangle($hole, 120, 156, 16, 36)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = foreach ($size in $sizes) {
    $bmp = Render $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) { $bmp.Save((Join-Path $assets 'lockin.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed frames (supported since Windows Vista).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($out)
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $assets 'lockin.ico'), $out.ToArray())
Write-Host "Wrote $(Join-Path $assets 'lockin.ico')"
