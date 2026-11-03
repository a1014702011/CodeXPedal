CodeXPedal 0.2.0-beta01 候选版本：新增 Claude 剩余额度，Windows 与 macOS 共用同一 Rust 额度核心。

## 主要更新

- 自动识别本机安装的 Codex 与 Claude Code：两者都有时任务栏、悬浮窗、菜单栏和横条同时显示两组额度，只装一个时只显示那一个。
- Claude Code 安装路径自动查找（原生安装、npm、WinGet/Homebrew、Claude 桌面版托管目录、PATH），也可用 `CODEXPEDAL_CLAUDE_PATH` 指定。
- Claude 额度默认来自设置中启用的“Claude 状态栏桥接”：读取 Claude Code 官方 statusLine 的 5小时/7天 `rate_limits`，不读凭据，原状态栏自动备份、停用即恢复。
- 可选“实时读取 Claude 额度”默认关闭：开启后读取本机 Claude 登录令牌，每分钟最多一次调用 Anthropic 官方 usage 接口（未公开文档，可能变化），令牌不保存、不代为刷新。
- 新增“安装并登录 Claude Code”：没装或没登录时启动询问一次，点一下即在终端执行官方安装与登录，你只需在浏览器点“授权”，之后自动显示 Claude 额度。
- 新增 `scripts/claude-quota-probe.py`，可在本机核对 Claude 安装路径、登录状态和额度。

## 使用提示

- Claude 额度需要 Pro/Max 订阅；状态栏桥接只在 Claude Code 交互会话发出请求后更新，空闲约3分钟后数字带 ≈。
- macOS 首次打开若提示无法打开，请到“系统设置 → 隐私与安全性”点击“仍要打开”。

本Release为草稿候选；真机交互和真实账户验证尚待独立验收，不代表稳定版。未配置Apple证书/公证时仅ad-hoc签名。
