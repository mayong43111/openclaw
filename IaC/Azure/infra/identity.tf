# ─── User-Assigned Managed Identity (for VMs) ────────────
resource "azurerm_user_assigned_identity" "openclaw_vm" {
  name                = local.mi_name
  resource_group_name = azurerm_resource_group.infra.name
  location            = azurerm_resource_group.infra.location

  tags = azurerm_resource_group.infra.tags
}

# ─── RBAC: VM MI → AI Foundry ────────────────────────────
resource "azurerm_role_assignment" "vm_aoai" {
  scope                = azurerm_cognitive_account.aoai.id
  role_definition_name = "Cognitive Services OpenAI User"
  principal_id         = azurerm_user_assigned_identity.openclaw_vm.principal_id
}

# ─── RBAC: Console (System MI) → Storage ─────────────────
resource "azurerm_role_assignment" "console_storage_queue" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Queue Data Contributor"
  principal_id         = azurerm_linux_web_app.console.identity[0].principal_id
}

resource "azurerm_role_assignment" "console_storage_table" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Table Data Contributor"
  principal_id         = azurerm_linux_web_app.console.identity[0].principal_id
}

resource "azurerm_role_assignment" "console_storage_blob" {
  scope                = azurerm_storage_account.main.id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_linux_web_app.console.identity[0].principal_id
}
