# Azure Windows VM + OpenClaw 部署计划

## 概览

在当前 Azure 订阅中创建一台 Windows VM 并安装 OpenClaw 2026.3.12。提供两种方式：

- **方式一：Packer 镜像（推荐）** — 使用 Packer 构建预装所有软件的 VM 镜像，从镜像创建 VM 后只需初始化配置即可使用
- **方式二：手动部署** — 创建空白 VM，逐步安装软件（见下方详细步骤）

> **参考来源**: [雨哥向前冲 @xiangxiang103 的 Windows 原生安装实战指南](https://x.com/xiangxiang103/status/2030445959474503856)

---

## 方式一：Packer 镜像部署（推荐）

Packer 项目位于 `packer/` 目录，详见 [packer/README.md](../packer/README.md)。

### 快速上手

```bash
# 1. 创建存放镜像的资源组
az group create --name rg-openclaw-images --location westus3

# 2. 构建镜像（约 15-20 分钟）
cd packer
packer init .
packer build .

# 3. 从镜像创建 VM
az group create --name rg-openclaw --location westus3
az vm create \
  --resource-group rg-openclaw \
  --name vm-openclaw \
  --image rg-openclaw-images/openclaw-windows-2026.3.12 \
  --size Standard_D2s_v5 \
  --admin-username azureuser \
  --admin-password '<密码>' \
  --public-ip-sku Standard

# 4. 开通 SSH
az vm extension set \
  --resource-group rg-openclaw --vm-name vm-openclaw \
  --name WindowsOpenSSH --publisher Microsoft.Azure.OpenSSH --version 3.0
az vm open-port --resource-group rg-openclaw --name vm-openclaw --port 22 --priority 1020

# 5. SSH 登录后初始化配置
ssh azureuser@<公网IP>
# 在 VM 上执行：
openclaw config set gateway.mode local
sc config OpenClawGateway start= auto
net start OpenClawGateway
```

### 镜像预装内容

- Windows Server 2022 Datacenter
- Node.js 22.16.0、Git 2.47.1、OpenClaw 2026.3.12
- OpenClaw Gateway 已注册为 Windows Service（手动启动，需先配置）

---

## 方式二：手动部署（详细步骤）

---

## ⚠️ Windows 避坑要点（写在前面）

1. **使用 cmd，不要用 PowerShell** — PowerShell 的脚本执行策略极易引发 npm 安装报错，全程使用管理员权限的经典 cmd 窗口。
2. **必须提前安装 Git** — OpenClaw 安装包强依赖 Git 客户端，不提前装好会安装失败。
3. **网络畅通** — 确保 VM 能无障碍访问 GitHub、npm 镜像源。
4. **关键参数 `--legacy-peer-deps`** — Windows 下直接 `npm install -g openclaw@latest` 大概率因依赖树冲突报错，必须追加此参数。
5. **Node.js 版本 >= 22.16.0** — OpenClaw 2026.3.12 要求 Node >= 22.16.0，22.14.0 等旧版无法启动网关。
6. **Windows Server 2022 无 winget** — Server 版本默认不带 winget，需用 MSI/exe 静默安装。
7. **网关需要先初始化配置** — 首次运行网关前必须执行 `openclaw config set gateway.mode local`，否则报 "Missing config"。
8. **Control UI 需要安全上下文** — 浏览器要求 HTTPS 或 localhost 才能使用 Control UI，公网 IP 直接访问会报 "requires device identity"，解决方案是用 SSH 隧道。

---

## 步骤

### 1. 创建资源组

```bash
az group create --name rg-openclaw --location westus3
```

### 2. 创建 Windows VM

```bash
az vm create \
  --resource-group rg-openclaw \
  --name vm-openclaw \
  --image Win2022Datacenter \
  --size Standard_D2s_v5 \
  --admin-username azureuser \
  --admin-password '<强密码,16+字符>' \
  --public-ip-sku Standard
```

- 镜像：Windows Server 2022 Datacenter
- 大小：Standard_D2s_v5 (2 vCPU / 8GB RAM)
- 公网 IP：Standard SKU

### 3. 开通远程命令行访问 (OpenSSH)

```bash
# 安装 OpenSSH 扩展
az vm extension set \
  --resource-group rg-openclaw --vm-name vm-openclaw \
  --name WindowsOpenSSH \
  --publisher Microsoft.Azure.OpenSSH \
  --version 3.0

# 开放 SSH 端口
az vm open-port --resource-group rg-openclaw --name vm-openclaw --port 22 --priority 1020
```

### 4. 安装 Node.js + Git（底层依赖）

#### 方案 A：交互式安装（SSH 登录后在 cmd 中执行）

```cmd
:: 安装 Node.js
winget install OpenJS.NodeJS
:: 关闭并重新打开 cmd，验证：
node -v
npm -v

:: 安装 Git
winget install --id Git.Git -e
:: 关闭并重新打开 cmd，验证：
git --version
```

> **重要**：每次 winget 安装完成后，必须关闭当前 cmd 窗口并重新打开，否则 PATH 不会生效。

#### 方案 B：远程静默安装（推荐，适用于 Server 2022 无 winget 场景）

> **实测发现**：Windows Server 2022 默认不带 winget，winget 安装命令会静默失败无输出。必须用 MSI/exe 直接下载安装。

```bash
az vm run-command invoke \
  --resource-group rg-openclaw --name vm-openclaw \
  --command-id RunPowerShellScript \
  --scripts "
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12;
    Write-Output '=== Installing Node.js 22.16.0 ===';
    Invoke-WebRequest -Uri 'https://nodejs.org/dist/v22.16.0/node-v22.16.0-x64.msi' -OutFile C:\node.msi;
    Start-Process msiexec.exe -ArgumentList '/i C:\node.msi /quiet /norestart' -Wait;
    Write-Output '=== Installing Git ===';
    Invoke-WebRequest -Uri 'https://github.com/git-for-windows/git/releases/download/v2.47.1.windows.2/Git-2.47.1.2-64-bit.exe' -OutFile C:\git-installer.exe;
    Start-Process C:\git-installer.exe -ArgumentList '/VERYSILENT /NORESTART' -Wait;
    \$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine');
    & 'C:\Program Files\nodejs\node.exe' -v;
    & 'C:\Program Files\nodejs\npm.cmd' -v;
    & 'C:\Program Files\Git\bin\git.exe' --version;
  "
```

> **关键**：Node.js 必须安装 **22.16.0 或更高版本**。实测 22.14.0 会启动报错 `openclaw requires Node >=22.16.0`。

### 5. 安装 OpenClaw 2026.3.12

```cmd
npm install -g openclaw@2026.3.12 --legacy-peer-deps
```

验证：
```cmd
openclaw --version
:: 期望输出: 2026.3.12
```

### 6. 初始化配置

首次启动网关前，**必须设置 gateway mode**，否则会报 `Missing config. Run openclaw setup or set gateway.mode=local`：

```cmd
openclaw config set gateway.mode local
```

如果要使用交互式向导完成完整配置：

```cmd
openclaw onboard
```

向导推荐选项：
- 安全确认：**Yes**
- 模式：**QuickStart**
- 模型渠道：按需选择（如 OpenAI）
- 通信渠道：按需选择（如 Telegram Bot API → 填入 Bot Token）
- Skills / Hooks：先选 **No / Skip for now**，后续再配置

### 7. 启动网关

网关的 `--bind` 参数**不接受 IP 地址**（如 `0.0.0.0`），只接受以下预设值：

| 值 | 说明 |
|---|---|
| `loopback` | 仅本机访问 127.0.0.1（最安全，推荐） |
| `lan` | 局域网可访问 0.0.0.0 |
| `tailnet` | Tailscale 网络 |
| `auto` | 自动选择 |
| `custom` | 自定义 |

**推荐使用 loopback + SSH 隧道的方式**（原因见下方"远程访问 Control UI"）：

```cmd
openclaw gateway run --bind loopback --port 18789 --force
```

> 如果选择 `--bind lan`，还需要额外配置：
> - 设置 `gateway.controlUi.allowedOrigins` 或 `gateway.controlUi.dangerouslyAllowHostHeaderOriginFallback=true`
> - 否则报错：`non-loopback Control UI requires gateway.controlUi.allowedOrigins`

### 8. 远程访问 Control UI（SSH 隧道）

> **实测问题**：直接用公网 IP 访问 `http://<IP>:18789` 时，浏览器报错 **"Control UI requires device identity (use HTTPS or localhost secure context)"**。这是因为浏览器安全策略要求 HTTPS 或 localhost 才能使用某些 Web API。

**解决方案：SSH 端口转发**（无需证书，最简单安全）：

```bash
# 从本地机器执行（-N 不执行远程命令，-f 后台运行）
ssh -L 18789:127.0.0.1:18789 -N -f azureuser@<公网IP>
```

然后浏览器打开 **http://localhost:18789** 即可正常访问。

- 本地 `localhost:18789` → SSH 隧道 → VM 上 `127.0.0.1:18789`
- 浏览器看到的是 `localhost`，满足安全上下文要求
- 网关绑定在 loopback，不暴露到公网

> **SSH 隧道断线恢复**：如果隧道意外断开（Ctrl+C 或网络中断），浏览器会无法访问。重新执行上述 SSH 命令即可恢复。如果本地端口被占用，先清理：`pkill -f "ssh.*18789"` 后重连。

### 8.1 Gateway Token 认证

首次打开 Control UI 时会提示 **"unauthorized: gateway token missing"**，需要输入 token 才能连接。

**获取 token 的方法**：

> `openclaw config get gateway.auth.token` 会返回 `__OPENCLAW_REDACTED__`（被脱敏），需要直接从配置文件读取：

```bash
# 通过 az vm run-command 从配置 JSON 中读取 token
az vm run-command invoke \
  --resource-group rg-openclaw --name vm-openclaw \
  --command-id RunPowerShellScript \
  --scripts "
    \$config = Get-Content 'C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json' -Raw | ConvertFrom-Json;
    Write-Output \$config.gateway.auth.token;
  " --query "value[0].message" -o tsv
```

将获取到的 token 粘贴到 Control UI 的 Settings 页面中。

> **注意**：通过 `az vm run-command` 运行的进程以 SYSTEM 账户执行，配置文件路径是 `C:\Windows\system32\config\systemprofile\.openclaw\`，而非用户目录 `C:\Users\azureuser\.openclaw\`。

### 9. 进阶配置（可选）

```cmd
openclaw config set tools.profile full
openclaw config validate
openclaw doctor
openclaw gateway restart
```

### 10. 网关保活

> **不要关闭网关的命令行窗口！** 它是 OpenClaw 持续运行的核心进程。

建议创建一个快捷启动批处理文件 `start-openclaw.bat`：

```bat
@echo off
title OpenClaw Gateway
set PATH=%PATH%;%APPDATA%\npm
openclaw gateway run --bind loopback --port 18789 --force
pause
```

以后双击即可启动网关。

### 11. 远程验证

```bash
ssh azureuser@<公网IP> "openclaw --version"
# 期望输出: 2026.3.12
```

---

## 安全建议

- NSG 限制源 IP（仅允许自己的出口 IP 访问 22 端口）
- 使用强密码（16+ 字符，混合大小写 + 数字 + 符号）
- 完成后考虑关闭不需要的端口（如果用 SSH 隧道，可以关闭 18789 端口的 NSG 规则）
- OpenClaw 权限较高，**务必备份重要数据**
- 网关绑定优先选择 `loopback`，通过 SSH 隧道访问
- 避免使用 `dangerouslyAllowHostHeaderOriginFallback=true`，此配置削弱了 origin 校验

---

## 🔧 实际部署踩坑记录

| # | 问题 | 现象 | 解决方案 |
|---|---|---|---|
| 1 | **winget 不可用** | Windows Server 2022 默认不带 winget，命令静默无输出 | 改用 MSI/exe 直接下载静默安装 |
| 2 | **Node.js 版本过低** | 安装了 22.14.0 后启动网关报错 `requires Node >=22.16.0` | 下载安装 Node.js 22.16.0+ |
| 3 | **`--bind 0.0.0.0` 无效** | 报错 `Invalid --bind (use "loopback", "lan", "tailnet", "auto", or "custom")` | 使用预设值如 `--bind loopback` 或 `--bind lan` |
| 4 | **网关缺少初始配置** | 报错 `Missing config. Run openclaw setup or set gateway.mode=local` | 先执行 `openclaw config set gateway.mode local` |
| 5 | **非 loopback 绑定被拒** | 报错 `non-loopback Control UI requires gateway.controlUi.allowedOrigins` | 配置 `allowedOrigins` 或使用 loopback + SSH 隧道 |
| 6 | **Control UI 设备身份错误** | 浏览器报 `requires device identity (use HTTPS or localhost secure context)` | 使用 SSH 端口转发 `ssh -L 18789:127.0.0.1:18789`，通过 localhost 访问 |
| 7 | **Gateway token 被脱敏** | `openclaw config get gateway.auth.token` 返回 `__OPENCLAW_REDACTED__` | 直接用 PowerShell 读取 JSON 配置文件获取明文 token |
| 8 | **SYSTEM 用户路径不同** | `az vm run-command` 以 SYSTEM 账户执行，配置路径为 `C:\Windows\system32\config\systemprofile\.openclaw\`，npm 全局安装路径为 `...\AppData\Roaming\npm` | 启动网关时需手动设置 PATH 包含 SYSTEM 用户的 npm 路径 |
| 9 | **`az vm run-command` 并发冲突** | 同时执行多条 run-command 报错 `Run command extension execution is in progress` | 等待上一条命令完成后再执行下一条，或 `sleep 30` 后重试 |
| 10 | **SSH 隧道断线** | Ctrl+C 或网络中断后 `localhost:18789` 无法访问 | 重新执行 `ssh -L 18789:127.0.0.1:18789 -N -f azureuser@<IP>`，端口占用时先 `pkill -f "ssh.*18789"` |

---

## 📋 实际部署信息（本次）

| 项目 | 值 |
|---|---|
| 资源组 | `rg-openclaw` |
| VM 名称 | `vm-openclaw` |
| 区域 | West US 3 |
| 公网 IP | `20.172.98.86` |
| 管理员 | `azureuser` |
| Node.js | v22.16.0 |
| Git | 2.47.1 |
| OpenClaw | 2026.3.12 (6472949) |
| 网关端口 | 18789 |
| 网关配置路径 | `C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json` |
