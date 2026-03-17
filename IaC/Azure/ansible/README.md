# OpenClaw Windows VM — Ansible Playbooks

使用 Packer 预构建镜像部署 OpenClaw Gateway 到 Azure Windows VM。  
支持**两种使用模式**：Console 自动化 和 手动 CLI。

---

## 架构概览

```
                                 ┌─ Console 自动化模式 ─────────────────────┐
                                 │  Console UI → Queue → TaskWorkerService │
                                 │  → ansible-playbook deploy-vm.yml       │
                                 │  → 解析 stdout 获取 IP/Token/URL        │
                                 │  → 写入 Table Storage                   │
                                 └─────────────────────────────────────────┘

                                 ┌─ 手动 CLI 模式 ─────────────────────────┐
                                 │  SSH → ansible-playbook deploy-vm.yml   │
                                 │  → Play 3 写入本地文件:                 │
                                 │    vms.yml / hosts.yml / host_vars/     │
                                 │  → configure-vm.yml 可用这些 inventory  │
                                 └─────────────────────────────────────────┘
```

### 路由方式

VM 通过 Private DNS Zone (`openclaw.internal`) 注册，nginx 使用 Azure DNS resolver (`168.63.129.16`) 按主机名路由到对应 VM。新增/删除 VM 无需重建容器。

| 组件 | 说明 |
|---|---|
| DNS Zone | `openclaw.internal` (linked to VNet) |
| A Record | `vm-ymms-openclaw-XX.openclaw.internal` → VM 私有 IP |
| TTL | 10s (快速切换) |
| nginx | `resolver 168.63.129.16`，变量 `proxy_pass` 实现每请求 DNS 解析 |
| nip.io | `*.20-38-7-158.nip.io`（dashed 格式避免 IP 解析歧义） |

---

## 镜像信息

| 属性 | 值 |
|---|---|
| Image Name | `openclaw-windows-2026.3.12` |
| Resource Group | `rg-openclaw-images` |
| Location | `westus3` |
| OS | Windows Server 2022 Datacenter |
| VM Size | `Standard_D2s_v5` |

### 镜像预装内容

- Node.js 22.16.0
- Git 2.53.0 (via Chocolatey)
- OpenClaw 2026.3.12 (`C:\openclaw`，已加入 PATH)
- NSSM (Windows Service 管理器，部署时禁用，改用 Scheduled Task)

### 关键路径

