// [AI修改] 集中保存 Windows 界面配置，默认不占用任务栏空间。
namespace CodeXPedal.Windows.Core.Models;

// [AI修改] 额度时间仅影响显示，不改变采样和定时读取。
public enum QuotaTimeMode { Observed, NextReset }

public sealed record AppSettings
{
    // [AI修改] 旧配置缺少字段时继续默认启用；显式 false 始终保留。
    public bool CodexEnabled { get; init; } = true;
    public bool ClaudeEnabled { get; init; } = true;
    public QuotaTimeMode QuotaTimeMode { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasEnabledProvider => CodexEnabled || ClaudeEnabled;

    public bool CompactMode { get; init; }

    public bool AlwaysOnTop { get; init; } = true;

    public bool ShowTaskbarStrip { get; init; }

    // [AI修改] 仅任务栏模式隐藏悬浮窗，但始终保留任务栏信息栏作为入口。
    public bool TaskbarOnlyMode { get; init; }

    public bool StartWithWindows { get; init; }

    public int RefreshIntervalSeconds { get; init; } = 60;

    // [AI修改] 实时读取是默认的 Claude 额度来源（用户已确认），需要读取本机 Claude 登录令牌；配置中已保存的选择保持不变。
    public bool ClaudeLiveQuota { get; init; } = true;

    // [AI修改] 用户在“安装并登录 Claude Code”弹窗中选择“不再提示”后不再在启动时询问。
    public bool ClaudeSetupPromptDismissed { get; init; }

    public double WindowLeft { get; init; } = double.NaN;

    public double WindowTop { get; init; } = double.NaN;

    // [AI修改] 归一化刷新范围，并避免仅任务栏模式被手工配置成没有可见入口。
    public AppSettings Normalize() => this with
    {
        // [AI修改] 损坏的时间模式回到最近获取时间。
        QuotaTimeMode = QuotaTimeMode == QuotaTimeMode.NextReset ? QuotaTimeMode.NextReset : QuotaTimeMode.Observed,
        ShowTaskbarStrip = TaskbarOnlyMode || ShowTaskbarStrip,
        RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 15, 3600),
    };
}
