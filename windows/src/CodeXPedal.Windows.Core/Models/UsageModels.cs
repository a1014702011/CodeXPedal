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
