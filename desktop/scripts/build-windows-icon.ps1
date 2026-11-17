param(
  [string]$Source = (Join-Path $PSScriptRoot '..\src\assets\velia-life-orb.png'),
  [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\build')
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($outputPath) | Out-Null

$sourceImage = [System.Drawing.Image]::FromFile($sourcePath)
$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngPayloads = [System.Collections.Generic.List[byte[]]]::new()

try {
  foreach ($size in $sizes) {
    $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    try {
      $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
      try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.DrawImage($sourceImage, 0, 0, $size, $size)
      }
      finally {
        $graphics.Dispose()
      }

      $memory = [System.IO.MemoryStream]::new()
      try {
        $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
        $pngPayloads.Add($memory.ToArray())
      }
      finally {
        $memory.Dispose()
      }

      if ($size -eq 32) {
        $bitmap.Save((Join-Path $outputPath 'tray-icon.png'), [System.Drawing.Imaging.ImageFormat]::Png)
      }
    }
    finally {
      $bitmap.Dispose()
    }
  }
}
finally {
  $sourceImage.Dispose()
}

$iconStream = [System.IO.MemoryStream]::new()
$writer = [System.IO.BinaryWriter]::new($iconStream)
try {
  $writer.Write([uint16]0)
  $writer.Write([uint16]1)
  $writer.Write([uint16]$sizes.Count)

  $payloadOffset = 6 + (16 * $sizes.Count)
  for ($index = 0; $index -lt $sizes.Count; $index += 1) {
    $size = $sizes[$index]
    $payload = $pngPayloads[$index]
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]$(if ($size -eq 256) { 0 } else { $size }))
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$payload.Length)
    $writer.Write([uint32]$payloadOffset)
    $payloadOffset += $payload.Length
  }

  foreach ($payload in $pngPayloads) {
    $writer.Write($payload)
  }
  $writer.Flush()
  [System.IO.File]::WriteAllBytes((Join-Path $outputPath 'icon.ico'), $iconStream.ToArray())
}
finally {
  $writer.Dispose()
  $iconStream.Dispose()
}

Write-Output "Generated $(Join-Path $outputPath 'icon.ico')"
Write-Output "Generated $(Join-Path $outputPath 'tray-icon.png')"
