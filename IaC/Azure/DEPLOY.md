# OpenClaw Azure 部署设计

## 1. 系统架构

```
用户浏览器
  │
  │  ① POST /api/vm（请求创建）     ④ GET /api/vm/{name}（轮询状态）
  ▼                                  ▼
┌──────────────────────────────────────────────────────────────────────┐
│  Azure 基础设施                                                      │
│                                                                      │
│  ┌──────────────────────┐  ┌──────────────────┐                      │
│  │ App Service          │  │ Storage Queue    │                      │
│  │ Console (Web UI+API) │─→│ vm-tasks         │                      │
│  │ 状态数据: Table/CosmosDB│  └────────┬─────────┘                      │
│  └──────────────────────┘             │                              │
│                                       │ ② 监听队列                   │
│                                       ▼                              │
│  ┌──────────────────────────────────────────────────────────┐        │
│  │  Ansible Worker（App Service BackgroundService）             │        │
│  │  ┌─────────────┐                                        │        │
│  │  │ Queue 轮询  │→ ansible-playbook → deploy-vm.yml    │        │
│  │  └─────────────┘                                        │        │
│  │  ③ 创建 VM + SSH 配置 + 注册 AppGw 路由 + 更新状态      │        │
│  └──────────────────────────┬───────────────────────────────┘        │
│                             │ SSH                                    │
│  ┌──────────────────────────┼───────────────────┐                    │
│  │  AI Foundry              │                   │                    │
│  │  auto-gen-chat            │                   │                    │
│  │  模型: gpt-5.4                         │                    │
│  │  认证: API Key                               │                    │
│  └──────────────────────────┼───────────────────┘                    │
│                             │
│  ┌──────────────────────────┼─────────────────────────────────┐      │
│  │  Application Gateway     │  (Standard_v2)                  │      │
│  │  *.{pip}.nip.io          │  SSL: 内部 CA 通配符证书        │      │
│  │                          │                                 │      │
│  │  按 Host Header 路由：   │                                 │      │
│  │    vm-01.{ip}.nip.io  →  │  后端池 vm-01 (10.0.1.4:18789)│      │
│  │    vm-02.{ip}.nip.io  →  │  后端池 vm-02 (10.0.1.5:18789)│      │
│  │    vm-N.{ip}.nip.io   →  │  后端池 vm-N  (10.0.1.X:18789)│      │
│  └──────────────┬───────────┘─────────────────────────────────┘      │
│                 │  snet-appgw (10.0.2.0/24)                          │
│   ┌──────────────────────────────────────────────────────────────┐  │
│   │             │  snet-vm (10.0.1.0/24)                         │  │
│   │  ┌──────────┐  ┌──────────┐  ┌──────────┐                   │  │
│   │  │ VM-01    │  │ VM-02    │  │ VM-N     │                   │  │
│   │  │ GW:18789 │  │ GW:18789 │  │ GW:18789 │                   │  │
│   │  └──────────┘  └──────────┘  └──────────┘                   │  │
│   │                                                              │  │
│   │  snet-worker (10.0.3.0/26) — App Service VNet 集成            │  │
│   └──────────────────────────────────────────────────────────────┘  │
└──────────────────────────────────────────────────────────────────────┘
       ⑤ 用户看到 status=ready 后，用 token 访问:
          https://vm-{name}.{appgw-ip}.nip.io
```

### 1.1 核心流程

```
用户请求创建 VM
       │
       ▼
┌──────────────┐     ┌──────────────┐
│ Console API  │────→│ Storage Queue│     状态: creating
│ 分配名称     │     │ vm-tasks     │
│ 写入记录     │     └──────┬───────┘
│ 返回 202     │            │
└──────────────┘            │
                            ▼
                   ┌──────────────────┐     ┌──────────────┐
                   │ BackgroundService│────→│ Azure        │
                   │ Queue 轮询      │     │ 创建 NIC + VM│
                   │ ansible-playbook│     └──────┴───────┘
                   └──────┬───────────┘            │
                          │                        │
                    ┌─────▼──────┐                 │
                    │ SSH 配置   │◄────────────────┘
                    │ openclaw   │  VM 就绪后 SSH 进入
                    │ config set │
                    │ 启动服务   │
                    │ 获取 token │
                    └─────┬──────┘
                          │
                    ┌─────▼───────────────┐
                    │ App Gateway         │
                    │ 创建后端池          │
                    │ 添加 Host 路由规则  │
                    └─────┬───────────────┘
                          │
                    ┌─────▼───────────────┐
                    │ 更新 VM 记录        │     状态: ready
                    │ 写入 url + token    │
                    └─────────────────────┘

用户轮询 GET /api/vm/{name}:
  status=creating  →  请等待...
  status=ready     →  { url, token }  →  连接使用
  status=failed    →  错误信息
```

### 1.2 访问模型

| 角色     | 访问方式                                                    |
|----------|------------------------------------------------------------|
| 最终用户 | Console Web UI → 创建 VM → 等待 ready → 获取 URL + token   |
| 最终用户 | `https://vm-{name}.{appgw-ip}.nip.io` → OpenClaw 控制面板  |
| 管理员   | Console Web UI / API → 管理所有 VM                         |
| 运维     | SSH 跳板机 → OpenClaw VM（仅管理 VNet 内）                  |

