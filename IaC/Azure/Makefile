# IaC/Azure/Makefile - OpenClaw Azure Infrastructure
# Usage:
#   make login                   # Interactive browser login
#   make login DEVICE_CODE=1     # Device code login (headless/SSH)
#   make init                    # Create RG, Key Vault, CA cert
#   make build-image             # Build Packer VM image

# ─── Load .env ────────────────────────────────────────────
-include .env
export

# ─── Configuration ────────────────────────────────────────
PREFIX           ?= ymms
SUBSCRIPTION_ID  ?= 25741150-3c69-49ef-b192-934816cf1782
LOCATION         ?= westus3
RESOURCE_GROUP   ?= rg-$(PREFIX)-openclaw
KEY_VAULT_NAME   ?= kv-$(PREFIX)-openclaw
CA_CERT_NAME     ?= openclaw-ca
CA_SUBJECT       ?= CN=OpenClaw Internal CA
CA_VALIDITY_MONTHS ?= 120

# Packer
PACKER           ?= packer
PACKER_DIR       := image

# Terraform
TF               ?= terraform
TF_DIR           := infra

# ─── login ────────────────────────────────────────────────
.PHONY: login
login: ## Login to Azure CLI
ifdef DEVICE_CODE
	az login --use-device-code
else
	az login
endif
	az account set --subscription $(SUBSCRIPTION_ID)
	@echo ""
	@echo "Logged in. Subscription: $(SUBSCRIPTION_ID)"

# ─── init ─────────────────────────────────────────────────
.PHONY: init
init: _init-rg _init-kv _init-ca ## Initialize RG, Key Vault, and CA cert
	@echo ""
	@echo "=== Init complete ==="
	@echo "  Resource Group : $(RESOURCE_GROUP)"
	@echo "  Key Vault      : $(KEY_VAULT_NAME)"
	@echo "  CA Certificate : $(CA_CERT_NAME)"

.PHONY: _init-rg
_init-rg:
	@echo "=== Creating Resource Group: $(RESOURCE_GROUP) ==="
	az group create \
		--name $(RESOURCE_GROUP) \
		--location $(LOCATION) \
		--output table

.PHONY: _init-kv
_init-kv:
	@echo "=== Creating Key Vault: $(KEY_VAULT_NAME) ==="
	@if az keyvault show --name $(KEY_VAULT_NAME) --resource-group $(RESOURCE_GROUP) --output none 2>/dev/null; then \
		echo "Key Vault '$(KEY_VAULT_NAME)' already exists, skipping creation."; \
	else \
		echo "Key Vault not found, checking soft-deleted state..."; \
		if az keyvault show-deleted --name $(KEY_VAULT_NAME) --output none 2>/dev/null; then \
			echo "Recovering soft-deleted Key Vault..."; \
			az keyvault recover --name $(KEY_VAULT_NAME); \
		else \
			echo "Creating new Key Vault..."; \
			az keyvault create \
				--name $(KEY_VAULT_NAME) \
				--resource-group $(RESOURCE_GROUP) \
				--location $(LOCATION) \
				--enabled-for-deployment true \
				--output table; \
		fi; \
	fi

