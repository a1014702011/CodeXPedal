// [AI修改] 优先发现官方 Windows standalone、Desktop 托管版本、npm 和用户显式配置的 Codex CLI。
namespace CodeXPedal.Windows.Core.Services;

public sealed record ResolvedCommand(string FileName, string? ScriptPath)
{
    // [AI修改] cmd.exe 的整条命令必须放在同一对引号中，兼容 npm 路径里的空格。
    public string BuildArguments(string arguments) => string.IsNullOrWhiteSpace(ScriptPath)
        ? arguments
        : $"/d /s /c \"\"{ScriptPath}\" {arguments}\"";
}

public static class CodexExecutableResolver
{
    public static ResolvedCommand? Resolve()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("CODEXPEDAL_CODEX_PATH"),
            // [AI修改] 只使用当前产品配置，不再读取旧品牌变量。
            Path.Combine(localAppData, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "packages", "standalone", "current", "bin", "codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "packages", "standalone", "current", "codex.exe"),
        };
        // [AI修改] 新版 Codex Desktop 会把可执行 CLI 放在用户目录的哈希版本子目录中。
        candidates.AddRange(FindDesktopManagedCandidates(localAppData));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "codex.cmd"));
        candidates.Add(Path.Combine(localAppData, "Programs", "codex", "codex.exe"));

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.exe"));
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.cmd"));
            candidates.Add(Path.Combine(directory.Trim('"'), "codex.bat"));
        }

        foreach (var candidate in candidates.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // [AI修改] 商店包内部 WindowsApps 路径受 ACL 保护，不能作为外部客户端的 CLI 入口。
            if (!File.Exists(candidate) || IsProtectedStorePath(candidate!))
            {
                continue;
            }

            var extension = Path.GetExtension(candidate);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                return new ResolvedCommand(
                    Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                    candidate);
            }

            return new ResolvedCommand(candidate!, string.Empty);
        }

        // [AI修改] 不回退到受保护的商店包，避免每次刷新都触发 Access denied。
        return null;
    }

    private static bool IsProtectedStorePath(string path)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var windowsApps = Path.Combine(programFiles, "WindowsApps") + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(windowsApps, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> FindDesktopManagedCandidates(string localAppData)
    {
        var binRoot = Path.Combine(localAppData, "OpenAI", "Codex", "bin");
        try
        {
            if (!Directory.Exists(binRoot))
            {
                return [];
            }

            // [AI修改] 选择最近更新的有效版本目录，不依赖会随升级变化的哈希名称。
            return Directory.EnumerateDirectories(binRoot)
                .Select(directory => Path.Combine(directory, "codex.exe"))
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