- **Console** = App Service（PaaS），无需管理 VM
- **Ansible Worker** = App Service 内置 BackgroundService，VNet 集成后可 SSH 到 VM 子网
- App Gateway **按域名路由**：每台 VM 独立子域名 + 独立后端池
- 通配符证书 `*.{appgw-ip}.nip.io` 覆盖所有 VM 子域名
- 创建过程异步：用户提交 → 队列 → Worker 执行 → 状态更新 → 用户获取结果

---

## 2. 系统组成

### 2.1 组件总览

| 组件            | Azure 服务         | 职责                              |
|-----------------|--------------------|------------------------------------|
| Console         | App Service        | Web UI + REST API，接受用户请求    |
| 任务队列        | Storage Queue      | 解耦 Console 和 Worker            |
| Ansible Worker  | App Service (BackgroundService) | 轮询队列，执行 Ansible playbook    |
| 状态存储        | Table Storage      | VM 记录（名称、状态、IP、token）   |
| AI Foundry      | Azure AI Foundry    | LLM 推理（gpt-5.4 模型部署）|
| App Gateway     | Application Gateway| HTTPS 终止 + 按域名路由到 VM      |
| OpenClaw VM     | Virtual Machine    | 运行 OpenClaw Gateway 实例        |

### 2.2 三层分离

| 层       | 内容                          | 构建时机              | 工具     |
|----------|-------------------------------|----------------------|----------|
| 镜像层   | OS + Node.js + OpenClaw + SSH | 提前构建 (Packer)     | `make build-image` |
| 基础设施层 | VNet + NSG + AppGw + App Service | 一次性部署 (Terraform) | `make infra-apply` |
| 实例层   | 单台 VM 的创建 + 配置 + 路由  | 按需异步创建 (Ansible) | Console → Queue → Worker |

### 2.3 关键设计决策

**异步队列模式取代同步调用：**

创建 VM 耗时较长（Azure 创建 VM + SSH 配置 + App Gateway 注册），同步等待
用户体验差且 HTTP 超时风险高。

设计：
1. Console 接收请求后立即返回 `202 Accepted` + VM 名称
2. 任务入 Storage Queue
3. BackgroundService 轮询队列，消费任务，执行 Ansible
4. 完成后更新 Table Storage 中的 VM 状态为 `ready`
5. 用户轮询 `/api/vm/{name}`，看到 `ready` 后获取 URL + token

**App Gateway 路由策略 — 按域名（Host Header）路由：**

每台 VM 创建时在 App Gateway 上动态注册：
1. 独立后端池 `pool-vm-{name}`（单一 IP）
2. Host 匹配规则 `vm-{name}.{ip}.nip.io`
3. 路由规则将该域名指向对应后端池

**设备配对 — 部署时禁用：**

deploy-vm.yml 在配置阶段设置 `gateway.controlUi.dangerouslyDisableDeviceAuth: true`，
跳过设备配对要求。token 本身即为认证凭据，配对在此场景下冗余。

---

## 3. 管理控制台（App Service）

### 3.1 职责

| 功能           | 描述                                                    |
|----------------|--------------------------------------------------------|
| 创建 VM        | 接受请求 → 分配名称 → 入队列 → 返回 202               |
| 删除 VM        | 入队列 → Worker 清理 Azure 资源 + App Gateway 路由     |
| 查看 VM 列表   | 从 Table Storage 读取所有 VM 状态                      |
| 查看 VM 详情   | 从 Table Storage 读取单台 VM（状态、URL、token）        |

### 3.2 API 设计

```
POST   /api/vm                创建 VM → 202 { name, status: "creating" }
DELETE /api/vm/{name}          删除 VM → 202 { status: "deleting" }
GET    /api/vm                 列出所有 VM（含状态）
GET    /api/vm/{name}          查看 VM 详情
                               status=creating → { name, status, created_at }
                               status=ready    → { name, status, url, token }
                               status=failed   → { name, status, error }
POST   /api/vm/{name}/restart  重启 OpenClaw 服务 → 入队列
```

### 3.3 VM 状态流转

```
creating  →  ready       正常完成
creating  →  failed      Ansible 执行失败
ready     →  deleting    用户请求删除
deleting  →  (deleted)   清理完毕，记录移除
ready     →  restarting  重启服务
restarting→  ready       重启完毕
```

### 3.4 创建 VM 流程

```python
# Console API (App Service)
def create_vm(user_id):
    name = allocate_next_name()        # vm-{prefix}-openclaw-{nn}
    password = generate_password()      # 随机强密码

    # 写入 Table Storage（状态: creating）
    table.upsert_entity({
        'PartitionKey': 'vm',
        'RowKey': name,
        'status': 'creating',
        'user_id': user_id,
        'created_at': utcnow(),
    })

    # 任务入队列
    queue.send_message(json.dumps({
        'action': 'create',
        'vm_name': name,
        'vm_admin_password': password,
    }))

    return 202, { 'name': name, 'status': 'creating' }
```

```python
# Ansible Worker (App Service BackgroundService — TaskWorkerService)
def handle_task(msg):
    task = json.loads(msg)
    if task['action'] == 'create':
        result = subprocess.run(
            ['ansible-playbook', 'deploy-vm.yml',
             '-e', f'vm_name={task["vm_name"]}',
             '-e', f'vm_admin_password={task["vm_admin_password"]}'],
            capture_output=True
        )
        if result.status == 'successful':
            token = result.get_fact('openclaw_token')
            vm_ip = result.get_fact('vm_private_ip')
            appgw_ip = get_appgw_public_ip()
            table.update_entity({
                'PartitionKey': 'vm',
                'RowKey': task['vm_name'],
                'status': 'ready',
                'vm_ip': vm_ip,
                'token': token,
                'url': f"https://{task['vm_name']}.{appgw_ip}.nip.io",
            })
        else:
            table.update_entity({
                'PartitionKey': 'vm',
                'RowKey': task['vm_name'],
                'status': 'failed',
                'error': str(result.stderr),
            })
```

