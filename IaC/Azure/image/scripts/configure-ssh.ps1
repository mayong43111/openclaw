# configure-ssh.ps1 - Enable OpenSSH Server for post-deploy Ansible access
# Runs during Packer image build (before Sysprep).
#
# Windows Server 2022 ships with OpenSSH but the Server component
# needs to be installed and started. This script:
#   1. Installs OpenSSH Server capability
#   2. Configures sshd to auto-start
#   3. Sets default shell to PowerShell
#   4. Opens firewall port 22

$ErrorActionPreference = "Stop"

Write-Output "=== Configuring OpenSSH Server ==="

# Install OpenSSH Server (available as a Windows capability)
$sshCapability = Get-WindowsCapability -Online | Where-Object Name -like "OpenSSH.Server*"
if ($sshCapability.State -ne "Installed") {
    Write-Output "Installing OpenSSH Server..."
    Add-WindowsCapability -Online -Name $sshCapability.Name
} else {
    Write-Output "OpenSSH Server already installed"
}

# Set sshd to auto-start
Set-Service sshd -StartupType Automatic
Write-Output "sshd set to Automatic start"

# Start sshd now (needed for Packer to verify)
Start-Service sshd
Write-Output "sshd started"

# Set default shell to PowerShell for SSH sessions
$regPath = "HKLM:\SOFTWARE\OpenSSH"
if (-not (Test-Path $regPath)) { New-Item -Path $regPath -Force | Out-Null }
Set-ItemProperty -Path $regPath -Name DefaultShell -Value "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"
Write-Output "Default SSH shell set to PowerShell"

# Firewall rule for SSH (Azure NSG also needed but this is defense-in-depth)
$existing = Get-NetFirewallRule -DisplayName "OpenSSH Server (sshd)" -ErrorAction SilentlyContinue
if (-not $existing) {
    New-NetFirewallRule -DisplayName "OpenSSH Server (sshd)" -Direction Inbound -Protocol TCP -LocalPort 22 -Action Allow -Profile Any
    Write-Output "Firewall rule added for SSH port 22"
} else {
    Write-Output "SSH firewall rule already exists"
}

Write-Output "=== OpenSSH Server configuration complete ==="
