# macOS 原生客户端

<!-- [AI修改] 原生界面不变，业务改为链接与Windows相同的共享Rust核心。 -->

macOS 13+，Apple Silicon/Intel独立构建。使用AppKit菜单栏、NSPanel横条与SwiftUI详情/设置，不包含旧仪表盘、配色包、徽章或后台会话统计。

共享规则在 ../shared/；本目录CodeXPedalCore只保留薄适配、CLI定位和有界stdio。失败保留旧时间并加≈；窗口按官方时长识别，不猜套餐。

实时额度需要官方CLI已安装、登录并联网。设置路径优先于CODEXPEDAL_CODEX_PATH，再查桌面版/Homebrew/PATH；错误显式路径不偷偷回退。不读取认证文件或会话正文。

开发需要Rust 1.97.1、Swift5.9+/Xcode命令行工具、Python3；用户不需要这些开发工具。make会先构建本架构Rust静态库，不动态链接开发机路径。

```sh
make source-check
make test
make build
make verify
make probe
make run
make package
```

make test开启覆盖率并保存build/macos-tests.log。构建产物build/CodeXPedal.app；package生成本架构ZIP/DMG及SHA256。无证书时ad-hoc签名，不声称Apple公证。

SwiftPM直接调用需先构建核心并指定CODEXPEDAL_CORE_LIB_DIR；推荐使用make入口。测试夹具根通过CODEXPEDAL_TEST_TEMP_ROOT注入，不写系统/tmp。

CI与真机分开。菜单栏/横条、20–42pt、仅菜单栏、详情设置、VoiceOver、多显示器、休眠/退出、真实额度、能耗尚需实际设备验收；未执行时保持NOT_RUN。源码和测试声明不等于已运行。

详见[共享核心](../docs/SHARED_CORE.md)、[测试](../docs/TESTING.md)、[发布](../docs/RELEASE.md)。