### 3.5 技术选型

| 组件            | 选型                         | 理由                          |
|-----------------|------------------------------|-------------------------------|
| Console + Worker| App Service (.NET 8 Razor Pages + BackgroundService) | PaaS，无需管理 VM，自带缩放，AAD 认证，BackgroundService 轮询队列 |
| 任务队列        | Azure Storage Queue          | 简单可靠，免费额度大          |
| 状态存储        | Azure Table Storage          | 低成本，KV 查询足够           |

> App Service 启用 **VNet 集成**（绑定 `snet-worker 10.0.3.0/26`），
> 使 BackgroundService 可通过内网 SSH 访问 `snet-vm` 中的 OpenClaw VM。
> BackgroundService 没有执行超时限制，适合长时间运行的 Ansible 任务。

---

## 4. VM 镜像（Packer）

```bash
make build-image
```

Packer 创建 Windows Server 2022 托管镜像，预装：

| 层                  | 详细信息                                              |
|---------------------|-------------------------------------------------------|
| Node.js             | v22.16.0（MSI 安装）                                  |
| Git                 | 最新版，Chocolatey 安装（CDN 超时 3 次重试）           |
| Python              | v3.12.9（静默安装 + pip + virtualenv/requests/playwright）|
| VS Build Tools 2022 | C++ 工作负载（编译 sharp、node-pty 等原生模块）        |
| OpenClaw            | v2026.3.12（`npm -g`，前缀 `C:\openclaw`）            |
| clawhub             | latest（技能注册表 CLI）                               |
| Playwright Chromium | Node.js playwright-core 浏览器二进制 + Python Playwright Chromium |
| Chromium            | 独立浏览器（Chocolatey），支持 headless               |
| FFmpeg              | 音视频处理（Discord 语音消息、媒体管道）               |
| PowerShell 7 (pwsh) | 现代 PowerShell，exec 工具优先使用                    |
| 常用工具            | 7-Zip, jq, curl, wget, vim, ripgrep, fd, less, bat, Sysinternals |
| 服务                | NSSM `OpenClawGateway`，手动启动模式（demand start）   |
| 防火墙              | 端口 18789（OpenClaw）+ 端口 22（SSH）已开放           |
| OpenSSH             | Server 已启用，自动启动，默认 Shell = PowerShell       |
| Sysprep             | 已泛化，可重复使用                                     |

构建流程（9 步）：
```
1. install-node.ps1          # Node.js (MSI 静默安装)
2. install-git.ps1           # Git (via Chocolatey，同时安装 Chocolatey)
3. install-python.ps1        # Python (静默安装 + pip + Playwright + Chromium)
4. install-common-tools.ps1  # 常用工具 (7zip, jq, rg, fd, bat, FFmpeg, pwsh, Chromium, etc.)
5. install-vsbuildtools.ps1  # VS Build Tools 2022 (C++ 工作负载，原生模块编译)
6. install-openclaw.ps1      # OpenClaw (npm global) + Playwright Chromium + clawhub
7. register-openclaw-service.ps1  # NSSM 服务注册 + 防火墙规则
8. configure-ssh.ps1         # OpenSSH Server (Ansible 管理用)
9. Sysprep                   # Azure 镜像通用化
```

构建细节：
- 使用**已有 RG + KV**（`build_resource_group`、`build_key_vault_name`）
  避免创建临时资源，绕过组织策略限制
- 所有构建资源标记 `SecurityControl=Ignore`
- WinRM 仅在构建期间使用（Azure 自动配置）；部署后的 VM 使用 SSH

输出：`openclaw-windows-{version}` 镜像位于 `rg-{prefix}-openclaw`。

---

## 5. Ansible Playbook

### 5.1 deploy-vm.yml — 创建并初始化 VM

Ansible Worker（App Service BackgroundService）调用此 playbook，传入 `vm_name` 和 `vm_admin_password`。

**Play 1: 创建 Azure 资源（localhost）**

| 步骤 | 操作                                                    |
|------|---------------------------------------------------------|
| 1    | 从 Terraform 输出获取基础资源信息                          |
| 2    | 创建 NIC（`{vm_name}-nic`），绑定 VM 子网               |
| 3    | 创建 VM（基于镜像，计算机名 ≤15 字符）                     |
| 4    | 获取 VM 私有 IP                                          |
| 5    | App Gateway: 创建后端池 `pool-{nn}` → [VM IP]           |
| 6    | App Gateway: 创建 HTTPS Listener `listener-{nn}`（绑定 `{vm_name}.{pip}.nip.io`）|
| 7    | App Gateway: 创建路由规则 `rule-{nn}` → listener + 后端池 |

**Play 2: 配置 OpenClaw + AOAI（SSH 到 VM）**

| 步骤 | 操作                                                    |
|------|---------------------------------------------------------|
| 1    | `openclaw config set gateway.mode local`                |
| 2    | `openclaw config set gateway.controlUi.dangerouslyAllowHostHeaderOriginFallback true` |
| 3    | `openclaw config set gateway.controlUi.dangerouslyDisableDeviceAuth true` |
| 4    | 合并 AOAI provider 配置到 `openclaw.json`（`#` fragment trick + API Key，见 7.3） |
| 5    | 重写 `openclaw-gateway.cmd`（`--bind lan`，Packer 镜像默认 `--bind loopback`） |
| 6    | 复制 `openclaw.json` 到 SYSTEM profile → 启动 NSSM 服务 |
| 7    | 等待 token 生成，提取 gateway auth token                 |

