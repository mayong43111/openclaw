# 私有技能仓库方案

## 背景

企业部署场景下，需要：
1. 用户不能从公共仓库（ClawHub / npm）安装技能和插件
2. `find_skill` 等能力只能从私有仓库检索

## OpenClaw 扩展体系

OpenClaw 有两套独立的扩展系统：

| | Skills（技能） | Plugins（插件） |
|---|---|---|
| 形态 | 目录 + `SKILL.md` 文件 | npm 包 / .ts/.js 模块 |
| 公共仓库 | ClawHub (`clawhub.com`) | npm (`registry.npmjs.org`) |
| 安装方式 | `npx clawhub` CLI | `openclaw plugins install <spec>` → `npm pack` |
| 加载配置 | `config.skills.*` | `config.plugins.*` |

---

## Skills 加载优先级（低→高）

1. `config.skills.load.extraDirs` — 额外目录
2. Bundled — 包内 `skills/` 目录
3. Managed — `~/.openclaw/skills/`
4. Personal — `~/.agents/skills/`
5. Project — `<workspace>/.agents/skills/`
6. Workspace — `<workspace>/skills/`

## Plugins 加载来源

1. Bundled（stock）— `<packageRoot>/extensions/`
2. Global — `~/.openclaw/extensions/`
3. Workspace — `<workspace>/.openclaw/extensions/`
4. `config.plugins.load.paths` — 额外路径

---

## 实施方案

### 第 1 层：配置级阻断

#### Skills — 禁用公共源，只加载私有目录

```json
// openclaw.json
{
  "skills": {
    "allowBundled": [],
    "load": {
      "extraDirs": ["/opt/openclaw-skills/"]
    }
  }
}
```

#### Plugins — 指向私有 npm registry

```bash
# 在 VM 上配置 ~/.npmrc
npm config set registry https://your-private-registry.com/

# 或使用 scoped registry
npm config set @yourscope:registry https://your-private-registry.com/
```

插件安装 (`openclaw plugins install`) 底层走 `npm pack`，会读取 npm 客户端配置。

---

### 第 2 层：代码级阻断（需改 OpenClaw 源码）

| 文件 | 改动 | 目的 |
|---|---|---|
| `src/agents/system-prompt.ts:182` | 替换 `clawhub.com` 为私有地址 | Agent 不推荐公共仓库 |
| `src/cli/skills-cli.format.ts:26` | 删除 `npx clawhub` 提示 | CLI 不引导到 ClawHub |
| `ui/src/ui/views/skills.ts:54` | 替换 ClawHub 链接 | Web UI 指向私有仓库 |
| `src/plugins/install.ts:487` | 添加 registry 白名单 | 阻止从非授权源安装 |

---

### 第 3 层：私有仓库服务

#### Skills — Git 仓库方案（推荐）

```
private-skills-repo/          (Azure DevOps / GitHub Enterprise)
├── research/
│   └── SKILL.md
├── code-review/
│   └── SKILL.md
└── ...
```

部署时在 deploy-vm.yml 中 clone：

```yaml
- name: Clone private skills repo
  ansible.windows.win_shell: |
    git clone https://your-repo.git C:\openclaw-skills
```

配置 `openclaw.json`：
```json
{ "skills": { "load": { "extraDirs": ["C:\\openclaw-skills"] } } }
```

#### Plugins — Azure Artifacts 方案

使用 Azure Artifacts 作为私有 npm registry：

```bash
# 创建 Azure Artifacts Feed
az artifacts feed create --name openclaw-plugins --organization https://dev.azure.com/yourorg

# VM 上配置 .npmrc
registry=https://pkgs.dev.azure.com/yourorg/_packaging/openclaw-plugins/npm/registry/
```

---

## 部署集成

在 `deploy-vm.yml` Play 2 中添加：

1. Clone 私有 skills 仓库到 VM
2. 配置 `~/.npmrc` 指向私有 registry
3. 设置 `openclaw.json` 的 `skills.allowBundled: []` 和 `skills.load.extraDirs`

更新 schedule（可选）：定时 `git pull` 同步最新 skills。

---

## 验证

```bash
# 确认 skills 只来自私有目录
openclaw skills list

# 确认 plugins install 走私有 registry
openclaw plugins install @yourscope/my-plugin

# 确认公共源不可达
openclaw plugins install some-public-plugin  # 应失败
```
