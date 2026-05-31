<#
.SYNOPSIS
    Installs or updates PhotoWell from an unsigned MSIX bundle (sideloading).

.DESCRIPTION
    On Windows 11 22H2 (build 22621) and later, MSIX packages can be installed
    without a code-signing certificate via Add-AppxPackage -AllowUnsigned.

    On Windows 10 or pre-22H2 Windows 11, an unsigned package cannot be
    installed.  The script explains the options and exits cleanly.

    This script installs for the CURRENT USER only (no administrator rights
    required for per-user MSIX).

.PARAMETER Bundle
    Path to the .msixbundle file.  If omitted, the script looks for the
    newest bundle in the same directory.

.EXAMPLE
    .\install-sideload.ps1
    .\install-sideload.ps1 -Bundle "C:\Downloads\PhotoWell_1.0.0.0.msixbundle"
#>
param(
    [string] $Bundle = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Locate bundle ─────────────────────────────────────────────────────────────
if (-not $Bundle) {
    $Bundle = Get-ChildItem "$PSScriptRoot\output\*.msixbundle" -ErrorAction SilentlyContinue |
              Sort-Object LastWriteTime -Descending |
              Select-Object -First 1 -ExpandProperty FullName
}

if (-not $Bundle -or -not (Test-Path $Bundle)) {
    Write-Error @"
No .msixbundle found.

Either:
  1. Run installer\build-msix.ps1 first, then re-run this script, or
  2. Pass the path explicitly:  .\install-sideload.ps1 -Bundle "path\to\file.msixbundle"
"@
    exit 1
}

Write-Host "`nPhotoWell Installer"
Write-Host "  Package : $(Split-Path $Bundle -Leaf)"

# ── Windows version gate ──────────────────────────────────────────────────────
$osVer  = [System.Environment]::OSVersion.Version
$build  = $osVer.Build

if ($build -lt 22000) {
    # Windows 10 — unsigned MSIX not supported by Add-AppxPackage
    Write-Warning @"

You are running Windows 10 (build $build).
Unsigned MSIX sideloading requires Windows 11 22H2 (build 22621) or later.

Options:
  A) Upgrade to Windows 11 22H2+ and re-run this script.

  B) Sign the package with a self-signed certificate (developer / testing only):
       1. In PowerShell (run as Administrator):
            `$cert = New-SelfSignedCertificate -Type Custom -Subject "CN=Luminary Software" ``
                     -KeyUsage DigitalSignature -FriendlyName "PhotoWell Dev" ``
                     -CertStoreLocation "Cert:\CurrentUser\My" ``
                     -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}")
       2. Export and trust the cert:
            Export-Certificate -Cert `$cert -FilePath PhotoWell.cer
            Import-Certificate -FilePath PhotoWell.cer -CertStoreLocation Cert:\LocalMachine\Root
       3. Rebuild:  installer\build-msix.ps1 -SignThumbprint `$cert.Thumbprint
       4. Re-run this script.
"@
    exit 1
}

if ($build -lt 22621) {
    # Windows 11 before 22H2 — AllowUnsigned may not work; warn but attempt
    Write-Warning "Windows 11 build $build detected. -AllowUnsigned works reliably from build 22621. Attempting anyway…"
}

# ── Install ───────────────────────────────────────────────────────────────────
Write-Host "  Installing…"
try {
    # ForceApplicationShutdown closes any running PhotoWell instance before updating.
    Add-AppxPackage -Path $Bundle -AllowUnsigned -ForceApplicationShutdown
}
catch {
    Write-Error @"
Installation failed: $_

If the error mentions "Deployment failed with HRESULT: 0x80073CF3" (package update conflict):
  Run this first to remove the existing installation:
    Get-AppxPackage *PhotoWell* | Remove-AppxPackage
  Then re-run this script.
"@
    exit 1
}

Write-Host @"

  PhotoWell installed successfully.
  Launch it from the Start menu — search for "PhotoWell".
"@
