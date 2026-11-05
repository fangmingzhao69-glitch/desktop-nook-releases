$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$iconSource = Join-Path $PSScriptRoot '..\DesktopNook.App\Assets\AppIcon.png'
$iconOutput = Join-Path $PSScriptRoot '..\DesktopNook.App\Assets\AppIcon.ico'
$image = [System.Drawing.Image]::FromFile([IO.Path]::GetFullPath($iconSource))
$frames = [System.Collections.Generic.List[byte[]]]::new()
$sizes = @(16,24,32,48,64,128,256)
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.DrawImage($image,0,0,$size,$size)
        $stream = [IO.MemoryStream]::new()
        $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
        $frames.Add($stream.ToArray())
        $stream.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
    $file = [IO.File]::Create([IO.Path]::GetFullPath($iconOutput))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i = 0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
} finally { $image.Dispose() }
Get-Item -LiteralPath $iconOutput | Select-Object FullName,Length
