Add-Type -AssemblyName System.Drawing

$outputPath = "c:\Rider\DockerTool\src\DockerManager.App\app.ico"
$sizes = @(256, 128, 64, 48, 32, 16)
$pngStreams = @()

foreach ($size in $sizes) {
    $bmp = [System.Drawing.Bitmap]::new($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # Background rounded container badge
    $pad = [math]::Max(2, [int]($size * 0.05))
    $rectSize = $size - (2 * $pad)
    $cornerRadius = [int]($size * 0.22)
    
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $d = $cornerRadius * 2
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $rectSize - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $rectSize - $d, $pad + $rectSize - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $rectSize - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Dark background gradient (Slate 950 to Slate 800)
    $pt1 = [System.Drawing.PointF]::new($pad, $pad)
    $pt2 = [System.Drawing.PointF]::new(($pad + $rectSize), ($pad + $rectSize))
    $c1 = [System.Drawing.Color]::FromArgb(255, 11, 15, 25)
    $c2 = [System.Drawing.Color]::FromArgb(255, 30, 41, 59)
    $bgBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($pt1, $pt2, $c1, $c2)
    $g.FillPath($bgBrush, $path)
    $bgBrush.Dispose()

    # Cyan / Blue glow border
    $borderPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 56, 189, 248), [math]::Max(1.5, $size * 0.03))
    $g.DrawPath($borderPen, $path)
    $borderPen.Dispose()

    # Modern Stylized Docker Container Stack
    $cx = $size / 2.0
    $cy = $size / 2.0
    $unit = $size / 16.0

    $boxW = $unit * 2.4
    $boxH = $unit * 2.0
    $boxGap = $unit * 0.4

    $bSky = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 56, 189, 248))
    $bBlue = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 14, 165, 233))
    $bInd = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 99, 102, 241))
    $bGreen = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 34, 197, 94))

    # Row 1: Top 2 blocks
    $g.FillRectangle($bSky, [float]($cx - $boxW - ($boxGap/2)), [float]($cy - ($unit * 3.4)), [float]$boxW, [float]$boxH)
    $g.FillRectangle($bSky, [float]($cx + ($boxGap/2)), [float]($cy - ($unit * 3.4)), [float]$boxW, [float]$boxH)

    # Row 2: Middle 3 blocks
    $g.FillRectangle($bBlue, [float]($cx - ($boxW * 1.5) - $boxGap), [float]($cy - ($unit * 1.0)), [float]$boxW, [float]$boxH)
    $g.FillRectangle($bBlue, [float]($cx - ($boxW / 2)), [float]($cy - ($unit * 1.0)), [float]$boxW, [float]$boxH)
    $g.FillRectangle($bBlue, [float]($cx + ($boxW * 0.5) + $boxGap), [float]($cy - ($unit * 1.0)), [float]$boxW, [float]$boxH)

    # Base Whale/Container Hull
    $hullPath = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $hx = [float]($cx - ($unit * 5.0))
    $hy = [float]($cy + ($unit * 1.4))
    $hw = [float]($unit * 10.0)
    $hh = [float]($unit * 3.6)
    $hullPath.AddArc($hx, $hy, $hw, $hh * 2, 0, 180)
    $hullPath.CloseFigure()
    $g.FillPath($bInd, $hullPath)
    $hullPath.Dispose()

    # Active status green dot
    $dotSize = [float]($unit * 1.4)
    $g.FillEllipse($bGreen, [float]($cx + ($unit * 3.0)), [float]($cy + ($unit * 2.2)), $dotSize, $dotSize)

    $bSky.Dispose()
    $bBlue.Dispose()
    $bInd.Dispose()
    $bGreen.Dispose()
    $path.Dispose()
    $g.Dispose()

    $ms = [System.IO.MemoryStream]::new()
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $pngStreams += $ms
}

# Write multi-resolution ICO file
$fs = [System.IO.FileStream]::new($outputPath, [System.IO.FileMode]::Create)
$bw = [System.IO.BinaryWriter]::new($fs)

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + ($sizes.Count * 16)

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $w = if ($s -ge 256) { 0 } else { $s }
    $h = if ($s -ge 256) { 0 } else { $s }
    $bytes = $pngStreams[$i].ToArray()

    $bw.Write([byte]$w)
    $bw.Write([byte]$h)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$bytes.Length)
    $bw.Write([uint32]$offset)

    $offset += $bytes.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bytes = $pngStreams[$i].ToArray()
    $bw.Write($bytes)
    $pngStreams[$i].Dispose()
}

$bw.Flush()
$fs.Close()
Write-Output "Successfully generated: $outputPath"