.PHONY: _init-ca
_init-ca:
	@echo "=== Creating CA Certificate: $(CA_CERT_NAME) ==="
	@if az keyvault certificate show --vault-name $(KEY_VAULT_NAME) --name $(CA_CERT_NAME) --output none 2>/dev/null; then \
		echo "CA certificate '$(CA_CERT_NAME)' already exists, skipping."; \
		az keyvault certificate show \
			--vault-name $(KEY_VAULT_NAME) \
			--name $(CA_CERT_NAME) \
			--query "{name:name, notBefore:attributes.notBefore, expires:attributes.expires, thumbprint:x509ThumbprintHex}" \
			--output table; \
	else \
		printf '{"issuerParameters":{"name":"Self"},"keyProperties":{"exportable":true,"keySize":4096,"keyType":"RSA"},"x509CertificateProperties":{"subject":"$(CA_SUBJECT)","validityInMonths":$(CA_VALIDITY_MONTHS),"keyUsage":["keyCertSign","cRLSign","digitalSignature"],"basicConstraints":{"isCA":true}}}' > /tmp/openclaw-ca-policy.json; \
		az keyvault certificate create \
			--vault-name $(KEY_VAULT_NAME) \
			--name $(CA_CERT_NAME) \
			--policy @/tmp/openclaw-ca-policy.json \
			--output table; \
		rm -f /tmp/openclaw-ca-policy.json; \
		echo "Waiting for certificate to be ready..."; \
		for i in $$(seq 1 30); do \
			status=$$(az keyvault certificate show \
				--vault-name $(KEY_VAULT_NAME) \
				--name $(CA_CERT_NAME) \
				--query "attributes.enabled" -o tsv 2>/dev/null); \
			if [ "$$status" = "true" ]; then \
				echo "CA certificate ready."; \
				break; \
			fi; \
			echo "  waiting... ($$i/30)"; \
			sleep 2; \
		done; \
		az keyvault certificate show \
			--vault-name $(KEY_VAULT_NAME) \
			--name $(CA_CERT_NAME) \
			--query "{name:name, notBefore:attributes.notBefore, expires:attributes.expires, thumbprint:x509ThumbprintHex}" \
			--output table; \
	fi

# ─── build-image ──────────────────────────────────────────
PACKER_VARS  := -var "subscription_id=$(SUBSCRIPTION_ID)" \
                -var "location=$(LOCATION)" \
                -var "image_resource_group=$(RESOURCE_GROUP)" \
                -var "build_resource_group=$(RESOURCE_GROUP)" \
                -var "build_key_vault_name=$(KEY_VAULT_NAME)"

.PHONY: build-image
build-image: ## Build OpenClaw Windows VM image with Packer
	@echo "=== Validating Packer template ==="
	cd $(PACKER_DIR) && $(PACKER) validate $(PACKER_VARS) .
	@echo ""
	@echo "=== Building image ==="
	cd $(PACKER_DIR) && $(PACKER) build -force -on-error=cleanup $(PACKER_VARS) .

# ─── infra (Terraform) ───────────────────────────────────
TF_VARS := -var "subscription_id=$(SUBSCRIPTION_ID)" -var "prefix=$(PREFIX)" -var "location=$(LOCATION)"

# SSL cert for App Gateway HTTPS (set these to enable HTTPS)
SSL_CERT_PFX     ?= $(TF_DIR)/certs/wildcard.pfx
SSL_CERT_PASSWORD ?=
ifneq ($(wildcard $(SSL_CERT_PFX)),)
TF_VARS += -var "ssl_cert_pfx_path=certs/wildcard.pfx" -var "ssl_cert_password=$(SSL_CERT_PASSWORD)"
endif

CERTS_DIR := $(TF_DIR)/certs

.PHONY: ssl-cert
ssl-cert: ## Generate wildcard SSL cert (CA + *.{ip}.nip.io)
	@mkdir -p $(CERTS_DIR)
	@APPGW_IP=$$(cd $(TF_DIR) && $(TF) output -raw appgw_public_ip 2>/dev/null); \
	if [ -z "$$APPGW_IP" ]; then \
		echo "ERROR: Run 'make infra-apply' first to create App Gateway."; exit 1; \
	fi; \
	echo "=== Generating CA (CA:TRUE) ==="; \
	if [ -f $(CERTS_DIR)/ca.key ]; then \
		echo "CA already exists, reusing."; \
	else \
		openssl req -x509 -new -nodes -newkey rsa:4096 \
			-keyout $(CERTS_DIR)/ca.key -out $(CERTS_DIR)/ca.pem \
			-days 3650 -subj "/CN=OpenClaw Internal CA" \
			-addext "basicConstraints=critical,CA:TRUE" \
			-addext "keyUsage=critical,keyCertSign,cRLSign"; \
	fi; \
	echo "=== Signing wildcard cert: *.$$APPGW_IP.nip.io ==="; \
	openssl req -new -nodes -newkey rsa:2048 \
		-keyout $(CERTS_DIR)/wildcard.key -out $(CERTS_DIR)/wildcard.csr \
		-subj "/CN=*.$$APPGW_IP.nip.io"; \
	printf "authorityKeyIdentifier=keyid,issuer\nbasicConstraints=CA:FALSE\nkeyUsage=digitalSignature,keyEncipherment\nextendedKeyUsage=serverAuth\nsubjectAltName=DNS:*.$$APPGW_IP.nip.io,DNS:$$APPGW_IP.nip.io\n" > $(CERTS_DIR)/wildcard.ext; \
	openssl x509 -req -in $(CERTS_DIR)/wildcard.csr \
		-CA $(CERTS_DIR)/ca.pem -CAkey $(CERTS_DIR)/ca.key -CAcreateserial \
		-out $(CERTS_DIR)/wildcard.pem -days 3650 -extfile $(CERTS_DIR)/wildcard.ext; \
	openssl pkcs12 -export -out $(CERTS_DIR)/wildcard.pfx \
		-inkey $(CERTS_DIR)/wildcard.key -in $(CERTS_DIR)/wildcard.pem \
		-certfile $(CERTS_DIR)/ca.pem -passout pass:; \
	echo ""; \
	echo "=== Certificate ready ==="; \
	echo "  CA:       $(CERTS_DIR)/ca.pem"; \
	echo "  Wildcard: *.$$APPGW_IP.nip.io"; \
	echo "  PFX:      $(CERTS_DIR)/wildcard.pfx"; \
	echo ""; \
	echo "Next: make infra-apply  (PFX auto-detected)"