**Play 3: 保存 VM 记录（localhost）**

| 步骤 | 操作                                                    |
|------|---------------------------------------------------------|
| 1    | 保存 VM 记录到 `vms.yml` + `host_vars/{vm_name}.yml`    |
| 2    | 重建 `inventory/hosts.yml`                              |
| 3    | 输出：`{ vm_name, vm_ip, token, url }`                  |

### 5.2 remove-vm.yml — 退役 VM（待实现）

```
1. App Gateway: 删除路由规则 + 后端池
2. Azure: 删除 VM、NIC、磁盘
3. 清理 inventory + vms.yml 记录
```

### 5.3 configure-vm.yml — 重新配置已有 VM

用于批量更新配置或升级 OpenClaw 版本，对已有 VM 重新运行配置步骤。
与 deploy-vm.yml Play 2 逻辑一致：设置 gateway、合并 AOAI provider 到 `openclaw.json`、
重写 launcher（`--bind lan`）、复制到 SYSTEM profile、重启服务。

```bash
make vm-configure AOAI_API_KEY=xxx
# 或
ansible-playbook configure-vm.yml -i inventory/ -e aoai_api_key=xxx
```

---

## 6. 基础设施（Terraform）

```bash
make infra-init     # 首次：下载 Terraform provider
make infra-apply    # 创建/更新所有基础设施资源
```

| 资源                | 配置                                                  |
|---------------------|-------------------------------------------------------|
| 资源组              | `rg-{prefix}-openclaw-infra`                          |
| 虚拟网络            | `10.0.0.0/22`（1024 IPs）                              |
| VM 子网             | `snet-vm` — `10.0.1.0/24`（251 可用，最多 200 VM）     |
| AppGw 子网          | `snet-appgw` — `10.0.2.0/24`                          |
| Worker 子网         | `snet-worker` — `10.0.3.0/26`（App Service VNet 集成）|
| 网络安全组          | SSH: 仅 snet-worker + 跳板机；18789: 仅 AppGw 子网    |
| NAT Gateway         | `nat-{prefix}-openclaw`，绑定 snet-vm（VM 出站访问 AI Foundry） |
| 公共 IP             | 静态，Standard SKU（App Gateway + NAT Gateway 各一个） |
| Storage Account     | Queue（vm-tasks）+ Table（vm-records）                 |
| App Service          | Console + Worker（Web UI + REST API + BackgroundService） |
| Function App        | 已移除 — Worker 已合并到 App Service BackgroundService  |
| AI Foundry          | `auto-gen-chat`（已有资源），部署 gpt-5.4   |
| 应用网关            | HTTPS (443) 通配符证书，按域名路由（每 VM 独立后端池 + Listener + Rule）|

> Terraform 管理 App Gateway（通配符证书 + 默认后端）。
> 每台 OpenClaw VM 的后端池和路由规则由 Ansible Worker 在 `deploy-vm.yml` 中动态添加。
> Terraform 使用 `lifecycle { ignore_changes }` 保护 Ansible 管理的动态路由状态，
> 因此 `terraform apply` 不会破坏已有 VM 的后端池、监听器和路由规则。

> Console（App Service）为公网 PaaS 服务，自带 HTTPS 域名。
> App Service 通过 VNet 集成的 `snet-worker` 子网 SSH 到 `snet-vm` 子网。

**VNet 对等互联（可选，仅运维跳板机需要）：**

如果运维跳板机在独立 VNet 中，需创建双向对等互联以便 SSH 进入：
```bash
# 跳板机 VNet → OpenClaw VNet
az network vnet peering create \
  --name peer-jumpbox-to-openclaw \
  --resource-group <jumpbox-rg> \
  --vnet-name <jumpbox-vnet> \
  --remote-vnet /subscriptions/.../vnet-{prefix}-openclaw \
  --allow-vnet-access

# 反向
az network vnet peering create \
  --name peer-openclaw-to-jumpbox \
  --resource-group rg-{prefix}-openclaw-infra \
  --vnet-name vnet-{prefix}-openclaw \
  --remote-vnet /subscriptions/.../jumpbox-vnet \
  --allow-vnet-access
```

---

## 7. AI Foundry（AOAI）

### 7.1 资源

使用已有的 `auto-gen-chat` AI Foundry 资源（`aif-auto-gen-chat`，非 Terraform 部署）：

| 项目                   | 配置                                                    |
|------------------------|--------------------------------------------------------|
| Azure AI Foundry Account | `aif-auto-gen-chat`（`rg-auto-gen-chat`）               |
| Endpoint               | `https://aif-auto-gen-chat.openai.azure.com/`           |
| 模型部署               | `gpt-5.4`                                         |
| 认证                   | API Key（`localAuth` 已启用）                            |

### 7.2 认证方案 — API Key + `#` Fragment Trick

使用 **Custom Provider** + 手动 JSON 配置接入 AI Foundry。
核心技巧：在 `baseUrl` 末尾加 `#`，利用 URL fragment 防止 SDK 路径拼接破坏查询参数。

**认证流程**：
```
OpenClaw VM（NSSM 服务以 SYSTEM 运行）
       │
       │ api-key header（Authorization: Bearer <key>）
       ▼
  AI Foundry (aif-auto-gen-chat)
       │ /openai/deployments/gpt-5.4/chat/completions?api-version=2024-10-21
       ▼
  推理结果（gpt-5.4）
```

