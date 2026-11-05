#requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$svg = New-Object Xml.XmlDocument
$svg.XmlResolver = $null
$svg.Load((Join-Path $PSScriptRoot 'Cultivate.svg'))
if ($svg.DocumentElement.GetAttribute('viewBox') -ne '0 0 512 512') {
    throw 'The icon renderer expects a 512 by 512 SVG viewBox.'
}

function ConvertTo-DrawingPath([string]$Data) {
    $tokens = [regex]::Matches($Data, '[A-Za-z]|-?\d+(?:\.\d+)?')
    $path = New-Object Drawing.Drawing2D.GraphicsPath
    $index = 0
    [single]$x = 0
    [single]$y = 0
    while ($index -lt $tokens.Count) {
        $command = $tokens[$index++].Value
        $count = switch -CaseSensitive ($command) { 'M' { 2 } 'L' { 2 } 'C' { 6 } 'Z' { 0 } default { throw "Unsupported SVG path command: $command" } }
        $values = @()
        for ($number = 0; $number -lt $count; $number++) {
            $values += [single]::Parse($tokens[$index++].Value, [Globalization.CultureInfo]::InvariantCulture)
        }
        switch ($command) {
            'M' { $path.StartFigure(); $x = $values[0]; $y = $values[1] }
            'L' { $path.AddLine($x, $y, $values[0], $values[1]); $x = $values[0]; $y = $values[1] }
            'C' { $path.AddBezier($x, $y, $values[0], $values[1], $values[2], $values[3], $values[4], $values[5]); $x = $values[4]; $y = $values[5] }
            'Z' { $path.CloseFigure() }
        }
    }
    return $path
}

function New-IconBitmap([int]$Size) {
    $working = New-Object Drawing.Bitmap ($Size * 4), ($Size * 4), ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [Drawing.Graphics]::FromImage($working)
    try {
        $graphics.Clear([Drawing.Color]::FromArgb(0, 0, 0, 0))
        $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.ScaleTransform(($Size * 4 / 512.0), ($Size * 4 / 512.0))
        foreach ($node in $svg.DocumentElement.ChildNodes) {
            if ($node.NodeType -ne [Xml.XmlNodeType]::Element -or $node.LocalName -in @('title', 'desc')) { continue }
            $fill = $node.GetAttribute('fill')
            $brush = $null
            if ($fill -and $fill -ne 'none') {
                $brush = New-Object Drawing.SolidBrush ([Drawing.ColorTranslator]::FromHtml($fill))
            }
            try {
                switch ($node.LocalName) {
                    'circle' {
                        [single]$radius = $node.GetAttribute('r')
                        $graphics.FillEllipse($brush, ([single]$node.GetAttribute('cx') - $radius),
                            ([single]$node.GetAttribute('cy') - $radius), ($radius * 2), ($radius * 2))
                    }
                    'path' {
                        $path = ConvertTo-DrawingPath $node.GetAttribute('d')
                        try {
                            if ($null -ne $brush) { $graphics.FillPath($brush, $path) }
                            $stroke = $node.GetAttribute('stroke')
                            if ($stroke) {
                                $pen = New-Object Drawing.Pen ([Drawing.ColorTranslator]::FromHtml($stroke)), ([single]$node.GetAttribute('stroke-width'))
                                try {
                                    $pen.StartCap = [Drawing.Drawing2D.LineCap]::Round
                                    $pen.EndCap = [Drawing.Drawing2D.LineCap]::Round
                                    $pen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
                                    $graphics.DrawPath($pen, $path)
                                } finally { $pen.Dispose() }
                            }
                        } finally { $path.Dispose() }
                    }
                    default { throw "Unsupported SVG element: $($node.LocalName)" }
                }
            } finally { if ($null -ne $brush) { $brush.Dispose() } }
        }
        $bitmap = New-Object Drawing.Bitmap $Size, $Size, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $resize = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $resize.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $resize.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $resize.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $resize.DrawImage($working, 0, 0, $Size, $Size)
        } finally { $resize.Dispose() }
        return $bitmap
    } finally { $graphics.Dispose(); $working.Dispose() }
}

function Get-IconPayload([Drawing.Bitmap]$Bitmap) {
    $stream = New-Object IO.MemoryStream
    $writer = $null
    try {
        $size = $Bitmap.Width
        if ($size -eq 256) {
            $Bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
        } else {
            $writer = New-Object IO.BinaryWriter $stream
            $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
            $pixels = New-Object byte[] ($size * $size * 4)
            $mask = New-Object byte[] ($maskStride * $size)
            # ICO DIB rows are bottom-up; their doubled height includes the transparency mask.
            for ($y = 0; $y -lt $size; $y++) {
                for ($x = 0; $x -lt $size; $x++) {
                    $pixel = $Bitmap.GetPixel($x, $y)
                    $offset = (($size - 1 - $y) * $size + $x) * 4
                    $pixels[$offset] = $pixel.B
                    $pixels[$offset + 1] = $pixel.G
                    $pixels[$offset + 2] = $pixel.R
                    $pixels[$offset + 3] = $pixel.A
                    if ($pixel.A -eq 0) {
                        $maskIndex = ($size - 1 - $y) * $maskStride + [int][Math]::Floor($x / 8.0)
                        $mask[$maskIndex] = $mask[$maskIndex] -bor (1 -shl (7 - $x % 8))
                    }
                }
            }
            $writer.Write([int]40)
            $writer.Write([int]$size)
            $writer.Write([int]($size * 2))
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([int]0)
            $writer.Write([int]($pixels.Length + $mask.Length))
            foreach ($value in @(0, 0, 0, 0)) { $writer.Write([int]$value) }
            $writer.Write([byte[]]$pixels)
            $writer.Write([byte[]]$mask)
            $writer.Flush()
        }
        return ,$stream.ToArray()
    } finally {
        if ($null -ne $writer) { $writer.Dispose() }
        $stream.Dispose()
    }
}

$preview = New-IconBitmap 512
try { $preview.Save((Join-Path $PSScriptRoot 'Cultivate.png'), [Drawing.Imaging.ImageFormat]::Png) }
finally { $preview.Dispose() }

$entries = @()
foreach ($size in @(16, 20, 24, 32, 40, 48, 64, 128, 256)) {
    $bitmap = New-IconBitmap $size
    try { $entries += [pscustomobject]@{ Size = $size; Bytes = (Get-IconPayload $bitmap) } }
    finally { $bitmap.Dispose() }
}
$file = [IO.File]::Create((Join-Path $PSScriptRoot 'Cultivate.ico'))
$writer = New-Object IO.BinaryWriter $file
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$entries.Count)
    [uint32]$offset = 6 + 16 * $entries.Count
    foreach ($entry in $entries) {
        $writer.Write([byte]($entry.Size % 256))
        $writer.Write([byte]($entry.Size % 256))
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$entry.Bytes.Length)
        $writer.Write($offset)
        $offset += $entry.Bytes.Length
    }
    foreach ($entry in $entries) { $writer.Write([byte[]]$entry.Bytes) }
} finally { $writer.Dispose(); $file.Dispose() }
Write-Host 'Created Cultivate.png (512px) and Cultivate.ico (16-256px) from Cultivate.svg.'
