# CodeXPedal

> 软件运行时提示无法打开，要执行：打开“系统设置 → 隐私与安全性”，向下找到 CodeXPedal，点击“仍要打开”。

轻量的 Codex / Claude 剩余额度桌面工具：Windows 任务栏与悬浮窗，macOS 菜单栏与横条浮窗。

在设置中可分别勾选「显示并获取 Codex 额度」「显示并获取 Claude 额度」，默认都开启。关闭一项后，各界面隐藏该项，后台也不再获取；只开一项时使用单行布局，两项都关则显示「没有可获取的 AI」。已勾选但未安装或未登录时可在详情查看原因。

按官方响应自动显示 5 小时、7 天窗口，不按套餐名称猜测；不显示月额度。「额度时间显示」默认显示最近获取时间（HH:mm:ss），也可显示下一次真实额度重置时间。紧凑界面每家显示最早的下次重置时间，Windows 任务栏省略周期前缀，详情按周期列出；跨天带日期，无有效重置时间显示 `--`。切换时间不触发额外读取，失败保留旧值并显示 ≈。

## 下载与运行

[查看 Releases](https://github.com/a1014702011/CodeXPedal/releases)。当前为 beta 预发布，不是稳定版，各平台的验证情况见当次发布说明。请下载下表的软件包，不要把源码 ZIP 当成安装包。

| 平台 | 软件包 | 使用前提 |
|---|---|---|
| Windows 10/11 x64 | 便携 ZIP，解压后运行 app/CodeXPedal.exe | 完整解压，包内自带 .NET 运行时和共享核心 |
| macOS 13+ Apple Silicon | arm64 ZIP / DMG | 原生应用，开发者公证状态见当次发布说明 |
| macOS 13+ Intel | x86_64 ZIP / DMG | 原生应用，开发者公证状态见当次发布说明 |

### macOS 首次打开

如果 macOS 提示无法打开 CodeXPedal，请打开“系统设置 → 隐私与安全性”，向下找到 CodeXPedal，点击“仍要打开”，然后重新启动应用。

实时额度需要已安装、已登录且联网的官方 Codex CLI。可复用官方桌面版附带的 CLI，或自行安装官方 CLI；本软件不内置 CLI、不代登录、不读取 Codex 账户凭据。找不到时设置 `CODEXPEDAL_CODEX_PATH`；Mac 也可在设置中选择路径。仅有 API Key 不代表具有 ChatGPT 订阅额度。

### Claude 额度

Claude 额度需要 Pro/Max 订阅，并在本机安装、登录 **Claude Code（命令行版）**；只装 Claude 桌面版读不到。CodeXPedal 启动时如果启用了 Claude 额度、发现你在用 Claude 但没装或没登录 Claude Code，会询问一次：点击“安装并登录”后打开可见的终端窗口，执行官方安装脚本（Windows：`irm https://claude.ai/install.ps1 | iex`；macOS：`curl -fsSL https://claude.ai/install.sh | bash`）和 `claude auth login`，你只需在浏览器里点击“授权”；登录成功后即可显示额度。也可以在设置中随时点击“安装并登录 Claude Code”，或选择“不再提示”。本软件不会代填账号或读取密码。如果 `~/.claude/settings.json` 的 `env` 把 Claude Code 指向中转或第三方接口（`ANTHROPIC_BASE_URL`、`ANTHROPIC_AUTH_TOKEN` 等），且本机没有订阅登录信息，就读不到 Claude 订阅额度。

Claude 没有像 Codex app-server 那样的官方额度命令，CodeXPedal 提供两种来源，实时读取失败时自动使用桥接数据：

1. **实时读取（默认开启）**：读取本机 Claude Code 登录令牌（Windows：`~/.claude/.credentials.json`；macOS：钥匙串“Claude Code-credentials”，系统会询问授权，拒绝后暂停且不重复弹窗），约每 2 分钟一次（被限流时自动退避，按服务器建议或 2→4→8→15 分钟逐级延长）调用 Anthropic 官方 usage 接口。该接口与 Claude Code `/usage` 同源但未公开文档，字段可能变化；令牌不保存、不上传到其他地方，过期时不代为刷新，需打开一次 Claude Code。可在设置中关闭。
2. **Claude 状态栏桥接（备选）**：不读取任何凭据，适合不想授权读取令牌时使用。在设置中点击“启用 Claude 状态栏桥接”，然后在终端运行 `claude` 并发送一条消息（桌面端可能不会更新），额度会在下次刷新时显示。本软件会把 Claude Code 的 `statusLine` 指向自己，只把官方 `rate_limits` 的 5小时/7天已用百分比与重置时间写入 `~/.claude/codexpedal-rate-limits.json`；原状态栏会备份，停用后恢复。仅 Pro/Max 订阅，只在发消息后更新，空闲超过约3分钟显示 ≈。完全不想读取令牌时，请同时关闭实时读取。

Claude Code 查找顺序：`CODEXPEDAL_CLAUDE_PATH` → 官方原生安装（`~/.local/bin`）→ npm → WinGet / Homebrew → Claude 桌面版托管目录 → PATH；有 CLI 或存在 Claude 配置目录（支持 `CLAUDE_CONFIG_DIR`）即视为已安装。接入前可用 `python scripts/claude-quota-probe.py`（加 `--oauth` 测试实时接口）在本机核对路径与额度。

普通用户不需要安装 Rust、Python、Node、.NET SDK 或 Xcode。Windows 的本地历史兜底不是实时数据，缺失时显示 `--`。Mac 不扫描会话正文。两端不上传账户与用量。

## 功能

- 官方响应决定单/双额度；整份快照更新，不拼接不同时间或不同桶。
- 根据设置显示和读取各家额度；开启两项时各占一行，各自显示所选时间及 ≈ 旧值标记。
- Windows 支持仅任务栏模式、紧凑/完整浮窗、详情与设置。
- Mac 支持仅菜单栏模式、20–42pt横条浮窗、详情与设置。
- 超时、断连和过期使用统一规则，所有显示入口共用同一数据真源。
- 原生界面分别实现；额度、剩余计算与状态共用同一个 Rust 核心。

## 开发与验证

`shared/` 是唯一业务核心，Swift 和 C# 通过版本化 C ABI 调用实际原生库，不复制解析器。

Windows：Rust 1.97.1、MSVC x64 构建工具、.NET 10 SDK、PowerShell 7。

```powershell
.\windows\scripts\build.ps1
.\windows\scripts\publish.ps1
```

Mac：Rust 1.97.1、Swift 5.9+ / Xcode Command Line Tools、Python 3。

```sh
make source-check
make test
make build
make verify
make probe
make run
```

[共享核心](docs/SHARED_CORE.md) · [测试](docs/TESTING.md) · [发布门禁](docs/RELEASE.md) · [分发与依赖](DISTRIBUTION.md) · [English](README.en.md)

## 验证边界

CI 编译/测试、真实系统交互、真实账户额度、签名公证是不同验收项。未执行项不能标为通过，每个版本的真机验证情况以当次发布说明为准。

## 许可

MIT。来源与必要版权见 [来源说明](docs/BRANDING.md) 和 [LICENSE](LICENSE)。本项目不是 OpenAI 或 Anthropic 官方产品。
