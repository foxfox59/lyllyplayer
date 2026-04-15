$ErrorActionPreference = "Stop"

# Builds LyllyPlayer.ico from Assets\icon-{16,24,...,256}.png (run from any cwd).
$assets = Join-Path $PSScriptRoot "..\LyllyPlayer.App\Assets" | Resolve-Path
$outIco = Join-Path $assets "LyllyPlayer.ico"

$sizes = @(16, 24, 32, 48, 64, 128, 256)
$pngPaths = foreach ($s in $sizes) { Join-Path $assets ("icon-$s.png") }

foreach ($p in $pngPaths) {
  if (-not (Test-Path -LiteralPath $p)) {
    throw "Missing expected PNG: $p"
  }
}

function Write-U16LE([System.IO.BinaryWriter] $bw, [int] $v) { $bw.Write([UInt16]$v) }
function Write-U32LE([System.IO.BinaryWriter] $bw, [int] $v) { $bw.Write([UInt32]$v) }

$images = foreach ($p in $pngPaths) {
  [PSCustomObject]@{
    Path = $p
    Bytes = [System.IO.File]::ReadAllBytes($p)
    Size = [int]([System.IO.Path]::GetFileNameWithoutExtension($p) -replace 'icon-','')
  }
}

# ICO header:
# ICONDIR: Reserved(2)=0, Type(2)=1, Count(2)=n
# ICONDIRENTRY: 16 bytes each
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)

Write-U16LE $bw 0
Write-U16LE $bw 1
Write-U16LE $bw $images.Count

$dirEntriesStart = $ms.Position
for ($i=0; $i -lt $images.Count; $i++) {
  # Placeholder 16 bytes
  $bw.Write([byte[]](0..15 | ForEach-Object { 0 }))
}

$imageDataOffsets = @()
foreach ($img in $images) {
  $imageDataOffsets += [int]$ms.Position
  $bw.Write($img.Bytes)
}

# Patch directory entries
$ms.Position = $dirEntriesStart
for ($i=0; $i -lt $images.Count; $i++) {
  $img = $images[$i]
  $w = $img.Size
  $h = $img.Size

  # Width/Height are bytes; 0 means 256.
  $bw.Write([byte]($(if ($w -ge 256) { 0 } else { $w })))
  $bw.Write([byte]($(if ($h -ge 256) { 0 } else { $h })))
  $bw.Write([byte]0)  # ColorCount
  $bw.Write([byte]0)  # Reserved
  Write-U16LE $bw 1   # Planes
  Write-U16LE $bw 32  # BitCount (PNG payload, keep 32)
  Write-U32LE $bw $img.Bytes.Length
  Write-U32LE $bw $imageDataOffsets[$i]
}

$bw.Flush()
[System.IO.File]::WriteAllBytes($outIco, $ms.ToArray())

$bw.Dispose()
$ms.Dispose()

Write-Host "Wrote $outIco"
