# IaC/Azure/infra - OpenClaw VM base infrastructure
#
# File layout:
#   providers.tf    - Terraform + provider config
#   variables.tf    - Input variables
#   locals.tf       - Naming conventions
#   main.tf         - Resource Group
#   network.tf      - VNet, Subnets, NSG, NAT Gateway, Public IPs
#   appgateway.tf   - Application Gateway
#   storage.tf      - Storage Account, Queue, Table, network rules
#   ai-foundry.tf   - AI Foundry (AIServices) + model deployments
#   webapp.tf       - App Service Plan, App Service (Console), Function App (Worker)
#   identity.tf     - Managed Identity + RBAC role assignments
#   outputs.tf      - All outputs
#
# IMPORTANT: App Gateway backend pools, listeners, and routing rules are
# dynamically managed by Ansible (deploy-vm.yml). Terraform uses
# lifecycle { ignore_changes } to avoid destroying Ansible-managed state.

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
  subscription_id     = var.subscription_id
  storage_use_azuread = true
  features {}
}