.PHONY: infra-init
infra-init: ## Initialize Terraform (download providers)
	cd $(TF_DIR) && $(TF) init

.PHONY: infra-plan
infra-plan: ## Plan infrastructure changes
	cd $(TF_DIR) && $(TF) plan $(TF_VARS)

.PHONY: infra-apply
infra-apply: ## Apply infrastructure (VNet, NSG, App Gateway)
	cd $(TF_DIR) && $(TF) apply $(TF_VARS)

.PHONY: infra-destroy
infra-destroy: ## Destroy infrastructure
	cd $(TF_DIR) && $(TF) destroy $(TF_VARS)

.PHONY: infra-output
infra-output: ## Show Terraform outputs
	cd $(TF_DIR) && $(TF) output

# ─── Console (App Service) ───────────────────────────────
CONSOLE_DIR      := console
CONSOLE_RG       := rg-$(PREFIX)-openclaw-infra
CONSOLE_APP      := app-$(PREFIX)-console

.PHONY: console-deploy
console-deploy: ## Deploy Console + Worker to App Service (ZIP deploy)
	@echo "=== Publishing .NET app ==="
	cd $(CONSOLE_DIR) && dotnet publish -c Release -o ./publish
	@echo "=== Creating ZIP package ==="
	cd $(CONSOLE_DIR)/publish && zip -r ../deploy.zip .
	@echo "=== Deploying to $(CONSOLE_APP) ==="
	az webapp deploy \
		--resource-group $(CONSOLE_RG) \
		--name $(CONSOLE_APP) \
		--src-path $(CONSOLE_DIR)/deploy.zip \
		--type zip \
		--async true
	rm -rf $(CONSOLE_DIR)/publish $(CONSOLE_DIR)/deploy.zip
	@echo ""
	@echo "=== Deployed ==="
	@echo "  App: https://$(CONSOLE_APP).azurewebsites.net"

# ─── vm (Ansible) ────────────────────────────────────────
ANSIBLE_DIR      := ansible
ANSIBLE_INVENTORY := $(ANSIBLE_DIR)/inventory
ANSIBLE_VARS     := -e prefix=$(PREFIX) -e location=$(LOCATION) -e subscription_id=$(SUBSCRIPTION_ID)
VM_PASSWORD      ?=
VM_NAME          ?=
AOAI_API_KEY     ?=

.PHONY: vm-deploy
vm-deploy: ## Deploy VM (VM_NAME=xx VM_PASSWORD=xx AOAI_API_KEY=xx)
	@test -n "$(VM_PASSWORD)" || (echo "ERROR: VM_PASSWORD required. Usage: make vm-deploy VM_NAME=01 VM_PASSWORD='P@ss' AOAI_API_KEY='key'"; exit 1)
	@test -n "$(VM_NAME)" || (echo "ERROR: VM_NAME required. Usage: make vm-deploy VM_NAME=01 VM_PASSWORD='P@ss' AOAI_API_KEY='key'"; exit 1)
	@test -n "$(AOAI_API_KEY)" || (echo "ERROR: AOAI_API_KEY required. Usage: make vm-deploy VM_NAME=01 VM_PASSWORD='P@ss' AOAI_API_KEY='key'"; exit 1)
	cd $(ANSIBLE_DIR) && ansible-playbook deploy-vm.yml \
		$(ANSIBLE_VARS) \
		-e "vm_admin_password=$(VM_PASSWORD)" \
		-e "vm_name=vm-$(PREFIX)-openclaw-$(VM_NAME)" \
		-e "aoai_api_key=$(AOAI_API_KEY)"

