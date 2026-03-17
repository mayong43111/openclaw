# ─── AI Foundry (AIServices) ─────────────────────────────
#
# kind=AIServices (not OpenAI) — supports OpenAI model deployments
# plus broader AI Foundry capabilities.
# local_auth disabled — all access via Managed Identity.

resource "azurerm_cognitive_account" "aoai" {
  name                  = local.aif_name
  resource_group_name   = azurerm_resource_group.infra.name
  location              = azurerm_resource_group.infra.location
  kind                  = "AIServices"
  sku_name              = "S0"
  custom_subdomain_name = local.aif_name
  local_auth_enabled    = false

  # VNet-only access via service endpoint
  network_acls {
    default_action = "Deny"
    virtual_network_rules {
      subnet_id = azurerm_subnet.vm.id
    }
  }

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
