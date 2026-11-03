// [AI修改] 自动发现 Claude Code CLI：显式变量、官方原生安装、npm、WinGet、Claude 桌面版托管目录和 PATH。
namespace CodeXPedal.Windows.Core.Services;

public static class ClaudeExecutableResolver
{
    public const string PathVariable = "CODEXPEDAL_CLAUDE_PATH";
    private const int MaximumManagedVersions = 64;

    // [AI修改] 与 Claude Code 一致：CLAUDE_CONFIG_DIR 优先，否则使用用户目录下的 .claude。
    public static string ConfigDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : Environment.ExpandEnvironmentVariables(configured.Trim());
    }

    public static string? Resolve() => Resolve(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    // [AI修改] 目录参数可注入，测试用隔离目录验证查找顺序，不依赖开发机真实安装。
    internal static string? Resolve(Func<string, string?> environment, string home, string appData, string localAppData)
    {
        var candidates = new List<string?>
        {
            environment(PathVariable),
            Path.Combine(home, ".local", "bin", "claude.exe"),
            Path.Combine(appData, "npm", "claude.cmd"),
            Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "claude.exe"),
        };
        candidates.AddRange(FindDesktopManagedCandidates(Path.Combine(appData, "Claude", "claude-code")));
        var pathValue = environment("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(256))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "claude.exe"));
            candidates.Add(Path.Combine(directory.Trim('"'), "claude.cmd"));
        }

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // [AI修改] 商店包内部 WindowsApps 路径受 ACL 保护，外部进程无法调用，不作为安装位置。
                if (File.Exists(candidate) && !IsProtectedStorePath(candidate!))
                {
                    return Path.GetFullPath(candidate!);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
            {
                // [AI修改] 用户 PATH 中的非法路径只跳过当前项，不影响其他候选。
            }
        }

        return null;
    }

    // [AI修改] 有 CLI 或有 Claude 配置目录都视为已安装（桌面版用户可能没有把 CLI 加入 PATH）。
    public static bool IsInstalled() => Resolve() is not null || Directory.Exists(ConfigDirectory());

    private static bool IsProtectedStorePath(string path)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(programFiles))
        {
            return false;
        }

        var windowsApps = Path.Combine(programFiles, "WindowsApps") + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> FindDesktopManagedCandidates(string root)
    {
        try
        {
            if (!Directory.Exists(root))
            {
                return [];
            }

            // [AI修改] 桌面版按版本号子目录存放 CLI，取最近更新的版本；目录数量有上限。
            return Directory.EnumerateDirectories(root)
                .Take(MaximumManagedVersions)
                .SelectMany(directory => new[] { Path.Combine(directory, "claude.exe"), Path.Combine(directory, "bin", "claude.exe") })
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}

// [AI修改] 统一的安装探测：界面只显示本机实际安装的工具；两者都没探测到时保留 Codex 分组作为默认入口。
public static class ProviderDetection
{
    public static (bool Codex, bool Claude) Detect()
    {
        var codex = CodexExecutableResolver.Resolve() is not null
            || Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
        return (codex, ClaudeExecutableResolver.IsInstalled());
    }
}
