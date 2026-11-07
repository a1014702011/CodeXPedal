// [AI修改] 为悬浮窗、任务栏和详情页提供一致的显示规则。
using System.Globalization;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Formatting;

public static class UsageFormatter
{
    public static string Percent(double? value) => value is null
        ? "--"
        : $"{Math.Round(value.Value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%";

    public static string Tokens(long? value)
    {
        if (value is null)
        {
            return "--";
        }

        var count = value.Value;
        if (count >= 1_000_000)
        {
            return $"{(count / 1_000_000D).ToString(count >= 10_000_000 ? "0.0" : "0.00", CultureInfo.InvariantCulture)}M";
        }

        if (count >= 1_000)
        {
            return $"{(count / 1_000D).ToString(count >= 100_000 ? "0" : "0.0", CultureInfo.InvariantCulture)}K";
        }

        return count.ToString(CultureInfo.InvariantCulture);
    }

    public static string Clock(DateTimeOffset value) => value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    // [AI修改] 只选择未来的真实重置时间，跨日显示日期，避免把旧时间误当成下一次。
    public static string ResetTime(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset || reset <= now) { return "--"; }
        var local = reset.ToLocalTime();
        var today = now.ToLocalTime();
        var format = local.Date == today.Date ? "HH:mm" : local.Year == today.Year ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm";
        return local.ToString(format, CultureInfo.InvariantCulture);
    }

    public static (string Label, string Value) QuotaTime(QuotaTimeMode mode, DateTimeOffset? observedAt,
        QuotaWindow? fiveHour, QuotaWindow? sevenDay, DateTimeOffset now)
    {
        if (mode != QuotaTimeMode.NextReset)
        {
            return ("最近获取", observedAt is { } at ? Clock(at) : "--:--:--");
        }

        var five = fiveHour?.ResetsAt;
        var seven = sevenDay?.ResetsAt;
        if (five > now && (!(seven > now) || five <= seven)) { return ("5时重置", ResetTime(five, now)); }
        return seven > now ? ("7天重置", ResetTime(seven, now)) : ("下次重置", "--");
    }
}
