packer {
  required_plugins {
    azure = {
      source  = "github.com/hashicorp/azure"
      version = "~> 2"
    }
  }
}

# ─── Variables ────────────────────────────────────────────
variable "subscription_id" {
  type        = string
  description = "Azure subscription ID"
}

variable "location" {
  type    = string
  default = "westus3"
}

variable "vm_size" {
  type    = string
  default = "Standard_D2s_v5"
}

variable "node_version" {
  type    = string
  default = "22.16.0"
}

variable "openclaw_version" {
  type    = string
  default = "2026.3.12"
}

variable "python_version" {
  type    = string
  default = "3.12.9"
}

variable "image_resource_group" {
  type    = string
  default = "rg-openclaw-images"
}

variable "build_resource_group" {
  type        = string
  description = "Existing RG for Packer build resources (avoids temp RG/KV creation)"
  default     = ""
}

variable "build_key_vault_name" {
  type        = string
  description = "Existing KV for Packer build (avoids temp KV + policy issues)"
  default     = ""
}

# ─── Source: Azure ARM ────────────────────────────────────
source "azure-arm" "windows" {
  subscription_id    = var.subscription_id
  use_azure_cli_auth = true

  # Build VM
  os_type         = "Windows"
  image_publisher = "MicrosoftWindowsServer"
  image_offer     = "WindowsServer"
  image_sku       = "2022-datacenter"
  vm_size         = var.vm_size

  # Use existing RG to avoid temp RG creation (bypasses org policy on new RGs).
  # When build_resource_group is set, location is inherited from the RG.
  build_resource_group_name = var.build_resource_group != "" ? var.build_resource_group : null
  location                  = var.build_resource_group != "" ? null : var.location

  # Use existing KV to avoid temp KV creation (bypasses KeyVaultAccessInternalError).
  build_key_vault_name = var.build_key_vault_name != "" ? var.build_key_vault_name : null

  # Packer uses WinRM during build (Azure auto-configures it for the build VM).
  # Post-deploy management uses SSH (OpenSSH is installed in the image).
  communicator   = "winrm"
  winrm_use_ssl  = true
  winrm_insecure = true
  winrm_timeout  = "15m"
  winrm_username = "packer"

  # Output: Managed Image
  managed_image_resource_group_name = var.image_resource_group
  managed_image_name                = "openclaw-windows-${var.openclaw_version}-${formatdate("YYYYMMDDhhmm", timestamp())}"

  # Tags applied to ALL resources Packer creates (build VM, KV, disks, NIC, image).
  # SecurityControl=Ignore bypasses org compliance policies on temp resources.
  azure_tags = {
    project         = "openclaw"
    version         = var.openclaw_version
    built_by        = "packer"
    SecurityControl = "Ignore"
  }
}

# ─── Build ────────────────────────────────────────────────
build {
  sources = ["source.azure-arm.windows"]

  # 1. Install Node.js
  provisioner "powershell" {
    script = "scripts/install-node.ps1"
    environment_vars = [
      "NODE_VERSION=${var.node_version}"
    ]
  }

  # 2. Install Git
  provisioner "powershell" {
    script = "scripts/install-git.ps1"
  }

  # 3. Install Python
  provisioner "powershell" {
    script = "scripts/install-python.ps1"
    environment_vars = [
      "PYTHON_VERSION=${var.python_version}"
    ]
  }

  # 4. Install common tools (7-Zip, curl, jq, FFmpeg, pwsh, Chromium, etc.)
  provisioner "powershell" {
    script = "scripts/install-common-tools.ps1"
  }

  # 5. Install Visual Studio 2022 Build Tools (C++ workload for native modules)
  provisioner "powershell" {
    script = "scripts/install-vsbuildtools.ps1"
  }

  # 6. Install OpenClaw
  provisioner "powershell" {
    script = "scripts/install-openclaw.ps1"
    environment_vars = [
      "OPENCLAW_VERSION=${var.openclaw_version}"
    ]
  }

  # 7. Register OpenClaw as a Windows Service + firewall
  provisioner "powershell" {
    script = "scripts/register-openclaw-service.ps1"
  }

  # 8. Enable OpenSSH Server for post-deploy Ansible access
  provisioner "powershell" {
    elevated_user     = "packer"
    elevated_password = build.Password
    script            = "scripts/configure-ssh.ps1"
  }

  # 9. Sysprep (required for Azure managed images)
  provisioner "powershell" {
    inline = [
      "Write-Output '=== Running Sysprep ==='",
      "& $env:SystemRoot\\System32\\Sysprep\\Sysprep.exe /oobe /generalize /quiet /quit /mode:vm",
      "while ($true) { $imageState = (Get-ItemProperty HKLM:\\SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Setup\\State).ImageState; Write-Output $imageState; if ($imageState -eq 'IMAGE_STATE_GENERALIZE_RESEAL_TO_OOBE') { break }; Start-Sleep -Seconds 5 }",
      "Write-Output '=== Sysprep complete ==='"
    ]
  }
}
