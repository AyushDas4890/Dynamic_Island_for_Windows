# Generates Assets\island.ico: a gradient pill (near-black to plum) with a green dot.
# Multi-size ICO with PNG frames (16, 24, 32, 48, 64, 256).
param([string]$Out = (Join-Path $PSScriptRoot "..\Assets\island.ico"))
Add-Type -AssemblyName System.Drawing

function Frame([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $h = [Math]::Max(6, [int]($s * 0.42)); $y = [int](($s - $h) / 2); $pad = [Math]::Max(1, [int]($s * 0.04))
    $rect = New-Object System.Drawing.RectangleF $pad, $y, ($s - 2 * $pad), $h
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 15, 17, 21)), ([System.Drawing.Color]::FromArgb(255, 75, 31, 77)), 0.0
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = $h
    $path.AddArc($rect.X, $rect.Y, $r, $r, 90, 180)
    $path.AddArc($rect.Right - $r, $rect.Y, $r, $r, 270, 180)
    $path.CloseFigure()
    $g.FillPath($brush, $path)
    $d = [Math]::Max(2, [int]($h * 0.34))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 48, 209, 88))), ($rect.Right - $h / 2 - $d / 2 - $pad), ($y + ($h - $d) / 2), $d, $d)
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    return ,$ms.ToArray()
}

$sizes = 16, 24, 32, 48, 64, 256
$frames = $sizes | ForEach-Object { ,(Frame $_) }
$fs = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $frames[$i].Length
    $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $w.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset); $offset += $len
}
foreach ($f in $frames) { $w.Write($f) }
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[System.IO.File]::WriteAllBytes($Out, $fs.ToArray())
"wrote $Out ($($fs.Length) bytes)"
