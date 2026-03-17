# OpenClaw Windows VM Image — Packer Project

使用 HashiCorp Packer 构建预装 OpenClaw + 完整开发环境的 Azure Windows Server 2022 镜像。

## 镜像内容

### 核心运行时
| 组件 | 版本 | 说明 |
|------|------|------|
| Windows Server 2022 Datacenter | — | 基础 OS |
| Node.js | 22.16.0+ | OpenClaw 运行时 |
| Python | 3.12.9 | 脚本 / AI 工具链 / Playwright |
| Git | 2.47+ | 版本控制 |
| OpenClaw | 指定版本 | 全局安装，注册为 Windows Service |
| clawhub | latest | 技能注册表 CLI（搜索/安装/发布技能）|
| VS Build Tools 2022 | latest | C++ 工作负载，编译原生 Node.js 模块（sharp、node-pty 等）|

### 浏览器 & 自动化
| 组件 | 来源 | 说明 |
|------|------|------|
| Chromium | Chocolatey | 独立浏览器，支持 headless |
| Playwright Chromium | playwright-core (Node.js) | OpenClaw 浏览器工具直接调用的浏览器二进制 |
| Playwright + Chromium | pip (Python) | Python 浏览器自动化框架 |

### 常用工具 (Chocolatey)
| 工具 | 说明 |
|------|------|
| 7-Zip | 压缩/解压 |
| jq | JSON 处理器 |
| curl (新版) | HTTP 客户端 |
| wget | HTTP 下载器 |
| vim | 文本编辑器 |
| ripgrep (rg) | 快速搜索 |
| fd | 快速文件查找 |
| less | 分页器 |
| bat | 带语法高亮的 cat |
| FFmpeg | 音视频处理（Discord 语音消息、媒体管道）|
| PowerShell 7 (pwsh) | 现代 PowerShell，exec 工具优先使用 |
| Sysinternals | Process Explorer、ProcMon 等 |

### Python 预装包
| 包 | 说明 |
|------|------|
| virtualenv | 虚拟环境管理 |
| requests | HTTP 库 |
| playwright | 浏览器自动化 |

### PowerShell 模块
| 模块 | 说明 |
|------|------|
| PSReadLine | 增强命令行编辑 |
| Terminal-Icons | 终端文件图标 |

### 系统服务
| 服务 | 说明 |
|------|------|
| OpenSSH Server | Ansible 远程管理 |
| OpenClaw Gateway (NSSM) | 手动启动，部署后由 Ansible 切换为 Scheduled Task |

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
packer build \
  -var 'openclaw_version=2026.3.12' \
  -var 'node_version=22.16.0' \
  -var 'python_version=3.12.9' \
  .
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
:: 核心组件
openclaw --version
clawhub --version
node -v
python --version
git --version
ffmpeg -version
pwsh --version

:: VS Build Tools
"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath

:: 浏览器（OpenClaw 使用的 playwright-core Chromium）
node -e "const pw = require('playwright-core'); console.log(pw.chromium.executablePath())"

:: 常用工具
jq --version
rg --version
7z --help | findstr "7-Zip"
```

## 构建流程

Packer 构建按以下顺序执行：

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

## 文件结构

```
image/
├── openclaw-windows.pkr.hcl          # Packer 主模板
├── variables.auto.pkrvars.hcl        # 变量值（订阅 ID 等）
├── README.md                         # 本文件
└── scripts/
    ├── install-node.ps1              # 安装 Node.js
    ├── install-git.ps1               # 安装 Git + Chocolatey
    ├── install-python.ps1            # 安装 Python + pip + Playwright
    ├── install-common-tools.ps1      # 安装常用工具 (7zip, jq, rg, bat, FFmpeg, pwsh, Chromium, etc.)
    ├── install-vsbuildtools.ps1      # 安装 VS Build Tools 2022 (C++ 工作负载)
    ├── install-openclaw.ps1          # 安装 OpenClaw + Playwright Chromium + clawhub
    ├── register-openclaw-service.ps1 # 注册 Windows Service
    └── configure-ssh.ps1             # 配置 OpenSSH Server
```

## Packer 变量

| 变量 | 默认值 | 说明 |
|------|--------|------|
| `subscription_id` | (必填) | Azure 订阅 ID |
| `location` | `westus3` | Azure 区域 |
| `vm_size` | `Standard_D2s_v5` | 构建 VM 规格 |
| `node_version` | `22.16.0` | Node.js 版本 |
| `python_version` | `3.12.9` | Python 版本 |
| `openclaw_version` | `2026.3.12` | OpenClaw 版本 |
| `image_resource_group` | `rg-openclaw-images` | 镜像输出资源组 |
| `build_resource_group` | (可选) | 构建用资源组（避免创建临时 RG） |
| `build_key_vault_name` | (可选) | 构建用 Key Vault |

## 注意事项

- 服务注册为 **手动启动** (`SERVICE_DEMAND_START`)，不会在首次开机时自动运行
- 部署后 Ansible 会将 NSSM 服务禁用，改用 Scheduled Task (Interactive + AtLogon) 以支持浏览器
- 首次使用必须先初始化配置（`openclaw onboard` 或 `openclaw config set gateway.mode local`）
- 服务日志：`C:\openclaw-gateway-stdout.log` / `C:\openclaw-gateway-stderr.log`
- 使用 NSSM 管理服务，支持日志轮转（10MB）
- Playwright Chromium 浏览器路径（OpenClaw 直接调用）：运行 `node -e "const pw = require('playwright-core'); console.log(pw.chromium.executablePath())"` 查看
- Chromium 独立路径：`C:\Program Files\Chromium\Application\chrome.exe`
- Python Playwright 浏览器路径：运行 `python -c "from playwright._impl._driver import compute_driver_executable; print(compute_driver_executable())"` 查看
- 镜像大小约 12-15 GB（含所有工具、VS Build Tools 和浏览器二进制）
- 构建耗时约 30-45 分钟（VS Build Tools 下载较大，取决于网络速度和 Chocolatey CDN）