**`#` Fragment Trick 原理**：

OpenAI SDK 内部用 `new URL(baseURL + path)` 拼接请求 URL。
如果 `baseUrl` 含查询参数（如 `?api-version=...`），SDK 追加的路径会破坏 URL：

```
# 不加 # — 错误！path 追加到查询参数后面
baseUrl:  https://host/openai/deployments/model/chat/completions?api-version=2024-10-21
SDK 拼接: https://host/openai/deployments/model/chat/completions?api-version=2024-10-21/chat/completions
                                                                                       ^^^^^^^^^^^^^^^^^^^ 错误

# 加 # — 正确！path 变成 fragment，HTTP 客户端发送时自动丢弃 fragment
baseUrl:  https://host/openai/deployments/model/chat/completions?api-version=2024-10-21#
SDK 拼接: https://host/openai/deployments/model/chat/completions?api-version=2024-10-21#/chat/completions
服务器收到: https://host/openai/deployments/model/chat/completions?api-version=2024-10-21  ✅
```

> **关键注意事项**：
> - `baseUrl` 必须包含完整路径（含 `/chat/completions`），因为 `#` 后面的 SDK 追加路径会被丢弃
> - `api-version` 必须作为查询参数写入 `baseUrl`，OpenClaw 运行时**不会**自动注入
> - `#` 仅影响 URL 解析，不影响认证或请求体

### 7.3 OpenClaw 配置

AOAI provider 配置直接合并到 `openclaw.json`（位于 `~/.openclaw/openclaw.json`，SYSTEM profile 对应 `C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json`）。

> **注意**：不要使用单独的 `settings.json`。Gateway 服务（NSSM 以 SYSTEM 运行）只读取 `openclaw.json`，
> 单独的 `settings.json` 不会被可靠加载。所有 AOAI 配置必须合并到 `openclaw.json` 中。

配置完成后，`openclaw.json` 中应包含以下内容（与 gateway 基础配置合并在同一个文件中）：

```json
{
  "models": {
    "mode": "merge",
    "providers": {
      "auto-gen-chat": {
        "baseUrl": "https://aif-auto-gen-chat.openai.azure.com/openai/deployments/gpt-5.4/chat/completions?api-version=2024-10-21#",
        "api": "openai-completions",
        "apiKey": "<AOAI_API_KEY>",
        "models": [
          {
            "id": "gpt-5.4",
            "name": "gpt-5.4 (Azure OpenAI)",
            "contextWindow": 128000,
            "maxTokens": 16384,
            "input": ["text"],
            "cost": { "input": 0, "output": 0, "cacheRead": 0, "cacheWrite": 0 },
            "reasoning": false
          }
        ]
      }
    }
  },
  "agents": {
    "defaults": {
      "model": {
        "primary": "auto-gen-chat/gpt-5.4"
      }
    }
  }
}
```

> **配置说明**：
> - `baseUrl` 末尾的 `#` 是必须的（防止 SDK 路径拼接破坏 `api-version` 参数）
> - `baseUrl` 包含完整路径 `/openai/deployments/gpt-5.4/chat/completions`
> - `api: "openai-completions"` 对应 Chat Completions API
> - `models.mode: "merge"` 保留内置模型列表，合并自定义 provider
> - `agents.defaults.model.primary` 设置默认使用的模型

**Ansible 自动化配置**（Playbook 中 PowerShell 脚本直接合并到 `openclaw.json`）：

```powershell
# 合并 AOAI provider 配置到 openclaw.json（由 deploy-vm.yml 自动执行）
$configPath = "$env:USERPROFILE\.openclaw\openclaw.json"
$config = Get-Content $configPath -Raw | ConvertFrom-Json

# 确保 models.providers 存在
if (-not $config.models) {
  $config | Add-Member -NotePropertyName 'models' -NotePropertyValue @{} -Force
}
if (-not $config.models.providers) {
  $config.models | Add-Member -NotePropertyName 'providers' -NotePropertyValue @{} -Force
}
$config.models | Add-Member -NotePropertyName 'mode' -NotePropertyValue 'merge' -Force

# 写入 auto-gen-chat provider（# fragment trick）
$provider = @{
  baseUrl  = "https://aif-auto-gen-chat.openai.azure.com/openai/deployments/gpt-5.4/chat/completions?api-version=2024-10-21#"
  api      = "openai-completions"
  apiKey   = "<AOAI_API_KEY>"
  models   = @(
    @{
      id            = "gpt-5.4"
      name          = "gpt-5.4 (Azure OpenAI)"
      contextWindow = 128000
      maxTokens     = 16384
      input         = @("text")
      cost          = @{ input = 0; output = 0; cacheRead = 0; cacheWrite = 0 }
      reasoning     = $false
    }
  )
}
$config.models.providers | Add-Member -NotePropertyName 'auto-gen-chat' -NotePropertyValue $provider -Force

# 设置默认模型
if (-not $config.agents) {
  $config | Add-Member -NotePropertyName 'agents' -NotePropertyValue @{} -Force
}
if (-not $config.agents.defaults) {
  $config.agents | Add-Member -NotePropertyName 'defaults' -NotePropertyValue @{} -Force
}
$config.agents.defaults | Add-Member -NotePropertyName 'model' -NotePropertyValue @{ primary = "auto-gen-chat/gpt-5.4" } -Force

# 写回
$config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8
```

### 7.4 MCAPSGov 策略约束

