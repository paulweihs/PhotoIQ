<#
.SYNOPSIS
    Generates the required MSIX visual asset PNGs from the app's icon.ico.

.DESCRIPTION
    MSIX requires several PNG assets at specific pixel dimensions. This script
    extracts the app icon and scales it into each required canvas using
    System.Drawing, preserving aspect ratio and centring on the app's dark
    background colour (#1E1E2E — Catppuccin Mocha Base).

    Run once before the first build. Regenerate after updating the icon.
    The generated files are small (<50 KB each) and should be checked into
    source control under installer/assets/.

.PARAMETER OutDir
    Destination folder for generated PNGs.  Default: installer\assets.

.EXAMPLE
    .\New-AppAssets.ps1
    .\New-AppAssets.ps1 -OutDir C:\custom\assets
#>
param(
    [string] $OutDir = "$PSScriptRoot\assets"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

# Prefer the high-res branding PNG over the ICO — System.Drawing's ICO loader
# doesn't handle PNG-compressed icons reliably on all .NET versions.
$brandingPng = "$PSScriptRoot\..\branding\icon-256.png"
$iconPath    = if (Test-Path $brandingPng) { Resolve-Path $brandingPng }
               else { Resolve-Path "$PSScriptRoot\..\src\PhotoWell.Desktop\icon.ico" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Write-Host "Source    : $iconPath"
Write-Host "Output dir: $OutDir`n"

# MSIX required assets and their canvas sizes.
# SplashScreen is shown by Windows during app launch — wider than tall.
$assets = @(
    @{ Name = "Square44x44Logo.png";    W = 44;  H = 44  }
    @{ Name = "Square150x150Logo.png";  W = 150; H = 150 }
    @{ Name = "Wide310x150Logo.png";    W = 310; H = 150 }
    @{ Name = "Square310x310Logo.png";  W = 310; H = 310 }
    @{ Name = "StoreLogo.png";          W = 50;  H = 50  }
    @{ Name = "SplashScreen.png";       W = 620; H = 300 }
)

# App background colour — Catppuccin Mocha Base (#1E1E2E)
$bgColor = [System.Drawing.Color]::FromArgb(0x1E, 0x1E, 0x2E)

$source = New-Object System.Drawing.Bitmap([string]$iconPath)

try {
    foreach ($asset in $assets) {
        $outPath = Join-Path $OutDir $asset.Name

        $canvas = New-Object System.Drawing.Bitmap($asset.W, $asset.H,
            [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [System.Drawing.Graphics]::FromImage($canvas)
        try {
            $g.Clear($bgColor)
            $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

            # Scale icon to 85 % of the canvas, centred, preserving aspect ratio
            $scale = [Math]::Min($asset.W / $source.Width, $asset.H / $source.Height) * 0.85
            $drawW = [int]($source.Width  * $scale)
            $drawH = [int]($source.Height * $scale)
            $x     = [int](($asset.W - $drawW) / 2)
            $y     = [int](($asset.H - $drawH) / 2)

            $destRect = New-Object System.Drawing.Rectangle($x, $y, $drawW, $drawH)
            $g.DrawImage($source, $destRect)
        } finally {
            $g.Dispose()
        }

        $canvas.Save($outPath, [System.Drawing.Imaging.ImageFormat]::Png)
        $canvas.Dispose()

        $kb = [Math]::Round((Get-Item $outPath).Length / 1KB, 1)
        Write-Host ("  {0,-26} {1,4}x{2,-4}  {3} KB" -f $asset.Name, $asset.W, $asset.H, $kb)
    }
} finally {
    $source.Dispose()
}

Write-Host "`nDone. Assets written to: $OutDir"
