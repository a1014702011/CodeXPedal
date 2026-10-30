// [AI修改] 删除重复解析器，官方和本地响应均交给共享核心。
using System.Text.Json;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed record RateLimitParseResult(bool IsAuthoritative, QuotaWindow? FiveHour, QuotaWindow? SevenDay,
    IReadOnlyList<string> Diagnostics, bool IsStandaloneCliUnavailable = false)
{
    internal NativeQuotaSnapshot Native { get; init; }
}
public static class RateLimitParser
{
    public const int FiveHourMinutes = 300;
    public const int SevenDayMinutes = 10080;
    public static RateLimitParseResult Parse(JsonElement result) => Parse(result, DateTimeOffset.UtcNow, false);
    internal static RateLimitParseResult Parse(JsonElement result, DateTimeOffset observedAt, bool local)
    {
        var status = SharedQuotaCore.Parse(result.GetRawText(), observedAt, local, out var native);
        if (status != 0) { return new(false, null, null, [SharedQuotaCore.ErrorMessage(status)]); }
        var windows = SharedQuotaCore.Windows(native);
        return new(true, windows.FiveHour, windows.SevenDay, []) { Native = native };
    }
}