MSFT 订阅下，管理组级别策略 `MCAPSGovDenyPolicies` 对 Terraform 部署的
AI Foundry 资源强制 `disableLocalAuth: true`，API Key 不可用。

**当前规避方案**：使用已有的 `auto-gen-chat` 资源（`localAuth` 已启用），不受此策略影响。

### 7.5 Token 刷新

API Key 认证**无需 token 刷新**。Key 为静态凭证，只需在配置中设置一次。

### 7.6 Terraform 资源示例（可选）

当前使用已有 `auto-gen-chat` 资源（`localAuth` 已启用），无需 Terraform 部署 AI Foundry。
如未来需独立部署，参考如下配置：

```hcl
# Azure OpenAI Account
resource "azurerm_cognitive_account" "aoai" {
  name                = "oai-${var.prefix}-openclaw"
  resource_group_name = azurerm_resource_group.infra.name
  location            = var.location
  kind                = "OpenAI"
  sku_name            = "S0"
}

# 模型部署
resource "azurerm_cognitive_deployment" "gpt54" {
  name                 = "gpt-5.4"
  cognitive_account_id = azurerm_cognitive_account.aoai.id
  model {
    format  = "OpenAI"
    name    = "gpt-5.4"
  }
  sku {
    name     = "GlobalStandard"
    capacity = 30
  }
}
```

---

## 8. 证书体系

### 8.1 CA 证书

`make ssl-cert` 会在本地用 openssl 生成 CA（确保 `CA:TRUE`）：

```
infra/certs/
├── ca.key          # CA 私钥（不提交代码库）
├── ca.pem          # CA 公钥（用户导入浏览器信任根存储）
├── wildcard.pfx    # 通配符证书 PFX（App Gateway 使用）
├── wildcard.pem    # 通配符证书公钥
└── wildcard.key    # 通配符证书私钥
```

> **已知问题：** Azure KV 生成的 CA 证书即使策略指定 `isCA:true`，
> 实际 `Basic Constraints` 仍为 `CA:FALSE`。因此使用本地 openssl 生成。
> KV 中的 CA 证书仅用于历史备份，不参与签发。

### 8.2 通配符证书

```bash
make ssl-cert
```

1. 从 Terraform 输出获取 App Gateway 公共 IP
2. 本地生成 CA（`CA:TRUE`，如已存在则复用）
3. CA 签发 `*.{appgw-ip}.nip.io`（10 年有效期，SAN 包含裸域名）
4. 输出 PFX 到 `infra/certs/wildcard.pfx`
5. 再次 `make infra-apply` 自动检测 PFX 并启用 HTTPS

```
证书链：
  OpenClaw 内部 CA  (CA:TRUE, 存储在 KV)
    └── *.20.38.7.158.nip.io
```

用户导入 CA 证书到浏览器信任根存储一次即可信任所有 VM 子域名。

---

## 9. 部署流程

### Phase 0: 环境配置

在 `IaC/Azure/` 目录创建 `.env`：

```bash
PREFIX=ymms
SUBSCRIPTION_ID=<your-subscription-id>
LOCATION=westus3
```

资源命名规则：

| 资源                 | 命名                           |
|----------------------|--------------------------------|
| 基础资源组           | `rg-{prefix}-openclaw`         |
| Key Vault            | `kv-{prefix}-openclaw`         |
| 基础设施资源组       | `rg-{prefix}-openclaw-infra`   |
| 虚拟网络             | `vnet-{prefix}-openclaw`       |
| 网络安全组           | `nsg-{prefix}-openclaw-vm`     |
| 应用网关             | `appgw-{prefix}-openclaw`      |
| 公共 IP              | `pip-{prefix}-appgw`           |
| Storage Account      | `st{prefix}openclaw`           |
| App Service          | `app-{prefix}-console`         |
| Azure AI Foundry         | `auto-gen-chat`（已有资源）    |
| OpenClaw VM          | `vm-{prefix}-openclaw-{nn}`    |

### Phase 1: 登录 Azure

```bash
make login                   # 浏览器登录
make login DEVICE_CODE=1     # 设备码登录（无头/SSH 环境）
```

### Phase 2: 初始化基础资源

```bash
make init     # RG + KV + CA（openssl 生成，上传 KV）
```

### Phase 3: 构建 VM 镜像

```bash
make build-image    # Packer: Windows 2022 + Node.js + OpenClaw + SSH
```

### Phase 4: 部署基础设施

```bash
make infra-init
make infra-apply                              # 先部署 VNet + NSG + App Gateway (HTTP)
make ssl-cert                                # 生成 CA + 通配符证书（需要 App Gateway PIP）
make infra-apply                              # 再次 apply 启用 HTTPS（PFX 自动检测）
```

### Phase 5: 部署 VM（MVP — 手动）

当前阶段使用 `make vm-deploy` 手动部署。Console + Worker 异步流程待实现。

```bash
# 手动创建 VM（运维直接调用 Ansible）
make vm-deploy VM_NAME=01 VM_PASSWORD='<password>'

# 输出示例：
#   VM:    vm-ymms-openclaw-01
#   IP:    10.0.1.4
#   Token: c8313ba9001bbf47...
#   URL:   https://vm-ymms-openclaw-01.20.38.7.158.nip.io
#   Model: auto-gen-chat/gpt-5.4

# 查看已部署 VM
make vm-list

# SSH 到 VM
make vm-ssh VM_NAME=01 VM_PASSWORD='<password>'
```

