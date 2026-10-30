# CodeXPedal

轻量的 Codex 剩余额度桌面工具：Windows 任务栏与悬浮窗，macOS 菜单栏与横条浮窗。

按官方响应自动显示5小时、7天窗口，不按套餐名称猜测；不显示月额度。右侧时间为最后一次有效采样的 HH:mm:ss，失败保留旧值并显示 ≈，不冒充实时数据。

## 下载与运行

[查看 Releases](https://github.com/a1014702011/CodeXPedal/releases)。首版仍处验证阶段：自动流水线生成候选包和草稿 Release，草稿不是公开下载；不要把源码 ZIP 当成软件安装包。

| 平台 | 软件包 | 使用前提 |
|---|---|---|
| Windows 10/11 x64 | 便携 ZIP，解压后运行 app/CodeXPedal.exe | 完整解压，包内自带 .NET 运行时和共享核心 |
| macOS 13+ Apple Silicon | arm64 ZIP / DMG | 原生应用，开发者公证状态见当次发布说明 |
| macOS 13+ Intel | x86_64 ZIP / DMG | 原生应用，开发者公证状态见当次发布说明 |

### macOS 首次打开

如果 macOS 提示无法打开 CodeXPedal，请打开“系统设置 → 隐私与安全性”，向下找到 CodeXPedal，点击“仍要打开”，然后重新启动应用。

实时额度需要已安装、已登录且联网的官方 Codex CLI。可复用官方桌面版附带的 CLI，或自行安装官方 CLI；本软件不内置 CLI、不代登录、不读取账户凭据。找不到时设置 `CODEXPEDAL_CODEX_PATH`；Mac 也可在设置中选择路径。仅有 API Key 不代表具有 ChatGPT 订阅额度。

普通用户不需要安装 Rust、Python、Node、.NET SDK 或 Xcode。Windows 的本地历史兜底不是实时数据，缺失时显示 `--`。Mac 不扫描会话正文。两端不上传账户与用量。

## 功能

- 官方响应决定单/双额度；整份快照更新，不拼接不同时间或不同桶。
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

CI 编译/测试、真实系统交互、真实账户额度、签名公证是不同验收项。未执行项不能标为通过；Mac mini 连接未恢复前，不能声称已完成该机器的真机验收。

## 许可

MIT。来源与必要版权见 [来源说明](docs/BRANDING.md) 和 [LICENSE](LICENSE)。本项目不是 OpenAI 官方产品。