| 路径 | 说明 |
|---|---|
| `C:\openclaw\` | OpenClaw 安装目录 |
| `C:\openclaw-gateway.cmd` | Gateway 启动脚本 (bind lan) |
| `C:\Users\azureuser\.openclaw\openclaw.json` | 用户级配置 (含 token) |

---

## Playbook 文件说明

| 文件 | 用途 | 模式 |
|---|---|---|
| `deploy-vm.yml` | 创建 VM + 配置 Gateway + DNS 注册 + 本地 registry | 两种模式共用 |
| `remove-vm.yml` | 删除 VM + DNS 清理 + 本地 registry 清理 | 两种模式共用 |
| `configure-vm.yml` | 重新配置已有 VM 的 Gateway/AOAI | 仅手动模式 |
| `group_vars/all.yml` | 默认变量（命名、镜像、AOAI 等） | 两种模式共用 |
| `inventory/hosts.yml` | Ansible inventory（由 Play 3 自动生成） | 仅手动模式 |
| `inventory/host_vars/*.yml` | 每 VM 的变量（由 Play 3 自动生成） | 仅手动模式 |
| `vms.yml` | VM 注册表（由 Play 3 自动生成） | 仅手动模式 |

> **Note**: `vms.yml` 和 `inventory/host_vars/` 已加入 `.gitignore`，包含敏感数据不提交。

---

## 模式 A: Console 自动化

通过 Console Web UI 创建/删除 VM，无需 SSH。

**流程**: Console UI → Azure Queue → TaskWorkerService → `ansible-playbook` → 解析 stdout

TaskWorkerService 从 Play 2 的 `Display connection info` 输出中解析 `IP`、`Token`、`URL`，写入 Table Storage。Play 3 写入的本地文件在容器中是临时的，不被读取。

无需手动操作。

---

## 模式 B: 手动 CLI

SSH 到 ansible 控制机后直接运行 playbook。

### 部署新 VM

```bash
ansible-playbook deploy-vm.yml \
  -e vm_name=vm-ymms-openclaw-12 \
  -e vm_admin_password='<PASSWORD>' \
  -e aoai_api_key='<KEY>'
```

deploy-vm.yml 执行三个 Play:
1. **Play 1**: 创建 NIC、VM、获取 IP、注册 DNS A 记录
2. **Play 2**: SSH 配置 Gateway (mode, AOAI, firewall, scheduled task, 重启, 获取 token)
3. **Play 3**: 写入本地文件 (`vms.yml`, `host_vars/`, `hosts.yml`)，供后续 `configure-vm.yml` 使用

### 重新配置已有 VM

```bash
ansible-playbook configure-vm.yml -i inventory/ \
  -e aoai_api_key='<KEY>' \
  -e ansible_password='<PASSWORD>'
```

> `ansible_password` 不存储在 host_vars（安全考虑），必须通过 `-e` 提供。

### 删除 VM

```bash
ansible-playbook remove-vm.yml -e vm_name=vm-ymms-openclaw-12
```

清理顺序: DNS A 记录 → VM + OS disk + data disks + NIC → 本地文件

### 查看已部署 VM

```bash
cat vms.yml
```

---

## TLS 证书 (可选)

当前通过 AppGW 提供 HTTPS 终止（wildcard nip.io 证书），VM 之间走 HTTP。
如需 VM 本地 TLS，可使用自签名证书:

```powershell
$tlsDir = "C:\openclaw-tls"
New-Item -Path $tlsDir -ItemType Directory -Force

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

# 重启 Scheduled Task
Stop-ScheduledTask -TaskName 'OpenClawGateway'
Start-ScheduledTask -TaskName 'OpenClawGateway'
```

---

## SSH 连接 (Ansible → VM)

deploy-vm.yml 使用 SSH (`ansible_connection: ssh`, `ansible_shell_type: powershell`) 连接 VM。
VM 没有公网 IP，Ansible 控制机必须在同一 VNet 或通过 VPN/bastion 连接。

```bash
# 手动测试 SSH
ssh azureuser@10.0.1.X
```

---

## 已知问题

| 问题 | 原因 | 解决方案 |
|---|---|---|
| `--force` 参数导致 gateway 崩溃 | Windows 无 `fuser`/`lsof` | launcher 脚本中不使用 `--force` |
| npm 安装在用户目录导致 Sysprep 后丢失 | Sysprep 删除用户配置文件 | npm prefix 设为 `C:\openclaw` (系统级) |
| Em dash 字符导致 PS 脚本解析失败 | SSH 传输编码问题 | 脚本中只使用 ASCII 字符 |
| Windows 防火墙阻止 18789 | 新 VM 无防火墙规则 | deploy-vm.yml 已自动添加 `New-NetFirewallRule` |
| nip.io IP 解析歧义 | `vm-11.20.38.7.158.nip.io` 解析到 `11.20.38.7` | 使用 dashed 格式 `20-38-7-158.nip.io` |

---

## 连接信息 (部署后)

| 协议 | URL |
|---|---|
| HTTPS (via AppGW) | `https://vm-ymms-openclaw-XX.20-38-7-158.nip.io` |
| HTTP (内网直连) | `http://<VM_PRIVATE_IP>:18789` |
| SSH | `ssh azureuser@<VM_PRIVATE_IP>` |

Gateway token 路径: `C:\Users\azureuser\.openclaw\openclaw.json` → `gateway.auth.token`
