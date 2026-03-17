# ─── Application Gateway ─────────────────────────────────
#
# Terraform manages the App Gateway skeleton (frontend, ports, SSL, default pool).
# Ansible dynamically adds per-VM backend pools, listeners, and routing rules.
# lifecycle { ignore_changes } protects Ansible-managed state.

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

  # Backend pool — VM IPs added by Ansible after deployment
  backend_address_pool {
    name = "openclaw-backend-pool"
  }

  # Backend HTTP settings — connect to OpenClaw Gateway
  backend_http_settings {
    name                  = "openclaw-backend-settings"
    cookie_based_affinity = "Disabled"
    port                  = var.openclaw_port
    protocol              = "Http"
    request_timeout       = 60
  }

  # HTTP listener
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

  # CRITICAL: Ansible dynamically adds per-VM backend pools, HTTPS listeners,
  # and routing rules via deploy-vm.yml. Without ignore_changes, terraform apply
  # would destroy all Ansible-managed App Gateway state.
  lifecycle {
    ignore_changes = [
      backend_address_pool,
      backend_http_settings,
      http_listener,
      request_routing_rule,
      redirect_configuration,
      probe,
      ssl_certificate,
    ]
  }
}
