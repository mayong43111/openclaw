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

Write-Output "=== OpenClaw installation complete ==="
