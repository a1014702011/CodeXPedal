// [AI修改] 修复部分启动器未传 windir 时 WPF 字体缓存构造无效 URI 的启动崩溃。
using System.IO;

namespace CodeXPedal.Windows.App.Services;

public static class WindowsEnvironmentBootstrap
{
    public static void EnsureWindirForWpf()
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("windir")))
        {
            return;
        }

        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (string.IsNullOrWhiteSpace(systemRoot)
            || !Path.IsPathFullyQualified(systemRoot)
            || !Directory.Exists(systemRoot))
        {
            return;
        }

        // [AI修改] 只补当前 CodeXPedal 进程，不改变用户或系统的环境变量配置。
        Environment.SetEnvironmentVariable("windir", systemRoot, EnvironmentVariableTarget.Process);
    }
}
