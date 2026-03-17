# OpenClaw VM 基础设施 — Terraform

使用 Terraform 管理 OpenClaw Windows VM 的共享基础设施资源。

## 架构概览

```
                         Internet
                            │
                   ┌────────┴────────┐
                   │ NAT Gateway     │ pip-ymms-nat (Static)
                   │ nat-ymms-openclaw│ → snet-vm 出站
                   └────────┬────────┘
                            │
                            ▼
                   ┌─────────────────┐
                   │  Public IP      │ pip-ymms-appgw (Static, Standard)
                   │  pip-ymms-appgw │
                   └────────┬────────┘
                            │
┌───────────────────────────┼──────────────────────────────────────────────┐
│  rg-ymms-openclaw-infra   │                                              │
│                            │                                              │
│  ┌─────────────────────────┼─────────────────────────────────────────┐   │
│  │  VNet: vnet-ymms-openclaw  (10.0.0.0/22)                         │   │
│  │                            │                                      │   │
│  │  ┌─────────────────────────▼──────────────────────────────────┐   │   │
│  │  │  snet-appgw  10.0.2.0/24                                  │   │   │
│  │  │                                                            │   │   │
│  │  │  ┌──────────────────────────────────────────────────────┐  │   │   │
│  │  │  │  Application Gateway (Standard_v2)                   │  │   │   │
│  │  │  │  appgw-ymms-openclaw                                 │  │   │   │
│  │  │  │                                                      │  │   │   │
│  │  │  │  Frontend:  HTTP(80) / HTTPS(443)                    │  │   │   │
│  │  │  │  SSL:       *.{pip}.nip.io 通配符证书                │  │   │   │
│  │  │  │  路由:      Host Header → per-VM 后端池              │  │   │   │
│  │  │  │             vm-01.{ip}.nip.io → pool-01 (10.0.1.4)  │  │   │   │
│  │  │  │             vm-02.{ip}.nip.io → pool-02 (10.0.1.5)  │  │   │   │
│  │  │  │  Backend:   → VM:18789 (HTTP)                        │  │   │   │
│  │  │  └──────────────────────────┬───────────────────────────┘  │   │   │
│  │  └─────────────────────────────┼──────────────────────────────┘   │   │
│  │                                │ :18789                           │   │
│  │  ┌─────────────────────────────▼──────────────────────────────┐   │   │
│  │  │  snet-vm  10.0.1.0/24                                     │   │   │
│  │  │  NSG: nsg-ymms-openclaw-vm      NAT GW: nat-ymms-openclaw │   │   │
│  │  │  ┌─ Rules ──────────────────────────────────────────────┐  │   │   │
│  │  │  │  1000  AllowRDP            ← 管理网段 (10.1.0.0/24) │  │   │   │
│  │  │  │  1010  AllowSSH            ← 管理网段 (10.1.0.0/24) │  │   │   │
│  │  │  │  1020  AllowOpenClaw:18789 ← snet-appgw (10.0.2.0/24)│  │   │   │
│  │  │  └──────────────────────────────────────────────────────┘  │   │   │
│  │  │                                                            │   │   │
│  │  │  ┌──────────┐  ┌──────────┐  ┌──────────┐                 │   │   │
│  │  │  │ VM-01    │  │ VM-02    │  │ VM-N     │  (Ansible 部署)  │   │   │
│  │  │  │ .4       │  │ .5       │  │ .X       │                 │   │   │
│  │  │  │ GW:18789 │  │ GW:18789 │  │ GW:18789 │                 │   │   │
│  │  │  └──────────┘  └──────────┘  └──────────┘                 │   │   │
│  │  └────────────────────────────────────────────────────────────┘   │   │
│  │                                                                   │   │
│  │  ┌────────────────────────────────────────────────────────────┐   │   │
│  │  │  snet-worker  10.0.3.0/26  (Function App VNet 集成)       │   │   │
│  │  │  Delegation: Microsoft.Web/serverFarms                     │   │   ││  │  │                                                            │   │   │
│  │  │  ┌──────────────────────────────────────────────────────┐  │   │   │
│  │  │  │  Function App: func-{prefix}-worker                    │  │   │   │
│  │  │  │  Ansible Worker (QueueTrigger)                        │  │   │   │
│  │  │  │  VNet 集成 → SSH 到 snet-vm                           │  │   │   │
│  │  │  └──────────────────────────────────────────────────────┘  │   │   ││  │  └────────────────────────────────────────────────────────────┘   │   │
│  └───────────────────────────────────────────────────────────────────┘   │
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  Storage Account: st{prefix}openclaw                              │  │
│  │  ├─ Queue: vm-tasks                                               │  │
│  │  └─ Table: vmrecords                                              │  │
│  │  网络: 仅 VNet（snet-vm + snet-worker）                          │  │
│  │  认证: Managed Identity（Storage Blob/Queue/Table Data Contributor）│  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  App Service Plan: plan-{prefix}-openclaw  (Linux, P1v3)              │  │
│  │                                                                      │  │
│  │  ┌─────────────────────────────┐  ┌─────────────────────────────┐  │  │
│  │  │  App Service (Console)         │  │  Function App (Worker)       │  │  │
│  │  │  app-{prefix}-console          │  │  func-{prefix}-worker        │  │  │
│  │  │  Web UI + REST API             │  │  QueueTrigger + Ansible      │  │  │
│  │  │  公网 PaaS（自带 HTTPS）       │  │  VNet 集成 → snet-worker    │  │  │
│  │  └─────────────────────────────┘  └─────────────────────────────┘  │  │
│  └────────────────────────────────────────────────────────────────────┘  │
│                                                                          │
│  ┌────────────────────────────────────────────────────────────────────┐  │
│  │  AI Foundry: aif-ymms-openclaw           Managed Identity            │  │
│  │  kind=AIServices (S0)                    id-ymms-openclaw-vm        │  │
│  │  ├─ gpt-4o       30K TPM (Global)     → Cognitive Services User  │  │
│  │  └─ gpt-4o-mini  60K TPM (Global)                                │  │
│  │  网络: 仅 VNet（snet-vm）  认证: 仅 MI（禁用 API Key）          │  │
│  └────────────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────────┘
                            ▲
                            │ SSH/RDP (VNet Peering)
                   ┌────────┴────────┐
                   │  Jumpbox VNet   │ 10.1.0.0/24
                   │  (管理网段)     │
                   └─────────────────┘
```

