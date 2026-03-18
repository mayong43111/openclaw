# 远程桌面（Guacamole on ACI）

## 架构概述

```
用户浏览器
    ↓ HTTPS
Application Gateway (appgw-ymms-openclaw)
    ↓ HTTP :8080
App Service (app-ymms-console) nginx
    ├─ /api/desktop/token  → ASP.NET (生成加密 JSON auth token)
    └─ /guac/              → proxy_pass ACI
ACI Container Group (aci-ymms-guacamole) @ 10.0.0.4:8080
    ├─ guacamole (Tomcat Web UI + JSON auth extension)
    └─ guacd (RDP proxy daemon, sidecar)
    ↓ RDP :3389
Windows VM (snet-vm)
    └─ console session #1
```

### 关键参数

| 资源 | 值 |
|------|------|
| ACI Container Group | `aci-ymms-guacamole` |
| ACI Subnet | `snet-aci` (10.0.0.0/28, delegated) |
| ACI IP | `10.0.0.4` |
| Guacamole 版本 | 1.5.5 |
| 认证方式 | **JSON auth extension**（动态，无需静态配置文件） |
| JSON Secret Key | App Service 配置 `Guacamole__JsonSecretKey` |
| Console 代理路径 | `/guac/` → `http://10.0.0.4:8080/guacamole/` |
| RDP 安全模式 | `any`（服务器自动协商） |

## 用户操作流程

1. 用户在 **运维页面** 点击 VM 的 **🖥️ 桌面** 按钮
2. 浏览器打开新标签页 `/Desktop?vm=vm-ymms-openclaw-XX`
3. 前端 JS 调用 `GET /api/desktop/token?vm=...`
4. Console 后端从 Table Storage 查询 VM IP，生成加密 JSON auth token
5. 前端将加密数据 POST 到 `/guac/api/tokens` 获取 Guacamole auth token
6. 自动跳转到 Guacamole 远程桌面全屏页面

**优势**：新增 VM 或修改密码无需重新部署 ACI，只需更新 Table Storage 或 App Service 配置。

## 加密算法（Guacamole JSON Auth）

Guacamole JSON auth extension 的加密流程（参考 `CryptoService.java`）：

```
1. signature = HMAC-SHA256(key, json_plaintext)     // 32 bytes
2. combined  = signature || json_plaintext           // 拼接
3. ciphertext = AES-128-CBC(key, combined, iv=0x00)  // NULL IV (全零)
4. result    = base64(ciphertext)                     // 不含 IV
```

**关键要点**：
- IV 是全零 16 字节（Guacamole 用 HMAC 签名作为有效 IV）
- 签名在加密**内部**（先签名拼接，再整体加密）
- 输出不包含 IV，仅 base64 编码密文
- 密钥：128-bit hex string，加密和签名共用同一密钥

实现代码：`console/Services/GuacamoleTokenService.cs`

JSON payload 格式：
```json
{
  "username": "console",
  "expires": "1742400000000",
  "connections": {
    "vm-ymms-openclaw-XX": {
      "protocol": "rdp",
      "parameters": {
        "hostname": "10.0.1.X",
        "port": "3389",
        "username": "azureuser",
        "password": "...",
        "security": "any",
        "ignore-cert": "true",
        "resize-method": "reconnect",
        "color-depth": "24"
      }
    }
  }
}
```

## 网络拓扑

```
VNet: vnet-ymms-openclaw (10.0.0.0/22)
├── snet-aci      10.0.0.0/28   (delegated: Microsoft.ContainerInstance)
├── snet-vm       10.0.1.0/24   (Windows VMs)
├── snet-appgw    10.0.2.0/24   (Application Gateway)
├── snet-worker   10.0.3.0/26   (App Service VNet integration)
└── snet-pe       10.0.3.64/28  (Private Endpoints)
```

App Service 通过 VNet integration（snet-worker）可以访问 ACI（snet-aci）和 VM（snet-vm）。

## 部署

### 1. 创建 ACI 委托子网

```bash
az network vnet subnet create \
  -g rg-ymms-openclaw-infra \
  --vnet-name vnet-ymms-openclaw \
  -n snet-aci \
  --address-prefix 10.0.0.0/28 \
  --delegations Microsoft.ContainerInstance/containerGroups
```

### 2. 生成 JSON auth 密钥

```bash
openssl rand -hex 16
# 例如: c74b61c7a638ccc64b0f6ab673a67134
```

### 3. 部署 ACI Container Group

YAML 模板：`IaC/Azure/aci/aci-guacamole.yaml`

核心配置：
- `JSON_SECRET_KEY` 环境变量触发 Guacamole entrypoint 自动启用 JSON auth extension
- 无需 Secret Volume 或 user-mapping.xml
- guacamole 和 guacd 以 sidecar 形式运行在同一容器组

