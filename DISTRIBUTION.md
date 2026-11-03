# 软件分发与运行前提

<!-- [AI修改] 区分候选构建、公开下载与真机验收，不沿用参考项目发行记录。 -->

- Windows 10/11 x64：完整解压便携ZIP，再运行 `app/CodeXPedal.exe`。共享Rust DLL在app目录，.NET Core/Windows Desktop运行时在runtime目录；不能只复制exe。
- Mac 13+：分别构建Apple Silicon/Intel原生app，提供ZIP与DMG。共享核心静态链接，不依赖开发机Rust目录；应用仅含自身图标和plist。
- 两端实时额度都需要官方Codex CLI已安装、登录并联网。CLI不内置，可复用桌面版或用户自行安装。统一路径变量为 `CODEXPEDAL_CODEX_PATH`；不再支持旧品牌变量和自动迁移。
- Claude Code不内置：用户在启动弹窗或设置中确认后，才在可见终端执行官方安装脚本和 `claude auth login`；登录需用户在浏览器授权，不代填账号。
- Claude额度可选：已安装Claude Code即显示Claude分组。默认“实时读取”：读取本机Claude登录令牌并只发往Anthropic官方接口，可在设置中关闭；状态栏桥接作为备选，由用户主动启用，读取官方`rate_limits`，不读凭据。路径变量为 `CODEXPEDAL_CLAUDE_PATH`，配置目录遵循 `CLAUDE_CONFIG_DIR`。
- 普通用户无需开发工具。Windows本地历史只作有来源标识的旧快照，不保证实时；Mac不读会话正文。不内置账户数据、CLI凭据、日志或遥测。
- 自动化先运行共享核心测试及两端原生测试，再打包并计算SHA-256；任何任务失败都不创建Release草稿。所有资产来自同一个提交。
- 当前仅允许草稿/候选包。Mac mini真机交互和真实额度对照未通过前不得公开发布；CI不等于现场验证。
- macOS无Apple Developer凭据时仅使用ad-hoc签名，不声称Developer ID、公证或免Gatekeeper提示。Windows没有证书时也不声称已数字签名。
- 新仓库不继承旧tag/Release成绩，不上传本地旧Git历史。发布记录以新仓库实际结果为准。