> **MVP 限制**：
> - 单后端池（多 VM → 需改用按域名路由）
> - Token 手动记录在 `ansible/vms.yml`（非数据库）
> - 无 Console Web UI，无异步队列

---

## 10. 目录结构

```
IaC/Azure/
├── .env                        # 环境配置（PREFIX, SUBSCRIPTION_ID, LOCATION）
├── Makefile                    # 编排器 — 基础设施操作入口
├── DEPLOY.md                   # 本文档
│
├── image/                      # Packer VM 镜像定义
│   ├── openclaw-windows.pkr.hcl
│   ├── variables.auto.pkrvars.hcl
│   ├── README.md
│   └── scripts/
│       ├── install-node.ps1
│       ├── install-git.ps1
│       ├── install-python.ps1
│       ├── install-common-tools.ps1
│       ├── install-vsbuildtools.ps1
│       ├── install-openclaw.ps1
│       ├── register-openclaw-service.ps1
│       └── configure-ssh.ps1
│
├── infra/                      # Terraform 基础设施
│   ├── main.tf                 #   VNet, NSG, AppGw, AI Foundry, MI
│   ├── README.md
│   ├── certs/                  #   CA + 通配符证书（make ssl-cert 生成，gitignore）
│   └── terraform.tfstate       #   (gitignore)
│
├── console/                    # Admin Console（.NET 8 Razor Pages + BackgroundService）
│   ├── OpenClaw.Console.csproj #   项目文件
│   ├── Program.cs              #   DI + Minimal API + Razor Pages 入口
│   ├── Dockerfile              #   自定义容器（.NET 8 + Ansible + az cli + SSH）
│   ├── appsettings.json        #   Storage Account / Queue / Table / Azure 配置
│   ├── Models/
│   │   ├── VmRecord.cs         #   Table Storage 实体
│   │   └── VmTaskMessage.cs    #   Queue 消息模型
│   ├── Services/
│   │   ├── VmTableService.cs   #   Azure Table Storage CRUD
│   │   ├── VmQueueService.cs   #   Azure Queue 发送
│   │   └── TaskWorkerService.cs#   BackgroundService 队列轮询 + Ansible 执行
│   └── Pages/
│       ├── Index.cshtml(.cs)   #   VM Dashboard（列表 + 创建/删除/重启）
│       └── VmDetail.cshtml(.cs)#   VM 详情（IP / Token / URL / Error）
│
└── ansible/                    # VM 部署与配置 Playbook
    ├── deploy-vm.yml           #   创建 VM + 配置 AOAI + 注册路由
    ├── configure-vm.yml        #   重新配置已有 VM
    ├── vms.yml                 #   VM 注册表（token + URL，自动生成）
    ├── group_vars/
    │   └── all.yml             #   全局变量默认值
    └── inventory/
        ├── hosts.yml           #   静态 inventory
        └── host_vars/
            └── vm-ymms-openclaw-01.yml
```

---

## 11. 安全模型

| 层                   | 控制措施                                              |
|----------------------|-------------------------------------------------------|
| 网络（NSG）          | SSH: 仅 Worker 子网（`10.0.3.0/26`）+ 跳板机网段     |
|                      | 端口 18789：仅 App Gateway 子网（`10.0.2.0/24`）      |
| 传输层               | HTTPS（TLS 1.2+）通过 App Gateway，CA 签发通配符证书   |
| VM 隔离              | 每台 VM 独立后端池 + 独立路由规则，域名 1:1 映射       |
| Gateway 认证         | 每台 VM 独立 token（自动生成），Console 返回给用户      |
| AOAI 认证            | API Key（存储在 VM 本地 `openclaw.json`，合并在 gateway 配置中）|
| AI Foundry 网络    | 已有资源 `auto-gen-chat`，通过公网 endpoint 访问      |
| Console 认证         | （待设计）App Service Authentication / OAuth / SSO     |
| 构建资源             | 标记 `SecurityControl=Ignore` 以绕过组织策略           |
| Console 网络         | App Service（PaaS），可配置 IP 限制或 Private Endpoint |
| Worker 网络          | App Service VNet 集成（`snet-worker`），内网 SSH 到 VM |
| 密钥管理             | CA 私钥在 KV；VM 密码 + token 在 Table Storage        |
|                      | AOAI API Key 存储在 VM 本地配置文件                   |

---

## 12. Make Target 参考

```
  login           登录 Azure CLI
  init            初始化 RG、Key Vault 和 CA 证书
  build-image     使用 Packer 构建 VM 镜像
  ssl-cert        生成通配符 SSL 证书
  infra-init      初始化 Terraform（下载 provider）
  infra-plan      预览基础设施变更
  infra-apply     部署基础设施（VNet、NSG、App Gateway）
  infra-destroy   销毁基础设施
  infra-output    显示 Terraform 输出
  vm-deploy       手动部署 VM（VM_NAME=xx VM_PASSWORD=xx）
  vm-remove       退役 VM（清理资源 + 路由 + 记录）
  vm-configure    重新配置已有 VM
  vm-ssh          SSH 到 VM（VM_NAME=xx）
  vm-ping         ping 所有已部署 VM
  vm-list         列出已部署 VM 及 token
  console-deploy  部署 Console App Service 代码（含 BackgroundService Worker）
  show-ca         显示 CA 证书详情
  download-ca     下载 CA 公共证书
  help            显示帮助
```

---

## 13. 实施状态

### Phase 1 MVP — 已完成 ✅