```bash
az container create -g rg-ymms-openclaw-infra --file IaC/Azure/aci/aci-guacamole.yaml
```

验证：
```bash
# 检查容器状态
az container show -g rg-ymms-openclaw-infra -n aci-ymms-guacamole \
  --query "{state:instanceView.state, ip:ipAddress.ip}" -o json

# 检查 JSON auth extension 已加载
az container logs -g rg-ymms-openclaw-infra -n aci-ymms-guacamole \
  --container-name guacamole | grep json
# 应输出: Extension "Encrypted JSON Authentication" (json) loaded.
```

### 4. 配置 App Service

```bash
az webapp config appsettings set \
  -g rg-ymms-openclaw-infra -n app-ymms-console --settings \
  Guacamole__JsonSecretKey=<hex-key> \
  Vm__RdpUsername=azureuser \
  'Vm__RdpPassword=<password>'
```

### 5. 部署 Console 镜像

```bash
cd IaC/Azure
docker build -f console/Dockerfile -t acrymmsopenclaw.azurecr.io/openclaw-console:latest .
docker push acrymmsopenclaw.azurecr.io/openclaw-console:latest
az webapp restart -g rg-ymms-openclaw-infra -n app-ymms-console
```

## 涉及的文件

| 文件 | 用途 |
|------|------|
| `aci/aci-guacamole.yaml` | ACI 容器组定义（guacamole + guacd） |
| `console/Services/GuacamoleTokenService.cs` | AES-128-CBC + HMAC-SHA256 加密服务 |
| `console/Program.cs` | `/api/desktop/token` API 端点注册 |
| `console/Pages/Desktop.cshtml` | 远程桌面页面（JS 完成 token 交换和跳转） |
| `console/Pages/Desktop.cshtml.cs` | Page model（加载 VM 列表） |
| `console/Pages/Operations.cshtml` | 运维页面（🖥️ 桌面按钮入口） |
| `console/nginx/nginx.conf` | `/guac/` 反向代理 + AAD 认证 |

## 添加新 VM

无需修改 Guacamole 配置或重新部署 ACI。只需：

1. 部署 VM（Ansible deploy-vm.yml）→ 自动写入 Table Storage
2. **确保 RDP 密码一致**：VM 的 azureuser 密码必须与 App Service 配置的 `Vm__RdpPassword` 一致
3. 在运维页面点击桌面按钮即可使用

如果密码不一致，用 Azure CLI 重置：
```bash
az vm user update -g rg-ymms-openclaw-infra -n vm-ymms-openclaw-XX \
  -u azureuser -p '<password>'
```

## 故障排除

### Guacamole 认证失败 (HTTP 403)

**检查 Guacamole 日志：**
```bash
az container logs -g rg-ymms-openclaw-infra -n aci-ymms-guacamole --container-name guacamole
```

| 日志信息 | 原因 | 修复 |
|----------|------|------|
| `Signature of submitted data is incorrect` | 加密算法实现错误 | 确认使用 NULL IV、签名在加密内部 |
| `Submitted data is not proper base64` | base64 编码错误 | 检查加密输出格式 |
| `Extension "Encrypted JSON Authentication" loaded` 未出现 | JSON auth 未启用 | 检查 `JSON_SECRET_KEY` 环境变量 |

### RDP 连接立即断开

**检查 guacd 日志：**
```bash
az container logs -g rg-ymms-openclaw-infra -n aci-ymms-guacamole --container-name guacd
```

| 日志信息 | 原因 | 修复 |
|----------|------|------|
| `Authentication failure (invalid credentials?)` | VM RDP 密码与配置不匹配 | `az vm user update` 重置密码 |
| `Connection refused` | VM RDP 未启用或端口未开放 | 检查 VM 的 Windows 远程桌面设置 |

### 请求挂起 / 超时

- 检查 nginx `proxy_pass` 中的 ACI IP 是否正确（ACI 重新部署后 IP 可能变更）
- 确认 VNet peering 已同步：`az network vnet peering sync`

## 注意事项

- **ACI IP 可能变更**：每次 `az container create` 重新部署 ACI，IP 地址可能改变。更新后需同步修改 `console/nginx/nginx.conf` 中的 `proxy_pass` 地址，重新构建部署 Console。
- **console=true 已移除**：当前不使用 `console=true` 参数，避免接管唯一桌面会话导致冲突。
- **security=any**：RDP 安全模式设为 `any`（而非 `nla`），因为部分 VM 的 NLA/CredSSP 协商会失败导致 `Authentication failure`。
- **Token 有效期**：JSON auth token 有效期 5 分钟（`expires` 字段），超时需重新打开桌面页面。
- **AAD 认证保护**：`/guac/` 路径通过 nginx 检查 `X-MS-CLIENT-PRINCIPAL` header 实现 AAD 登录保护，未登录用户会被 302 重定向到 AAD 登录。
