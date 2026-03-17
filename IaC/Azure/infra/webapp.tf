# ─── App Service Plan (shared by Console + Worker) ───────
resource "azurerm_service_plan" "main" {
  name                = local.plan_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  os_type             = "Linux"
  sku_name            = "P1v3"

  tags = azurerm_resource_group.infra.tags
}

# ─── App Service — Console (Web UI + API + BackgroundService Worker) ─
resource "azurerm_linux_web_app" "console" {
  name                = local.console_name
  location            = azurerm_resource_group.infra.location
  resource_group_name = azurerm_resource_group.infra.name
  service_plan_id     = azurerm_service_plan.main.id
  https_only          = true

  virtual_network_subnet_id = azurerm_subnet.worker.id

  identity {
    type = "SystemAssigned"
  }

  site_config {
    application_stack {
      dotnet_version = "8.0"
    }
    ftps_state             = "Disabled"
    vnet_route_all_enabled = true
  }

  app_settings = {
    "Storage__AccountName"  = azurerm_storage_account.main.name
    "Storage__QueueName"    = azurerm_storage_queue.vm_tasks.name
    "Storage__TableName"    = "vmrecords"
    "Vm__Prefix"            = var.prefix
    "Azure__SubscriptionId" = var.subscription_id
    "Azure__ResourceGroup"  = azurerm_resource_group.infra.name
    "Azure__AppGatewayName" = azurerm_application_gateway.main.name
  }

  tags = azurerm_resource_group.infra.tags
}

# Function App removed — Worker is now a BackgroundService inside the
# Console App Service above (TaskWorkerService.cs polls Storage Queue
# and runs Ansible playbooks). This eliminates the need for a separate
# compute resource and simplifies the deployment.
