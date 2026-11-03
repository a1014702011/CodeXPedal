// [AI修改] 一键安装并登录 Claude Code CLI：用户确认后在可见窗口执行官方安装脚本和 claude auth login，程序不代填账号、不读取密码。
using System.Diagnostics;
using System.Text;

namespace CodeXPedal.Windows.Core.Services;

public enum ClaudeCliStatus
{
    // 未安装 Claude Code CLI
    NotInstalled,
    // 已安装但没有订阅登录信息
    NotLoggedIn,
    // 已安装并有登录信息，可读取额度
    Ready,
}

public static class ClaudeCliSetup
{
    // [AI修改] 官方文档给出的 Windows PowerShell 安装命令，只在用户点击“安装并登录”后执行。
    public const string InstallCommand = "irm https://claude.ai/install.ps1 | iex";

    public static ClaudeCliStatus GetStatus() => GetStatus(ClaudeExecutableResolver.Resolve(), ClaudeExecutableResolver.ConfigDirectory());

    // [AI修改] 只检查登录文件是否存在且含令牌字段，不解析、不保存令牌内容；环境变量令牌同样视为已登录。
    internal static ClaudeCliStatus GetStatus(string? executable, string configDirectory)
    {
        if (executable is null)
        {
            return ClaudeCliStatus.NotInstalled;
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")))
        {
            return ClaudeCliStatus.Ready;
        }

        var credentials = Path.Combine(configDirectory, ".credentials.json");
        try
        {
            var info = new FileInfo(credentials);
            return info.Exists && info.Length is > 0 and <= 65_536 && File.ReadAllText(credentials).Contains("\"accessToken\"", StringComparison.Ordinal)
                ? ClaudeCliStatus.Ready
                : ClaudeCliStatus.NotLoggedIn;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ClaudeCliStatus.NotLoggedIn;
        }
    }

    // [AI修改] 生成在 PowerShell 窗口中运行的脚本：未安装才安装，随后打开浏览器登录；路径用单引号并转义，避免注入。
    public static string BuildScript(string? executable)
    {
        var known = executable is null ? string.Empty : executable.Replace("'", "''", StringComparison.Ordinal);
        var script = new StringBuilder();
        script.AppendLine("$ErrorActionPreference = 'Stop'");
        script.AppendLine("$Host.UI.RawUI.WindowTitle = 'CodeXPedal：安装并登录 Claude Code'");
        script.AppendLine($"$claude = '{known}'");
        script.AppendLine("if (-not $claude -or -not (Test-Path -LiteralPath $claude)) {");
        script.AppendLine("  Write-Host 'CodeXPedal：正在用官方脚本安装 Claude Code，请稍候（下载时没有进度显示）…'");
        script.AppendLine("  " + InstallCommand);
        script.AppendLine("  $claude = Join-Path $env:USERPROFILE '.local\\bin\\claude.exe'");
        script.AppendLine("}");
        script.AppendLine("Write-Host ''");
        script.AppendLine("Write-Host 'CodeXPedal：正在打开浏览器登录 Claude，请在浏览器中点击“授权”。'");
        script.AppendLine("Write-Host '如果浏览器没有自动打开，请按窗口中的提示复制链接或粘贴验证码。'");
        script.AppendLine("& $claude auth login");
        script.AppendLine("Write-Host ''");
        script.AppendLine("Write-Host 'CodeXPedal：完成后会自动显示 Claude 额度，可以关闭此窗口。'");
        return script.ToString();
    }

    // [AI修改] 以可见 PowerShell 窗口运行，用户能看到进度并在需要时粘贴验证码；EncodedCommand 避免引号与中文转义问题。
    public static void Launch()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(BuildScript(ClaudeExecutableResolver.Resolve())));
        // [AI修改] Base64 参数不含空格，直接拼接参数串即可兼容 ShellExecute。
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -NoExit -EncodedCommand " + encoded,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        using var _ = Process.Start(startInfo);
    }
}
