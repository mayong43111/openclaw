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

# ─── Network ─────────────────────────────────────────────

variable "vnet_address_space" {
  type    = string
  default = "10.0.0.0/22"
}

variable "vm_subnet_prefix" {
  type    = string
  default = "10.0.1.0/24"
}

variable "appgw_subnet_prefix" {
  type    = string
  default = "10.0.2.0/24"
}

variable "worker_subnet_prefix" {
  type    = string
  default = "10.0.3.0/26"
}

variable "openclaw_port" {
  type    = number
  default = 18789
}

variable "management_source_cidr" {
  type        = string
  description = "CIDR for SSH/RDP access (e.g. jumpbox VNet)"
  default     = "10.1.0.0/24"
}

# ─── Application Gateway ────────────────────────────────

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

# ─── AI Foundry ──────────────────────────────────────────

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
