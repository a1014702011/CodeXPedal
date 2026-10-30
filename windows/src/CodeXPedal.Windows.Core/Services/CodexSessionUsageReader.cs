// [AI修改] 解析本地 rollout JSONL 的 token_count 事件，并去除分叉会话继承的事件前缀。
using System.Globalization;
using System.Text.Json;
using CodeXPedal.Windows.Core.Infrastructure;

namespace CodeXPedal.Windows.Core.Services;

internal sealed record DetailedTokenUsage(long Today, long SevenDay, int ParsedFiles, int Events);

internal sealed class CodexSessionUsageReader
{
    private readonly Dictionary<string, CachedSession> _cache = new(StringComparer.OrdinalIgnoreCase);

    public DetailedTokenUsage? Read(
        WinSqliteDatabase database,
        IReadOnlySet<string?> columns,
        DateTimeOffset now,
        ICollection<string> diagnostics)
    {
        if (!columns.Contains("id") || !columns.Contains("rollout_path"))
        {
            diagnostics.Add("Codex threads 表缺少 session 日志路径");
            return null;
        }

        var localNow = now.ToLocalTime();
        var dayStart = new DateTimeOffset(localNow.Date, localNow.Offset);
        var sevenDayStart = dayStart.AddDays(-6);
        var sources = database.Query(
                "SELECT id, rollout_path AS rolloutPath, updated_at AS updatedAt FROM threads WHERE rollout_path IS NOT NULL AND rollout_path <> '' AND COALESCE(tokens_used, 0) > 0;")
            .Select(row => new SessionSource(
                Convert.ToString(row.GetValueOrDefault("id"), CultureInfo.InvariantCulture) ?? string.Empty,
                Convert.ToString(row.GetValueOrDefault("rolloutPath"), CultureInfo.InvariantCulture) ?? string.Empty,
                ToDateTimeOffset(row.GetValueOrDefault("updatedAt"))))
            .Where(source => source.ThreadId.Length > 0 && File.Exists(source.Path))
            .DistinctBy(source => source.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sources.Length == 0)
        {
            diagnostics.Add("未找到 Codex session 日志");
            return null;
        }

        var sourceById = sources.ToDictionary(source => source.ThreadId, StringComparer.OrdinalIgnoreCase);
        var recentSources = sources.Where(source => source.UpdatedAt >= sevenDayStart).ToArray();
        // [AI修改] 不用截断后的事件假装完整统计，超上限由上层明确回退到线程估算。
        if (sources.Length > 256 || recentSources.Length > 32)
        {
            diagnostics.Add("本地会话数量超过精细统计上限，改用线程汇总估算");
            _cache.Clear();
            return null;
        }
        var sessions = new Dictionary<string, CachedSession>(StringComparer.OrdinalIgnoreCase);
        var lockedFileCount = 0;
        foreach (var source in recentSources)
        {
            sessions[source.ThreadId] = ReadSessionOrEmpty(source, ref lockedFileCount);
        }

        // [AI修改] 只为分叉前缀去重按需读取父会话，避免全量扫描全部历史 JSONL。
        foreach (var session in sessions.Values.ToArray())
        {
            if (!string.IsNullOrWhiteSpace(session.ForkedFromId)
                && !sessions.ContainsKey(session.ForkedFromId)
                && sourceById.TryGetValue(session.ForkedFromId, out var parentSource))
            {
                sessions[session.ForkedFromId] = ReadSessionOrEmpty(parentSource, ref lockedFileCount);
            }
        }

        if (lockedFileCount > 0)
        {
            diagnostics.Add($"有 {lockedFileCount} 个Codex会话因过大或无法读取暂未计入");
        }

        long today = 0;
        long sevenDay = 0;
        var parsedFiles = 0;
        var eventCount = 0;

        foreach (var source in recentSources)
        {
            var session = sessions[source.ThreadId];
            if (session.Events.Count == 0)
            {
                continue;
            }

            parsedFiles++;
            var inheritedPrefix = 0;
            if (!string.IsNullOrWhiteSpace(session.ForkedFromId)
                && sessions.TryGetValue(session.ForkedFromId, out var parent))
            {
                var upperBound = Math.Min(session.Events.Count, parent.Events.Count);
                while (inheritedPrefix < upperBound
                    && session.Events[inheritedPrefix].Identity == parent.Events[inheritedPrefix].Identity)
                {
                    inheritedPrefix++;
                }
            }

            foreach (var tokenEvent in session.Events.Skip(inheritedPrefix))
            {
                eventCount++;
                if (tokenEvent.Timestamp >= sevenDayStart)
                {
                    sevenDay += tokenEvent.Tokens;
                }

                if (tokenEvent.Timestamp >= dayStart)
                {
                    today += tokenEvent.Tokens;
                }
            }
        }

        if (eventCount == 0)
        {
            diagnostics.Add("未找到 Codex token_count 事件");
            return null;
        }

        return new DetailedTokenUsage(today, sevenDay, parsedFiles, eventCount);
    }

