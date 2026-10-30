// [AI修改] 为悬浮窗、任务栏和详情页提供一致的显示规则。
using System.Globalization;

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
}
