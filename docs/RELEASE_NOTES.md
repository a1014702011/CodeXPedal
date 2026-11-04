CodeXPedal 0.2.0-beta01 预发布版本：新增 Claude 剩余额度，Windows 与 macOS 共用同一 Rust 额度核心。

## 主要更新

- 自动识别本机安装的 Codex 与 Claude Code：两者都有时任务栏、悬浮窗、菜单栏和横条同时显示两组额度，只装一个时只显示那一个。
- Claude Code 安装路径自动查找（原生安装、npm、WinGet/Homebrew、Claude 桌面版托管目录、PATH），也可用 `CODEXPEDAL_CLAUDE_PATH` 指定。
- Claude 额度默认“实时读取”：读取本机 Claude 登录令牌，约每 2 分钟一次调用 Anthropic 官方 usage 接口（未公开文档，可能变化），令牌不保存、不刷新，可在设置中关闭；macOS 需授权钥匙串，拒绝后暂停且不重复弹窗。
- “Claude 状态栏桥接”作为备选：不读凭据，在设置中启用后于终端运行 Claude Code 并发送消息，读取官方 statusLine 的 5小时/7天 `rate_limits`，原状态栏自动备份、停用即恢复。设置页写明了使用步骤。
- 新增“安装并登录 Claude Code”：没装或没登录时启动询问一次，点一下即在终端执行官方安装与登录，你只需在浏览器点“授权”，之后自动显示 Claude 额度。
- 修复 Mac 菜单栏多余空白、下拉菜单文字发灰，以及悬浮窗读数折行和第二行被裁切的问题。
- 新增 `scripts/claude-quota-probe.py`，可在本机核对 Claude 安装路径、登录状态和额度。

## 使用提示

- Claude 额度需要 Pro/Max 订阅和 Claude Code 登录；状态栏桥接只在终端版 Claude Code 发出请求后更新，空闲约3分钟后数字带 ≈。
- macOS 首次打开若提示无法打开，请到“系统设置 → 隐私与安全性”点击“仍要打开”。

## 验证情况

本版本是公开预发布，不是稳定版。

- CI：Windows x64、macOS arm64 与 Intel 的构建和自动测试全部通过。
- Mac mini（Apple 芯片）：曾用真实账户看到 Codex 与 Claude 额度；本版的设置页、下拉菜单对齐和悬浮窗修复尚未完成真机确认。
- Windows：作者已完成真机测试。
- macOS 13、Intel Mac：本版未做真机验收。
- 签名：未配置 Apple 证书和公证，仅 ad-hoc 签名。