    private CachedSession ReadSessionOrEmpty(SessionSource source, ref int lockedFileCount)
    {
        try
        {
            return ReadSession(source);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lockedFileCount++;
            return new CachedSession(0, DateTime.MinValue, null, []);
        }
    }

    private CachedSession ReadSession(SessionSource source)
    {
        var info = new FileInfo(source.Path);
        if (_cache.TryGetValue(source.Path, out var cached)
            && cached.FileLength == info.Length
            && cached.LastWriteTimeUtc == info.LastWriteTimeUtc)
        {
            return cached;
        }

        var events = new List<TokenEvent>();
        string? forkedFromId = null;
        var state = new CodexTokenCounterState();
        // [AI修改] 固定本次读取快照，文件持续追加或超大行不能导致无限增长。
        using var reader = new StringReader(BoundedLocalText.ReadSnapshot(source.Path));
        while (reader.ReadLine() is { } line)
        {
            var mayBeMetadata = line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal)
                || line.Contains("\"type\": \"session_meta\"", StringComparison.Ordinal);
            var mayBeToken = line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal)
                || line.Contains("\"type\": \"token_count\"", StringComparison.Ordinal);
            if (!mayBeMetadata && !mayBeToken)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("payload", out var payload)
                    || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (mayBeMetadata
                    && root.TryGetProperty("type", out var rootType)
                    && rootType.GetString() == "session_meta")
                {
                    if (payload.TryGetProperty("forked_from_id", out var parent))
                    {
                        forkedFromId = parent.GetString();
                    }

                    continue;
                }

                if (!payload.TryGetProperty("type", out var payloadType)
                    || payloadType.GetString() != "token_count"
                    || !root.TryGetProperty("timestamp", out var timestampElement)
                    || !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp)
                    || !payload.TryGetProperty("info", out var infoElement))
                {
                    continue;
                }

                var cumulative = ReadSample(infoElement, "total_token_usage");
                var lastUsage = ReadSample(infoElement, "last_token_usage");
                var delta = CodexTokenCounterNormalizer.Consume(cumulative, lastUsage, state);
                if (delta is > 0)
                {
                    if (events.Count >= 4096) { throw new IOException("会话事件超过统计上限"); }
                    events.Add(new TokenEvent(timestamp.ToLocalTime(), delta.Value, EventIdentity(cumulative, lastUsage)));
                }
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException)
            {
                // [AI修改] rollout 末行可能在写入中，跳过单行而不是放弃整个统计。
            }
        }

        cached = new CachedSession(info.Length, info.LastWriteTimeUtc, forkedFromId, events);
        // [AI修改] 缓存随长期运行有固定容量，达到上限重新取样，不积累全部历史。
        if (_cache.Count >= 64) { _cache.Clear(); }
        _cache[source.Path] = cached;
        return cached;
    }

    private static CodexTokenSample? ReadSample(JsonElement info, string propertyName)
    {
        if (info.ValueKind != JsonValueKind.Object
            || !info.TryGetProperty(propertyName, out var usage)
            || usage.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new CodexTokenSample(
            ReadInt64(usage, "input_tokens"),
            ReadInt64(usage, "output_tokens"),
            ReadInt64(usage, "total_tokens"));
    }

    private static long? ReadInt64(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string EventIdentity(CodexTokenSample? cumulative, CodexTokenSample? lastUsage) =>
        $"{cumulative?.InputTokens}:{cumulative?.OutputTokens}:{cumulative?.TotalTokens}|{lastUsage?.InputTokens}:{lastUsage?.OutputTokens}:{lastUsage?.TotalTokens}";

    private static DateTimeOffset ToDateTimeOffset(object? value)
    {
        var seconds = value switch
        {
            long number => number,
            int number => number,
            string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        };
        try
        {
            return seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime() : DateTimeOffset.MinValue;
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    private sealed record SessionSource(string ThreadId, string Path, DateTimeOffset UpdatedAt);

    private sealed record TokenEvent(DateTimeOffset Timestamp, long Tokens, string Identity);

    private sealed record CachedSession(
        long FileLength,
        DateTime LastWriteTimeUtc,
        string? ForkedFromId,
        IReadOnlyList<TokenEvent> Events);
}
