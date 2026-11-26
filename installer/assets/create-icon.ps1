# Regenerates the original DevDoctor icon from geometric shapes; no downloaded artwork or fonts.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
function Remove-PngMetadata([byte[]]$Png) {
    $clean=[IO.MemoryStream]::new()
    try {
        $clean.Write($Png,0,8)
        $position=8
        while($position -lt $Png.Length) {
            $length=[Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($Png,$position))
            if($length -lt 0 -or $position+12+$length -gt $Png.Length){throw 'Generated PNG has an invalid chunk length.'}
            $kind=[Text.Encoding]::ASCII.GetString($Png,$position+4,4)
            # Retain image/alpha data and existing CRCs; discard optional color profiles and metadata.
            if($kind -in @('IHDR','PLTE','IDAT','IEND','tRNS')){$clean.Write($Png,$position,12+$length)}
            $position+=12+$length
            if($kind -eq 'IEND'){break}
        }
        return ,$clean.ToArray()
    } finally {$clean.Dispose()}
}
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,32,48,64,128,256)
foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform($size / 256.0,$size / 256.0)
    $shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $shape.AddArc(4,4,112,112,180,90)
    $shape.AddArc(140,4,112,112,270,90)
    $shape.AddArc(140,140,112,112,0,90)
    $shape.AddArc(4,140,112,112,90,90)
    $shape.CloseFigure()
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#102748'))
    $graphics.FillPath($background,$shape)
    $blue = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#72bbff'),15)
    $white = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#f5fbff'),13)
    foreach ($pen in @($blue,$white)) { $pen.StartCap='Round'; $pen.EndCap='Round'; $pen.LineJoin='Round' }
    $graphics.DrawLines($blue,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(84,64),[System.Drawing.PointF]::new(64,64),[System.Drawing.PointF]::new(64,192),[System.Drawing.PointF]::new(84,192)))
    $graphics.DrawLines($blue,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(172,64),[System.Drawing.PointF]::new(192,64),[System.Drawing.PointF]::new(192,192),[System.Drawing.PointF]::new(172,192)))
    $graphics.DrawLines($white,[System.Drawing.PointF[]]@([System.Drawing.PointF]::new(82,132),[System.Drawing.PointF]::new(106,132),[System.Drawing.PointF]::new(119,98),[System.Drawing.PointF]::new(138,164),[System.Drawing.PointF]::new(152,132),[System.Drawing.PointF]::new(174,132)))
    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
    $frames.Add((Remove-PngMetadata ($stream.ToArray())))
    $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose(); $shape.Dispose(); $background.Dispose(); $blue.Dispose(); $white.Dispose()
}
$output = [System.IO.File]::Create((Join-Path $PSScriptRoot 'DevDoctor.ico'))
$writer = [System.IO.BinaryWriter]::new($output)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i=0;$i -lt $sizes.Count;$i++) {
    $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($frame in $frames) { $writer.Write($frame) }
$writer.Dispose()