## 管理的资源

| 资源 | 名称 | 说明 |
|------|------|------|
| Resource Group | `rg-{prefix}-openclaw-infra` | 所有基础设施资源的容器 |
| VNet | `vnet-{prefix}-openclaw` | 地址空间 `10.0.0.0/22`（1024 IPs） |
| VM Subnet | `snet-vm` | `10.0.1.0/24`（251 可用，最多 200 台 VM） |
| AppGw Subnet | `snet-appgw` | `10.0.2.0/24`（251 可用，Application Gateway 专用） |
| Worker Subnet | `snet-worker` | `10.0.3.0/26`（Function App VNet 集成，delegation: Microsoft.Web/serverFarms） |
| NSG | `nsg-{prefix}-openclaw-vm` | VM 子网安全规则（SSH/RDP/OpenClaw） |
| Public IP (AppGw) | `pip-{prefix}-appgw` | Application Gateway 公网 IP（Standard SKU） |
| Public IP (NAT) | `pip-{prefix}-nat` | NAT Gateway 公网 IP（Standard SKU） |
| NAT Gateway | `nat-{prefix}-openclaw` | VM 子网出站互联网（Azure 2024+ 无默认 SNAT） |
| Application Gateway | `appgw-{prefix}-openclaw` | HTTP/HTTPS 负载均衡 → VM :18789 |
| Storage Account | `st{prefix}openclaw` | Queue（vm-tasks）+ Table（vmrecords），仅 VNet 访问，MI 认证 |
| App Service Plan | `plan-{prefix}-openclaw` | Linux P1v3，Console + Worker 共用 |
| App Service (Console) | `app-{prefix}-console` | Web UI + REST API，接受用户创建/管理 VM 请求 |
| Function App (Worker) | `func-{prefix}-worker` | QueueTrigger + Ansible，VNet 集成到 snet-worker，SSH 到 VM |
| AI Foundry | `aif-{prefix}-openclaw` | Azure AI Foundry（kind=AIServices），仅 VNet 访问，MI 认证，禁用 API Key |
| Managed Identity | `id-{prefix}-openclaw-vm` | VM 用，已授予 Cognitive Services OpenAI User 角色 |

## 前置要求

