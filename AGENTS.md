# CodeXPedal 协作规则

<!-- [AI修改] 记录共享核心工程的当前边界，历史发行流程不作验收证据。 -->

- 业务真源：shared/src/lib.rs；唯一ABI头：shared/include/codexpedal_core.h。
- Windows：windows/src/原生WPF与任务栏；Mac：macos/Sources/原生菜单栏与横条。宿主只做ABI、CLI/进程、设置与系统适配，不再复制业务解析。
- 写入前给方案并取得确认；保留无关改动、编码/BOM与CRLF。新改代码加适度中文[AI修改]注释，日志中文，Git提交中文。
- 正式需求：实际300/10080分钟窗口决定显示，不猜订阅；无月额度。整份更新、失败保留原值/原时间，所有入口共用陈旧标记。
- 不读取CLI凭据、不自动登录/安装、不上传账户和用量、不恢复旧仪表盘或旧资源。
- Windows保留仅任务栏模式；Mac横条只显示额度和右侧HH:mm:ss，20–42pt，支持仅菜单栏。无新视觉方向调整。
- 测试：cargo test --manifest-path shared/Cargo.toml --locked；Windows scripts/build.ps1；Mac make test / make build / make verify。测试调用真实共享库。
- 内存/生命周期：输出、缓存、遍历、等待必须有界；报告整个生产目录风险清单，静态检查不能代替运行或安全审计。
- 同步README中英、核心、测试、分发和发布说明。Windows本机临时输出F盘；CI/Mac使用独立构建目录。不得提交账户、个人路径、运行日志。
- 持久实施清单：docs/IMPLEMENTATION_CODEXPEDAL_REBRAND_AND_QUOTAS.md（仅本地、不上传）。
- 当前用户已确认重构两端、清理旧实现、创建同名公开仓库并用新初始提交上传，配置自动构建和草稿Release；保留原本地Git历史/备份，不强推/覆盖远端。
- 公开Release前必须完成真机与真实额度门禁；自动化只创建草稿候选。Mac mini不可用则标NOT_RUN，不能把CI等同该设备验收。稳定版另需确认。
- 已确认范围直接执行；新权限/扩大改动需再确认。发布/安全/测试流程以docs/RELEASE.md和docs/TESTING.md为当前依据，旧项目技能/SOP不适用于已移除功能。
