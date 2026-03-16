# install-node.ps1 - Install Node.js (silent MSI)
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$version = $env:NODE_VERSION
Write-Output "=== Installing Node.js v$version ==="

$msiUrl  = "https://nodejs.org/dist/v$version/node-v$version-x64.msi"
$msiPath = "C:\node-install.msi"

# Retry download up to 3 times
$maxRetries = 3
for ($i = 1; $i -le $maxRetries; $i++) {
    try {
        Write-Output "Download attempt $i/$maxRetries : $msiUrl"
        Invoke-WebRequest -Uri $msiUrl -OutFile $msiPath -UseBasicParsing -TimeoutSec 120
        Write-Output "Download succeeded."
        break
    } catch {
        Write-Output "Download failed: $_"
        if ($i -eq $maxRetries) { throw "Failed to download Node.js after $maxRetries attempts" }
        Start-Sleep -Seconds 10
    }
}

Write-Output "Installing..."
Start-Process msiexec.exe -ArgumentList "/i $msiPath /quiet /norestart" -Wait -NoNewWindow

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Verify
$nodeVer = & "C:\Program Files\nodejs\node.exe" -v
$npmVer  = & "C:\Program Files\nodejs\npm.cmd" -v
Write-Output "Node.js: $nodeVer"
Write-Output "npm: $npmVer"

# Cleanup
Remove-Item $msiPath -Force -ErrorAction SilentlyContinue
Write-Output "=== Node.js installation complete ==="
