// [AI修改] 将真实快照转换成悬浮窗、任务栏和详情页共用的可观察属性。
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodeXPedal.Windows.Core.Formatting;
using CodeXPedal.Windows.Core.Models;
using CodeXPedal.Windows.Core.Services;

namespace CodeXPedal.Windows.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private UsageSnapshot _snapshot = UsageSnapshot.Empty(DateTimeOffset.Now);
    private AppSettings _settings;
    private bool _isRefreshing;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings.Normalize();
        // [AI修改] 首次刷新前按默认快照显示 Codex 行，避免悬浮窗空白。
        UpdateRows();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? SnapshotChanged;

    public event EventHandler? SettingsChanged;

    public UsageSnapshot Snapshot => _snapshot;

    public AppSettings Settings => _settings;

    // [AI修改] 5 小时额度是否显示完全由官方窗口拓扑决定，不猜测用户订阅等级。
    // [AI修改] 所有窗口、任务栏、托盘共用陈旧标记和原成功时间。
    private bool QuotaStale => SharedQuotaCore.IsStale(_snapshot, DateTimeOffset.UtcNow, _settings.RefreshIntervalSeconds);
    public string FiveHourQuota => QuotaText(_snapshot.FiveHourQuota);

    public bool HasFiveHourQuota => _snapshot.FiveHourQuota is not null;

    public bool HasNoFiveHourQuota => !HasFiveHourQuota;

    public string SevenDayQuota => QuotaText(_snapshot.SevenDayQuota);

    public string RefreshedAt => _snapshot.QuotaObservedAt is { } at ? UsageFormatter.Clock(at) : "--:--:--";
    private string QuotaText(QuotaWindow? window) => window is null ? "--" : (QuotaStale ? "≈" : "") + UsageFormatter.Percent(window.RemainingPercent);

    public string TodayTokens => UsageFormatter.Tokens(_snapshot.CodexLocal?.TodayTokens);

    public string SevenDayTokens => UsageFormatter.Tokens(_snapshot.CodexLocal?.SevenDayTokens);

    public string LifetimeTokens => UsageFormatter.Tokens(_snapshot.CodexLocal?.LifetimeTokens);

    public string ActiveTasks => _snapshot.CodexLocal?.ActiveTasks.ToString() ?? "--";

    public string PendingTasks => _snapshot.CodexLocal?.PendingTasks.ToString() ?? "--";

    public string ClaudeTodayTokens => UsageFormatter.Tokens(_snapshot.ClaudeLocal?.TodayTokens);

    // [AI修改] 已勾选但暂时读不到也保留占位；关闭后所有入口同时隐藏。
    public const string NoProviderText = "没有可获取的 AI";
    public bool ShowCodex => _settings.CodexEnabled;
    public bool ShowClaude => _settings.ClaudeEnabled;
    public bool NoProviders => !_settings.HasEnabledProvider;
    public bool CanRefresh => !NoProviders && !IsRefreshing;
    public bool ShowResetTimes => _settings.QuotaTimeMode == QuotaTimeMode.NextReset;
    public bool ShowObservedTimes => !ShowResetTimes;
    public bool ShowCodexObservedTime => ShowCodex && ShowObservedTimes;

    public bool ShowBoth => ShowCodex && ShowClaude;

    public bool ShowCodexTokens => IsFull && ShowCodex;

    public string HeaderSubtitle => NoProviders ? "剩余额度" : ShowBoth ? "Codex · Claude · 剩余额度" : ShowClaude ? "Claude · 剩余额度" : "Codex · 剩余额度";

    public ProviderQuotaViewModel CodexRow { get; } = new("Codex");

    public ProviderQuotaViewModel ClaudeRow { get; } = new("Claude");

    // [AI修改] Claude 额度使用独立快照和同一共享陈旧规则，不借用 Codex 的采样时间。
    private bool ClaudeStale => SharedQuotaCore.IsStale(_snapshot.ClaudeQuota, DateTimeOffset.UtcNow, _settings.RefreshIntervalSeconds);

    public string ClaudeFiveHourQuota => ClaudeRow.FiveHourQuota;

    public bool HasClaudeFiveHourQuota => ClaudeRow.HasFiveHourQuota;

    public string ClaudeSevenDayQuota => ClaudeRow.SevenDayQuota;

    public string ClaudeRefreshedAt => ClaudeRow.RefreshedAt;

    public string ClaudeQuotaSourceLabel => _snapshot.ClaudeQuota.Source switch
    {
        ClaudeQuotaSource.LiveOAuth => ClaudeStale ? "Claude 额度来源：官方实时接口历史快照（非实时）" : "Claude 额度来源：官方实时接口",
        ClaudeQuotaSource.StatusLine => $"Claude 额度来源：Claude Code 状态栏（{ClaudeRow.RefreshedAt}{(ClaudeStale ? "，已过期" : string.Empty)}）",
        _ => "Claude 额度来源：不可用",
    };

    // [AI修改] 将额度来源与错误诊断分开呈现，避免本地快照被误认为读取失败。
    public string QuotaSourceLabel => _snapshot.QuotaSource switch
    {
        QuotaDataSource.OfficialAppServer => QuotaStale ? "额度来源：官方历史快照（非实时）" : "额度来源：官方Codex实时接口",
        QuotaDataSource.LocalSession => $"额度来源：本机会话快照（{UsageFormatter.Clock(_snapshot.QuotaObservedAt ?? _snapshot.RefreshedAt)}）",
        _ => "额度来源：不可用",
    };

    // [AI修改] 这一行统计只来自 Codex 本地数据，文案明确标注 Codex，避免与 Claude 混淆。
    public string TokenSourceLabel => _snapshot.CodexLocal is null
        ? "Codex Token 统计：本地数据不可用"
        : _snapshot.CodexLocal.UsesDetailedSessionEvents
            ? "Codex Token 统计：今日/7天按会话记录 · 累计按本机任务汇总"
            : "Codex Token 统计：按本机任务更新时间汇总";

    public string Diagnostics
    {
        get
        {
            if (NoProviders) { return NoProviderText; }
            // [AI修改] 关闭后不暴露旧的对应来源诊断，等待首次有效读取时也不误称正常。
            var notes = (ShowCodex ? _snapshot.CodexDiagnostics : [])
                .Concat(ShowClaude ? _snapshot.ClaudeDiagnostics : []).ToArray();
            if (notes.Length > 0) { return string.Join(Environment.NewLine, notes); }
            if (_snapshot.QuotaObservedAt is null && _snapshot.ClaudeQuota.ObservedAt is null
                && _snapshot.Diagnostics.Count > 0) { return "等待额度数据"; }
            return "数据读取正常";
        }
    }

    public bool IsCompact => _settings.CompactMode;

    public bool IsFull => !_settings.CompactMode;

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set
        {
            if (SetField(ref _isRefreshing, value)) { OnPropertyChanged(nameof(CanRefresh)); }
        }
    }

    public void ApplySnapshot(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseSnapshotProperties();
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ApplySettings(AppSettings settings)
    {
        _settings = settings.Normalize();
        OnPropertyChanged(nameof(Settings));
        OnPropertyChanged(nameof(IsCompact));
        OnPropertyChanged(nameof(IsFull));
        // [AI修改] 设置改变即更新所有输出，不依赖后台请求成功。
        RaiseSnapshotProperties();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        SnapshotChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleCompact() => ApplySettings(_settings with { CompactMode = !_settings.CompactMode });

    public void UpdateWindowPosition(double left, double top)
    {
        _settings = _settings with { WindowLeft = left, WindowTop = top };
    }

    // [AI修改] 两行额度在同一处按快照刷新，所有入口读到的是同一份结果。
    private void UpdateRows()
    {
        var now = DateTimeOffset.Now;
        CodexRow.Update(ShowCodex, ShowBoth, _snapshot.FiveHourQuota, _snapshot.SevenDayQuota, _snapshot.QuotaObservedAt,
            QuotaStale, _settings.QuotaTimeMode, now);
        var claude = _snapshot.ClaudeQuota;
        // [AI修改] Claude 订阅（Pro/Max）都有 5 小时与 7 天窗口，未读到数据前两格都显示 “--”。
        ClaudeRow.Update(ShowClaude, ShowClaude, claude.FiveHourQuota, claude.SevenDayQuota, claude.ObservedAt,
            ClaudeStale, _settings.QuotaTimeMode, now, placeholderBothWhenEmpty: true);
    }

    private void RaiseSnapshotProperties()
    {
        UpdateRows();
        OnPropertyChanged(nameof(Snapshot));
        OnPropertyChanged(nameof(ShowCodex));
        OnPropertyChanged(nameof(ShowClaude));
        OnPropertyChanged(nameof(ShowBoth));
        OnPropertyChanged(nameof(NoProviders));
        OnPropertyChanged(nameof(CanRefresh));
        OnPropertyChanged(nameof(ShowResetTimes));
        OnPropertyChanged(nameof(ShowObservedTimes));
        OnPropertyChanged(nameof(ShowCodexObservedTime));
        OnPropertyChanged(nameof(ShowCodexTokens));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(ClaudeFiveHourQuota));
        OnPropertyChanged(nameof(HasClaudeFiveHourQuota));
        OnPropertyChanged(nameof(ClaudeSevenDayQuota));
        OnPropertyChanged(nameof(ClaudeRefreshedAt));
        OnPropertyChanged(nameof(ClaudeQuotaSourceLabel));
        OnPropertyChanged(nameof(FiveHourQuota));
        OnPropertyChanged(nameof(HasFiveHourQuota));
        OnPropertyChanged(nameof(HasNoFiveHourQuota));
        OnPropertyChanged(nameof(SevenDayQuota));
        OnPropertyChanged(nameof(RefreshedAt));
        OnPropertyChanged(nameof(TodayTokens));
        OnPropertyChanged(nameof(SevenDayTokens));
        OnPropertyChanged(nameof(LifetimeTokens));
        OnPropertyChanged(nameof(ActiveTasks));
        OnPropertyChanged(nameof(PendingTasks));
        OnPropertyChanged(nameof(ClaudeTodayTokens));
        OnPropertyChanged(nameof(QuotaSourceLabel));
        OnPropertyChanged(nameof(TokenSourceLabel));
        OnPropertyChanged(nameof(Diagnostics));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
