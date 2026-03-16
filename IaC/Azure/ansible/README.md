# OpenClaw Windows VM Deployment Guide

使用 Packer 预构建镜像部署 OpenClaw Gateway 到 Azure Windows VM。

## 镜像信息

| 属性 | 值 |
|---|---|
| Image Name | `openclaw-windows-2026.3.12` |
| Resource Group | `rg-openclaw-images` |
| Location | `westus3` |
| OS | Windows Server 2022 Datacenter |
| VM Size | `Standard_D2s_v5` (推荐) |

### 镜像预装内容

- Node.js 22.16.0
- Git 2.53.0 (via Chocolatey)
- OpenClaw 2026.3.12 (安装在 `C:\openclaw`，已加入系统 PATH)
- NSSM (Windows Service 管理器)
- OpenClawGateway Windows Service (已注册，手动启动模式)

> **注意**: 镜像不包含 CA 证书或 TLS 证书。证书应在部署后通过 Ansible 或手动配置。WinRM 使用 Azure 默认自签名证书。

### 镜像中的关键路径

| 路径 | 说明 |
|---|---|
| `C:\openclaw\` | OpenClaw 安装目录 (npm global prefix) |
| `C:\openclaw-gateway.cmd` | Gateway 启动脚本 |

---

## 快速部署

### 1. 从镜像创建 VM

```bash
az vm create \
  --resource-group <RESOURCE_GROUP> \
  --name <VM_NAME> \
  --image openclaw-windows-2026.3.12 \
  --size Standard_D2s_v5 \
  --admin-username azureuser \
  --admin-password '<PASSWORD>' \
  --location westus3 \
  --nsg-rule RDP \
  --public-ip-sku Standard
```

> **注意**: Windows 计算机名不能超过 15 个字符。

### 2. 开放 Gateway 端口 (NSG)

```bash
az network nsg rule create \
  --resource-group <RESOURCE_GROUP> \
  --nsg-name <VM_NAME>NSG \
  --name AllowOpenClaw \
  --priority 1010 \
  --direction Inbound \
  --access Allow \
  --protocol Tcp \
  --destination-port-ranges 18789
```

### 3. 初始化配置并启动服务

通过 `az vm run-command` 或 Ansible 在 VM 上执行:

```powershell
# 刷新 PATH
$env:Path = [System.Environment]::GetEnvironmentVariable("Path", "Machine")

# 初始化 gateway 配置
openclaw config set gateway.mode local

# 启用远程访问 (绑定到 LAN)
# 修改 launcher 脚本
$launcher = @"
@echo off
set PATH=C:\Program Files\nodejs;C:\openclaw;C:\Program Files\Git\bin;%PATH%
openclaw gateway run --bind lan --port 18789
"@
Set-Content "C:\openclaw-gateway.cmd" $launcher -Encoding ASCII

# Control UI 允许远程访问 (二选一)
# 方式 A: 允许 Host Header 回退 (快速但安全性较低)
openclaw config set gateway.controlUi.dangerouslyAllowHostHeaderOriginFallback true

# 方式 B: 指定允许的 Origin (推荐生产环境)
# openclaw config set gateway.controlUi.allowedOrigins "https://your-domain.com"

# 设置服务自动启动并启动
sc.exe config OpenClawGateway start= auto
Start-Service OpenClawGateway
```

### 4. 启用 HTTPS (TLS)

部署后需要生成 TLS 证书。可以使用自签名证书或 CA 签名证书:

#### 方式 A: 自签名证书 (快速)

```powershell
$tlsDir = "C:\openclaw-tls"
New-Item -Path $tlsDir -ItemType Directory -Force

# 生成自签名证书
$vmIP = "<ACTUAL_VM_IP>"
$cert = New-SelfSignedCertificate `
    -Subject "CN=OpenClaw Gateway" `
    -DnsName "localhost", $vmIP `
    -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5) `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1","2.5.29.17={text}IPAddress=$vmIP&DNS=localhost")

# 导出 PEM
$openssl = "C:\Program Files\Git\usr\bin\openssl.exe"
$certPass = ConvertTo-SecureString -String "openclaw-tls" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath "$tlsDir\openclaw.pfx" -Password $certPass
& $openssl pkcs12 -in "$tlsDir\openclaw.pfx" -clcerts -nokeys -out "$tlsDir\cert.pem" -passin pass:openclaw-tls
& $openssl pkcs12 -in "$tlsDir\openclaw.pfx" -nocerts -nodes -out "$tlsDir\key.pem" -passin pass:openclaw-tls

# 配置 TLS
openclaw config set gateway.tls.enabled true
openclaw config set gateway.tls.certPath "C:\openclaw-tls\cert.pem"
openclaw config set gateway.tls.keyPath "C:\openclaw-tls\key.pem"
openclaw config set gateway.tls.autoGenerate false

