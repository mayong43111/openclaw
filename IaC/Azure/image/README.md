# OpenClaw Windows VM Image — Packer Project

使用 HashiCorp Packer 构建预装 OpenClaw 的 Azure Windows Server 2022 镜像。

## 镜像内容

- Windows Server 2022 Datacenter
- Node.js 22.16.0+
- Git 2.47.1
- OpenClaw (指定版本，全局安装)
- OpenClaw Gateway 注册为 Windows Service（手动启动，未初始化配置）

## 前置要求

- [Packer](https://www.packer.io/downloads) v1.9+
- Azure CLI 已登录 (`az login`)
- 镜像资源组已创建

## 使用方法

### 1. 创建镜像资源组

```bash
az group create --name rg-openclaw-images --location westus3
```

### 2. 构建镜像

```bash
cd packer
packer init .
packer build .
```

自定义版本：

```bash
packer build -var 'openclaw_version=2026.3.12' -var 'node_version=22.16.0' .
```

### 3. 从镜像创建 VM

```bash
az vm create \
  --resource-group rg-openclaw \
  --name vm-openclaw \
  --image rg-openclaw-images/openclaw-windows-2026.3.12 \
  --size Standard_D2s_v5 \
  --admin-username azureuser \
  --admin-password '<密码>' \
  --public-ip-sku Standard
```

### 4. 首次启动配置

SSH 登录后：

```cmd
:: 方式一：交互式向导
openclaw onboard

:: 方式二：最小配置
openclaw config set gateway.mode local

:: 启用服务自动启动
sc config OpenClawGateway start= auto

:: 启动服务
net start OpenClawGateway
```

### 5. 验证

```cmd
sc query OpenClawGateway
openclaw --version
```

## 文件结构

```
packer/
├── openclaw-windows.pkr.hcl          # Packer 主模板
├── variables.auto.pkrvars.hcl        # 变量值（订阅 ID 等）
├── README.md                         # 本文件
└── scripts/
    ├── install-node.ps1              # 安装 Node.js
    ├── install-git.ps1               # 安装 Git
    ├── install-openclaw.ps1          # 安装 OpenClaw
    └── register-openclaw-service.ps1 # 注册 Windows Service
```

## 注意事项

- 服务注册为 **手动启动** (`SERVICE_DEMAND_START`)，不会在首次开机时自动运行
- 首次使用必须先初始化配置（`openclaw onboard` 或 `openclaw config set gateway.mode local`）
- 配置完成后可改为自动启动：`sc config OpenClawGateway start= auto`
- 服务日志：`C:\openclaw-gateway-stdout.log` / `C:\openclaw-gateway-stderr.log`
- 使用 NSSM 管理服务，支持日志轮转（10MB）
