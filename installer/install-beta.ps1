<#
.SYNOPSIS
    PhotoWell beta installer for trusted testers.

.DESCRIPTION
    Trusts the included developer certificate (requires admin — you will see a UAC prompt)
    then installs PhotoWell from the bundled .msixbundle.

    Run by right-clicking this file and choosing "Run with PowerShell".
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path $MyInvocation.MyCommand.Path
$Bundle    = Join-Path $ScriptDir "PhotoWell_1.0.0.0.msixbundle"
$Cert      = Join-Path $ScriptDir "PhotoWell-dev.cer"

if (-not (Test-Path $Bundle)) { Write-Error "Bundle not found: $Bundle"; exit 1 }
if (-not (Test-Path $Cert))   { Write-Error "Certificate not found: $Cert"; exit 1 }

Write-Host ""
Write-Host "PhotoWell Beta Installer"
Write-Host "========================"
Write-Host ""

# -- Step 1: Trust the certificate (requires admin) ----------------------------
Write-Host "Step 1/2  Trusting developer certificate..."
Write-Host "           (Windows will ask for administrator permission)"
Write-Host ""

$importCmd = "Import-Certificate -FilePath '$Cert' -CertStoreLocation Cert:\LocalMachine\Root"
$result = Start-Process powershell.exe `
    -ArgumentList "-NoProfile -NonInteractive -Command $importCmd" `
    -Verb RunAs `
    -Wait `
    -PassThru

if ($result.ExitCode -ne 0) {
    Write-Error "Certificate trust step failed or was cancelled. Installation aborted."
    exit 1
}

Write-Host "  Certificate trusted."
Write-Host ""

# -- Step 2: Install the package -----------------------------------------------
Write-Host "Step 2/2  Installing PhotoWell..."

# Remove any existing installation first to avoid update conflicts.
$existing = Get-AppxPackage -Name "LuminarySoftware.PhotoWell" -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "  Removing previous installation..."
    Remove-AppxPackage -Package $existing.PackageFullName
}

Add-AppxPackage -Path $Bundle -ForceApplicationShutdown

Write-Host ""
Write-Host "  PhotoWell installed successfully!"
Write-Host "  Launch it from the Start menu -- search for 'PhotoWell'."
Write-Host ""
Write-Host "Press any key to close..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
