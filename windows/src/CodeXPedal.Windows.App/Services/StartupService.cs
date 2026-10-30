// [AI修改] 仅在当前用户的 Run 项维护 CodeXPedal 开机启动，不需要管理员权限。
using System.IO;
using Microsoft.Win32;

namespace CodeXPedal.Windows.App.Services;

public static class StartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CodeXPedal";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            var desired = enabled && !string.IsNullOrWhiteSpace(Environment.ProcessPath)
                ? $"\"{Environment.ProcessPath}\""
                : null;
            var current = key.GetValue(ValueName) as string;
            if (desired is null)
            {
                if (current is not null)
                {
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
                }

                // [AI修改] 仅修改本产品启动项，不触碰其他产品配置。

                return;
            }

            if (!string.Equals(current, desired, StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue(ValueName, desired, RegistryValueKind.String);
            }

            // [AI修改] 移除旧品牌兼容行为，不再读写旧注册表值。
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            System.Diagnostics.Debug.WriteLine($"开机启动设置失败：{exception.Message}");
        }
    }
}
