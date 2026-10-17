# Renders the Anchor app icon (blue rounded tile + white anchor) into a multi-size .ico (PNG-compressed entries).
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\Anchor.Desktop\Assets\anchor.ico'))
Add-Type -AssemblyName System.Drawing
function Render([int]$n) {
    $bmp = New-Object System.Drawing.Bitmap $n, $n, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $s = $n / 256.0
    # tile
    $r = 56 * $s; $d = $r * 2
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($n - $d, 0, $d, $d, 270, 90)
    $path.AddArc($n - $d, $n - $d, $d, $d, 0, 90); $path.AddArc(0, $n - $d, $d, $d, 90, 90); $path.CloseFigure()
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ((New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $n, $n), ([System.Drawing.ColorTranslator]::FromHtml('#7DB4FF')), ([System.Drawing.ColorTranslator]::FromHtml('#3568D8')))
    $g.FillPath($grad, $path)
    # anchor
    $w = [Math]::Max(1.5, 16 * $s)
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), $w
    $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
    $g.DrawEllipse($pen, (128 - 20) * $s, (50 - 20) * $s, 40 * $s, 40 * $s)          # ring
    $g.DrawLine($pen, 128 * $s, 70 * $s, 128 * $s, 196 * $s)                          # shaft
    $g.DrawLine($pen, 94 * $s, 104 * $s, 162 * $s, 104 * $s)                          # stock
    $g.DrawArc($pen, 56 * $s, 92 * $s, 144 * $s, 118 * $s, 10, 160)                   # flukes
    $br = [System.Drawing.Brushes]::White
    $tri1 = @((New-Object System.Drawing.PointF (50 * $s), (142 * $s)), (New-Object System.Drawing.PointF (78 * $s), (146 * $s)), (New-Object System.Drawing.PointF (58 * $s), (170 * $s)))
    $tri2 = @((New-Object System.Drawing.PointF (206 * $s), (142 * $s)), (New-Object System.Drawing.PointF (178 * $s), (146 * $s)), (New-Object System.Drawing.PointF (198 * $s), (170 * $s)))
    $g.FillPolygon($br, $tri1); $g.FillPolygon($br, $tri2)
    $g.Dispose(); return $bmp
}
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($n in $sizes) { $b = Render $n; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); ,$ms.ToArray() }
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
# preview png for eyeballing
(Render 256).Save((Join-Path (Split-Path $Out -Parent) 'anchor-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Wrote $Out"
