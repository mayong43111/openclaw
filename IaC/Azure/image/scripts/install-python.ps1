# install-python.ps1 - Install Python (silent installer + pip)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$version = $env:PYTHON_VERSION
Write-Output "=== Installing Python $version ==="

$exeUrl  = "https://www.python.org/ftp/python/$version/python-$version-amd64.exe"
$exePath = "C:\python-install.exe"

$maxRetries = 3
for ($i = 1; $i -le $maxRetries; $i++) {
    try {
        Write-Output "Download attempt $i/$maxRetries : $exeUrl"
        Invoke-WebRequest -Uri $exeUrl -OutFile $exePath -UseBasicParsing -TimeoutSec 120
        Write-Output "Download succeeded."
        break
    } catch {
        Write-Output "Download failed: $_"
        if ($i -eq $maxRetries) { throw "Failed to download Python after $maxRetries attempts" }
        Start-Sleep -Seconds 10
    }
}

# Silent install: add to PATH, install for all users, include pip
Write-Output "Installing..."
Start-Process $exePath -ArgumentList "/quiet InstallAllUsers=1 PrependPath=1 Include_pip=1 Include_launcher=1" -Wait -NoNewWindow

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Verify
$pyVer  = & python --version 2>&1
$pipVer = & pip --version 2>&1
Write-Output "Python: $pyVer"
Write-Output "pip: $pipVer"

# Install commonly used Python packages
Write-Output "Installing common Python packages..."
& pip install --quiet --no-warn-script-location `
    virtualenv `
    requests `
    playwright

# Install Playwright browsers (Chromium only, for browser automation)
Write-Output "Installing Playwright Chromium browser..."
& python -m playwright install chromium --with-deps

# Cleanup
Remove-Item $exePath -Force -ErrorAction SilentlyContinue
Write-Output "=== Python installation complete ==="
