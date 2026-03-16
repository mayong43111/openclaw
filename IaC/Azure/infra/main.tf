# IaC/Azure/infra - OpenClaw VM base infrastructure
#
# Resources:
#   - Resource Group
#   - VNet + Subnets (vm, appgw)
#   - NSG (vm subnet)
#   - Application Gateway (HTTP -> VM backend)

terraform {
  required_version = ">= 1.5"
  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.0"
    }
  }
}

provider "azurerm" {
  subscription_id = var.subscription_id
  features {}
}

# ─── Variables ────────────────────────────────────────────
variable "subscription_id" {
  type = string
}

variable "prefix" {
  type    = string
  default = "ymms"
}

variable "location" {
  type    = string
  default = "westus3"
}

variable "vnet_address_space" {
  type    = string
  default = "10.0.0.0/16"
}

variable "vm_subnet_prefix" {
  type    = string
  default = "10.0.1.0/24"
}

variable "appgw_subnet_prefix" {
  type    = string
  default = "10.0.2.0/24"
}

variable "openclaw_port" {
  type    = number
  default = 18789
}

variable "ssl_cert_pfx_path" {
  type        = string
  description = "Path to PFX file for App Gateway HTTPS listener"
  default     = ""
}

variable "ssl_cert_password" {
  type        = string
  description = "Password for the PFX file"
  default     = ""
  sensitive   = true
}

variable "management_source_cidr" {
  type        = string
  description = "CIDR for SSH/RDP access (e.g. jumpbox VNet)"
  default     = "10.1.0.0/16"
}

variable "aoai_gpt4o_capacity" {
  type        = number
  description = "TPM capacity for gpt-4o deployment (K tokens/min)"
  default     = 30
}

variable "aoai_gpt4o_mini_capacity" {
  type        = number
  description = "TPM capacity for gpt-4o-mini deployment (K tokens/min)"
  default     = 60
}

# ─── Locals ───────────────────────────────────────────────
locals {
  rg_name    = "rg-${var.prefix}-openclaw-infra"
  vnet_name  = "vnet-${var.prefix}-openclaw"
  nsg_name   = "nsg-${var.prefix}-openclaw-vm"
  appgw_name = "appgw-${var.prefix}-openclaw"
  pip_name   = "pip-${var.prefix}-appgw"
  aoai_name  = "oai-${var.prefix}-openclaw"
  mi_name    = "id-${var.prefix}-openclaw-vm"
}

# ─── Resource Group ──────────────────────────────────────
resource "azurerm_resource_group" "infra" {
  name     = local.rg_name
  location = var.location

  tags = {
    project = "openclaw"
    managed = "terraform"
  }
}

# ─── VNet ─────────────────────────────────────────────────
resource "azurerm_virtual_network" "main" {
  name                = local.vnet_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  address_space       = [var.vnet_address_space]

  tags = azurerm_resource_group.infra.tags
}

# VM Subnet
resource "azurerm_subnet" "vm" {
  name                 = "snet-vm"
  resource_group_name  = azurerm_resource_group.infra.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = [var.vm_subnet_prefix]
}

# Application Gateway Subnet (dedicated, required by Azure)
resource "azurerm_subnet" "appgw" {
  name                 = "snet-appgw"
  resource_group_name  = azurerm_resource_group.infra.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = [var.appgw_subnet_prefix]
}

# ─── NSG (VM Subnet) ─────────────────────────────────────
resource "azurerm_network_security_group" "vm" {
  name                = local.nsg_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name

  # Allow RDP from management network (jumpbox VNet via peering)
  security_rule {
    name                       = "AllowRDP"
    priority                   = 1000
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "3389"
    source_address_prefix      = var.management_source_cidr
    destination_address_prefix = "*"
  }

  # Allow SSH from management network (Ansible via jumpbox)
  security_rule {
    name                       = "AllowSSH"
    priority                   = 1010
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "22"
    source_address_prefix      = var.management_source_cidr
    destination_address_prefix = "*"
  }

  # Allow OpenClaw Gateway from Application Gateway subnet
  security_rule {
    name                       = "AllowOpenClawFromAppGw"
    priority                   = 1020
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = tostring(var.openclaw_port)
    source_address_prefix      = var.appgw_subnet_prefix
    destination_address_prefix = "*"
  }

  tags = azurerm_resource_group.infra.tags
}

# Associate NSG with VM subnet
resource "azurerm_subnet_network_security_group_association" "vm" {
  subnet_id                 = azurerm_subnet.vm.id
  network_security_group_id = azurerm_network_security_group.vm.id
}

# ─── Public IP for Application Gateway ───────────────────
resource "azurerm_public_ip" "appgw" {
  name                = local.pip_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  allocation_method   = "Static"
  sku                 = "Standard"

  tags = azurerm_resource_group.infra.tags
}

