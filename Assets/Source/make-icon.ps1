# Generates Assets\Journal.ico (and a 1024px preview PNG) for Windows Journal.
# Fluent-style object icon: a blue notebook with page edges and a coral bookmark ribbon.
# Usage: powershell -ExecutionPolicy Bypass -File Assets\Source\make-icon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..'))

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

function C([int]$a, [int]$r, [int]$g, [int]$b) { [System.Drawing.Color]::FromArgb($a, $r, $g, $b) }
function P([single]$x, [single]$y) { New-Object System.Drawing.PointF $x, $y }

function RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$rl, [single]$rr) {
    # $rl = radius of left corners, $rr = radius of right corners
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $rl * 2, $rl * 2, 180, 90)
    if ($rr -gt 0) {
        $p.AddArc($x + $w - $rr * 2, $y, $rr * 2, $rr * 2, 270, 90)
        $p.AddArc($x + $w - $rr * 2, $y + $h - $rr * 2, $rr * 2, $rr * 2, 0, 90)
    }
    else {
        $p.AddLine(($x + $w), $y, ($x + $w), ($y + $h))
    }
    $p.AddArc($x, $y + $h - $rl * 2, $rl * 2, $rl * 2, 90, 90)
    $p.CloseFigure()
    $p
}

function LinBrush($x1, $y1, $x2, $y2, $c1, $c2) {
    New-Object System.Drawing.Drawing2D.LinearGradientBrush (P $x1 $y1), (P $x2 $y2), $c1, $c2
}

# Draws the icon into a 1024x1024 design space. $simple drops fine detail for tiny sizes.
function Draw-Icon([System.Drawing.Graphics]$g, [bool]$simple) {
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'

    # Enlarge the artwork so it fills ~92% of the frame (content bounds are about 150..870 x 110..990)
    $g.TranslateTransform(512, 540, 'Prepend')
    $g.ScaleTransform(1.07, 1.07, 'Prepend')
    $g.TranslateTransform(-510, -560, 'Prepend')

    # Soft drop shadow (stacked translucent shapes approximate a blur)
    if (-not $simple) {
        for ($i = 0; $i -lt 10; $i++) {
            $grow = 30 - $i * 3
            $sh = RoundRect (165 - $grow / 2) (140 - $grow / 2 + 20) (705 + $grow) (770 + $grow) (70 + $grow / 2) (60 + $grow / 2)
            $g.FillPath((New-Object System.Drawing.SolidBrush (C 5 0 20 60)), $sh)
        }
    }

    # Bookmark ribbon (drawn first so the pages/cover overlap its top)
    $rib = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rib.AddPolygon(@((P 590 700), (P 730 700), (P 730 1000), (P 660 935), (P 590 1000)))
    $g.FillPath((LinBrush 590 700 730 1000 (C 255 255 150 110) (C 255 226 58 72)), $rib)
    if (-not $simple) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush (C 70 255 255 255)), 590, 700, 26, 290)
    }

    # Page block peeking out on the right and bottom
    $pages = RoundRect 230 150 640 750 60 70
    $g.FillPath((LinBrush 230 150 870 900 (C 255 255 255 255) (C 255 214 221 232)), $pages)
    if (-not $simple) {
        $pen = New-Object System.Drawing.Pen (C 255 196 205 220), 6
        foreach ($k in 1..3) {
            $o = $k * 16
            $g.DrawArc($pen, (870 - 140 - $o), (150 + $o), 140, 140, 270, 90)
            $g.DrawLine($pen, (870 - $o), (220 + $o), (870 - $o), (830 - $o))
            $g.DrawArc($pen, (870 - 140 - $o), (900 - 140 - $o), 140, 140, 0, 90)
            $g.DrawLine($pen, (800 - $o), (900 - $o), 300, (900 - $o))
        }
    }

    # Cover
    $cover = RoundRect 150 110 680 770 64 56
    $g.FillPath((LinBrush 150 110 830 880 (C 255 79 196 255) (C 255 22 82 210)), $cover)

    # Top sheen on the cover
    if (-not $simple) {
        $sheen = RoundRect 150 110 680 300 64 56
        $g.FillPath((LinBrush 0 110 0 410 (C 70 255 255 255) (C 0 255 255 255)), $sheen)
    }

    # Spine
    $spine = RoundRect 150 110 150 770 64 0
    $g.FillPath((LinBrush 150 0 300 0 (C 255 16 70 170) (C 255 30 100 205)), $spine)
    $g.FillRectangle((New-Object System.Drawing.SolidBrush (C 90 255 255 255)), 298, 110, 8, 770)

    # Cover label with writing lines
    $label = RoundRect 390 300 330 170 28 28
    $g.FillPath((New-Object System.Drawing.SolidBrush (C 245 255 255 255)), $label)
    $lineBrush = New-Object System.Drawing.SolidBrush (C 255 22 98 205)
    if ($simple) {
        $g.FillPath($lineBrush, (RoundRect 440 360 230 50 25 25))
    }
    else {
        $g.FillPath($lineBrush, (RoundRect 435 345 240 30 15 15))
        $g.FillPath((New-Object System.Drawing.SolidBrush (C 255 130 172 230)), (RoundRect 435 400 160 26 13 13))
    }

    # Outer hairline for crispness on any background
    $g.DrawPath((New-Object System.Drawing.Pen (C 60 10 40 110), 4), $cover)
}

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::Transparent)
    if ($size -ge 48) {
        # Render big then downscale for smooth gradients
        $big = New-Object System.Drawing.Bitmap 1024, 1024
        $bg = [System.Drawing.Graphics]::FromImage($big)
        $bg.Clear([System.Drawing.Color]::Transparent)
        Draw-Icon $bg $false
        $bg.Dispose()
        $g.InterpolationMode = 'HighQualityBicubic'
        $g.DrawImage($big, 0, 0, $size, $size)
        $big.Dispose()
    }
    else {
        # Small sizes: draw directly, simplified, so shapes snap to pixels
        $g.ScaleTransform($size / 1024.0, $size / 1024.0)
        Draw-Icon $g $true
    }
    $g.Dispose()
    $bmp
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$pngs = foreach ($s in $sizes) {
    $bmp = Render $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed frames
$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$d); $bw.Write([byte]$d); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$pngs[$i].Length); $bw.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
[IO.File]::WriteAllBytes((Join-Path $OutDir 'Journal.ico'), $out.ToArray())

