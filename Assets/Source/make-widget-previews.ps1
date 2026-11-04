# Draws the previews shown in the Add widgets list (Assets\Widget\Prompt-*.png, Progress-*.png) at the size Windows expects.
# Usage: powershell -ExecutionPolicy Bypass -File Assets\Source\make-widget-previews.ps1
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$widgetDir = Join-Path $PSScriptRoot '..\Widget'
$icon = [System.Drawing.Image]::FromFile((Join-Path $widgetDir 'Icon.png'))

function Rounded($x, $y, $w, $h, $r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90); $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90); $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $p.CloseFigure(); $p
}

function Draw-Preview($name, $dark, $lines, $button) {
    $back = if ($dark) { '#2B2B2B' } else { '#FAFAFA' }
    $text = if ($dark) { '#FFFFFF' } else { '#1B1B1B' }
    $subtle = if ($dark) { '#C8C8C8' } else { '#5C5C5C' }
    $accent = if ($dark) { '#C4A6F0' } else { '#7350AF' }
    $onAccent = if ($dark) { '#1B1B1B' } else { '#FFFFFF' }

    $bmp = New-Object System.Drawing.Bitmap 300, 304, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'ClearTypeGridFit'
    $g.Clear([System.Drawing.Color]::Transparent)
    $brush = { param($hex) New-Object System.Drawing.SolidBrush ([System.Drawing.ColorTranslator]::FromHtml($hex)) }

    $g.FillPath((& $brush $back), (Rounded 0 0 299 303 12))
    $g.DrawImage($icon, 16, 14, 20, 20)
    $g.DrawString('Journal', (New-Object System.Drawing.Font('Segoe UI', 10)), (& $brush $subtle), 42, 14)

    $y = 52
    foreach ($line in $lines) {
        $font = New-Object System.Drawing.Font($(if ($line.Font) { $line.Font } else { 'Segoe UI' }), $line.Size,$(if ($line.Bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }))
        $area = New-Object System.Drawing.RectangleF(16, $y, 268, 150)
        $measured = $g.MeasureString($line.Text, $font, 268)
        $g.DrawString($line.Text, $font, (& $brush $(if ($line.Subtle) { $subtle } else { $text })), $area)
        $y += $measured.Height + 8
    }

    $g.FillPath((& $brush $accent), (Rounded 16 248 268 40 6))
    $center = New-Object System.Drawing.StringFormat; $center.Alignment = 'Center'; $center.LineAlignment = 'Center'
    $g.DrawString($button, (New-Object System.Drawing.Font('Segoe UI', 11)), (& $brush $onAccent), (New-Object System.Drawing.RectangleF(16, 248, 268, 40)), $center)

    $g.Dispose()
    $bmp.Save((Join-Path $widgetDir "$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    "Wrote $name.png"
}

$on = [string][char]0x2B24; $off = [string][char]0x25EF
$prompt = @(
    @{ Text = 'What made you smile today?'; Size = 15; Bold = $true }
    @{ Text = 'Ready to write?'; Size = 10; Subtle = $true }
)
$progress = @(
    @{ Text = '3 days this week'; Size = 17; Bold = $true }
    @{ Text = (@($on, $on, $off, $on, $off, $off, $off) -join ' '); Size = 11; Font = 'Segoe UI Symbol' }
    @{ Text = 'Ready to write?'; Size = 10; Subtle = $true }
)
foreach ($dark in $false, $true) {
    $suffix = if ($dark) { 'dark' } else { 'light' }
    Draw-Preview "Prompt-$suffix" $dark $prompt 'Write'
    Draw-Preview "Progress-$suffix" $dark $progress 'Write'
}
$icon.Dispose()
