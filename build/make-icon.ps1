# Renders the app icon with WPF and writes Assets\app.ico (multi-size, PNG-compressed) and Assets\app-256.png.
param([string]$OutDir = (Join-Path $PSScriptRoot '..\src\ServiceMonitor\Assets'))

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

function Render-Icon([int]$size) {
    $s = $size / 256.0
    $dv = New-Object System.Windows.Media.DrawingVisual
    $dc = $dv.RenderOpen()

    $bg = New-Object System.Windows.Media.LinearGradientBrush(
        [System.Windows.Media.Color]::FromRgb(0x1A, 0x7F, 0xD4), [System.Windows.Media.Color]::FromRgb(0x0B, 0x4F, 0x9C), 90)
    $dc.DrawRoundedRectangle($bg, $null, (New-Object System.Windows.Rect((8 * $s), (8 * $s), (240 * $s), (240 * $s))), (52 * $s), (52 * $s))

    # Heartbeat line
    $pen = New-Object System.Windows.Media.Pen([System.Windows.Media.Brushes]::White, [Math]::Max(1.5, 18 * $s))
    $pen.LineJoin = 'Round'; $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
    $geo = New-Object System.Windows.Media.StreamGeometry
    $ctx = $geo.Open()
    $pts = @(@(40, 140), @(86, 140), @(106, 98), @(132, 186), @(156, 112), @(170, 140), @(216, 140))
    $ctx.BeginFigure((New-Object System.Windows.Point(($pts[0][0] * $s), ($pts[0][1] * $s))), $false, $false)
    foreach ($p in $pts[1..($pts.Count - 1)]) { $ctx.LineTo((New-Object System.Windows.Point(($p[0] * $s), ($p[1] * $s))), $true, $true) }
    $ctx.Close()
    $dc.DrawGeometry($null, $pen, $geo)

    # Notification dot
    $ring = New-Object System.Windows.Media.Pen((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0x0B, 0x4F, 0x9C))), (10 * $s))
    $dot = New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(0xFF, 0x8C, 0x1A))
    $dc.DrawEllipse($dot, $ring, (New-Object System.Windows.Point((196 * $s), (62 * $s))), (34 * $s), (34 * $s))
    $dc.Close()

    $bmp = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($dv)
    $enc = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object System.IO.MemoryStream
    $enc.Save($ms)
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$images = @{}
foreach ($size in $sizes) { $images[$size] = Render-Icon $size }
[IO.File]::WriteAllBytes((Join-Path $OutDir 'app-256.png'), $images[256])

$ico = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
foreach ($size in $sizes) {
    $dim = if ($size -ge 256) { 0 } else { $size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$size].Length); $w.Write([uint32]$offset)
    $offset += $images[$size].Length
}
foreach ($size in $sizes) { $w.Write($images[$size]) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ico.ToArray())
"Wrote $OutDir\app.ico and app-256.png"
