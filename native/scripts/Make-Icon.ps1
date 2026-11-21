$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,PresentationFramework,WindowsBase
$assets = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\DesktopNook.App\Assets'))
$drawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'AppIconDrawing.xaml')))
$drawing.Freeze()
$smallDrawing = [Windows.Markup.XamlReader]::Parse([IO.File]::ReadAllText((Join-Path $assets 'AppIconSmallDrawing.xaml')))
$smallDrawing.Freeze()
function Render-Icon([int]$size) {
    $visual = [Windows.Media.DrawingVisual]::new()
    $context = $visual.RenderOpen()
    $context.PushTransform([Windows.Media.ScaleTransform]::new($size/1024.0,$size/1024.0))
    $context.DrawDrawing($(if($size -le 24){$smallDrawing}else{$drawing}))
    $context.Pop(); $context.Close()
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.MemoryStream]::new()
    try { $encoder.Save($stream); return ,$stream.ToArray() } finally { $stream.Dispose() }
}
[IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.png'),(Render-Icon 256))
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

$artifacts = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\artifacts'))
[void][IO.Directory]::CreateDirectory($artifacts)
$welcome = [Windows.Media.DrawingVisual]::new()
$context = $welcome.RenderOpen()
try {
    $background = [Windows.Media.SolidColorBrush]::new([Windows.Media.ColorConverter]::ConvertFromString('#171A2B'))
    $context.DrawRectangle($background, $null, [Windows.Rect]::new(0, 0, 164, 314))
    $context.PushTransform([Windows.Media.TranslateTransform]::new(31, 85))
    $context.PushTransform([Windows.Media.ScaleTransform]::new(102.0/1024.0, 102.0/1024.0))
    $context.DrawDrawing($drawing)
    $context.Pop()
    $context.Pop()
} finally { $context.Close() }
$welcomeBitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(164, 314, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$welcomeBitmap.Render($welcome)
$rgbBitmap = [Windows.Media.Imaging.FormatConvertedBitmap]::new($welcomeBitmap, [Windows.Media.PixelFormats]::Bgr24, $null, 0)
$welcomeEncoder = [Windows.Media.Imaging.BmpBitmapEncoder]::new()
$welcomeEncoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($rgbBitmap))
$welcomeFile = [IO.File]::Create((Join-Path $artifacts 'InstallerBrand.bmp'))
try { $welcomeEncoder.Save($welcomeFile) } finally { $welcomeFile.Dispose() }
Get-Item -LiteralPath (Join-Path $assets 'AppIcon.ico') | Select-Object FullName,Length
