# Draws the app icon (src\NewUOAM.App\Assets\app.ico + docs\img\icon.png): a dark rounded tile
# with a map diamond (the app's 45-degree view) and the red player square. Re-run only to change
# the design; the outputs are committed. ASCII only (PowerShell 5.1).
Add-Type -AssemblyName System.Drawing
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function New-IconBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0

    # rounded background
    $r = 48 * $s
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($size - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($size - 2 * $r - 1, $size - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $size - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 34, 40))
    $g.FillPath($bg, $path)

    # map diamond: water, land, a road
    $c = $size / 2.0
    $d = 96 * $s
    $diamond = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF $c, ($c - $d)),
        (New-Object System.Drawing.PointF ($c + $d), $c),
        (New-Object System.Drawing.PointF $c, ($c + $d)),
        (New-Object System.Drawing.PointF ($c - $d), $c))
    $water = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 38, 84, 140))
    $g.FillPolygon($water, $diamond)
    $land = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($c - 10 * $s), ($c - $d + 10 * $s)),
        (New-Object System.Drawing.PointF ($c + $d - 6 * $s), ($c - 4 * $s)),
        (New-Object System.Drawing.PointF ($c + 18 * $s), ($c + 70 * $s)),
        (New-Object System.Drawing.PointF ($c - 40 * $s), ($c + 30 * $s)),
        (New-Object System.Drawing.PointF ($c - $d + 14 * $s), ($c - 6 * $s)))
    $g.SetClip($path)
    $landBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 86, 132, 64))
    $g.FillPolygon($landBrush, $land)
    $road = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 176, 150, 102)), ([Math]::Max(1.5, 7 * $s))
    $g.DrawLine($road, ($c - 50 * $s), ($c - 10 * $s), ($c + 40 * $s), ($c + 20 * $s))
    $edge = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 200, 210, 220)), ([Math]::Max(1, 6 * $s))
    $g.DrawPolygon($edge, $diamond)

    # player: red square with a light outline
    $p = [Math]::Max(3, 34 * $s)
    $o = [Math]::Max(1, 5 * $s)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillRectangle($white, ($c - $p / 2 - $o), ($c - $p / 2 - $o), ($p + 2 * $o), ($p + 2 * $o))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 230, 32, 32))
    $g.FillRectangle($red, ($c - $p / 2), ($c - $p / 2), $p, $p)
    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($size in $sizes) {
    $bmp = New-IconBitmap $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($size -eq 256) {
        $siteImg = Join-Path $root 'docs\img'
        New-Item -ItemType Directory -Force $siteImg | Out-Null
        $bmp.Save((Join-Path $siteImg 'icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (Vista+).
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$pngs[$i].Length); $w.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $root 'src\NewUOAM.App\Assets\app.ico'), $out.ToArray())
Write-Host 'Wrote src\NewUOAM.App\Assets\app.ico and docs\img\icon.png'
