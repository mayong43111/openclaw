# install-git.ps1 - Install Git for Windows via Chocolatey (most reliable on Azure VMs)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Output "=== Installing Git ==="

# Install Chocolatey if not present
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Output "Installing Chocolatey..."
    Set-ExecutionPolicy Bypass -Scope Process -Force
    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
}

# Install Git via Chocolatey (with retry for CDN timeouts)
Write-Output "Installing Git via Chocolatey..."
$maxRetries = 3
for ($i = 1; $i -le $maxRetries; $i++) {
    Write-Output "Attempt $i/$maxRetries..."
    choco install git -y --no-progress 2>&1
    if ($LASTEXITCODE -eq 0 -and (Test-Path "C:\Program Files\Git\bin\git.exe")) {
        Write-Output "Git installed successfully on attempt $i"
        break
    }
    if ($i -lt $maxRetries) {
        Write-Output "Git install failed (exit code $LASTEXITCODE), retrying in 30s..."
        Start-Sleep -Seconds 30
    } else {
        Write-Error "Git installation failed after $maxRetries attempts"
        exit 1
    }
}

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Verify
$gitVer = & "C:\Program Files\Git\bin\git.exe" --version
Write-Output "Git: $gitVer"

Write-Output "=== Git installation complete ==="
