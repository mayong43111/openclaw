# register-openclaw-service.ps1 - Register OpenClaw gateway as a Windows Service
# The service is registered but NOT started (no config initialized yet).
# After deploying a VM from this image, run `openclaw onboard` or
# `openclaw config set gateway.mode local` then `Start-Service OpenClawGateway`.
$ErrorActionPreference = "Stop"

# Refresh PATH to pick up tools installed by previous provisioners
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

Write-Output "=== Registering OpenClaw as Windows Service ==="

# OpenClaw is installed in C:\openclaw (system-wide, survives Sysprep)
$openclawDir = "C:\openclaw"
$nodeExe = "C:\Program Files\nodejs\node.exe"

Write-Output "Node: $nodeExe"
Write-Output "OpenClaw dir: $openclawDir"

# Install NSSM via Chocolatey (Chocolatey was installed in install-git.ps1)
Write-Output "Installing NSSM via Chocolatey..."
$ErrorActionPreference = "Continue"
choco install nssm -y --no-progress 2>&1 | Write-Output
$ErrorActionPreference = "Stop"

# Verify nssm is available
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
$nssmPath = Get-Command nssm -ErrorAction SilentlyContinue
if (-not $nssmPath) {
    Write-Error "NSSM not found after installation"
    exit 1
}
Write-Output "NSSM: $($nssmPath.Source)"

# Create a launcher script that sets PATH and runs openclaw
$launcherPath = "C:\openclaw-gateway.cmd"
@"
@echo off
set PATH=C:\Program Files\nodejs;$openclawDir;C:\Program Files\Git\bin;%PATH%
openclaw gateway run --bind loopback --port 18789
"@ | Set-Content -Path $launcherPath -Encoding ASCII

# Register the service with NSSM
$serviceName = "OpenClawGateway"

# Remove if exists (idempotent) - use Continue to ignore "service not found" errors
$ErrorActionPreference = "Continue"
nssm remove $serviceName confirm 2>&1 | Out-Null
$ErrorActionPreference = "Stop"

$ErrorActionPreference = "Continue"
nssm install $serviceName "cmd.exe" "/c $launcherPath" 2>&1 | Write-Output
nssm set $serviceName DisplayName "OpenClaw Gateway" 2>&1 | Write-Output
nssm set $serviceName Description "OpenClaw AI Gateway Service - configure before starting" 2>&1 | Write-Output
nssm set $serviceName Start SERVICE_DEMAND_START 2>&1 | Write-Output
nssm set $serviceName AppStdout "C:\openclaw-gateway-stdout.log" 2>&1 | Write-Output
nssm set $serviceName AppStderr "C:\openclaw-gateway-stderr.log" 2>&1 | Write-Output
nssm set $serviceName AppRotateFiles 1 2>&1 | Write-Output
nssm set $serviceName AppRotateBytes 10485760 2>&1 | Write-Output
nssm set $serviceName AppEnvironmentExtra "PATH=C:\Program Files\nodejs;$openclawDir;C:\Program Files\Git\bin" 2>&1 | Write-Output
$ErrorActionPreference = "Stop"

# Verify
$svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Output "Service '$serviceName' registered successfully (Status: $($svc.Status), StartType: $($svc.StartupType))"
} else {
    Write-Error "Failed to register service"
    exit 1
}

Write-Output ""
Write-Output "=== Configuring Windows Firewall ==="
New-NetFirewallRule -DisplayName "OpenClaw Gateway" -Direction Inbound -Protocol TCP -LocalPort 18789 -Action Allow -Profile Any
Write-Output "Firewall rule added for port 18789"

Write-Output ""
Write-Output "=== Service Registration Complete ==="
Write-Output ""
Write-Output "After deploying a VM from this image:"
Write-Output "  1. openclaw config set gateway.mode local"
Write-Output "     (or run: openclaw onboard)"
Write-Output "  2. sc config OpenClawGateway start= auto"
Write-Output "  3. Start-Service OpenClawGateway"
