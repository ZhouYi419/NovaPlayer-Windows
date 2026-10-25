[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path $PSScriptRoot -Parent
$path = Join-Path $taskRoot 'NovaPlayer.App/Assets/NovaPlayer.ico'
$frames = @()
# 与 SVG 母版使用相同的深色圆角底板、青蓝渐变和播放轮廓；独立绘制每个尺寸，避免小图标模糊。
foreach ($size in @(16, 24, 32, 48, 64, 128, 256)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $background = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#121722'))
    $gradient = [Drawing.Drawing2D.LinearGradientBrush]::new([Drawing.Point]::new(70, 55), [Drawing.Point]::new(195, 205), [Drawing.ColorTranslator]::FromHtml('#4AD4FA'), [Drawing.ColorTranslator]::FromHtml('#3552DC'))
    $pen = [Drawing.Pen]::new($gradient, 20)
    $shape = [Drawing.Drawing2D.GraphicsPath]::new()
    $stream = [IO.MemoryStream]::new()
    try {
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform($size / 256.0, $size / 256.0)
        $shape.AddArc(8, 8, 108, 108, 180, 90)
        $shape.AddArc(140, 8, 108, 108, 270, 90)
        $shape.AddArc(140, 140, 108, 108, 0, 90)
        $shape.AddArc(8, 140, 108, 108, 90, 90)
        $shape.CloseFigure()
        $graphics.FillPath($background, $shape)
        $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
        $graphics.DrawPolygon($pen, [Drawing.PointF[]]@([Drawing.PointF]::new(88, 62), [Drawing.PointF]::new(194, 128), [Drawing.PointF]::new(88, 194)))
        $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        $frames += ,@{ Size = $size; Bytes = $stream.ToArray() }
    } finally { $stream.Dispose(); $shape.Dispose(); $pen.Dispose(); $gradient.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
$output = [IO.File]::Create($path)
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$frames.Count)
    $offset = 6 + 16 * $frames.Count
    foreach ($frame in $frames) {
        $dimension = if ($frame.Size -eq 256) { 0 } else { $frame.Size }
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([uint16]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frame.Bytes.Length); $writer.Write([uint32]$offset)
        $offset += $frame.Bytes.Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame.Bytes) }
} finally { $writer.Dispose(); $output.Dispose() }
Write-Output "已生成七档应用图标：$path"
