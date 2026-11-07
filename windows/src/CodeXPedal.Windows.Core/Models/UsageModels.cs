// [AI修改] 定义 Windows 客户端统一的数据快照；按官方返回的窗口自动显示 5 小时和 7 天额度。
namespace CodeXPedal.Windows.Core.Models;

public enum QuotaWindowKind
{
    FiveHour,
    SevenDay,
}

// [AI修改] 明确区分官方实时接口、本地快照和不可用状态，避免把正常兜底显示成错误。
public enum QuotaDataSource
{
    Unavailable,
    OfficialAppServer,
    LocalSession,
}

// [AI修改] Claude 额度来源：默认调用官方实时接口，状态栏桥接缓存作为备选。
public enum ClaudeQuotaSource
{
    Unavailable,
    LiveOAuth,
    StatusLine,
}

public sealed record QuotaWindow(
    QuotaWindowKind Kind,
    double UsedPercent,
    int DurationMinutes,
    DateTimeOffset? ResetsAt)
{
    // [AI修改] 剩余计算与边界语义仅在共享Rust核心定义。
    public double RemainingPercent => Services.SharedQuotaCore.Remaining(UsedPercent);
}

public sealed record LocalUsage(
    long TodayTokens,
    long SevenDayTokens,
    long LifetimeTokens,
    int ActiveTasks,
    int PendingTasks,
    string DatabasePath,
    bool UsesDetailedSessionEvents);

public sealed record ClaudeUsage(
    long TodayTokens,
    int TaskCount,
    int ParsedFileCount);

// [AI修改] Claude 额度与 Codex 额度各自独立保存采样时间和失败状态，界面不拼接两家数据。
public sealed record ClaudeQuotaSnapshot(
    QuotaWindow? FiveHourQuota,
    QuotaWindow? SevenDayQuota,
    ClaudeQuotaSource Source,
    DateTimeOffset? ObservedAt,
    bool ReadFailed)
{
    public static ClaudeQuotaSnapshot Empty { get; } = new(null, null, ClaudeQuotaSource.Unavailable, null, false);
}

public sealed record UsageSnapshot(
    DateTimeOffset RefreshedAt,
    QuotaWindow? FiveHourQuota,
    QuotaWindow? SevenDayQuota,
    QuotaDataSource QuotaSource,
    DateTimeOffset? QuotaObservedAt,
    LocalUsage? CodexLocal,
    ClaudeUsage? ClaudeLocal,
    IReadOnlyList<string> Diagnostics)
{
    // [AI修改] 失败与来源分别记录，旧官方快照不能被重新标为实时。
    public bool QuotaReadFailed { get; init; }
    // [AI修改] 两家诊断分开保存，取消显示时不用等待新快照就能隐藏对应诊断。
    public IReadOnlyList<string> CodexDiagnostics { get; init; } = [];
    public IReadOnlyList<string> ClaudeDiagnostics { get; init; } = [];
    // [AI修改] 安装探测只影响数据可用性，界面可见性由设置决定。
    public ClaudeQuotaSnapshot ClaudeQuota { get; init; } = ClaudeQuotaSnapshot.Empty;
    public bool CodexDetected { get; init; } = true;
    public bool ClaudeDetected { get; init; }
    // [AI修改] 初始快照不把未知数据误报为 0。
    public static UsageSnapshot Empty(DateTimeOffset now) => new(
        now,
        null,
        null,
        QuotaDataSource.Unavailable,
        null,
        null,
        null,
        ["等待首次刷新"]);
}
