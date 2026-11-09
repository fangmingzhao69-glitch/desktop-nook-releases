$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\DesktopNook.App\Assets'))
$drawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'AppIconDrawing.xaml')))
$drawing.Freeze()
function Render-Icon([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size/1024.0,$size/1024.0))
    $context.DrawDrawing($drawing)
    $context.Pop(); $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'),(Render-Icon 512))
$sizes = @(16,20,24,32,40,48,64,96,128,256)
$frames = @($sizes | ForEach-Object { ,(Render-Icon $_) })
$writer = [IO.BinaryWriter]::new([IO.File]::Create((Join-Path $assets 'AppIcon.ico')))
try {
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16*$sizes.Count
    for($i=0;$i -lt $sizes.Count;$i++) {
        $dimension=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
        $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
        $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach($frame in $frames){$writer.Write([byte[]]$frame)}
} finally { $writer.Dispose() }
Get-Item -LiteralPath (Join-Path $assets 'AppIcon.ico') | Select-Object FullName,Length
