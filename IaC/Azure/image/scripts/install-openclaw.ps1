# install-openclaw.ps1 - Install OpenClaw globally via npm
# IMPORTANT: We set npm global prefix to C:\openclaw so the install survives
# Sysprep (which deletes per-user profiles like C:\Users\packer\AppData).
$ErrorActionPreference = "Stop"

$version = $env:OPENCLAW_VERSION
Write-Output "=== Installing OpenClaw $version ==="

# Ensure npm is on PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Set npm global prefix to a system-wide directory that survives Sysprep
$globalPrefix = "C:\openclaw"
New-Item -ItemType Directory -Path $globalPrefix -Force | Out-Null
& "C:\Program Files\nodejs\npm.cmd" config set prefix $globalPrefix
Write-Output "npm global prefix set to: $globalPrefix"

# Install globally with --legacy-peer-deps (required on Windows to avoid dependency conflicts)
# npm writes deprecation warnings to stderr which PowerShell treats as errors with $ErrorActionPreference=Stop
$ErrorActionPreference = "Continue"
& "C:\Program Files\nodejs\npm.cmd" install -g "openclaw@$version" --legacy-peer-deps 2>&1 | Write-Output
$npmExit = $LASTEXITCODE
$ErrorActionPreference = "Stop"

if ($npmExit -ne 0) {
    Write-Error "npm install failed with exit code $npmExit"
    exit 1
}

# Add C:\openclaw to system PATH so all users can find openclaw
$machinePath = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
if ($machinePath -notlike "*$globalPrefix*") {
    [System.Environment]::SetEnvironmentVariable("Path", "$machinePath;$globalPrefix", "Machine")
    Write-Output "Added $globalPrefix to system PATH"
}
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Verify
$env:Path = "$globalPrefix;$env:Path"
$ErrorActionPreference = "Continue"
$clawVer = & cmd /c "openclaw --version" 2>&1
$ErrorActionPreference = "Stop"
Write-Output "OpenClaw: $clawVer"

# Install Playwright Chromium browser binary (required by OpenClaw browser tools)
# playwright-core is already a core dependency of openclaw; we just need the browser binary.
Write-Output "=== Installing Playwright Chromium browser ==="
$ErrorActionPreference = "Continue"
& node "$globalPrefix\node_modules\openclaw\node_modules\playwright-core\cli.js" install --with-deps chromium 2>&1 | Write-Output
$ErrorActionPreference = "Stop"
Write-Output "Playwright Chromium installed."

# Install clawhub (skill registry CLI)
Write-Output "=== Installing clawhub ==="
$ErrorActionPreference = "Continue"
& "C:\Program Files\nodejs\npm.cmd" install -g clawhub --legacy-peer-deps 2>&1 | Write-Output
$ErrorActionPreference = "Stop"
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
$ErrorActionPreference = "Continue"
$hubVer = & cmd /c "clawhub help" 2>&1 | Select-Object -First 1
$ErrorActionPreference = "Stop"
Write-Output "clawhub: installed ($hubVer)"

# Verify node:sqlite (built into Node 22+)
Write-Output "=== Verifying native dependencies ==="
$env:Path = "$globalPrefix;$env:Path"
$ErrorActionPreference = "Continue"
$result = & cmd /c "node -e `"require('node:sqlite'); console.log('node:sqlite OK')`"" 2>&1
if ($LASTEXITCODE -eq 0) {
    Write-Output "  node:sqlite: OK"
} else {
    Write-Output "  WARNING: node:sqlite failed to load (non-fatal): $result"
}
$ErrorActionPreference = "Stop"

Write-Output "=== OpenClaw installation complete ==="
exit 0