.PHONY: vm-configure
vm-configure: ## Reconfigure existing VM(s) (AOAI_API_KEY=xx)
	@test -d $(ANSIBLE_INVENTORY)/host_vars || (echo "No inventory. Deploy a VM first."; exit 1)
	@test -n "$(AOAI_API_KEY)" || (echo "ERROR: AOAI_API_KEY required. Usage: make vm-configure AOAI_API_KEY='key'"; exit 1)
	cd $(ANSIBLE_DIR) && ansible-playbook configure-vm.yml \
		-i inventory \
		$(ANSIBLE_VARS) \
		-e "aoai_api_key=$(AOAI_API_KEY)" \
		--ask-pass

.PHONY: vm-ping
vm-ping: ## Ping all deployed VMs via SSH
	@test -d $(ANSIBLE_INVENTORY)/host_vars || (echo "No inventory. Deploy a VM first."; exit 1)
	cd $(ANSIBLE_DIR) && ansible openclaw_vm -i inventory -m ansible.builtin.raw -a "hostname" \
		--ask-pass

.PHONY: vm-list
vm-list: ## List deployed VMs and tokens
	@if [ -f $(ANSIBLE_DIR)/vms.yml ]; then \
		echo ""; \
		echo "=== Deployed OpenClaw VMs ==="; \
		echo ""; \
		cat $(ANSIBLE_DIR)/vms.yml; \
	else \
		echo "No VMs deployed yet."; \
	fi

.PHONY: vm-ssh
vm-ssh: ## SSH to VM (VM_NAME=01 VM_PASSWORD=xx)
	@test -n "$(VM_NAME)" || (echo "ERROR: VM_NAME required. Usage: make vm-ssh VM_NAME=01 VM_PASSWORD='P@ss'"; exit 1)
	@test -n "$(VM_PASSWORD)" || (echo "ERROR: VM_PASSWORD required."; exit 1)
	@VM_IP=$$(cd $(ANSIBLE_DIR) && python3 -c "import yaml; d=yaml.safe_load(open('vms.yml')); vm=[v for v in d['vms'] if v['name']=='vm-$(PREFIX)-openclaw-$(VM_NAME)']; print(vm[0]['ip'] if vm else '')" 2>/dev/null); \
	if [ -z "$$VM_IP" ]; then echo "VM vm-$(PREFIX)-openclaw-$(VM_NAME) not found in registry."; exit 1; fi; \
	sshpass -p "$(VM_PASSWORD)" ssh -o StrictHostKeyChecking=no azureuser@$$VM_IP

# ─── helpers ──────────────────────────────────────────────
.PHONY: validate
validate: ## Validate Packer template only
	cd $(PACKER_DIR) && $(PACKER) validate $(PACKER_VARS) .

.PHONY: show-ca
show-ca: ## Show CA certificate details from Key Vault
	az keyvault certificate show \
		--vault-name $(KEY_VAULT_NAME) \
		--name $(CA_CERT_NAME) \
		--output json

.PHONY: download-ca
download-ca: ## Download CA public cert (PEM) to ./ca.pem
	az keyvault certificate download \
		--vault-name $(KEY_VAULT_NAME) \
		--name $(CA_CERT_NAME) \
		--file ca.pem \
		--encoding PEM
	@echo "CA public cert saved to ca.pem"

.PHONY: help
help: ## Show this help
	@grep -E '^[a-zA-Z_-]+:.*?## .*$$' Makefile | \
		awk 'BEGIN {FS = ":.*?## "}; {printf "  \033[36m%-16s\033[0m %s\n", $$1, $$2}'

.DEFAULT_GOAL := help
