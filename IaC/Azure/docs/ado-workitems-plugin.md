# Azure DevOps Work Items 插件

## 概述

`azure-devops-workitems` 是一个 OpenClaw 插件，让 Agent 能直接操作 Azure DevOps Work Items（查询、创建、更新、查看详情）。

认证方式为 **Service Principal（client_credentials）**，无需 PAT。

## 架构

```
┌────────────────────┐
│   Agent (gpt-5.4)  │
│   "查询所有任务"    │
└────────┬───────────┘
         │ 调用 ado_query_workitems 工具
         ▼
┌────────────────────────────────┐
│  azure-devops-workitems plugin │
│  (index.ts · jiti 运行时加载)   │
│                                │
│  1. 获取 SP Bearer Token       │
│     POST login.microsoftonline │
│     .com/{tenant}/oauth2/v2.0  │
│     /token                     │
│     scope=499b84ac.../.default │
│                                │
│  2. 调用 DevOps REST API       │
│     dev.azure.com/{org}/       │
│     {project}/_apis/wit/...    │
│                                │
│  3. 返回结构化结果给 Agent      │
└────────────────────────────────┘
```

Token 自动缓存，过期前 60 秒刷新。

## 注册的工具

| 工具名 | 功能 | 关键参数 |
|--------|------|----------|
| `ado_query_workitems` | WIQL 查询 work items | `query` (WIQL 语句) |
| `ado_get_workitem` | 获取单个 work item 完整详情 | `id` (工单 ID) |
| `ado_create_workitem` | 创建新 work item | `type`, `title`, `description`, `assignedTo`, `priority` 等 |
| `ado_update_workitem` | 更新已有 work item | `id`, 以及要修改的字段 (`title`, `state`, `comment` 等) |

## 前置条件

### 1. Service Principal

需要一个在 Azure DevOps 组织中已授权的 SP：

```bash
# 创建 SP
az ad sp create-for-rbac --name openclaw-devops-mcp --skip-assignment

# 输出中的 appId = AZURE_CLIENT_ID
# 输出中的 password = AZURE_CLIENT_SECRET
# 输出中的 tenant = AZURE_TENANT_ID
```

然后在 Azure DevOps 组织设置中手动添加该 SP 为用户（Basic 许可证）：
- 组织设置 → 用户 → 添加用户 → 选择 "Service Principal"
- 或通过 API：`POST https://vsaex.dev.azure.com/{org}/_apis/ServicePrincipalEntitlements`

### 2. 环境变量

VM 上需要以下 Machine 级环境变量：

| 变量 | 说明 | 示例 |
|------|------|------|
| `AZURE_CLIENT_ID` | SP 的 Application (Client) ID | `5e088c83-efaa-4978-8a03-...` |
| `AZURE_CLIENT_SECRET` | SP 的 Client Secret | `lSa8Q~66ketl...` |
| `AZURE_TENANT_ID` | Azure AD Tenant ID | `f74af430-12a3-4377-...` |
| `ADO_ORG` | DevOps 组织名 | `yongmams` |
| `ADO_PROJECT` | 默认项目名（可选，默认 `OpenClaw-PoC`） | `OpenClaw-PoC` |

## 手动部署步骤

以下以 VM-16（10.0.1.4）为例，从 jumpbox 操作。

### 步骤 1：设置环境变量

```powershell
# 在 VM 上执行（PowerShell）
[System.Environment]::SetEnvironmentVariable("AZURE_CLIENT_ID", "<client-id>", "Machine")
[System.Environment]::SetEnvironmentVariable("AZURE_CLIENT_SECRET", "<secret>", "Machine")
[System.Environment]::SetEnvironmentVariable("AZURE_TENANT_ID", "<tenant-id>", "Machine")
[System.Environment]::SetEnvironmentVariable("ADO_ORG", "yongmams", "Machine")
[System.Environment]::SetEnvironmentVariable("ADO_PROJECT", "OpenClaw-PoC", "Machine")
```

