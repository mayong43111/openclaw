# ─── Network ──────────────────────────────────────────────
output "resource_group_name" {
  value = azurerm_resource_group.infra.name
}

output "vnet_name" {
  value = azurerm_virtual_network.main.name
}

output "vm_subnet_id" {
  value = azurerm_subnet.vm.id
}

output "worker_subnet_id" {
  value = azurerm_subnet.worker.id
}

output "nsg_id" {
  value = azurerm_network_security_group.vm.id
}

output "appgw_public_ip" {
  value = azurerm_public_ip.appgw.ip_address
}

output "appgw_name" {
  value = azurerm_application_gateway.main.name
}

output "nat_gateway_public_ip" {
  value = azurerm_public_ip.nat.ip_address
}

# ─── Storage ─────────────────────────────────────────────
output "storage_account_name" {
  value = azurerm_storage_account.main.name
}

output "storage_account_connection_string" {
  description = "For initial debug/migration only — use MI + RBAC in production"
  value       = azurerm_storage_account.main.primary_connection_string
  sensitive   = true
}

# ─── AI Foundry ──────────────────────────────────────────
output "aoai_endpoint" {
  value = azurerm_cognitive_account.aoai.endpoint
}

# ─── Compute ─────────────────────────────────────────────
output "console_default_hostname" {
  value = azurerm_linux_web_app.console.default_hostname
}

# ─── Identity ────────────────────────────────────────────
output "managed_identity_id" {
  value = azurerm_user_assigned_identity.openclaw_vm.id
}

output "managed_identity_client_id" {
  value = azurerm_user_assigned_identity.openclaw_vm.client_id
}
