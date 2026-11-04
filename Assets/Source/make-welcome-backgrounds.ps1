# Draws the welcome-slideshow backgrounds (Assets\Welcome\*.jpg): original artwork, soft ribbons like the turning
# pages of a journal, in the app's purple. The slides show cards on top, so the art is softened into color and depth
# instead of competing with them. Blurring once here, not at runtime, keeps it identical even when the window
# loses focus (Fluent acrylic falls back to a flat color then).
# Usage: powershell -ExecutionPolicy Bypass -File Assets\Source\make-welcome-backgrounds.ps1
param(
    [int]$Width = 1280,
    [int]$Height = 720,
    # Blur radius in pixels at the output size. Larger = softer.
    [int]$Radius = 7
)

Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

# Three box-blur passes per direction approximate a Gaussian blur, with no ringing or blockiness.
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class SoftBlur
{
    public static void Apply(Bitmap bmp, int radius)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int w = bmp.Width, h = bmp.Height, n = w * h * 4;
        var px = new byte[n];
        Marshal.Copy(data.Scan0, px, 0, n);
        var tmp = new byte[n];
        for (int pass = 0; pass < 3; pass++)
        {
            Box(px, tmp, w, h, radius, true);
            Box(tmp, px, w, h, radius, false);
        }
        Marshal.Copy(px, 0, data.Scan0, n);
        bmp.UnlockBits(data);
    }

    // Running-sum box blur along rows (horizontal) or columns, clamping at the edges.
    static void Box(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int lines = horizontal ? h : w, len = horizontal ? w : h, div = r * 2 + 1;
        for (int line = 0; line < lines; line++)
        {
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += src[Idx(line, Clamp(k, len), w, horizontal) + c];
                for (int i = 0; i < len; i++)
                {
                    dst[Idx(line, i, w, horizontal) + c] = (byte)(sum / div);
                    sum += src[Idx(line, Clamp(i + r + 1, len), w, horizontal) + c] - src[Idx(line, Clamp(i - r, len), w, horizontal) + c];
                }
            }
        }
    }

    static int Clamp(int i, int len) { return i < 0 ? 0 : i >= len ? len - 1 : i; }

    static int Idx(int line, int i, int w, bool horizontal) { return (horizontal ? line * w + i : i * w + line) * 4; }
}
"@

function Color($hex, $alpha = 255) {
    [System.Drawing.Color]::FromArgb($alpha, [System.Drawing.ColorTranslator]::FromHtml($hex))
}

# One ribbon: a wave across the picture that tapers from thick to thin, shaded along its length.
function Draw-Ribbon($g, $y, $rise, $thickness, $from, $to) {
    $w = $Width + 200; $left = -100
    $top = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($left, $y + $rise),
        [System.Drawing.PointF]::new($left + $w * 0.33, $y - $rise * 1.2),
        [System.Drawing.PointF]::new($left + $w * 0.66, $y + $rise * 0.8),
        [System.Drawing.PointF]::new($left + $w, $y - $rise))
    $bottom = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($left + $w, $y - $rise + $thickness * 0.25),
        [System.Drawing.PointF]::new($left + $w * 0.66, $y + $rise * 0.8 + $thickness * 0.7),
        [System.Drawing.PointF]::new($left + $w * 0.33, $y - $rise * 1.2 + $thickness),
        [System.Drawing.PointF]::new($left, $y + $rise + $thickness * 0.8))

    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddBeziers($top)
    $path.AddLine($top[3], $bottom[0])
    $path.AddBeziers($bottom)
    $path.CloseFigure()

    $bounds = New-Object System.Drawing.RectangleF($left, 0, $w, $Height)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($bounds, $from, $to, [single]0)
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()
}

function Draw-Art($name, $backFrom, $backTo, $ribbons) {
    $bmp = New-Object System.Drawing.Bitmap $Width, $Height, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $rect = New-Object System.Drawing.Rectangle(0, 0, $Width, $Height)
    $back = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $backFrom, $backTo, [single]35)
    $g.FillRectangle($back, $rect)
    $back.Dispose()
    foreach ($r in $ribbons) { Draw-Ribbon $g @r }
    $g.Dispose()

    [SoftBlur]::Apply($bmp, $Radius)

    $target = Join-Path $outDir "$name.jpg"
    $bmp.Save($target, $jpeg, $quality)
    $bmp.Dispose()
    "Wrote $target ($([int]((Get-Item $target).Length / 1KB)) KB)"
}

$outDir = Join-Path $PSScriptRoot '..\Welcome'
New-Item -ItemType Directory $outDir -Force | Out-Null

$jpeg = [System.Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$quality = New-Object System.Drawing.Imaging.EncoderParameters 1
$quality.Param[0] = New-Object System.Drawing.Imaging.EncoderParameter([System.Drawing.Imaging.Encoder]::Quality, [long]90)

Draw-Art 'welcome-light' (Color '#F4EEFF') (Color '#DCE6FF') @(
    @{ y = 120; rise = 60; thickness = 240; from = (Color '#C9B6F5' 150); to = (Color '#F3C6E4' 110) }
    @{ y = 270; rise = 90; thickness = 260; from = (Color '#A58BE8' 150); to = (Color '#9DB8F5' 150) }
    @{ y = 430; rise = 70; thickness = 300; from = (Color '#F0B8DA' 130); to = (Color '#8E73DC' 170) }
)

Draw-Art 'welcome-dark' (Color '#1B1333') (Color '#0F1A38') @(
    @{ y = 120; rise = 60; thickness = 240; from = (Color '#4A2F8F' 170); to = (Color '#7A2F73' 120) }
    @{ y = 270; rise = 90; thickness = 260; from = (Color '#6A47C4' 170); to = (Color '#2F4FA8' 170) }
    @{ y = 430; rise = 70; thickness = 300; from = (Color '#8A3C86' 150); to = (Color '#5A3DB8' 190) }
)