- [Terraform](https://www.terraform.io/downloads) >= 1.5
- Azure CLI 已登录 (`az login`)
- 对目标订阅有 Contributor 权限

## 使用方法

### 初始化

```bash
cd IaC/Azure/infra
terraform init
```

### 查看计划

```bash
terraform plan
```

### 应用

```bash
terraform apply
```

自定义变量：

```bash
terraform apply \
  -var 'subscription_id=<your-sub-id>' \
  -var 'prefix=myenv' \
  -var 'management_source_cidr=10.1.0.0/24'
```

### 启用 HTTPS

提供 PFX 证书后，Application Gateway 自动配置 HTTPS 监听器和 HTTP→HTTPS 重定向：

```bash
terraform apply \
  -var 'ssl_cert_pfx_path=certs/wildcard.pfx' \
  -var 'ssl_cert_password=<pfx-password>'
```

未配置证书时，Application Gateway 仅监听 HTTP(80) 直连后端。

## 变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `subscription_id` | (必填) | Azure 订阅 ID |
| `prefix` | `ymms` | 资源名称前缀 |
| `location` | `westus3` | Azure 区域 |
| `vnet_address_space` | `10.0.0.0/22` | VNet 地址空间（1024 IPs） |
| `vm_subnet_prefix` | `10.0.1.0/24` | VM 子网 CIDR（251 可用，最多 200 VM） |
| `appgw_subnet_prefix` | `10.0.2.0/24` | Application Gateway 子网 CIDR |
| `worker_subnet_prefix` | `10.0.3.0/26` | Worker 子网 CIDR（Function App VNet 集成） |
| `openclaw_port` | `18789` | OpenClaw Gateway 监听端口 |
| `ssl_cert_pfx_path` | (空) | PFX 证书路径，留空则仅 HTTP |
| `ssl_cert_password` | (空) | PFX 证书密码 |
| `management_source_cidr` | `10.1.0.0/24` | 允许 SSH/RDP 的源 CIDR（如 jumpbox 子网） |
| `aoai_gpt4o_capacity` | `30` | gpt-4o 部署容量（K tokens/min） |
| `aoai_gpt4o_mini_capacity` | `60` | gpt-4o-mini 部署容量（K tokens/min） |

## 输出

| 输出 | 说明 |
|------|------|
| `resource_group_name` | 基础设施资源组名 |
| `vnet_name` | VNet 名称 |
| `vm_subnet_id` | VM 子网完整资源 ID |
| `worker_subnet_id` | Worker 子网完整资源 ID |
| `appgw_public_ip` | Application Gateway 公网 IP 地址 |
| `appgw_name` | Application Gateway 名称 |
| `nsg_id` | VM 子网 NSG 完整资源 ID |
| `nat_gateway_public_ip` | NAT Gateway 公网 IP 地址 |
| `storage_account_name` | Storage Account 名称 |
| `storage_account_connection_string` | Storage Account 连接字符串（sensitive） |
| `aoai_endpoint` | AI Foundry 端点 URL |
| `managed_identity_id` | 用户托管标识的完整资源 ID |
| `managed_identity_client_id` | 用户托管标识的 Client ID |

## NSG 安全规则

| 优先级 | 名称 | 方向 | 源 | 目标端口 | 说明 |
|--------|------|------|-----|---------|------|
| 1000 | AllowRDP | Inbound | 管理网段 | 3389 | RDP 远程桌面 |
| 1010 | AllowSSH | Inbound | 管理网段 | 22 | SSH（Ansible 管理） |
| 1020 | AllowOpenClawFromAppGw | Inbound | AppGw 子网 | 18789 | OpenClaw Gateway 流量 |

## Application Gateway 路由

- **无证书模式**：HTTP(80) → 直连后端池 → VM:18789
- **有证书模式**：
  - HTTPS(443) → 后端池 → VM:18789
  - HTTP(80) → 301 重定向到 HTTPS

每个 VM 部署后，Ansible 会向 Application Gateway 添加对应的后端池、监听器和路由规则（per-VM 路由）。

## 文件结构

```
infra/
├── providers.tf               # Terraform + provider 配置
├── variables.tf               # 输入变量
├── locals.tf                  # 命名约定
├── main.tf                    # Resource Group
├── network.tf                 # VNet, 子网, NSG, NAT Gateway, Public IP
├── appgateway.tf              # Application Gateway
├── storage.tf                 # Storage Account, Queue, Table, 网络规则
├── ai-foundry.tf              # AI Foundry (AIServices) + 模型部署
├── webapp.tf                  # App Service Plan, Console, Function App Worker
├── identity.tf                # Managed Identity + RBAC 角色分配
├── outputs.tf                 # 所有输出
├── terraform.tfstate          # 状态文件（勿手动编辑）
├── terraform.tfstate.backup   # 状态备份
├── .terraform.lock.hcl        # Provider 版本锁定
├── README.md                  # 本文件
└── certs/                     # SSL 证书（不提交到 Git）
    ├── ca.key                 # CA 私钥
    ├── ca.pem                 # CA 证书
    ├── wildcard.key           # 通配符证书私钥
    ├── wildcard.pem           # 通配符证书
    └── wildcard.pfx           # PFX 格式（App Gateway 使用）
```

## 与 Ansible/Packer 的关系

```
Terraform (infra/)          Packer (image/)           Ansible (ansible/)
─────────────────          ─────────────────         ─────────────────
创建基础设施：               构建 VM 镜像：              部署 & 配置 VM：
  VNet / Subnet               Windows Server 2022       从镜像创建 VM
  NSG                          Node.js / Python          配置 OpenClaw Gateway
  NAT Gateway                  OpenClaw + 工具链          注册到 App Gateway
  Application Gateway          VS Build Tools            设置 Scheduled Task
  Storage Account              FFmpeg / pwsh             自动登录 + 重启
  App Service (Console)
  Function App (Worker)
  Managed Identity
  AI Foundry
```

## 注意事项

- `terraform.tfstate` 包含敏感信息，不要提交到公开仓库
- `certs/` 目录包含私钥，应通过 `.gitignore` 排除
- Application Gateway 使用 Standard_v2 SKU（最小 capacity=1），持续产生费用
- AI Foundry 使用 `kind=AIServices`（而非 `kind=OpenAI`），命名为 `aif-{prefix}-openclaw`，支持 OpenAI 模型部署和 AI Foundry 功能
- VM 不由 Terraform 管理，由 Ansible playbook 创建和配置
- **TF/Ansible 边界**：Terraform 创建 App Gateway 骨架（前端 IP、端口、SSL、默认后端池）。Ansible 动态添加 per-VM 后端池、监听器和路由规则。Terraform 使用 `lifecycle { ignore_changes }` 保护 Ansible 管理的状态，`terraform apply` 不会破坏已有 VM 路由
- NAT Gateway 是 Azure 2024+ VM 出站互联网的必要组件（Azure 已取消默认出站 SNAT）

## 安全设计

### VNet 集成（禁止公网访问）

所有数据面资源均通过子网服务端点或网络规则限制为仅 VNet 内部访问：

| 资源 | 网络限制方式 | 允许的子网 |
|------|-------------|------------|
| Storage Account | `network_rules` + 子网服务端点 `Microsoft.Storage` | snet-vm, snet-worker |
| AI Foundry | `network_acls` + 子网服务端点 `Microsoft.CognitiveServices` | snet-vm |
| App Service (Console) | PaaS 公网服务，自带 HTTPS 域名，可配 IP 限制或 Private Endpoint | N/A（公网入口） |
| Function App (Worker) | VNet 集成到 snet-worker，通过内网 SSH 到 VM | snet-worker |
| Application Gateway | 部署在专用 snet-appgw 子网 | N/A（公网入口，按设计暴露） |
| VM | NSG 限制入站（仅 AppGw:18789 + 管理:SSH/RDP） | N/A |

> **Service Endpoint vs Private Endpoint**：当前使用服务端点（成本更低、配置更简单），
> 流量经 Azure 骨干网不走公网。如需更严格隔离可升级为 Private Endpoint。

### Managed Identity 认证（零密钥）

所有资源间通信使用 User-Assigned Managed Identity（`id-{prefix}-openclaw-vm`），不使用 API Key / 连接字符串：

| 资源 | RBAC 角色 | 说明 |
|------|-----------|------|
| AI Foundry | `Cognitive Services OpenAI User` | VM 通过 MI 调用模型推理，`local_auth_enabled = false` 禁用 API Key |
| Storage Account | `Storage Queue Data Contributor` | Console + Worker 读写 Queue |
| Storage Account | `Storage Table Data Contributor` | Console + Worker 读写 Table |
| Storage Account | `Storage Blob Data Contributor` | 预留（日志/artifacts 等场景） |

> Console 和 Worker 使用 System-Assigned Managed Identity（各自的 App Service/Function App 自带），
> VM 使用 User-Assigned Managed Identity（`id-{prefix}-openclaw-vm`）。
> 两类 MI 均通过 RBAC 授权访问 Storage 和 AI Foundry。

> `storage_account_connection_string` 输出仅用于初始调试/迁移，生产环境应全部使用 MI + RBAC。
