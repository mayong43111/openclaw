# ─── VNet ─────────────────────────────────────────────────
resource "azurerm_virtual_network" "main" {
  name                = local.vnet_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  address_space       = [var.vnet_address_space]

  tags = azurerm_resource_group.infra.tags
}

# ─── Subnets ──────────────────────────────────────────────

# VM Subnet — service endpoints for Storage + AI Foundry
resource "azurerm_subnet" "vm" {
  name                 = "snet-vm"
  resource_group_name  = azurerm_resource_group.infra.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = [var.vm_subnet_prefix]
  service_endpoints    = ["Microsoft.Storage", "Microsoft.CognitiveServices"]
}

# Application Gateway Subnet (dedicated, required by Azure)
resource "azurerm_subnet" "appgw" {
  name                 = "snet-appgw"
  resource_group_name  = azurerm_resource_group.infra.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = [var.appgw_subnet_prefix]
}

# Worker Subnet — Function App VNet integration, service endpoint for Storage
resource "azurerm_subnet" "worker" {
  name                 = "snet-worker"
  resource_group_name  = azurerm_resource_group.infra.name
  virtual_network_name = azurerm_virtual_network.main.name
  address_prefixes     = [var.worker_subnet_prefix]
  service_endpoints    = ["Microsoft.Storage"]

  delegation {
    name = "delegation-webapp"
    service_delegation {
      name = "Microsoft.Web/serverFarms"
      actions = [
        "Microsoft.Network/virtualNetworks/subnets/action",
      ]
    }
  }
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

  # Allow SSH from Worker subnet (Function App Ansible → VM)
  security_rule {
    name                       = "AllowSSHFromWorker"
    priority                   = 1015
    direction                  = "Inbound"
    access                     = "Allow"
    protocol                   = "Tcp"
    source_port_range          = "*"
    destination_port_range     = "22"
    source_address_prefix      = var.worker_subnet_prefix
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

resource "azurerm_subnet_network_security_group_association" "vm" {
  subnet_id                 = azurerm_subnet.vm.id
  network_security_group_id = azurerm_network_security_group.vm.id
}

# ─── Public IPs ──────────────────────────────────────────

resource "azurerm_public_ip" "appgw" {
  name                = local.pip_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  allocation_method   = "Static"
  sku                 = "Standard"

  tags = azurerm_resource_group.infra.tags
}

resource "azurerm_public_ip" "nat" {
  name                = local.nat_pip
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  allocation_method   = "Static"
  sku                 = "Standard"

  tags = azurerm_resource_group.infra.tags
}

# ─── NAT Gateway (VM outbound internet) ──────────────────
resource "azurerm_nat_gateway" "main" {
  name                = local.nat_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  sku_name            = "Standard"

  tags = azurerm_resource_group.infra.tags
}

resource "azurerm_nat_gateway_public_ip_association" "main" {
  nat_gateway_id       = azurerm_nat_gateway.main.id
  public_ip_address_id = azurerm_public_ip.nat.id
}

resource "azurerm_subnet_nat_gateway_association" "vm" {
  subnet_id      = azurerm_subnet.vm.id
  nat_gateway_id = azurerm_nat_gateway.main.id
}