### 步骤 2：上传插件文件

```bash
# 从 jumpbox SCP 三个文件到 VM
scp package.json openclaw.plugin.json index.ts \
  azureuser@10.0.1.4:'C:\openclaw-plugins\azure-devops-workitems\'
```

### 步骤 3：安装 npm 依赖

```powershell
cd C:\openclaw-plugins\azure-devops-workitems
npm install --omit=dev
```

### 步骤 4：注册插件

```powershell
openclaw plugins install -l C:\openclaw-plugins\azure-devops-workitems
```

### 步骤 5：配置允许列表

在 `openclaw.json` 的 `plugins` 下添加：

```json
{
  "plugins": {
    "allow": ["azure-devops-workitems"]
  }
}
```

### 步骤 6：重启 Gateway

```powershell
Stop-ScheduledTask -TaskName OpenClawGateway
Start-Sleep -Seconds 2
Start-ScheduledTask -TaskName OpenClawGateway
```

### 步骤 7：验证

```powershell
# 检查插件状态
openclaw plugins list
# 应显示 azure-devops-workitems | loaded

# 测试工具调用
openclaw agent --agent main --message "用 ado_query_workitems 查询所有 work items"
```

## 集成到 deploy-vm.yml（Ansible Playbook）

如果要在自动化部署中加入此插件，需修改以下文件：

### group_vars/all.yml — 添加变量声明

```yaml
# Azure DevOps Service Principal (for ado plugin)
ado_org: "yongmams"
ado_project: "OpenClaw-PoC"
ado_sp_client_id: ""       # -e ado_sp_client_id=xxx
ado_sp_client_secret: ""   # -e ado_sp_client_secret=xxx
ado_sp_tenant_id: ""       # -e ado_sp_tenant_id=xxx
```

### deploy-vm.yml Play 1 — add_host 传递变量

在 `Add VM to in-memory inventory` 任务中增加：

```yaml
ado_org_fact: "{{ ado_org }}"
ado_project_fact: "{{ ado_project }}"
ado_sp_client_id_fact: "{{ ado_sp_client_id }}"
ado_sp_client_secret_fact: "{{ ado_sp_client_secret }}"
ado_sp_tenant_id_fact: "{{ ado_sp_tenant_id }}"
```

### deploy-vm.yml Play 2 — 在 reboot 之前插入任务

在 AOAI 配置完成之后、`Reboot VM` 之前，插入以下任务：

