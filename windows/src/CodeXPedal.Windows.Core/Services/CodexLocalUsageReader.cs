// [AI修改] 从 Codex 本地线程数据库估算 Token 与今日任务，不访问网络。
using CodeXPedal.Windows.Core.Infrastructure;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class CodexLocalUsageReader
{
    private readonly CodexSessionUsageReader _sessionUsage = new();

    public LocalUsage? Read(DateTimeOffset now, ICollection<string> diagnostics)
    {
        var databasePath = ResolveDatabasePath();
        if (databasePath is null)
        {
            diagnostics.Add("未找到 Codex state_5.sqlite");
            return null;
        }

        try
        {
            using var database = new WinSqliteDatabase(databasePath);
            var columns = database.Query("PRAGMA table_info(threads);")
                .Select(row => Convert.ToString(row.GetValueOrDefault("name")))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (!columns.Contains("tokens_used") || !columns.Contains("updated_at"))
            {
                diagnostics.Add("Codex threads 表缺少 Token 统计字段");
                return null;
            }

            var tokenRows = database.Query("SELECT COALESCE(tokens_used, 0) AS tokens, updated_at AS updatedAt FROM threads;");
            var localNow = now.ToLocalTime();
            var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
            var sevenDayStart = dayStart.AddDays(-6);
            long todayTokens = 0;
            long sevenDayTokens = 0;
            long lifetimeTokens = 0;
            foreach (var row in tokenRows)
            {
                var tokens = ToInt64(row.GetValueOrDefault("tokens"));
                var updatedAt = ToDateTimeOffset(row.GetValueOrDefault("updatedAt"));
                lifetimeTokens += tokens;
                if (updatedAt >= sevenDayStart)
                {
                    sevenDayTokens += tokens;
                }

                if (updatedAt >= dayStart)
                {
                    todayTokens += tokens;
                }
            }

            var detailed = _sessionUsage.Read(database, columns, now, diagnostics);
            var (activeTasks, pendingTasks) = ReadTaskCounts(database, columns, now, dayStart);
            if (detailed is not null)
            {
                todayTokens = detailed.Today;
                sevenDayTokens = detailed.SevenDay;
            }
            else
            {
                diagnostics.Add("Token 统计已回退到线程更新时间估算");
            }

            return new LocalUsage(
                todayTokens,
                sevenDayTokens,
                lifetimeTokens,
                activeTasks,
                pendingTasks,
                databasePath,
                detailed is not null);
        }
        catch (Exception exception) when (exception is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            diagnostics.Add($"Codex 本地数据库读取失败：{exception.Message}");
            return null;
        }
    }

    private static (int Active, int Pending) ReadTaskCounts(
        WinSqliteDatabase database,
        IReadOnlySet<string?> columns,
        DateTimeOffset now,
        DateTimeOffset dayStart)
    {
        if (!columns.Contains("updated_at"))
        {
            return (0, 0);
        }

        var lastActiveExpression = columns.Contains("recency_at")
            ? "CASE WHEN COALESCE(recency_at, 0) > 0 THEN recency_at ELSE updated_at END"
            : "updated_at";
        var filters = new List<string> { $"{lastActiveExpression} >= {dayStart.ToUnixTimeSeconds()}" };
        if (columns.Contains("archived"))
        {
            filters.Add("COALESCE(archived, 0) = 0");
        }

        if (columns.Contains("preview"))
        {
            filters.Add("TRIM(COALESCE(preview, '')) <> ''");
        }

        var sql = $"SELECT {lastActiveExpression} AS lastActiveAt FROM threads WHERE {string.Join(" AND ", filters)};";
        var rows = database.Query(sql);
        var activeThreshold = now.AddHours(-2);
        var active = rows.Count(row => ToDateTimeOffset(row.GetValueOrDefault("lastActiveAt")) >= activeThreshold);
        return (active, Math.Max(0, rows.Count - active));
    }

    private static string? ResolveDatabasePath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Path.Combine(home, ".codex", "state_5.sqlite"),
            Path.Combine(home, ".codex", "sqlite", "state_5.sqlite"),
        }.FirstOrDefault(File.Exists);
    }

    private static long ToInt64(object? value) => value switch
    {
        long number => number,
        int number => number,
        double number => checked((long)number),
        string text when long.TryParse(text, out var number) => number,
        _ => 0,
    };

    private static DateTimeOffset ToDateTimeOffset(object? value)
    {
        var seconds = ToInt64(value);
        if (seconds <= 0)
        {
            return DateTimeOffset.MinValue;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
