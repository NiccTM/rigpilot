param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\src\PCHelper.GameBarWidget\Assets')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore
Add-Type -AssemblyName WindowsBase

$output = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null

function New-RigPilotImage {
    param(
        [int]$Width,
        [int]$Height,
        [string]$Path
    )

    $scale = [Math]::Min($Width, $Height) / 64.0
    $offsetX = ($Width - (64.0 * $scale)) / 2.0
    $offsetY = ($Height - (64.0 * $scale)) / 2.0
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try {
        $context.PushTransform((New-Object System.Windows.Media.TranslateTransform($offsetX, $offsetY)))
        $context.PushTransform((New-Object System.Windows.Media.ScaleTransform($scale, $scale)))
        $context.DrawRoundedRectangle(
            (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(16, 30, 50))),
            $null,
            (New-Object System.Windows.Rect(0, 0, 64, 64)),
            17,
            17)
        $outer = [System.Windows.Media.Geometry]::Parse('M32 5 55 18v26L32 58 9 44V18z')
        $context.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(105, 173, 255))), $null, $outer)
        $inner = [System.Windows.Media.Geometry]::Parse('m32 11 17 10v20L32 52 15 41V21z')
        $context.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(12, 22, 38))), $null, $inner)
        $arrow = [System.Windows.Media.Geometry]::Parse('m32 17 12 14h-8v14h-8V31h-8z')
        $context.DrawGeometry((New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(243, 246, 250))), $null, $arrow)
        $context.DrawEllipse(
            (New-Object System.Windows.Media.SolidColorBrush([System.Windows.Media.Color]::FromRgb(80, 214, 160))),
            $null,
            (New-Object System.Windows.Point(48, 13)),
            3.5,
            3.5)
        $context.Pop()
        $context.Pop()
    }
    finally {
        $context.Close()
    }

    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap(
        $Width,
        $Height,
        96,
        96,
        [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    [void]$encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Create)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}

New-RigPilotImage -Width 44 -Height 44 -Path (Join-Path $output 'RigPilotMark44.png')
New-RigPilotImage -Width 150 -Height 150 -Path (Join-Path $output 'RigPilotMark150.png')
New-RigPilotImage -Width 50 -Height 50 -Path (Join-Path $output 'RigPilotStore.png')
New-RigPilotImage -Width 620 -Height 300 -Path (Join-Path $output 'RigPilotSplash.png')
