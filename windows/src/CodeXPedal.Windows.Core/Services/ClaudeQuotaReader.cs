// [AI修改] Claude 剩余额度读取：默认只读状态栏桥接缓存；用户显式开启后才读取本机登录令牌调用官方 usage 接口。
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

// [AI修改] 状态栏桥接缓存：Claude Code 每次刷新状态栏时由本程序写入，只含 rate_limits，不含会话内容。
public static class ClaudeStatusLineCache
{
    public const string FileName = "codexpedal-rate-limits.json";
    internal const int MaximumBytes = 1_048_576;

    public static string PathFor(string configDirectory) => Path.Combine(configDirectory, FileName);

    // [AI修改] 采样时间取文件修改时间，宿主不解析业务字段；文件过大或不可读时返回 false。
    internal static bool TryRead(string configDirectory, out string json, out DateTimeOffset modifiedAt)
    {
        json = string.Empty;
        modifiedAt = default;
        var path = PathFor(configDirectory);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumBytes)
            {
                return false;
            }

            modifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            json = BoundedLocalText.ReadSnapshot(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public sealed record ClaudeUsageResponse(string? Json, string? Error);

// [AI修改] 官方 usage 接口（Claude Code /usage 同源，未公开文档）。令牌只在内存中使用一次，不记录、不缓存、不代为刷新。
public sealed class ClaudeOAuthUsageClient : IDisposable
{
    private const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBeta = "oauth-2025-04-20";
    private const int MaximumCredentialBytes = 65_536;
    private const int MaximumResponseBytes = 1_048_576;
    private readonly HttpClient _http;

    public ClaudeOAuthUsageClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
    }

    public async Task<ClaudeUsageResponse> ReadAsync(string configDirectory, CancellationToken cancellationToken)
    {
        var token = LoadToken(configDirectory, DateTimeOffset.UtcNow, out var tokenError);
        if (token is null)
        {
            return new(null, tokenError);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBeta);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("CodeXPedal/1.0");
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(null, "Claude 登录令牌无效或已过期，请打开一次 Claude Code 后重试");
            }

            if ((int)response.StatusCode == 429)
            {
                return new(null, "Claude 额度接口请求过于频繁，稍后自动重试");
            }

            if (!response.IsSuccessStatusCode)
            {
                return new(null, $"Claude 额度接口返回 HTTP {(int)response.StatusCode}");
            }

            // [AI修改] 响应按上限分块读取，超过上限立即放弃，不把截断内容交给解析器。
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[MaximumResponseBytes + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
            }

            return total > MaximumResponseBytes
                ? new(null, "Claude 额度响应超过安全上限")
                : new(Encoding.UTF8.GetString(buffer, 0, total), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(null, "Claude 额度接口超时，请检查网络");
        }
        catch (HttpRequestException)
        {
            return new(null, "Claude 额度接口网络失败，请检查网络");
        }
    }

    // [AI修改] 只取 accessToken 与过期时间；环境变量 CLAUDE_CODE_OAUTH_TOKEN 优先，与 Claude Code 行为一致。
    internal static string? LoadToken(string configDirectory, DateTimeOffset now, out string? error)
    {
        error = null;
        var environmentToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken.Trim();
        }

        var path = Path.Combine(configDirectory, ".credentials.json");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaximumCredentialBytes)
            {
                error = "未找到 Claude 登录信息，请先用 Claude Code 登录 Pro/Max 账号";
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!document.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var tokenElement)
                || tokenElement.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(tokenElement.GetString()))
            {
                error = "Claude 登录信息不是订阅账号，无法读取额度";
                return null;
            }

            if (oauth.TryGetProperty("expiresAt", out var expires) && expires.TryGetInt64(out var expiresAt)
                && DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(expiresAt, 0, 253_402_300_799_000)) <= now)
            {
                // [AI修改] 不使用刷新令牌，避免与 Claude Code 争用导致对方登录失效。
                error = "Claude 登录令牌已过期，请打开一次 Claude Code 让其自动刷新";
                return null;
            }

            return tokenElement.GetString()!.Trim();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            error = "Claude 登录信息读取失败";
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