```yaml
# --- Azure DevOps Work Items Plugin ---
- name: Set ADO Service Principal env vars (Machine scope)
  ansible.windows.win_shell: !unsafe |
    [System.Environment]::SetEnvironmentVariable("ADO_ORG", $env:V_ADO_ORG, "Machine")
    [System.Environment]::SetEnvironmentVariable("ADO_PROJECT", $env:V_ADO_PROJECT, "Machine")
    [System.Environment]::SetEnvironmentVariable("AZURE_CLIENT_ID", $env:V_CLIENT_ID, "Machine")
    [System.Environment]::SetEnvironmentVariable("AZURE_CLIENT_SECRET", $env:V_CLIENT_SECRET, "Machine")
    [System.Environment]::SetEnvironmentVariable("AZURE_TENANT_ID", $env:V_TENANT_ID, "Machine")
    Write-Output "ADO SP env vars set"
  environment:
    V_ADO_ORG: "{{ ado_org_fact }}"
    V_ADO_PROJECT: "{{ ado_project_fact }}"
    V_CLIENT_ID: "{{ ado_sp_client_id_fact }}"
    V_CLIENT_SECRET: "{{ ado_sp_client_secret_fact }}"
    V_TENANT_ID: "{{ ado_sp_tenant_id_fact }}"
  no_log: true
  when: ado_sp_client_id_fact | length > 0

- name: Create plugin directory
  ansible.windows.win_file:
    path: 'C:\openclaw-plugins\azure-devops-workitems'
    state: directory
  when: ado_sp_client_id_fact | length > 0

- name: Deploy plugin files
  ansible.windows.win_copy:
    src: "{{ playbook_dir }}/../plugins/azure-devops-workitems/{{ item }}"
    dest: 'C:\openclaw-plugins\azure-devops-workitems\{{ item }}'
  loop:
    - package.json
    - openclaw.plugin.json
    - index.ts
  when: ado_sp_client_id_fact | length > 0

- name: Install plugin npm dependencies
  ansible.windows.win_shell: |
    cd C:\openclaw-plugins\azure-devops-workitems
    npm install --omit=dev
  when: ado_sp_client_id_fact | length > 0

- name: Link-install plugin into OpenClaw
  ansible.windows.win_shell: !unsafe |
    $env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")
    openclaw plugins install -l C:\openclaw-plugins\azure-devops-workitems
  when: ado_sp_client_id_fact | length > 0

- name: Add plugin to allow list in openclaw.json
  ansible.windows.win_shell: !unsafe |
    $configPath = Join-Path $env:USERPROFILE ".openclaw\openclaw.json"
    $config = Get-Content $configPath -Raw | ConvertFrom-Json
    if (-not $config.plugins) {
      $config | Add-Member -NotePropertyName "plugins" -NotePropertyValue ([PSCustomObject]@{}) -Force
    }
    $existing = @()
    if ($config.plugins.allow) { $existing = @($config.plugins.allow) }
    if ($existing -notcontains "azure-devops-workitems") {
      $existing += "azure-devops-workitems"
    }
    $config.plugins | Add-Member -NotePropertyName "allow" -NotePropertyValue $existing -Force
    $config | ConvertTo-Json -Depth 20 | Set-Content $configPath -Encoding UTF8
    Write-Output "plugins.allow updated"
  when: ado_sp_client_id_fact | length > 0
```

### Makefile — 传入 SP 凭据

```makefile
vm-deploy:
	ansible-playbook deploy-vm.yml \
	  -e vm_name=$(VM_NAME) \
	  -e vm_admin_password='$(VM_PASSWORD)' \
	  -e aoai_api_key='$(AOAI_API_KEY)' \
	  -e ado_sp_client_id='$(ADO_SP_CLIENT_ID)' \
	  -e ado_sp_client_secret='$(ADO_SP_CLIENT_SECRET)' \
	  -e ado_sp_tenant_id='$(ADO_SP_TENANT_ID)'
```

调用示例：

```bash
make vm-deploy VM_NAME=vm-ymms-openclaw-17 \
  VM_PASSWORD='OpenClaw@2026!' \
  AOAI_API_KEY='xxx' \
  ADO_SP_CLIENT_ID='5e088c83-efaa-4978-8a03-3d21b3524f12' \
  ADO_SP_CLIENT_SECRET='lSa8Q~66ke...' \
  ADO_SP_TENANT_ID='f74af430-12a3-4377-b0bb-20cc68a19822'
```

不传 `ADO_SP_CLIENT_ID` 时，所有 ADO 相关任务自动跳过（`when` 条件），不影响现有部署流程。

## 插件源码位置

```
IaC/Azure/plugins/azure-devops-workitems/
├── package.json              # npm 包定义，依赖 @sinclair/typebox
├── openclaw.plugin.json      # OpenClaw 插件清单
└── index.ts                  # 入口：注册 4 个工具 + SP Token 获取逻辑
```

## 安全注意事项

- SP 的 Client Secret 通过 `no_log: true` 防止 Ansible 日志泄露
- 环境变量写入 Machine 级（注册表），不进入文件系统
- Token 缓存仅在进程内存中，不持久化
- SP 权限应遵循最小权限原则，仅授予 Work Items 相关的 scope（`vso.work_full`）
- 不要将 SP Secret 提交到 Git 仓库
