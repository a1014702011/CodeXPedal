# Windows 客户端

<!-- [AI修改] 说明共享核心、当前路径变量、便携运行时与真实测试入口。 -->

面向Windows10/11 x64。完整解压便携ZIP，运行app/CodeXPedal.exe；runtime目录与app内codexpedal_core.dll必须保留。无需安装.NET SDK、Rust、Python或Node。

实时额度需要官方Codex CLI已安装、登录并联网。自动查找standalone与官方桌面版的用户目录CLI，找不到时设置CODEXPEDAL_CODEX_PATH；不调用受保护的WindowsApps内部路径。CLI不内置，不自动安装/登录。旧品牌环境变量、配置迁移和启动项兼容已移除，不删除旧数据。

额度、5小时/7天识别、剩余计算和旧快照判断均来自shared/真实Rust DLL，不再维护C#重复解析。失败不刷新采样时间，所有显示入口用≈标识旧值。Windows本地历史兜底不是实时，缺失为--。Token/任务统计为本机可选估算，与官方额度不同。

保留任务栏自适应字号、紧凑/完整浮窗、仅任务栏、详情与设置。任务栏/DPI/Explorer退出空间恢复仍需真实系统验收。

## 开发

Rust1.97.1、MSVC x64构建工具、.NET10 SDK、PowerShell7；构建过程先生成共享DLL（静态链接MSVC运行库），再构建和测试.NET宿主。

```powershell
.\windows\scripts\build.ps1 -Configuration Release
.\windows\scripts\publish.ps1 -DotnetRoot 'C:\Program Files\dotnet'
```

测试通过CODEXPEDAL_TEST_REPORT输出逐项JSON。临时目录本机默认F盘，CI通过CODEXPEDAL_TEST_TEMP_ROOT注入。使用真实测试宿主模拟CLI进程，不连接账户；保留失败日志。

publish输出ZIP，包含共享核心、项目与第三方许可证、.NET运行时和使用说明。不得只验证exe存在就称软件可运行。见[测试](../docs/TESTING.md)和[发布](../docs/RELEASE.md)。