// [AI修改] Claude 额度状态：只接受通过共享核心校验的新结果，失败保留旧值与旧采样时间。
public sealed class ClaudeQuotaReader : IDisposable
{
    // [AI修改] 官方接口最少间隔 60 秒，避免短刷新间隔触发限流。
    private static readonly TimeSpan MinimumLiveInterval = TimeSpan.FromSeconds(60);
    private readonly ClaudeOAuthUsageClient _live;
    private readonly Func<string> _configDirectory;
    private NativeQuotaSnapshot _last;
    private ClaudeQuotaSource _lastSource;
    private DateTimeOffset _lastLiveAttempt = DateTimeOffset.MinValue;
    private string? _lastLiveError;

    public ClaudeQuotaReader(ClaudeOAuthUsageClient? live = null, Func<string>? configDirectory = null)
    {
        _live = live ?? new ClaudeOAuthUsageClient();
        _configDirectory = configDirectory ?? ClaudeExecutableResolver.ConfigDirectory;
    }

    public async Task<ClaudeQuotaSnapshot> ReadAsync(bool liveEnabled, DateTimeOffset now, ICollection<string> diagnostics, CancellationToken cancellationToken)
    {
        var configDirectory = _configDirectory();
        var incoming = default(NativeQuotaSnapshot);
        var source = _lastSource;
        var failed = false;
        if (liveEnabled)
        {
            if (now - _lastLiveAttempt >= MinimumLiveInterval || _lastLiveAttempt > now)
            {
                _lastLiveAttempt = now;
                var response = await _live.ReadAsync(configDirectory, cancellationToken).ConfigureAwait(false);
                _lastLiveError = response.Error;
                if (response.Json is not null)
                {
                    var status = SharedQuotaCore.ParseClaude(response.Json, now, out var parsed);
                    if (status == 0) { incoming = parsed; source = ClaudeQuotaSource.LiveOAuth; }
                    else { _lastLiveError = SharedQuotaCore.ClaudeErrorMessage(status); }
                }
            }

            failed = _lastLiveError is not null;
            if (failed) { diagnostics.Add(_lastLiveError!); }
        }

        // [AI修改] 未开启实时或实时失败时，比当前快照更新的状态栏缓存才可替换，旧缓存不能覆盖新结果。
        if (incoming.AbiVersion != 1
            && ClaudeStatusLineCache.TryRead(configDirectory, out var json, out var modifiedAt)
            && modifiedAt.ToUnixTimeMilliseconds() / 1000D > _last.ObservedAt)
        {
            var status = SharedQuotaCore.ParseClaude(json, modifiedAt, out var parsed);
            if (status == 0) { incoming = parsed; source = ClaudeQuotaSource.StatusLine; failed = false; }
            else { diagnostics.Add("状态栏缓存：" + SharedQuotaCore.ClaudeErrorMessage(status)); }
        }

        _last = SharedQuotaCore.Apply(_last, incoming, incoming.AbiVersion == 1 ? 0 : 1);
        if (_last.AbiVersion != 1)
        {
            if (!liveEnabled)
            {
                diagnostics.Add("Claude 额度：尚无数据。请在设置中启用“Claude 状态栏桥接”后在 Claude Code 里发一条消息，或开启实时读取。");
            }

            return ClaudeQuotaSnapshot.Empty with { ReadFailed = failed };
        }

        _lastSource = source;
        var windows = SharedQuotaCore.Windows(_last);
        var observed = DateTimeOffset.FromUnixTimeMilliseconds((long)(_last.ObservedAt * 1000D));
        return new ClaudeQuotaSnapshot(windows.FiveHour, windows.SevenDay, source, observed, failed);
    }

    public void Dispose() => _live.Dispose();
}