# ─── Application Gateway ─────────────────────────────────
resource "azurerm_application_gateway" "main" {
  name                = local.appgw_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name

  sku {
    name     = "Standard_v2"
    tier     = "Standard_v2"
    capacity = 1
  }

  gateway_ip_configuration {
    name      = "appgw-ip-config"
    subnet_id = azurerm_subnet.appgw.id
  }

  frontend_ip_configuration {
    name                 = "appgw-frontend-ip"
    public_ip_address_id = azurerm_public_ip.appgw.id
  }

  frontend_port {
    name = "http-port"
    port = 80
  }

  frontend_port {
    name = "https-port"
    port = 443
  }

  # SSL certificate (from PFX)
  dynamic "ssl_certificate" {
    for_each = var.ssl_cert_pfx_path != "" ? [1] : []
    content {
      name     = "wildcard-nip-io"
      data     = filebase64(var.ssl_cert_pfx_path)
      password = var.ssl_cert_password
    }
  }

  # Backend pool - VM IPs will be added after VM deployment
  backend_address_pool {
    name = "openclaw-backend-pool"
  }

  # Backend HTTP settings - connect to OpenClaw Gateway
  backend_http_settings {
    name                  = "openclaw-backend-settings"
    cookie_based_affinity = "Disabled"
    port                  = var.openclaw_port
    protocol              = "Http"
    request_timeout       = 60
  }

  # HTTP listener - redirect to HTTPS when cert is configured
  http_listener {
    name                           = "openclaw-http-listener"
    frontend_ip_configuration_name = "appgw-frontend-ip"
    frontend_port_name             = "http-port"
    protocol                       = "Http"
  }

  # HTTPS listener (only when cert is provided)
  dynamic "http_listener" {
    for_each = var.ssl_cert_pfx_path != "" ? [1] : []
    content {
      name                           = "openclaw-https-listener"
      frontend_ip_configuration_name = "appgw-frontend-ip"
      frontend_port_name             = "https-port"
      protocol                       = "Https"
      ssl_certificate_name           = "wildcard-nip-io"
    }
  }

  # HTTP -> HTTPS redirect (when cert is provided)
  dynamic "redirect_configuration" {
    for_each = var.ssl_cert_pfx_path != "" ? [1] : []
    content {
      name                 = "http-to-https"
      redirect_type        = "Permanent"
      target_listener_name = "openclaw-https-listener"
      include_path         = true
      include_query_string = true
    }
  }

  # Routing: HTTPS -> backend (when cert provided)
  dynamic "request_routing_rule" {
    for_each = var.ssl_cert_pfx_path != "" ? [1] : []
    content {
      name                       = "openclaw-https-routing"
      priority                   = 100
      rule_type                  = "Basic"
      http_listener_name         = "openclaw-https-listener"
      backend_address_pool_name  = "openclaw-backend-pool"
      backend_http_settings_name = "openclaw-backend-settings"
    }
  }

  # HTTP routing: redirect to HTTPS if cert provided, otherwise direct to backend
  request_routing_rule {
    name                        = "openclaw-http-routing"
    priority                    = 200
    rule_type                   = "Basic"
    http_listener_name          = "openclaw-http-listener"
    redirect_configuration_name = var.ssl_cert_pfx_path != "" ? "http-to-https" : null
    backend_address_pool_name   = var.ssl_cert_pfx_path != "" ? null : "openclaw-backend-pool"
    backend_http_settings_name  = var.ssl_cert_pfx_path != "" ? null : "openclaw-backend-settings"
  }

  tags = azurerm_resource_group.infra.tags
}

# ─── Azure OpenAI ─────────────────────────────────────────
resource "azurerm_cognitive_account" "aoai" {
  name                  = local.aoai_name
  resource_group_name   = azurerm_resource_group.infra.name
  location              = azurerm_resource_group.infra.location
  kind                  = "OpenAI"
  sku_name              = "S0"
  custom_subdomain_name = local.aoai_name
  local_auth_enabled    = true

  tags = azurerm_resource_group.infra.tags
}

resource "azurerm_cognitive_deployment" "gpt4o" {
  name                 = "gpt-4o"
  cognitive_account_id = azurerm_cognitive_account.aoai.id
  model {
    format  = "OpenAI"
    name    = "gpt-4o"
    version = "2024-11-20"
  }
  sku {
    name     = "GlobalStandard"
    capacity = var.aoai_gpt4o_capacity
  }
}

resource "azurerm_cognitive_deployment" "gpt4o_mini" {
  name                 = "gpt-4o-mini"
  cognitive_account_id = azurerm_cognitive_account.aoai.id
  model {
    format  = "OpenAI"
    name    = "gpt-4o-mini"
    version = "2024-07-18"
  }
  sku {
    name     = "GlobalStandard"
    capacity = var.aoai_gpt4o_mini_capacity
  }
}

# ─── User-Assigned Managed Identity (for future MI auth) ─
resource "azurerm_user_assigned_identity" "openclaw_vm" {
  name                = local.mi_name
  resource_group_name = azurerm_resource_group.infra.name
  location            = azurerm_resource_group.infra.location

  tags = azurerm_resource_group.infra.tags
}

resource "azurerm_role_assignment" "vm_aoai" {
  scope                = azurerm_cognitive_account.aoai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.openclaw_vm.principal_id
}

# ─── Outputs ──────────────────────────────────────────────
output "resource_group_name" {
  value = azurerm_resource_group.infra.name
}

output "vnet_name" {
  value = azurerm_virtual_network.main.name
}

output "vm_subnet_id" {
  value = azurerm_subnet.vm.id
}

output "appgw_public_ip" {
  value = azurerm_public_ip.appgw.ip_address
}

output "appgw_name" {
  value = azurerm_application_gateway.main.name
}

output "nsg_id" {
  value = azurerm_network_security_group.vm.id
}

output "aoai_endpoint" {
  value = azurerm_cognitive_account.aoai.endpoint
}

output "managed_identity_id" {
  value = azurerm_user_assigned_identity.openclaw_vm.id
}

output "managed_identity_client_id" {
  value = azurerm_user_assigned_identity.openclaw_vm.client_id
}
