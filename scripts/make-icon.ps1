# Generates assets\icon.ico (multi-size, PNG-compressed) and assets\icon-256.png for the app and installer.
# Pure System.Drawing: no external tools needed. Re-run any time you change the artwork below.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\assets'))
Add-Type -AssemblyName System.Drawing
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

function New-Frame([int]$size) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
  $g.Clear([System.Drawing.Color]::Transparent)
  $s = [single]$size
  $r = $s * 0.24

  # rounded tile
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $m = $s * 0.03; $d = $r * 2; $w = $s - 2 * $m
  $path.AddArc($m, $m, $d, $d, 180, 90); $path.AddArc($m + $w - $d, $m, $d, $d, 270, 90)
  $path.AddArc($m + $w - $d, $m + $w - $d, $d, $d, 0, 90); $path.AddArc($m, $m + $w - $d, $d, $d, 90, 90); $path.CloseFigure()
  $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 20, 18, 46)), ([System.Drawing.Color]::FromArgb(255, 9, 10, 22))
  $g.FillPath($bg, $path)
  $pen = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 0)), ([System.Drawing.PointF]::new($s, $s)), ([System.Drawing.Color]::FromArgb(255, 139, 123, 255)), ([System.Drawing.Color]::FromArgb(255, 53, 214, 196))
  $p = New-Object System.Drawing.Pen $pen, ([single][Math]::Max(1.2, $s * 0.03))
  $g.DrawPath($p, $path)

  # two "brain" lobes + bolt
  $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new($s * 0.2, $s * 0.2)), ([System.Drawing.PointF]::new($s * 0.8, $s * 0.8)), ([System.Drawing.Color]::FromArgb(255, 139, 123, 255)), ([System.Drawing.Color]::FromArgb(255, 53, 214, 196))
  $lw = $s * 0.27; $lh = $s * 0.5; $top = $s * 0.27
  $g.FillEllipse($grad, $s * 0.5 - $lw - $s * 0.015, $top, $lw, $lh)
  $alpha = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new($s * 0.2, $s * 0.2)), ([System.Drawing.PointF]::new($s * 0.8, $s * 0.8)), ([System.Drawing.Color]::FromArgb(200, 139, 123, 255)), ([System.Drawing.Color]::FromArgb(200, 53, 214, 196))
  $g.FillEllipse($alpha, $s * 0.5 + $s * 0.015, $top, $lw, $lh)
  $bolt = [System.Drawing.PointF[]]@(
    [System.Drawing.PointF]::new($s * 0.54, $s * 0.3), [System.Drawing.PointF]::new($s * 0.4, $s * 0.54), [System.Drawing.PointF]::new($s * 0.495, $s * 0.54),
    [System.Drawing.PointF]::new($s * 0.455, $s * 0.72), [System.Drawing.PointF]::new($s * 0.62, $s * 0.46), [System.Drawing.PointF]::new($s * 0.52, $s * 0.46))
  $g.FillPolygon((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 10, 11, 24))), $bolt)
  $g.Dispose()
  return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
  $bmp = New-Frame $sz
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  if ($sz -eq 256) { $bmp.Save((Join-Path $OutDir 'icon-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
  $bmp.Dispose()
  , @($sz, $ms.ToArray())
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$pngs.Count)
$offset = 6 + 16 * $pngs.Count
foreach ($e in $pngs) {
  $sz = $e[0]; $bytes = $e[1]
  $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz }))); $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([uint16]1); $bw.Write([uint16]32)
  $bw.Write([uint32]$bytes.Length); $bw.Write([uint32]$offset)
  $offset += $bytes.Length
}
foreach ($e in $pngs) { $bw.Write($e[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'icon.ico'), $out.ToArray())
Write-Host "Wrote $OutDir\icon.ico ($($out.Length) bytes) and icon-256.png"
