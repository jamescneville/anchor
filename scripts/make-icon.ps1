# Renders the Anchor app icon into a multi-size .ico (PNG-compressed entries) plus a preview PNG.
# Usage: make-icon.ps1 [-Variant A|B] [-Out path.ico] [-PreviewBoth]
param(
    [ValidateSet('A', 'B')][string]$Variant = 'A',
    [string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Anchor.Desktop\Assets\anchor.ico'),
    [switch]$PreviewBoth
)
Add-Type -AssemblyName System.Drawing

function Col([string]$hex, [int]$a = 255) { $c = [System.Drawing.ColorTranslator]::FromHtml($hex); [System.Drawing.Color]::FromArgb($a, $c.R, $c.G, $c.B) }

function RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath; $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90); $p.CloseFigure(); return $p
}

# The anchor, drawn on a 256-unit canvas through $g (already scaled). $pen is the stroke style.
function DrawAnchor($g, $pen, $fill, [float]$grow = 0) {
    $w = $pen.Width
    $g.DrawEllipse($pen, 128 - 21, 58 - 21, 42, 42)                                   # ring
    $g.DrawLine($pen, 128, 80, 128, 196)                                               # shaft
    $g.DrawLine($pen, 92, 112, 164, 112)                                               # stock
    $g.DrawArc($pen, 54, 90, 148, 120, 8, 164)                                         # curved arms
    foreach ($sx in 1, -1) {                                                           # pointed flukes
        $tipx = 128 + $sx * 76; $tipy = 156
        $pts = @(
            (New-Object System.Drawing.PointF ($tipx + $sx * 4), ($tipy - 30 - $grow)),
            (New-Object System.Drawing.PointF ($tipx - $sx * 30 - $grow * $sx), ($tipy - 6)),
            (New-Object System.Drawing.PointF ($tipx - $sx * 1), ($tipy + 12 + $grow)))
        $g.FillPolygon($fill, $pts)
    }
}

function Render([int]$n, [string]$variant) {
    $ss = 4; $big = $n * $ss                                                           # supersample, then downscale
    $bmp = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform($big / 256.0, $big / 256.0)

    $tile = RoundedRect 4 4 248 248 58
    if ($variant -eq 'A') {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256), (Col '#27355F'), (Col '#0E1427'))
    } else {
        $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 256, 256), (Col '#6FA8FF'), (Col '#6B4DFF'))
    }
    $g.FillPath($bg, $tile)
    # soft top highlight
    $hl = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 0, 0), (New-Object System.Drawing.PointF 0, 140), (Col '#FFFFFF' 46), (Col '#FFFFFF' 0))
    $g.SetClip($tile); $g.FillRectangle($hl, 0, 0, 256, 140); $g.ResetClip()
    # hairline inner border
    $edge = New-Object System.Drawing.Pen (Col '#FFFFFF' 40), 2.5
    $g.DrawPath($edge, (RoundedRect 5.5 5.5 245 245 56.5))

    if ($variant -eq 'A') {
        # glow: draw a fat translucent anchor on its own layer, blur by down/up-sampling, composite under the crisp one
        $glow = New-Object System.Drawing.Bitmap $big, $big, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $gg = [System.Drawing.Graphics]::FromImage($glow); $gg.SmoothingMode = 'AntiAlias'; $gg.ScaleTransform($big / 256.0, $big / 256.0)
        $gp = New-Object System.Drawing.Pen (Col '#4FD8FF' 200), 34; $gp.StartCap = 'Round'; $gp.EndCap = 'Round'
        DrawAnchor $gg $gp (New-Object System.Drawing.SolidBrush (Col '#4FD8FF' 200)) 8
        $gg.Dispose()
        $small = New-Object System.Drawing.Bitmap ([Math]::Max(8, [int]($big / 10))), ([Math]::Max(8, [int]($big / 10)))
        $sg = [System.Drawing.Graphics]::FromImage($small); $sg.InterpolationMode = 'HighQualityBicubic'; $sg.DrawImage($glow, 0, 0, $small.Width, $small.Height); $sg.Dispose()
        $g.ResetTransform(); $g.SetClip($tile); $g.InterpolationMode = 'HighQualityBicubic'
        $g.DrawImage($small, 0, 0, $big, $big); $g.ResetClip()
        $g.ScaleTransform($big / 256.0, $big / 256.0)
        $stroke = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.PointF 128, 30), (New-Object System.Drawing.PointF 128, 220), (Col '#FFFFFF'), (Col '#8FD6FF'))
        $fillB = $stroke
    } else {
        $shadow = New-Object System.Drawing.Pen (Col '#1A1050' 90), 17; $shadow.StartCap = 'Round'; $shadow.EndCap = 'Round'
        $g.TranslateTransform(0, 7); DrawAnchor $g $shadow (New-Object System.Drawing.SolidBrush (Col '#1A1050' 90)); $g.TranslateTransform(0, -7)
        $stroke = New-Object System.Drawing.SolidBrush (Col '#FFFFFF'); $fillB = $stroke
    }
    $pen = New-Object System.Drawing.Pen $stroke, 16; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    DrawAnchor $g $pen $fillB
    $g.Dispose()

    $outBmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $og = [System.Drawing.Graphics]::FromImage($outBmp); $og.InterpolationMode = 'HighQualityBicubic'; $og.SmoothingMode = 'HighQuality'; $og.PixelOffsetMode = 'HighQuality'
    $og.DrawImage($bmp, 0, 0, $n, $n); $og.Dispose(); $bmp.Dispose()
    return $outBmp
}

$dir = Split-Path $Out -Parent
if ($PreviewBoth) {
    $sheet = New-Object System.Drawing.Bitmap 1100, 330
    $sg = [System.Drawing.Graphics]::FromImage($sheet); $sg.Clear([System.Drawing.ColorTranslator]::FromHtml('#0E1014'))
    $x = 20
    foreach ($v in 'A', 'B') {
        $sg.DrawImage((Render 256 $v), $x, 20, 256, 256); $x += 280
        foreach ($sz in 64, 32, 16) { $sg.DrawImage((Render $sz $v), $x, 20 + (256 - $sz) / 2, $sz, $sz); $x += $sz + 16 }
        $x += 40
    }
    $sheet.Save((Join-Path $dir 'anchor-compare.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Host 'Wrote anchor-compare.png'; return
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) { $b = Render $n $Variant; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); , $ms.ToArray() }
$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $n = $sizes[$i]; $wh = if ($n -ge 256) { 0 } else { $n }
    $bw.Write([byte]$wh); $bw.Write([byte]$wh); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Close(); $fs.Close()
(Render 256 $Variant).Save((Join-Path $dir 'anchor-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $Out (variant $Variant)"
