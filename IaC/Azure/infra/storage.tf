# ─── Storage Account (Queue + Table for Console/Worker) ──
resource "azurerm_storage_account" "main" {
  name                          = local.sa_name
  resource_group_name           = azurerm_resource_group.infra.name
  location                      = azurerm_resource_group.infra.location
  account_tier                  = "Standard"
  account_replication_type      = "LRS"
  min_tls_version               = "TLS1_2"
  shared_access_key_enabled     = false

  tags = azurerm_resource_group.infra.tags
}

resource "azurerm_storage_queue" "vm_tasks" {
  name                 = "vm-tasks"
  storage_account_name = azurerm_storage_account.main.name
}

# Table "vmrecords" is created via Azure CLI (az storage table create --auth-mode login)
# because the azurerm provider's Table resource uses key-based auth internally,
# which is blocked by the subscription's Azure Policy (allowSharedKeyAccess = false).
# The table is a one-time setup and does not need ongoing TF management.

# Network rules applied after data-plane resources (Queue) are created,
# so the TF runner can provision them before the account is locked down.
resource "azurerm_storage_account_network_rules" "main" {
  storage_account_id = azurerm_storage_account.main.id
  default_action     = "Deny"
  virtual_network_subnet_ids = [
    azurerm_subnet.vm.id,
    azurerm_subnet.worker.id,
  ]

  depends_on = [
    azurerm_storage_queue.vm_tasks,
  ]
}