| 组件              | 状态   | 说明                                             |
|-------------------|--------|--------------------------------------------------|
| Terraform 基础设施 | ✅ 完成 | VNet, NSG, App Gateway (HTTPS)                    |
| Packer 镜像       | ✅ 完成 | `openclaw-windows-2026.3.12`                      |
| deploy-vm.yml     | ✅ 完成 | 3-Play: 创建 VM → 配置 AOAI → 保存记录          |
| AOAI 认证         | ✅ 完成 | API Key + `#` trick（`auto-gen-chat/gpt-5.4`）|
| Token 刷新        | N/A    | API Key 为静态凭证，无需刷新                        |
| HTTPS + nip.io    | ✅ 完成 | 通配符证书 `*.{ip}.nip.io`，HTTP 301→HTTPS       |
| App Gateway 路由  | ✅ 完成 | 每 VM 独立后端池 + Host Listener + 路由规则  |

### 已知环境约束

- **MCAPSGov 策略**：管理组级别策略对 Terraform 部署的 AI Foundry 资源强制
  `disableLocalAuth=true`。当前使用已有 `auto-gen-chat` 资源（`localAuth` 已启用）规避。
- **AI Foundry Endpoint**：`https://aif-auto-gen-chat.openai.azure.com/`
- **baseUrl 格式**：完整路径 + `?api-version=2024-10-21#`（`#` fragment trick 防止 SDK 路径拼接破坏查询参数）

### 待实现

- [x] SSL 证书 + HTTPS (App Gateway) — 内部 CA 签发通配符证书
- [x] 多 VM 按域名路由（每 VM 独立后端池 `pool-{nn}` + Host Listener `listener-{nn}` + 路由规则 `rule-{nn}`）
- [x] remove-vm.yml（退役 VM + 清理路由 + 记录）
- [x] Console App Service（Web UI + REST API）
- [x] Worker BackgroundService（队列轮询 + Ansible 异步执行）
- [x] 设备配对 — 部署时禁用 `dangerouslyDisableDeviceAuth`
- [ ] 评估迁移到 Managed Identity Keyless 认证（exec secrets provider + MI token）

---

## 14. 故障排查

| 症状                             | 原因与修复                                            |
|----------------------------------|-------------------------------------------------------|
| Packer `KeyVaultAccessInternalError` | Azure KV 传播延迟。使用已有 KV（`build_key_vault_name`）|
| SSH `Permission denied`          | SSH 密钥未部署。先用 `sshpass` + 密码，再推送密钥      |
| HTTPS `502 Bad Gateway`          | VM 未注册后端池 / Gateway 未运行。检查 `make vm-list`  |
| HTTPS 证书警告                   | CA 未导入浏览器，或 CA 为 `CA:FALSE`。用 openssl 重新生成 |
| 域名访问到错误 VM                | Host 路由规则未正确创建。检查 App Gateway 路由配置     |
| Chocolatey 504 超时              | CDN 间歇性故障。脚本已内置 3 次重试                    |
| 部署后找不到 token               | 配置路径不匹配（Sysprep 重命名用户目录）。使用 `$env:USERPROFILE` |
| 计算机名超过 15 字符             | Windows 限制。Playbook 截短为 `{prefix}-oc-{nn}`      |
| Console 创建 VM 超时             | BackgroundService 无执行超时限制，检查 Ansible 日志 |
| 队列消息未被消费                 | App Service 未启动或 BackgroundService 异常。检查 App Service 日志 |
| Worker 无法 SSH 到 VM            | snet-worker 子网未委派给 App Service，或 NSG 规则缺失 |
| AOAI 返回 401 Unauthorized       | API Key 错误或未配置。检查 `openclaw.json` 中 `models.providers.auto-gen-chat.apiKey` 字段 |
| AOAI 返回 403 Forbidden          | API Key 无权访问该模型部署，检查 AI Foundry 资源访问控制   |
| OpenClaw 无法连接 AOAI           | 检查 endpoint URL 和 deployment 名称是否正确           |
| VM 无法访问 AOAI（超时/无响应）  | VM 子网缺少 NAT Gateway。Azure 2024+ 默认不提供出站 internet，需创建 NAT Gateway 绑定到 snet-vm |
| AOAI 返回 404 Resource not found | `baseUrl` 缺少 `#` 后缀，导致 SDK 路径拼接破坏 `api-version` 参数。确保 `baseUrl` 末尾有 `#`，且包含完整 `/openai/deployments/{model}/chat/completions?api-version=2024-10-21#` 路径 |
| AOAI `AuthenticationTypeDisabled` | MCAPSGov 策略禁止 API Key。更换到 `localAuth` 已启用的 AI Foundry 资源 |
| Gateway "Unknown config keys"    | `models.primary` 不是有效配置键，不要设置它             |
| Gateway config "input" 类型错误  | PowerShell `ConvertTo-Json` 展平单元素数组。用 `Add-Member` 合并方式替代直接写 JSON |
| HTTPS 502 Bad Gateway            | Terraform apply 可能清空后端池。重新 `az network application-gateway address-pool update --servers <vm-ip>` |
| HTTPS 502 因 Gateway bind        | Packer 镜像默认 `--bind loopback`（仅 localhost）。Playbook 需重写为 `--bind lan` 以便 App Gateway 到达 |
| SSH PowerShell `$` 变量被吞      | 不要内联 PowerShell 到 SSH，改为 SCP 脚本 + 远程执行   |
| gpt-5.3-codex 返回 400/404      | gpt-5.3-codex 不支持 Chat Completions API 也不支持 Responses API。换用 gpt-5.4 或 gpt-4o |
