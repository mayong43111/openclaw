# OpenClaw Admin Console

OpenClaw VM 管理控制台 — .NET 8 Razor Pages + Minimal API，部署为 Azure App Service。

## 功能

| 功能 | 说明 |
|------|------|
| VM Dashboard | 状态卡片（Total / Ready / Creating / Failed）+ VM 列表 |
| 创建 VM | 自动分配名称 + 生成密码 → 写 Table（creating）→ 入 Queue |
| 删除 VM | 更新 Table（deleting）→ 入 Queue → Worker 异步清理 |
| 重启 VM | 更新 Table（restarting）→ 入 Queue → Worker 异步执行 |
| VM 详情 | IP / Token / URL / Error / 创建时间 |
| REST API | `GET /api/vm` 列表、`GET /api/vm/{name}` 详情 |

## 技术栈

- **.NET 8** — ASP.NET Core Razor Pages + Minimal API
- **Azure.Data.Tables** — Table Storage CRUD（VM 状态记录）
- **Azure.Storage.Queues** — Queue 发送（创建/删除/重启任务）
- **Azure.Identity** — `DefaultAzureCredential`（App Service 上用 Managed Identity，本地用 `az login`）
- **认证** — 不在代码中处理，全部交给 App Service 的 AAD Authentication

## 项目结构

```
console/
├── OpenClaw.Console.csproj      # 项目文件（net8.0 + Azure SDK）
├── Program.cs                   # DI 注册 + Razor Pages + Minimal API
├── appsettings.json             # Storage Account / Queue / Table 配置
├── Models/
│   ├── VmRecord.cs              # ITableEntity — Table Storage 实体
│   └── VmTaskMessage.cs         # Queue 消息（action + vmName）
├── Services/
│   ├── VmTableService.cs        # Table Storage CRUD
│   └── VmQueueService.cs        # Queue 发送（create / delete / restart）
├── Pages/
│   ├── Index.cshtml(.cs)        # VM Dashboard（列表 + 创建/删除/重启操作）
│   ├── VmDetail.cshtml(.cs)     # VM 详情页
│   ├── Error.cshtml(.cs)        # 错误页
│   └── Shared/_Layout.cshtml    # 全局布局（Bootstrap dark navbar）
└── wwwroot/                     # 静态资源（Bootstrap）
```

## 配置

`appsettings.json`：

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `Storage:AccountName` | `stymmsopenclaw` | Azure Storage Account 名称 |
| `Storage:TableName` | `vmrecords` | Table Storage 表名 |
| `Storage:QueueName` | `vm-tasks` | Queue 名称 |
| `Vm:Prefix` | `ymms` | VM 名称前缀（`vm-{prefix}-openclaw-{nn}`） |

App Service 上通过 **App Settings**（环境变量）覆盖，格式：`Storage__AccountName`。

## 本地开发

```bash
# 前置：az login（获取 DefaultAzureCredential）
az login

# 确保本地用户有 Storage 数据面 RBAC（Table + Queue）
az role assignment create \
  --assignee <your-object-id> \
  --role "Storage Table Data Contributor" \
  --scope /subscriptions/.../storageAccounts/stymmsopenclaw

az role assignment create \
  --assignee <your-object-id> \
  --role "Storage Queue Data Contributor" \
  --scope /subscriptions/.../storageAccounts/stymmsopenclaw

# 运行
cd IaC/Azure/console
dotnet run

# 访问 http://localhost:5000
```

## 部署到 App Service

```bash
cd IaC/Azure/console
dotnet publish -c Release -o ./publish

# 打包 zip
cd publish && zip -r ../console.zip . && cd ..

# 部署
az webapp deploy \
  --resource-group rg-ymms-openclaw-infra \
  --name app-ymms-console \
  --src-path console.zip \
  --type zip
```

App Service 需要：
1. **System-Assigned Managed Identity** 已启用（Terraform 已配置）
2. RBAC：`Storage Queue Data Contributor` + `Storage Table Data Contributor`（Terraform 已配置）
3. App Settings：`Storage__AccountName=stymmsopenclaw`（Terraform 已通过 `app_settings` 配置）

## REST API

```bash
# 列出所有 VM
curl https://app-ymms-console.azurewebsites.net/api/vm

# 查看单个 VM
curl https://app-ymms-console.azurewebsites.net/api/vm/vm-ymms-openclaw-01
```

响应示例：

```json
{
  "name": "vm-ymms-openclaw-01",
  "status": "ready",
  "vmIp": "10.0.1.4",
  "url": "https://vm-ymms-openclaw-01.20.38.7.158.nip.io",
  "token": "c8313ba9001bbf47...",
  "createdAt": "2026-03-16T10:00:00Z",
  "error": null
}
```

## 认证

代码中**不包含任何认证逻辑**。生产环境通过 App Service Authentication（Easy Auth）配置 AAD：

```bash
az webapp auth update \
  --resource-group rg-ymms-openclaw-infra \
  --name app-ymms-console \
  --enabled true \
  --action LoginWithAzureActiveDirectory
```

所有未认证请求自动重定向到 AAD 登录页面。
