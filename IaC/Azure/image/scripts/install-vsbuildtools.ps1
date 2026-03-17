# install-vsbuildtools.ps1 - Install Visual Studio 2022 Build Tools (C++ workload)
# Needed for compiling native Node.js modules (sharp, better-sqlite3, node-pty, etc.)

$ErrorActionPreference = "Stop"
Write-Output "=== Installing Visual Studio 2022 Build Tools ==="

# Install via Chocolatey with the C++ (VCTools) workload
$pkgParams = '--add Microsoft.VisualStudio.Workload.VCTools --includeRecommended --quiet --wait'
choco install visualstudio2022buildtools -y --timeout 1200 --package-parameters "$pkgParams"

if ($LASTEXITCODE -ne 0) {
    Write-Output "WARNING: VS Build Tools Chocolatey exit code $LASTEXITCODE (may still be OK for partial install)"
}

# Verify installation
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $installed = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($installed) {
        Write-Output "VS Build Tools installed at: $installed"
    } else {
        Write-Warning "vswhere found but VC Tools component not detected"
    }
} else {
    Write-Warning "vswhere not found - VS Build Tools may not have installed correctly"
}

# Set npm config to use the build tools for native compilation
npm config set msvs_version 2022

Write-Output "=== VS Build Tools installation complete ==="
exit 0
