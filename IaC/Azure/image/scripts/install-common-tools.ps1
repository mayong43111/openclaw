# install-common-tools.ps1 - Install common utilities via Chocolatey
$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

Write-Output "=== Installing common tools ==="

# Ensure Chocolatey is available (installed by install-git.ps1)
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {
    Write-Error "Chocolatey not found. Ensure install-git.ps1 runs first."
    exit 1
}

# Tools to install via Chocolatey
$packages = @(
    "7zip"               # Archive utility
    "jq"                 # JSON processor (CLI)
    "curl"               # HTTP client (newer than built-in)
    "wget"               # HTTP downloader
    "vim"                # Text editor
    "ripgrep"            # Fast grep alternative (rg)
    "fd"                 # Fast find alternative
    "less"               # Pager
    "bat"                # cat with syntax highlighting
    "ffmpeg"             # Audio/video processing (Discord voice messages, media pipeline)
    "pwsh"               # PowerShell 7 (exec tool prefers pwsh over PS 5.1)
    "chromium"           # Headless-capable browser
    "sysinternals"       # Process Explorer, ProcMon, etc.
)

foreach ($pkg in $packages) {
    Write-Output "--- Installing $pkg ---"
    $maxRetries = 2
    for ($i = 1; $i -le $maxRetries; $i++) {
        choco install $pkg -y --no-progress 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Output "$pkg installed."
            break
        }
        if ($i -lt $maxRetries) {
            Write-Output "$pkg install failed, retrying in 15s..."
            Start-Sleep -Seconds 15
        } else {
            Write-Output "WARNING: $pkg installation failed after $maxRetries attempts (non-fatal)"
            $global:LASTEXITCODE = 0
        }
    }
}

# Refresh PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# Add useful PowerShell modules
Write-Output "Installing PowerShell modules..."
Install-PackageProvider -Name NuGet -MinimumVersion 2.8.5.201 -Force -Scope AllUsers | Out-Null
Install-Module -Name PSReadLine -Force -Scope AllUsers -SkipPublisherCheck
Install-Module -Name Terminal-Icons -Force -Scope AllUsers

# Summary
Write-Output ""
Write-Output "=== Installed tools summary ==="
$verify = @{
    "7z"       = "7z --help 2>&1 | Select-Object -First 1"
    "jq"       = "jq --version 2>&1"
    "curl"     = "curl.exe --version 2>&1 | Select-Object -First 1"
    "rg"       = "rg --version 2>&1 | Select-Object -First 1"
    "fd"       = "fd --version 2>&1"
    "bat"      = "bat --version 2>&1"
    "ffmpeg"   = "ffmpeg -version 2>&1 | Select-Object -First 1"
    "ffprobe"  = "ffprobe -version 2>&1 | Select-Object -First 1"
    "pwsh"     = "pwsh --version 2>&1"
    "chromium" = "Test-Path 'C:\Program Files\Chromium\Application\chrome.exe'"
}

foreach ($tool in $verify.Keys) {
    try {
        $result = Invoke-Expression $verify[$tool] 2>&1
        Write-Output "  $tool : $result"
    } catch {
        Write-Output "  $tool : not verified"
    }
}

Write-Output "=== Common tools installation complete ==="
exit 0
