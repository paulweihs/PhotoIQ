<#
.SYNOPSIS
    Publishes PhotoWell for x64 and arm64, packages each into a signed or
    unsigned .msix, and bundles both into a single .msixbundle.

.DESCRIPTION
    Produces artifacts ready for:
      • Microsoft Store upload  — upload the .msixbundle to Partner Center;
                                   they handle signing.
      • Sideload (direct DL)   — use installer\install-sideload.ps1 on
                                   Windows 11 22H2+, or sign with a cert.

    Prerequisites:
      • .NET 8 SDK
      • Windows 10 SDK (makeappx.exe / makepri.exe) — installed via
        VS Installer → Individual Components → "Windows 10 SDK (10.0.19041.0)"
        or later.

.PARAMETER Version
    Package version — Major.Minor.Build.Revision (e.g. "1.2.0.0").
    Default: "1.0.0.0".

.PARAMETER Configuration
    dotnet build configuration.  Default: Release.

.PARAMETER SignThumbprint
    SHA-1 thumbprint of a code-signing cert in the Current User certificate
    store.  If empty, packages are built unsigned.

.PARAMETER OutDir
    Output folder for .msix and .msixbundle files.
    Default: installer\output.

.EXAMPLE
    # Unsigned build (Store upload or Win11 sideload)
    .\build-msix.ps1

    # Signed build
    .\build-msix.ps1 -Version 1.2.0.0 -SignThumbprint ABCDEF0123456789...

    # Clean build (removes previous publish/package/bundle dirs first)
    .\build-msix.ps1 -Clean

    # Clean + signed release
    .\build-msix.ps1 -Version 1.2.0.0 -SignThumbprint ABCDEF0123456789... -Clean
#>
param(
    [string] $Version        = "1.0.0.0",
    [string] $Configuration  = "Release",
    [string] $SignThumbprint = "",
    [string] $OutDir         = "",
    [switch] $Clean
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Paths ─────────────────────────────────────────────────────────────────────
$RepoRoot     = (Resolve-Path "$PSScriptRoot\..")
$SrcProject   = "$RepoRoot\src\PhotoWell.Desktop\PhotoWell.Desktop.csproj"
$ManifestTpl  = "$PSScriptRoot\AppxManifest.template.xml"
$AssetsScript = "$PSScriptRoot\New-AppAssets.ps1"
$AssetsDir    = "$PSScriptRoot\assets"
if (-not $OutDir) { $OutDir = "$PSScriptRoot\output" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# ── Optional clean ────────────────────────────────────────────────────────────
if ($Clean) {
    Write-Host "`nCleaning previous publish/package/bundle artifacts…"
    Remove-Item -Recurse -Force "$PSScriptRoot\publish" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "$PSScriptRoot\package" -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force "$PSScriptRoot\bundle"  -ErrorAction SilentlyContinue
    Write-Host "  Clean complete."
}

# ── Validate version ──────────────────────────────────────────────────────────
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must be in Major.Minor.Build.Revision format (e.g. 1.0.0.0). Got: '$Version'"
}

Write-Host @"

╔══════════════════════════════════════════════════════╗
║  PhotoWell  MSIX Build  v$Version
╚══════════════════════════════════════════════════════╝
  Config    : $Configuration
  Signing   : $(if ($SignThumbprint) { "cert $SignThumbprint" } else { "UNSIGNED" })
  Output    : $OutDir
"@

# ── Locate Windows SDK tools ──────────────────────────────────────────────────
function Find-SdkTool([string] $ToolName) {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    )
    foreach ($root in $roots) {
        if (-not (Test-Path $root)) { continue }
        $hit = Get-ChildItem "$root\*\x64\$ToolName" -ErrorAction SilentlyContinue |
               Sort-Object {
                   [version]($_.FullName -replace '^.*\\(\d+\.\d+\.\d+\.\d+)\\.*$', '$1')
               } -Descending |
               Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }
    throw @"
Could not find $ToolName.
Install the Windows 10 SDK via VS Installer → Individual Components → "Windows 10 SDK".
"@
}

$MakeAppx = Find-SdkTool "makeappx.exe"
$MakePri  = Find-SdkTool "makepri.exe"
$SignTool  = if ($SignThumbprint) { Find-SdkTool "signtool.exe" } else { $null }

Write-Host "  makeappx : $MakeAppx"
Write-Host "  makepri  : $MakePri"

# ── Generate visual assets ────────────────────────────────────────────────────
$requiredAssets = @("Square44x44Logo.png", "Square150x150Logo.png", "StoreLogo.png")
$missingAssets  = $requiredAssets | Where-Object { -not (Test-Path "$AssetsDir\$_") }
if ($missingAssets) {
    if (-not (Test-Path $AssetsScript)) {
        throw "Asset generator not found at '$AssetsScript'. Cannot generate: $($missingAssets -join ', ')"
    }
    Write-Host "`nGenerating MSIX visual assets ($($missingAssets.Count) missing)…"
    & $AssetsScript -OutDir $AssetsDir
    if ($LASTEXITCODE -ne 0) { throw "New-AppAssets.ps1 failed — check that icon.ico is present in the installer directory" }
} else {
    Write-Host "`nAssets already present — skipping generation."
}

