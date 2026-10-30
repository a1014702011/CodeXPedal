// [AI修改] 可选读取 Claude Code 本地 JSONL 的今日 Token，未安装时保持为空。
using System.Text.Json;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class ClaudeLocalUsageReader
{
    public ClaudeUsage? Read(DateTimeOffset now, ICollection<string> diagnostics)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var projectsPath = Path.Combine(home, ".claude", "projects");
        if (!Directory.Exists(projectsPath))
        {
            return null;
        }

        var localNow = now.ToLocalTime();
        var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        long todayTokens = 0;
        var parsedFiles = 0;
        try
        {
            // [AI修改] 不跟随重解析目录；超上限明确不可用，不能把截断当完整今日统计。
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var paths = Directory.EnumerateFiles(projectsPath, "*.jsonl", options).Take(4097).ToArray();
            if (paths.Length > 4096) { throw new IOException("本地统计目录数量过多"); }
            foreach (var path in paths)
            {
                if (File.GetLastWriteTime(path) < localNow.DateTime.Date)
                {
                    continue;
                }

                parsedFiles++;
                if (parsedFiles > 32) { throw new IOException("今日本地统计文件数量过多"); }
                using var reader = new StringReader(BoundedLocalText.ReadSnapshot(path));
                while (reader.ReadLine() is { } line)
                {
                    todayTokens += ReadLineTokens(line, dayStart);
                }
            }

            var tasksPath = Path.Combine(home, ".claude", "tasks");
            var taskCount = Directory.Exists(tasksPath)
                ? Directory.EnumerateFiles(tasksPath, "*", options).Take(4097).Count()
                : 0;
            if (taskCount > 4096) { throw new IOException("本地任务数量超过统计上限"); }
            return new ClaudeUsage(todayTokens, taskCount, parsedFiles);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            diagnostics.Add($"Claude Code 本地数据读取失败：{exception.Message}");
            return null;
        }
    }

    private static long ReadLineTokens(string line, DateTimeOffset dayStart)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return 0;
        }

        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!IsToday(root, dayStart)
            || !root.TryGetProperty("message", out var message)
            || !message.TryGetProperty("usage", out var usage))
        {
            return 0;
        }

        return ReadInt64(usage, "input_tokens")
            + ReadInt64(usage, "output_tokens")
            + ReadInt64(usage, "cache_creation_input_tokens")
            + ReadInt64(usage, "cache_read_input_tokens");
    }

    private static bool IsToday(JsonElement root, DateTimeOffset dayStart)
    {
        if (!root.TryGetProperty("timestamp", out var timestamp))
        {
            return false;
        }

        return timestamp.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(timestamp.GetString(), out var value)
            && value.ToLocalTime() >= dayStart;
    }

    private static long ReadInt64(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number) ? number : 0;
}
