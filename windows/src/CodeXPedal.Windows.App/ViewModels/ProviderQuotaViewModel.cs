// [AI修改] 单个工具（Codex 或 Claude）一行额度的显示模型，悬浮窗两行共用同一模板，避免重复布局。
using System.ComponentModel;
using System.Windows;
using CodeXPedal.Windows.Core.Formatting;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.App.ViewModels;

public sealed class ProviderQuotaViewModel(string label) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Label { get; } = label;

    public bool IsVisible { get; private set; }

    // [AI修改] 只显示 Codex 时保持原有外观；出现 Claude 后两行都加名称标签以便区分。
    public bool ShowLabel { get; private set; }

    public string FiveHourQuota { get; private set; } = "--";

    public bool HasFiveHourQuota { get; private set; }

    public string SevenDayQuota { get; private set; } = "--";

    public string RefreshedAt { get; private set; } = "--:--:--";
    // [AI修改] 同一行同时给紧凑栏和详情提供已选时间，以及各周期真实重置时间。
    public string TimeLabel { get; private set; } = "最近获取";
    public string TimeValue { get; private set; } = "--:--:--";
    public string StripTime { get; private set; } = "--:--:--";
    public string FiveHourReset { get; private set; } = "--";
    public string SevenDayReset { get; private set; } = "--";

    // [AI修改] 没有 5 小时窗口时 7 天成为首列，去掉其左侧分隔线。
    public Thickness SevenDayBorder => HasFiveHourQuota ? new Thickness(1, 0, 0, 0) : new Thickness(0);

    // [AI修改] placeholderBothWhenEmpty：尚无任何成功数据时同时显示 5 小时和 7 天的 “--”，
    // 避免把“还没读到”误显示成“没有 5 小时窗口”；读到数据后仍按官方返回的窗口决定。
    public void Update(bool visible, bool showLabel, QuotaWindow? fiveHour, QuotaWindow? sevenDay, DateTimeOffset? observedAt, bool stale,
        QuotaTimeMode timeMode, DateTimeOffset now, bool placeholderBothWhenEmpty = false)
    {
        IsVisible = visible;
        ShowLabel = showLabel;
        HasFiveHourQuota = fiveHour is not null || (placeholderBothWhenEmpty && observedAt is null);
        FiveHourQuota = Text(fiveHour, stale);
        SevenDayQuota = Text(sevenDay, stale);
        RefreshedAt = observedAt is { } at ? UsageFormatter.Clock(at) : "--:--:--";
        // [AI修改] 紧凑模式只显示一项重置时间，详情仍可分别显示两个周期。
        (TimeLabel, TimeValue) = UsageFormatter.QuotaTime(timeMode, observedAt, fiveHour, sevenDay, now);
        StripTime = timeMode == QuotaTimeMode.NextReset ? $"{TimeLabel} {TimeValue}" : TimeValue;
        FiveHourReset = UsageFormatter.ResetTime(fiveHour?.ResetsAt, now);
        SevenDayReset = UsageFormatter.ResetTime(sevenDay?.ResetsAt, now);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    private static string Text(QuotaWindow? window, bool stale) =>
        window is null ? "--" : (stale ? "≈" : string.Empty) + UsageFormatter.Percent(window.RemainingPercent);
}
