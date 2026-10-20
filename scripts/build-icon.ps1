[CmdletBinding()]
param()

. (Join-Path $PSScriptRoot 'common.ps1')
Add-Type -AssemblyName System.Drawing

# Keep the generated artwork unchanged; encode Windows icon sizes from its alpha PNG.
$source = Join-Path $script:MountloomRoot 'src/Mountloom.App/Assets/Mountloom.png'
$destination = Assert-MountloomChildPath (Join-Path $script:MountloomRoot 'src/Mountloom.App/Assets/Mountloom.ico')
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$artwork = [Drawing.Bitmap]::FromFile($source)
$frames = [Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [Drawing.Bitmap]::new($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([Drawing.Color]::Transparent)
            $graphics.CompositingMode = [Drawing.Drawing2D.CompositingMode]::SourceCopy
            $graphics.CompositingQuality = [Drawing.Drawing2D.CompositingQuality]::HighQuality
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.DrawImage($artwork, [Drawing.Rectangle]::new(0, 0, $size, $size))
            $bitmap.Save($buffer, [Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($buffer.ToArray())
        }
        finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }

    $output = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($output)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($index = 0; $index -lt $sizes.Count; $index++) {
            $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$index].Length)
            $writer.Write([uint32]$offset)
            $offset += $frames[$index].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
        $writer.Flush()
        [IO.File]::WriteAllBytes($destination, $output.ToArray())
    }
    finally { $writer.Dispose(); $output.Dispose() }
}
finally { $artwork.Dispose() }
Write-Host "Windows icon: $destination ($($sizes -join ', ') px)"
