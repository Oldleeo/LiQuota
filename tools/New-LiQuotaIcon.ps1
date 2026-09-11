param(
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [string]$SourceImage = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Assets\LiQuota-master-v3.png')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundedRectanglePath {
    param([float]$X, [float]$Y, [float]$Width, [float]$Height, [float]$Radius)

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($X, $Y, $diameter, $diameter, 180, 90)
    $path.AddArc($X + $Width - $diameter, $Y, $diameter, $diameter, 270, 90)
    $path.AddArc($X + $Width - $diameter, $Y + $Height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($X, $Y + $Height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function New-SimplifiedPngBytes {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)

    $inset = [Math]::Max(1, [Math]::Round($Size * 0.06))
    $side = $Size - (2 * $inset)
    $tile = New-RoundedRectanglePath $inset $inset $side $side ($Size * 0.22)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#06323E'))
    $graphics.FillPath($background, $tile)

    $ringInset = [Math]::Round($Size * 0.25)
    $ringSide = $Size - (2 * $ringInset)
    $ring = [System.Drawing.Pen]::new(
        [System.Drawing.ColorTranslator]::FromHtml('#35E8B0'),
        [Math]::Max(1.6, $Size * 0.13))
    $ring.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $ring.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $graphics.DrawArc($ring, $ringInset, $ringInset, $ringSide, $ringSide, 38, 278)

    $dotSize = [Math]::Max(1.5, $Size * 0.11)
    $dot = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#D4FFF3'))
    $graphics.FillEllipse($dot, $Size * 0.70, $Size * 0.22, $dotSize, $dotSize)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $dot.Dispose()
    $ring.Dispose()
    $background.Dispose()
    $tile.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

function New-ResizedPngBytes {
    param(
        [System.Drawing.Image]$Source,
        [int]$Size
    )

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $bitmap.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $graphics.DrawImage($Source, 0, 0, $Size, $Size)

    $stream = [System.IO.MemoryStream]::new()
    $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    $stream.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
    return ,$bytes
}

$resolvedSource = (Resolve-Path -LiteralPath $SourceImage -ErrorAction Stop).Path
[System.IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$source = [System.Drawing.Image]::FromFile($resolvedSource)
try {
    $sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
    $images = foreach ($size in $sizes) {
        $bytes = if ($size -le 20) {
            New-SimplifiedPngBytes -Size $size
        }
        else {
            New-ResizedPngBytes -Source $source -Size $size
        }
        [pscustomobject]@{ Size = $size; Bytes = $bytes }
    }

    [System.IO.File]::WriteAllBytes(
        (Join-Path $OutputDirectory 'LiQuota.png'),
        (New-ResizedPngBytes -Source $source -Size 1024))

    $iconPath = Join-Path $OutputDirectory 'LiQuota.ico'
    $file = [System.IO.File]::Create($iconPath)
    $writer = [System.IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$images.Count)

        $offset = 6 + (16 * $images.Count)
        foreach ($image in $images) {
            $dimension = if ($image.Size -ge 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }

        foreach ($image in $images) {
            $writer.Write([byte[]]$image.Bytes)
        }
    }
    finally {
        $writer.Dispose()
        $file.Dispose()
    }

    Write-Output $iconPath
}
finally {
    $source.Dispose()
}
