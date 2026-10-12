$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assets = Join-Path $PSScriptRoot '..\src\CodexProxy\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$svg = @'
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <defs><linearGradient id="blue" x2="1" y2="1"><stop stop-color="#60B6FF"/><stop offset="1" stop-color="#2F7FE8"/></linearGradient></defs>
  <rect x="8" y="8" width="240" height="240" rx="54" fill="url(#blue)"/>
  <path d="M67 83L108 124L67 165M138 165H176V113" fill="none" stroke="white" stroke-width="18" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="176" cy="91" r="13" fill="#D6EEFF"/>
</svg>
'@
[System.IO.File]::WriteAllText((Join-Path $assets 'app.svg'), $svg)
$frames = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.ScaleTransform($size / 256.0, $size / 256.0)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(8, 8, 108, 108, 180, 90)
    $shape.AddArc(140, 8, 108, 108, 270, 90)
    $shape.AddArc(140, 140, 108, 108, 0, 90)
    $shape.AddArc(8, 140, 108, 108, 90, 90)
    $shape.CloseFigure()
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(8, 8, 240, 240),
        [System.Drawing.ColorTranslator]::FromHtml('#60B6FF'),
        [System.Drawing.ColorTranslator]::FromHtml('#2F7FE8'), [single]45)
    $graphics.FillPath($gradient, $shape)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 18)
    $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $chevron = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(67,83), [System.Drawing.PointF]::new(108,124), [System.Drawing.PointF]::new(67,165))
    $route = [System.Drawing.PointF[]]@([System.Drawing.PointF]::new(138,165), [System.Drawing.PointF]::new(176,165), [System.Drawing.PointF]::new(176,113))
    $graphics.DrawLines($pen, $chevron)
    $graphics.DrawLines($pen, $route)
    $dot = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#D6EEFF'))
    $graphics.FillEllipse($dot, 163, 78, 26, 26)
    $memory = [System.IO.MemoryStream]::new()
    $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
    $frames += ,($memory.ToArray())
    if ($size -eq 256) { $bitmap.Save((Join-Path $assets 'app.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $memory.Dispose(); $dot.Dispose(); $pen.Dispose(); $gradient.Dispose(); $shape.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
}
$file = [System.IO.File]::Create((Join-Path $assets 'app.ico'))
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
for ($i = 0; $i -lt $frames.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
$writer.Dispose(); $file.Dispose()
Write-Output 'Created SVG, PNG and multi-resolution ICO (16–256 px).'
