# Generates assets/app.ico: a disk platter snapped in two like a cookie, with a jagged break,
# the halves pulled apart and a few crumbs below. Drawn once on a 256x256 canvas and scaled to
# every icon size. Run with Windows PowerShell:  powershell -File assets/make-icon.ps1 [-Preview x.png]
param([string]$Preview)
Add-Type -AssemblyName System.Drawing

$sizes = 16, 24, 32, 48, 64, 128, 256
$out = Join-Path $PSScriptRoot 'app.ico'

function C([int]$r, [int]$g, [int]$b, [int]$a = 255) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function P([float]$x, [float]$y) { New-Object System.Drawing.PointF $x, $y }

# The break: a jagged line from top to bottom through the platter.
$crack = @(
    (P 120 6), (P 131 30), (P 116 48), (P 134 66), (P 118 86), (P 137 104),
    (P 121 124), (P 140 142), (P 124 162), (P 141 182), (P 127 204), (P 136 226)
)
$cx = 128; $cy = 116; $radius = 102

function Region-Path([bool]$left) {
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $edge = if ($left) { -40 } else { 296 }
    $points = @((P $edge -40)) + $crack + @((P $edge 300))
    $path.AddPolygon([System.Drawing.PointF[]]$points)
    return $path
}

function Draw-Platter($g) {
    # Rim and platter.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 0x17 0x24 0x38)), $cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius)
    $inner = $radius - 9
    $rect = New-Object System.Drawing.RectangleF ($cx - $inner), ($cy - $inner), (2 * $inner), (2 * $inner)
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, (C 0x6F 0xB1 0xFF), (C 0x1D 0x4E 0xD8), 55
    $g.FillEllipse($grad, $rect)

    # Tracks.
    foreach ($r in 78, 64, 50) {
        $pen = New-Object System.Drawing.Pen (C 255 255 255 70), 2.2
        $g.DrawEllipse($pen, $cx - $r, $cy - $r, 2 * $r, 2 * $r)
    }

    # A glint, like light on a platter.
    $glint = New-Object System.Drawing.Drawing2D.GraphicsPath
    $glint.AddPie(($cx - $inner), ($cy - $inner), (2 * $inner), (2 * $inner), 200, 40)
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 255 255 55)), $glint)

    # Hub.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 0xE6 0xEC 0xF4)), $cx - 26, $cy - 26, 52, 52)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 0x17 0x24 0x38)), $cx - 10, $cy - 10, 20, 20)
}

function Draw-Piece($g, [bool]$left, [float]$dx, [float]$dy, [float]$angle) {
    # Each half pivots outwards around the bottom of the break, so the gap opens like a V,
    # the way a cookie snaps.
    $px = 130; $py = 208
    $state = $g.Save()
    $g.TranslateTransform($px + $dx, $py + $dy)
    $g.RotateTransform($angle)
    $g.TranslateTransform(-$px, -$py)

    $region = Region-Path $left
    $g.SetClip($region)
    Draw-Platter $g

    # The broken edge shows the lighter inside of the disk, like the inside of a cookie.
    $disk = New-Object System.Drawing.Drawing2D.GraphicsPath
    $disk.AddEllipse($cx - $radius, $cy - $radius, 2 * $radius, 2 * $radius)
    $clip = New-Object System.Drawing.Region $region
    $clip.Intersect($disk)
    $g.Clip = $clip
    $edgePen = New-Object System.Drawing.Pen (C 0xC7 0xDE 0xFF), 7
    $edgePen.LineJoin = 'Miter'
    $g.DrawLines($edgePen, [System.Drawing.PointF[]]$crack)
    $g.Restore($state)
}

function Draw-Crumb($g, [float[]]$xy, $color) {
    $pts = for ($i = 0; $i -lt $xy.Length; $i += 2) { P $xy[$i] $xy[$i + 1] }
    $g.FillPolygon((New-Object System.Drawing.SolidBrush $color), [System.Drawing.PointF[]]$pts)
    $g.DrawPolygon((New-Object System.Drawing.Pen (C 0x17 0x24 0x38), 2), [System.Drawing.PointF[]]$pts)
}

function Draw-Logo($g) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Soft shadow under the pieces.
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (C 0 0 0 45)), 30, 226, 196, 20)

    Draw-Piece $g $true -10 0 -11
    Draw-Piece $g $false 10 0 11

    # Crumbs that fell out of the break.
    Draw-Crumb $g @(110, 226, 124, 218, 131, 232, 117, 239) (C 0x3B 0x82 0xF6)
    Draw-Crumb $g @(136, 230, 149, 224, 152, 238, 140, 241) (C 0x1D 0x4E 0xD8)
    Draw-Crumb $g @(92, 236, 101, 230, 106, 241) (C 0x6F 0xB1 0xFF)
    Draw-Crumb $g @(158, 238, 167, 233, 170, 244, 161, 246) (C 0xC7 0xDE 0xFF)
    Draw-Crumb $g @(125, 243, 132, 240, 134, 248, 127, 249) (C 0x6F 0xB1 0xFF)
}

$images = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($s / 256.0, $s / 256.0)
    Draw-Logo $g
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    if ($s -ge 256) {
        # Large frame: PNG, as Windows expects for 256 px.
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        if ($Preview) { $bmp.Save($Preview, [System.Drawing.Imaging.ImageFormat]::Png) }
    }
    else {
        # Small frames: classic 32-bit DIB (bottom-up BGRA + AND mask) so every tool can read them.
        $bw = New-Object System.IO.BinaryWriter $ms
        $bw.Write([UInt32]40); $bw.Write([Int32]$s); $bw.Write([Int32]($s * 2))
        $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]0)
        $bw.Write([UInt32]($s * $s * 4)); $bw.Write([Int32]0); $bw.Write([Int32]0); $bw.Write([UInt32]0); $bw.Write([UInt32]0)
        for ($y = $s - 1; $y -ge 0; $y--) {
            for ($x = 0; $x -lt $s; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
            }
        }
        $maskStride = [Math]::Ceiling($s / 32) * 4
        $bw.Write((New-Object byte[] ($maskStride * $s)))
        $bw.Flush()
    }

    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Bytes = $ms.ToArray() }
}

# ICO container.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$images.Count)
$offset = 6 + 16 * $images.Count
foreach ($img in $images) {
    $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$img.Bytes.Length); $w.Write([UInt32]$offset)
    $offset += $img.Bytes.Length
}
foreach ($img in $images) { $w.Write($img.Bytes) }
$w.Close()
"Wrote $out"