# ── Helper: sign a file if thumbprint was provided ────────────────────────────
function Invoke-Sign([string] $Path) {
    if (-not $SignThumbprint) { return }
    Write-Host "  Signing $(Split-Path $Path -Leaf)…"
    & $SignTool sign /sha1 $SignThumbprint /fd SHA256 `
        /tr http://timestamp.digicert.com /td SHA256 $Path
    if ($LASTEXITCODE -ne 0) { throw "signtool failed for: $Path" }
}

# ── Build each architecture ───────────────────────────────────────────────────
$Architectures = @("x64", "arm64")
$PackageFiles  = [System.Collections.Generic.List[string]]::new()

foreach ($arch in $Architectures) {
    Write-Host "`n┌─ $arch ─────────────────────────────────────────────────────"

    $publishDir = "$PSScriptRoot\publish\$arch"
    $pkgDir     = "$PSScriptRoot\package\$arch"
    $msixOut    = "$OutDir\PhotoWell_${Version}_${arch}.msix"

    # 1. Publish self-contained
    Write-Host "│  Publishing…"
    Remove-Item -Recurse -Force $publishDir -ErrorAction SilentlyContinue
    dotnet publish $SrcProject `
        --configuration $Configuration `
        --runtime "win-$arch" `
        --self-contained true `
        --output $publishDir `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:GenerateDocumentationFile=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($arch)" }

    # 2. Assemble staging directory
    Write-Host "│  Assembling package directory…"
    Remove-Item -Recurse -Force $pkgDir -ErrorAction SilentlyContinue
    Copy-Item -Recurse $publishDir $pkgDir

    # 3. Assets
    $assetsTarget = "$pkgDir\Assets"
    New-Item -ItemType Directory -Force -Path $assetsTarget | Out-Null
    Copy-Item "$AssetsDir\*" $assetsTarget -Force

    # 4. Stamp manifest
    $manifest = (Get-Content $ManifestTpl -Raw) `
                    -replace '\{\{VERSION\}\}',      $Version `
                    -replace '\{\{ARCHITECTURE\}\}', $arch
    [System.IO.File]::WriteAllText("$pkgDir\AppxManifest.xml", $manifest,
        [System.Text.Encoding]::UTF8)

    # 5. Generate Package Resource Index
    Write-Host "│  Generating PRI resources…"
    Push-Location $pkgDir
    try {
        & $MakePri createconfig /cf priconfig.xml /dq en-US /o 2>&1 | Out-Null
        & $MakePri new /pr . /cf priconfig.xml /mn AppxManifest.xml /o /of resources.pri 2>&1 | Out-Null
        Remove-Item priconfig.xml -ErrorAction SilentlyContinue
    } finally {
        Pop-Location
    }

    # 6. Pack
    Write-Host "│  Packing MSIX…"
    & $MakeAppx pack /d $pkgDir /p $msixOut /o /nv
    if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($arch)" }

    # 7. Sign (if requested)
    Invoke-Sign $msixOut

    $sizeMb = [Math]::Round((Get-Item $msixOut).Length / 1MB, 1)
    Write-Host "└─ Done: $(Split-Path $msixOut -Leaf)  ($sizeMb MB)"
    $PackageFiles.Add($msixOut)
}

# ── Bundle both architectures ─────────────────────────────────────────────────
Write-Host "`n┌─ Bundle ────────────────────────────────────────────────────────"
$bundleStage = "$PSScriptRoot\bundle"
$bundleOut   = "$OutDir\PhotoWell_${Version}.msixbundle"

Remove-Item -Recurse -Force $bundleStage -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $bundleStage | Out-Null
foreach ($pkg in $PackageFiles) { Copy-Item $pkg $bundleStage -Force }

& $MakeAppx bundle /d $bundleStage /p $bundleOut /o
if ($LASTEXITCODE -ne 0) { throw "makeappx bundle failed" }

Invoke-Sign $bundleOut

$bundleMb = [Math]::Round((Get-Item $bundleOut).Length / 1MB, 1)
Write-Host "└─ Bundle: $(Split-Path $bundleOut -Leaf)  ($bundleMb MB)"

# ── Summary ───────────────────────────────────────────────────────────────────
Write-Host @"

╔══════════════════════════════════════════════════════╗
║  Build complete — PhotoWell $Version
╚══════════════════════════════════════════════════════╝
"@
Write-Host "  Bundle  : $bundleOut"
foreach ($p in $PackageFiles) {
    Write-Host "  Package : $p"
}

if (-not $SignThumbprint) {
    Write-Host @"

  ⚠  Packages are UNSIGNED.

  Microsoft Store:
    Upload PhotoWell_${Version}.msixbundle to Partner Center.
    They sign it — no cert needed from you.

  Sideload (direct download):
    Run installer\install-sideload.ps1  (Windows 11 22H2+)
    Or sign: build-msix.ps1 -SignThumbprint <SHA1>
"@
}
