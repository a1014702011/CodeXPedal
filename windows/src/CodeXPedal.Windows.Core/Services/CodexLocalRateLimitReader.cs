// [AI修改] 只适配Windows文件共享读取；额度规则统一调用Rust核心。
using System.Globalization;
using System.Text;
using System.Text.Json;
using CodeXPedal.Windows.Core.Models;
namespace CodeXPedal.Windows.Core.Services;

public sealed record LocalRateLimitSnapshot(QuotaWindow? FiveHour, QuotaWindow? SevenDay, DateTimeOffset ObservedAt);
public sealed class CodexLocalRateLimitReader(string? sessionRoot = null)
{
    private const int MaximumTailBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, CachedSample> _cache = new(StringComparer.OrdinalIgnoreCase);
    public LocalRateLimitSnapshot? Read(DateTimeOffset now, ICollection<string> diagnostics)
    {
        var directory = sessionRoot ?? ResolveSessionRoot();
        if (directory is null) { diagnostics.Add("未找到Codex sessions目录，本地额度不可用"); return null; }
        try
        {
            // [AI修改] 枚举/缓存有数量上限，不进入重解析目录。
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            var files = Directory.EnumerateFiles(directory, "*.jsonl", options).Take(4096).Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc).Take(24).ToArray();
            var selected = files.Select(file => file.FullName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var key in _cache.Keys.Where(key => !selected.Contains(key)).ToArray()) { _cache.Remove(key); }
            LocalRateLimitSnapshot? latest = null;
            foreach (var file in files)
            {
                try { var sample = ReadLatest(file); if (sample is not null && (latest is null || sample.ObservedAt > latest.ObservedAt)) { latest = sample; } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { diagnostics.Add("本地额度文件暂时无法共享读取"); }
            }
            if (latest is null) { diagnostics.Add("本地session未找到有效额度快照"); return null; }
            if (new[] { latest.FiveHour, latest.SevenDay }.Any(window => window?.ResetsAt is { } reset && reset <= now))
            { diagnostics.Add("本地session的额度快照已过期"); return null; }
            return latest;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { diagnostics.Add("本地额度目录无法读取"); return null; }
    }
    private LocalRateLimitSnapshot? ReadLatest(FileInfo file)
    {
        file.Refresh();
        if (_cache.TryGetValue(file.FullName, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc) { return cached.Sample; }
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - MaximumTailBytes);
        stream.Seek(start, SeekOrigin.Begin);
        // [AI修改] 冻结读取长度，持续追加的文件也不能突破4MiB。
        var bytes = new byte[(int)(length - start)];
        var count = 0;
        while (count < bytes.Length) { var read = stream.Read(bytes, count, bytes.Length - count); if (read == 0) { break; } count += read; }
        using var reader = new StringReader(Encoding.UTF8.GetString(bytes, 0, count));
        if (start > 0) { _ = reader.ReadLine(); }
        LocalRateLimitSnapshot? latest = null;
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 1_048_576 || !line.Contains("\"rate_limits\"", StringComparison.Ordinal)) { continue; }
            var sample = TryParse(line);
            if (sample is not null && (latest is null || sample.ObservedAt > latest.ObservedAt)) { latest = sample; }
        }
        _cache[file.FullName] = new CachedSample(file.Length, file.LastWriteTimeUtc, latest);
        return latest;
    }
    private static LocalRateLimitSnapshot? TryParse(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "event_msg"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object
                || !payload.TryGetProperty("type", out var payloadType) || payloadType.GetString() != "token_count"
                || !payload.TryGetProperty("rate_limits", out var limits)
                || !root.TryGetProperty("timestamp", out var timestamp) || timestamp.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(timestamp.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var observedAt)) { return null; }
            var parsed = RateLimitParser.Parse(limits, observedAt, true);
            return parsed.IsAuthoritative ? new LocalRateLimitSnapshot(parsed.FiveHour, parsed.SevenDay, observedAt) : null;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return null; }
    }
    private static string? ResolveSessionRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        return new[] { string.IsNullOrWhiteSpace(configured) ? null : Path.Combine(configured, "sessions"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions") }
            .FirstOrDefault(path => path is not null && Directory.Exists(path));
    }
    private sealed record CachedSample(long Length, DateTime Modified, LocalRateLimitSnapshot? Sample);
}
