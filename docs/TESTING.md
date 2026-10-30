# 测试与证据

<!-- [AI修改] 测试声明、实际执行、构建、运行、设备和发布分别记录。 -->

## 共享核心

```sh
cargo fmt --manifest-path shared/Cargo.toml --check
cargo clippy --manifest-path shared/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path shared/Cargo.toml --locked
```

覆盖93%剩余、单/双/空/未知窗口、多桶优先/拒绝混用、重复/畸形/越界、可选重置、输入上限、时间边界、失败保留与C ABI布局。

## Windows

`windows/scripts/build.ps1` 构建真实Rust DLL与.NET宿主，运行测试exe。设置 `CODEXPEDAL_TEST_REPORT` 输出逐例JSON，核对executed/passed/failed/skipped和进程退出码；失败时不发布。测试使用假CLI真实子进程，不连接用户账户。

本机临时根统一为 `F:\codex_tmp\`；CI通过 `CODEXPEDAL_TEST_TEMP_ROOT` 指定runner隔离目录。保留红/绿日志，不删除首次失败。任务栏与Explorer、详情窗口、DPI和退出空间恢复仍需Windows真机验证。

可选本地统计另外限制文件快照4MiB、精细统计32个近期会话、缓存64项/每会话4096事件、数据库4096行/文本4096字节。超限明确降级或不可用，不把截断结果当完整统计；这些限制不影响官方额度读取。

## Mac

`make test` 先构建本架构共享静态库，再运行Swift XCTest并开启覆盖率，日志在 `build/macos-tests.log`。测试直接调用生产绑定，包括真实子进程的部分响应、EOF、输出上限、超时、取消和ID隔离。临时夹具使用显式隔离目录，不放系统/tmp。

`make package` 执行源码边界、XCTest、构建、资源/签名和包校验。CI使用arm64与Intel各自的原生runner。目标声明不等于macOS 13已真机通过，ad-hoc签名不等于Apple公证。

## 发布前未覆盖项

菜单栏/浮窗/详情/设置、20–42pt、单/双额度、仅菜单栏/任务栏、多显示器、缩放、VoiceOver、休眠唤醒、退出回收、真实账户同时间对照、空闲内存/能耗及长时间运行分别记录；CI不替代设备验收。没有证据时写NOT_RUN，不能填0失败或全部通过。

源码边界脚本覆盖全生产文件清单并输出静态风险报告；它不是完整安全审计或真实内存测试。
