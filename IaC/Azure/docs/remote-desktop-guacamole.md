# 远程桌面（Guacamole on ACI）部署文档

## 架构概述

```
用户浏览器
    ↓ HTTPS
Application Gateway (appgw-ymms-openclaw)
    ↓ HTTP :8080
App Service (app-ymms-console) nginx
    ↓ /guac/ → proxy_pass
ACI Container Group (aci-ymms-guacamole) @ 10.0.0.5:8080
    ├─ guacamole (Tomcat Web UI)
    └─ guacd (RDP/VNC proxy daemon, sidecar)
    ↓ RDP :3389
Windows VM (snet-vm, e.g. 10.0.1.4)
    └─ console session #1 (桌面)
```

### 关键参数

| 资源 | 值 |
|------|------|
| ACI Container Group | `aci-ymms-guacamole` |
| ACI Subnet | `snet-aci` (10.0.0.0/28) |
| ACI 实际 IP | `10.0.0.5` |
| Guacamole Web | `http://10.0.0.5:8080/guacamole/` |
| Console 代理路径 | `/guac/` → Guacamole |
| Guacamole 版本 | 1.5.5 |
| 认证 | XML user-mapping (admin/openclaw2026) |
| RDP 参数 | `console=true` (接管 session #1) |

## 网络拓扑

```
VNet: vnet-ymms-openclaw (10.0.0.0/22)
├── snet-aci      10.0.0.0/28   (delegated: Microsoft.ContainerInstance)
├── snet-vm       10.0.1.0/24   (Windows VMs)
├── snet-appgw    10.0.2.0/24   (Application Gateway)
├── snet-worker   10.0.3.0/26   (App Service VNet integration)
└── snet-pe       10.0.3.64/28  (Private Endpoints)
```

## 部署步骤

### 1. 创建 ACI 委托子网

```bash
az network vnet subnet create \
  -g rg-ymms-openclaw-infra \
  --vnet-name vnet-ymms-openclaw \
  -n snet-aci \
  --address-prefix 10.0.0.0/28 \
  --delegations Microsoft.ContainerInstance/containerGroups
```

### 2. 准备 Guacamole 配置文件

**guacamole.properties:**
```properties
guacd-hostname: 127.0.0.1
guacd-port: 4822
user-mapping: /etc/guacamole/user-mapping.xml
skip-if-unavailable: true
```

**user-mapping.xml:**
```xml
<user-mapping>
    <authorize username="admin" password="openclaw2026">
        <connection name="vm-ymms-openclaw-16">
            <protocol>rdp</protocol>
            <param name="hostname">10.0.1.4</param>
            <param name="port">3389</param>
            <param name="username">azureuser</param>
            <param name="password">OpenClaw@Reset2026!</param>
            <param name="security">nla</param>
            <param name="ignore-cert">true</param>
            <param name="console">true</param>
            <param name="resize-method">reconnect</param>
            <param name="color-depth">24</param>
        </connection>
    </authorize>
</user-mapping>
```

要添加更多 VM，在 `<authorize>` 标签内添加更多 `<connection>` 块即可。
connection name 必须与 Console 中的 VM 名称一致。

### 3. 部署 ACI Container Group

ACI 使用 **Secret Volume** 挂载配置文件（无需 Storage Account），环境变量设定 guacd 地址。

```bash
# Base64 编码配置
PROPS_B64=$(base64 -w0 guacamole.properties)
MAPPING_B64=$(base64 -w0 user-mapping.xml)
SUBNET_ID=$(az network vnet subnet show -g rg-ymms-openclaw-infra \
  --vnet-name vnet-ymms-openclaw -n snet-aci --query id -o tsv)

# 生成 YAML（替换 base64 值）并部署
az container create -g rg-ymms-openclaw-infra --file aci-guacamole.yaml
```

**YAML 模板** 见 `IaC/Azure/aci/aci-guacamole.yaml`。

### 4. 验证

```bash
# 检查容器状态
az container show -g rg-ymms-openclaw-infra -n aci-ymms-guacamole \
  --query "containers[].{name:name, state:instanceView.currentState.state}" -o table

# 测试 Guacamole Web UI
curl -s -o /dev/null -w "HTTP %{http_code}" http://10.0.0.5:8080/guacamole/

# 测试认证 API
curl -s -X POST http://10.0.0.5:8080/guacamole/api/tokens \
  -d 'username=admin&password=openclaw2026'
```

### 5. Console 集成

Console 通过 nginx 反向代理 `/guac/` 到 ACI Guacamole。

**nginx 配置要点：**
- 路径 `/guac/` → `proxy_pass http://10.0.0.5:8080/guacamole/`
- 需要 WebSocket 支持 (`Upgrade` / `Connection` headers)
- 需要 AAD 认证（检查 `X-MS-CLIENT-PRINCIPAL` header）

**Desktop 页面：**
- 所有 VM 都显示桌面图标
- 点击后通过 Guacamole API 获取 token 和连接 ID
- 如果 Guacamole 中未配置该 VM 的连接，显示友好错误提示
- 不 hardcode 哪些 VM 可用

## 添加新 VM 的桌面连接

1. 更新 `user-mapping.xml` 添加新 `<connection>` 块
2. Base64 编码新文件
3. 更新 ACI YAML 中的 secret volume
4. 重新部署 ACI：`az container create -g rg-ymms-openclaw-infra --file aci-guacamole.yaml`

## 注意事项

- **ACI IP 偏移**：ACI Container Group 报告的 IP（如 10.0.0.4）和容器实际 IP（如 10.0.0.5）可能不同。nginx 应使用实际可达的 IP。
- **console=true**：RDP 连接使用 `console=true` 参数，会接管 Windows session #1（即实际桌面会话），而不是创建新 session。
- **Azure Policy**：订阅级 Policy 禁用了 Storage Account 共享密钥访问，因此使用 ACI Secret Volume 代替 Azure File Share。
- **VNet Peering**：jumpbox VNet 通过 peering 连接到 openclaw VNet，添加新子网后需要 `az network vnet peering sync` 同步路由。