foreach ($s in 64, 256) {
    # 64px for the title bar, 256px for the welcome slideshow
    $png = Render $s
    $png.Save((Join-Path $OutDir "Journal-$s.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $png.Dispose()
}

# MSIX logos (Assets\Package): the icon centered on a transparent canvas at each size Windows asks for.
function Save-Logo([string]$name, [int]$width, [int]$height, [int]$iconSize) {
    $canvas = New-Object System.Drawing.Bitmap $width, $height
    $g = [System.Drawing.Graphics]::FromImage($canvas)
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.InterpolationMode = 'HighQualityBicubic'
    $icon = Render $iconSize
    $g.DrawImage($icon, [int](($width - $iconSize) / 2), [int](($height - $iconSize) / 2), $iconSize, $iconSize)
    $icon.Dispose(); $g.Dispose()
    $canvas.Save((Join-Path $packageDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $canvas.Dispose()
}

$packageDir = Join-Path $OutDir 'Package'
New-Item -ItemType Directory $packageDir -Force | Out-Null
Save-Logo 'Square44x44Logo.scale-200.png' 88 88 88
foreach ($t in 16, 24, 32, 48, 256) {
    # Taskbar, Start and File Explorer pick these by exact pixel size; "unplated" means no tile behind it.
    Save-Logo "Square44x44Logo.targetsize-$t.png" $t $t $t
    Save-Logo "Square44x44Logo.targetsize-${t}_altform-unplated.png" $t $t $t
}
Save-Logo 'Square150x150Logo.scale-200.png' 300 300 200
Save-Logo 'Wide310x150Logo.scale-200.png' 620 300 200
Save-Logo 'StoreLogo.scale-100.png' 50 50 50
Save-Logo 'LockScreenLogo.scale-200.png' 48 48 48
Save-Logo 'SplashScreen.scale-200.png' 1240 600 300

# Widgets board: provider icon plus light/dark previews for the "Add widget" picker.
$widgetDir = Join-Path $OutDir 'Widget'
New-Item -ItemType Directory $widgetDir -Force | Out-Null
$icon64 = Render 64
$icon64.Save((Join-Path $widgetDir 'Icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)

foreach ($theme in @(
        @{ Name = 'Screenshot-light.png'; Card = (C 255 251 251 251); Text = (C 255 26 26 26); Subtle = (C 255 96 96 96); Accent = (C 255 0 95 184); OnAccent = (C 255 255 255 255) },
        @{ Name = 'Screenshot-dark.png'; Card = (C 255 44 44 44); Text = (C 255 255 255 255); Subtle = (C 255 200 200 200); Accent = (C 255 96 205 255); OnAccent = (C 255 0 0 0) })) {
    $shot = New-Object System.Drawing.Bitmap 300, 304
    $g = [System.Drawing.Graphics]::FromImage($shot)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.FillPath((New-Object System.Drawing.SolidBrush $theme.Card), (RoundRect 0 0 299 303 16 16))
    $g.DrawImage($icon64, 16, 14, 20, 20)
    $font = { param($size, $style) New-Object System.Drawing.Font 'Segoe UI', $size, $style, ([System.Drawing.GraphicsUnit]::Pixel) }
    $text = New-Object System.Drawing.SolidBrush $theme.Text
    $subtle = New-Object System.Drawing.SolidBrush $theme.Subtle
    $g.DrawString('Journal', (& $font 13 'Regular'), $subtle, 42, 15)
    $g.DrawString('5', (& $font 44 'Bold'), $text, 12, 40)
    $g.DrawString('day streak', (& $font 15 'Regular'), $text, 16, 98)
    $g.DrawString(('You wrote today ' + [char]0x2713), (& $font 12 'Regular'), $subtle, 16, 122)
    $g.DrawString('What made you smile today?', (& $font 15 'Regular'), $text, (New-Object System.Drawing.RectangleF 16, 156, 268, 60))
    $g.FillPath((New-Object System.Drawing.SolidBrush $theme.Accent), (RoundRect 16 248 268 36 6 6))
    $format = New-Object System.Drawing.StringFormat; $format.Alignment = 'Center'; $format.LineAlignment = 'Center'
    $g.DrawString('Write more', (& $font 14 'Regular'), (New-Object System.Drawing.SolidBrush $theme.OnAccent), (New-Object System.Drawing.RectangleF 16, 248, 268, 36), $format)
    $g.Dispose()
    $shot.Save((Join-Path $widgetDir $theme.Name), [System.Drawing.Imaging.ImageFormat]::Png)
    $shot.Dispose()
}
$icon64.Dispose()

$preview = Render 1024
$preview.Save((Join-Path $PSScriptRoot 'Journal-1024.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()
"Wrote $(Join-Path $OutDir 'Journal.ico')"