# 重启服务
Restart-Service OpenClawGateway
```

#### 方式 B: CA 签名证书 (推荐生产环境)

由 Ansible playbook 生成 CA 和 TLS 证书，参考 Ansible 配置管理部分。

### 5. 获取 Gateway Token

服务首次启动时会自动生成 token:

```powershell
$configFile = "C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json"
$cfg = Get-Content $configFile -Raw | ConvertFrom-Json
$cfg.gateway.auth.token
```

> **说明**: 服务以 SYSTEM 账户运行，配置文件位于 `C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json`。

---

## 为实际 IP 重新生成 TLS 证书

如果已有 CA 证书，可以用 CA 签发包含实际 IP 的 TLS 证书:

```powershell
$caDir = "C:\openclaw-ca"
$tlsDir = "C:\openclaw-tls"
$vmIP = "<ACTUAL_VM_IP>"

# 导入 CA (需要 CA PFX 文件，由 Ansible 部署到 VM)
$caPassword = ConvertTo-SecureString -String "openclaw-ca-internal" -Force -AsPlainText
$caCert = Import-PfxCertificate -FilePath "$caDir\openclaw-ca.pfx" -CertStoreLocation "Cert:\LocalMachine\My" -Password $caPassword

# 生成新证书 (包含实际 IP)
$newCert = New-SelfSignedCertificate `
    -Subject "CN=OpenClaw Gateway" `
    -DnsName "localhost", $vmIP `
    -KeyLength 2048 -KeyAlgorithm RSA -HashAlgorithm SHA256 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(5) `
    -CertStoreLocation "Cert:\LocalMachine\My" `
    -Signer $caCert `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.1","2.5.29.17={text}IPAddress=$vmIP&DNS=localhost")

# 导出 PEM
$openssl = "C:\Program Files\Git\usr\bin\openssl.exe"
$certPass = ConvertTo-SecureString -String "openclaw-tls" -Force -AsPlainText
Export-PfxCertificate -Cert $newCert -FilePath "$tlsDir\openclaw.pfx" -Password $certPass
& $openssl pkcs12 -in "$tlsDir\openclaw.pfx" -clcerts -nokeys -out "$tlsDir\cert.pem" -passin pass:openclaw-tls
& $openssl pkcs12 -in "$tlsDir\openclaw.pfx" -nocerts -nodes -out "$tlsDir\key.pem" -passin pass:openclaw-tls

# 清理 CA 私钥
Remove-Item "Cert:\LocalMachine\My\$($caCert.Thumbprint)" -ErrorAction SilentlyContinue

# 重启
Restart-Service OpenClawGateway
```

---

## WinRM (Ansible 管理)

### 默认行为

VM 使用 Azure 默认的 WinRM 自签名 HTTPS 监听器 (端口 5986)。无需额外配置即可通过 Ansible 连接。

### Ansible 连接配置

```yaml
# inventory
[openclaw_windows]
20.14.23.70

[openclaw_windows:vars]
ansible_user=azureuser
ansible_password=<PASSWORD>
ansible_connection=winrm
ansible_winrm_transport=basic
ansible_winrm_server_cert_validation=ignore
ansible_port=5986
ansible_winrm_scheme=https
```

### 开放 WinRM 端口 (NSG)

```bash
az network nsg rule create \
  --resource-group <RESOURCE_GROUP> \
  --nsg-name <VM_NAME>NSG \
  --name AllowWinRM \
  --priority 1020 \
  --direction Inbound \
  --access Allow \
  --protocol Tcp \
  --destination-port-ranges 5986
```

---

## 已知问题

| 问题 | 原因 | 解决方案 |
|---|---|---|
| `--force` 参数导致 gateway 崩溃 | Windows 无 `fuser`/`lsof` | launcher 脚本中不使用 `--force` |
| npm 安装在用户目录导致 Sysprep 后丢失 | Sysprep 删除用户配置文件 | npm prefix 设为 `C:\openclaw` (系统级) |
| Em dash 字符导致 PS 脚本解析失败 | WinRM 传输编码问题 | 脚本中只使用 ASCII 字符 |
| Control UI 安全上下文要求 | 非 loopback 绑定需要 origin 配置 | 设置 `allowedOrigins` 或使用回退模式 |

---

## 连接信息 (部署后)

| 协议 | URL |
|---|---|
| HTTPS | `https://<VM_IP>:18789` |
| WSS | `wss://<VM_IP>:18789` |
| WinRM | `https://<VM_IP>:5986` |

Gateway token 从配置文件读取:
`C:\Windows\system32\config\systemprofile\.openclaw\openclaw.json` -> `gateway.auth.token`
