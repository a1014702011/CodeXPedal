// [AI修改] Claude Code 状态栏桥接：用官方文档的 statusLine rate_limits 字段获取额度，不读取任何凭据。
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeXPedal.Windows.Core.Services;

public enum ClaudeBridgeState
{
    NotConfigured,
    Enabled,
    EnabledOtherPath,
    OtherCommand,
    SettingsUnreadable,
}

public static class ClaudeStatusLineHook
{
    public const string Argument = "--claude-statusline";
    private const int MaximumInput = 1_048_576;

    // [AI修改] 作为 statusLine 命令运行：stdin 为 Claude Code 会话 JSON，只摘取 rate_limits 原子写入缓存；任何错误都不抛出，避免破坏 Claude 界面。
    public static string Run(Stream input, string configDirectory, DateTimeOffset now)
    {
        try
        {
            var buffer = new byte[MaximumInput + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = input.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            if (total == 0 || total > MaximumInput)
            {
                return "Claude 剩余 --";
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, total));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object)
            {
                // [AI修改] 首次 API 响应前或非订阅账号没有该字段，此时不覆盖已有缓存。
                return "Claude 剩余 --（首次回复后显示）";
            }

            var payload = "{\"rate_limits\":" + limits.GetRawText() + "}";
            WriteAtomically(ClaudeStatusLineCache.PathFor(configDirectory), payload);
            return Format(payload, now);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return "Claude 剩余 --";
        }
    }

    // [AI修改] 状态栏文字也通过共享核心计算剩余百分比，与悬浮窗口径一致。
    internal static string Format(string payload, DateTimeOffset now)
    {
        if (SharedQuotaCore.ParseClaude(payload, now, out var native) != 0)
        {
            return "Claude 剩余 --";
        }

        var (five, seven) = SharedQuotaCore.Windows(native);
        var parts = new List<string>();
        if (five is not null) { parts.Add("5h " + Formatting.UsageFormatter.Percent(five.RemainingPercent)); }
        if (seven is not null) { parts.Add("7d " + Formatting.UsageFormatter.Percent(seven.RemainingPercent)); }
        return parts.Count == 0 ? "Claude 剩余 --" : "Claude 剩余 " + string.Join(" · ", parts);
    }

    private static void WriteAtomically(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        try
        {
            File.WriteAllText(temporary, content, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }
}

// [AI修改] 只在用户点击“启用/停用”时修改 Claude 的 settings.json；原 statusLine 备份后可完整恢复。
public static class ClaudeStatusLineBridge
{
    public const string BackupFileName = "codexpedal-statusline-backup.json";
    private const int MaximumSettingsBytes = 1_048_576;
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string SettingsPath(string configDirectory) => Path.Combine(configDirectory, "settings.json");

    // [AI修改] 正斜杠路径在 cmd、PowerShell 与 Git Bash 中都能正确执行。
    public static string BuildCommand(string executablePath) =>
        "\"" + executablePath.Replace('\\', '/') + "\" " + ClaudeStatusLineHook.Argument;

    public static (ClaudeBridgeState State, string? Command) GetState(string configDirectory, string executablePath)
    {
        if (!TryLoad(configDirectory, out var root))
        {
            return (ClaudeBridgeState.SettingsUnreadable, null);
        }

        var command = CurrentCommand(root);
        if (command is null)
        {
            return (ClaudeBridgeState.NotConfigured, null);
        }

        if (!IsOurs(command))
        {
            return (ClaudeBridgeState.OtherCommand, command);
        }

        return string.Equals(command, BuildCommand(executablePath), StringComparison.OrdinalIgnoreCase)
            ? (ClaudeBridgeState.Enabled, command)
            : (ClaudeBridgeState.EnabledOtherPath, command);
    }

    public static void Enable(string configDirectory, string executablePath)
    {
        if (!TryLoad(configDirectory, out var root))
        {
            throw new IOException("Claude settings.json 无法解析，未做修改");
        }

        var settingsPath = SettingsPath(configDirectory);
        var backupPath = Path.Combine(configDirectory, BackupFileName);
        var existing = root["statusLine"];
        var existingCommand = CurrentCommand(root);
        // [AI修改] 只备份用户自己的状态栏；重复启用或路径变更时不覆盖首次备份。
        if (existing is not null && (existingCommand is null || !IsOurs(existingCommand)))
        {
            var backup = new JsonObject { ["statusLine"] = existing.DeepClone() };
            WriteText(backupPath, backup.ToJsonString(WriteOptions));
        }

        if (File.Exists(settingsPath) && !File.Exists(settingsPath + ".codexpedal.bak"))
        {
            File.Copy(settingsPath, settingsPath + ".codexpedal.bak");
        }

        root["statusLine"] = new JsonObject
        {
            ["type"] = "command",
            ["command"] = BuildCommand(executablePath),
            ["padding"] = 0,
        };
        WriteText(settingsPath, root.ToJsonString(WriteOptions));
    }

    public static void Disable(string configDirectory)
    {
        if (!TryLoad(configDirectory, out var root))
        {
            throw new IOException("Claude settings.json 无法解析，未做修改");
        }

        var command = CurrentCommand(root);
        if (command is null || !IsOurs(command))
        {
            return;
        }

        var backupPath = Path.Combine(configDirectory, BackupFileName);
        JsonNode? restored = null;
        if (File.Exists(backupPath) && new FileInfo(backupPath).Length <= MaximumSettingsBytes)
        {
            restored = JsonNode.Parse(File.ReadAllText(backupPath), documentOptions: ReadOptions)?["statusLine"]?.DeepClone();
        }

        if (restored is null) { root.Remove("statusLine"); }
        else { root["statusLine"] = restored; }
        WriteText(SettingsPath(configDirectory), root.ToJsonString(WriteOptions));
        if (File.Exists(backupPath)) { File.Delete(backupPath); }
    }

    private static bool IsOurs(string command) => command.Contains(ClaudeStatusLineHook.Argument, StringComparison.Ordinal);

    private static string? CurrentCommand(JsonObject root) =>
        root["statusLine"] is JsonObject statusLine && statusLine["command"] is JsonValue value && value.TryGetValue<string>(out var command)
            ? command
            : root["statusLine"] is null ? null : string.Empty;

    private static bool TryLoad(string configDirectory, out JsonObject root)
    {
        root = new JsonObject();
        var path = SettingsPath(configDirectory);
        try
        {
            if (!File.Exists(path))
            {
                return true;
            }

            if (new FileInfo(path).Length > MaximumSettingsBytes)
            {
                return false;
            }

            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            if (JsonNode.Parse(text, documentOptions: ReadOptions) is not JsonObject parsed)
            {
                return false;
            }

            root = parsed;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static void WriteText(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".codexpedal.tmp";
        File.WriteAllText(temporary, content + Environment.NewLine, new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }
}
