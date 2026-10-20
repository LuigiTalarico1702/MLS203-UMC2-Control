param([string]$AssemblyPath = "$PSScriptRoot/../src/MLS203.Control/bin/x64/Release/net48/MLS203.Control.exe")
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework
[void][Reflection.Assembly]::LoadFrom((Resolve-Path $AssemblyPath))
$app = New-Object Mls203.Control.App
$app.InitializeComponent()
$window = New-Object Mls203.Control.MainWindow
$view = $window.Content
$root = $window.FindName('LayoutRoot')
$connection = $window.FindName('ConnectionGroup')
$product = $window.FindName('ProductText')
foreach ($size in @(@(1920,1000),@(1366,700),@(1093,550),@(800,480))) {
    $width,$height = $size
    $view.Measure([Windows.Size]::new($width,$height))
    $view.Arrange([Windows.Rect]::new(0,0,$width,$height)); $view.UpdateLayout()
    $before = $connection.ActualHeight
    $product.Text = "Channel 1: MLS203 long product description for testing`nChannel 2: MLS203 long product description for testing"
    $view.UpdateLayout()
    if ($connection.ActualHeight -ne $before) { throw 'Connection resized after channel names appeared' }
    foreach ($name in @('ConnectionGroup','LegendGroup','AxisControlsGroup','JogGroup','StatusText')) {
        $element=$window.FindName($name)
        $bounds=$element.TransformToAncestor($view).TransformBounds([Windows.Rect]::new(0,0,$element.ActualWidth,$element.ActualHeight))
        if ($bounds.Bottom -gt $height + 1 -or $bounds.Right -gt $width + 1 -or $bounds.Top -lt -1 -or $bounds.Left -lt -1) { throw "$name exceeds viewport $width x $height" }
    }
    $power = $window.FindName('PowerStatusLed')
    $powerBottom = $power.TransformToAncestor($root).Transform([Windows.Point]::new(0,$power.ActualHeight)).Y
    $productTop = $product.TransformToAncestor($root).Transform([Windows.Point]::new(0,0)).Y
    $productBottom = $product.TransformToAncestor($connection).Transform([Windows.Point]::new(0,$product.ActualHeight)).Y
    if ($productBottom -gt $connection.ActualHeight - 8) { throw 'Channel text clipped by Connection' }
    if ($powerBottom -gt $productTop) { throw 'Power LED must be above channels' }
    $bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new($width,$height,96,96,[Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($view)
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $file=[IO.File]::Create("$env:TEMP/mls203-layout-$width.png")
    $encoder.Save($file); $file.Close()
    $product.Text = ''
}
$window.Close(); $app.Shutdown()
'Layout checks passed at four viewport sizes; screenshots saved to TEMP.'
