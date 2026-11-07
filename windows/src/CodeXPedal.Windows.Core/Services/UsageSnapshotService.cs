// [AI修改] 单一快照出口：平台只读取数据，成功/失败替换与时间保留调用共享核心。
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class UsageSnapshotService : IDisposable
{
    private readonly CodexAppServerClient _appServer = new();
    private readonly CodexLocalRateLimitReader _localRateLimits = new();
    private readonly Func<DateTimeOffset, ICollection<string>, LocalUsage?> _readCodexLocal;
    private readonly ClaudeLocalUsageReader _claudeLocal = new();
    private readonly ClaudeQuotaReader _claudeQuota = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private NativeQuotaSnapshot _lastQuota;
    private QuotaDataSource _lastSource;
    private bool _resetClaudeThrottle;

    // [AI修改] 测试可隔离本机 Token 数据库，仍走生产额度刷新流程和真实 CLI 协议。
    public UsageSnapshotService() : this(new CodexLocalUsageReader().Read) { }
    internal UsageSnapshotService(Func<DateTimeOffset, ICollection<string>, LocalUsage?> readCodexLocal) => _readCodexLocal = readCodexLocal;

    // [AI修改] 在调用任何探测、CLI、凭据或本地读取前检查设置。
    public async Task<UsageSnapshot> RefreshAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!settings.CodexEnabled) { await _appServer.StopSessionAsync().ConfigureAwait(false); }
            var now = DateTimeOffset.Now;
            if (!settings.HasEnabledProvider)
            {
                return UsageSnapshot.Empty(now) with { CodexDetected = false, Diagnostics = [] };
            }

            // [AI修改] 保留分组诊断，以便在已有快照上即时隐藏关闭的来源。
            var diagnostics = new List<string>();
            var claudeDiagnostics = new List<string>();
            var claudeDetected = settings.ClaudeEnabled && ClaudeExecutableResolver.IsInstalled();
            if (claudeDetected && Interlocked.Exchange(ref _resetClaudeThrottle, false)) { _claudeQuota.ResetLiveThrottle(); }
            cancellationToken.ThrowIfCancellationRequested();
            var claude = claudeDetected
                ? await _claudeQuota.ReadAsync(settings.ClaudeLiveQuota, DateTimeOffset.UtcNow, claudeDiagnostics, cancellationToken).ConfigureAwait(false)
                : ClaudeQuotaSnapshot.Empty;
            if (settings.ClaudeEnabled && !claudeDetected) { claudeDiagnostics.Add("Claude：未检测到 Claude Code，请安装并登录。"); }
            cancellationToken.ThrowIfCancellationRequested();
            var showCodex = settings.CodexEnabled;
            var quota = showCodex
                ? await _appServer.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false)
                : new RateLimitParseResult(false, null, null, []);
            cancellationToken.ThrowIfCancellationRequested();
            diagnostics.InsertRange(0, quota.Diagnostics);
            var incoming = quota.Native;
            var failed = showCodex && !quota.IsAuthoritative;
            if (showCodex && !failed) { _lastSource = QuotaDataSource.OfficialAppServer; }
            else if (failed)
            {
                var local = _localRateLimits.Read(DateTimeOffset.UtcNow, diagnostics);
                // [AI修改] 旧本地事件不能覆盖更新的官方快照，不跨事件拼接。
                if (local is not null && local.ObservedAt.ToUnixTimeMilliseconds() / 1000D > _lastQuota.ObservedAt)
                {
                    incoming = SharedQuotaCore.FromWindows(local.FiveHour, local.SevenDay, local.ObservedAt);
                    _lastSource = QuotaDataSource.LocalSession;
                }
            }
            // [AI修改] 关闭 Codex 时不处理或返回历史缓存，重新开启仍遵守原快照时间。
            cancellationToken.ThrowIfCancellationRequested();
            if (showCodex) { _lastQuota = SharedQuotaCore.Apply(_lastQuota, incoming, incoming.AbiVersion == 1 ? 0 : 1); }
            var windows = SharedQuotaCore.Windows(showCodex ? _lastQuota : default);
            var observed = showCodex && _lastQuota.AbiVersion == 1
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(_lastQuota.ObservedAt * 1000D)) : (DateTimeOffset?)null;
            if (failed && observed is not null) { diagnostics.Add("显示上次有效快照，≈不代表实时额度"); }
            now = DateTimeOffset.Now;
            var codexLocal = showCodex ? _readCodexLocal(now, diagnostics) : null;
            cancellationToken.ThrowIfCancellationRequested();
            var claudeLocal = claudeDetected ? _claudeLocal.Read(now, claudeDiagnostics) : null;
            cancellationToken.ThrowIfCancellationRequested();
            return new UsageSnapshot(now, windows.FiveHour, windows.SevenDay,
                observed is null ? QuotaDataSource.Unavailable : _lastSource, observed,
                codexLocal, claudeLocal, [.. diagnostics, .. claudeDiagnostics])
            {
                CodexDiagnostics = diagnostics,
                ClaudeDiagnostics = claudeDiagnostics,
                QuotaReadFailed = failed,
                ClaudeQuota = claude,
                CodexDetected = showCodex && (!quota.IsStandaloneCliUnavailable || observed is not null),
                ClaudeDetected = claudeDetected,
            };
        }
        finally { _refreshGate.Release(); }
    }
    // [AI修改] Claude 登录完成后由界面调用，使下一次刷新立即读取实时额度；只置标志，实际重置在刷新锁内完成，避免跨线程改时间。
    public void ResetClaudeLiveThrottle() => Volatile.Write(ref _resetClaudeThrottle, true);

    // [AI修改] 即使两项都关闭（不进入刷新），也回收闲置的官方子进程。
    public Task StopCodexSessionAsync() => _appServer.StopSessionAsync();

    public void Dispose()
    {
        // [AI修改] 退出只回收本应用CLI；异步finally仍可能释放信号量，不提前销毁。
        _appServer.Dispose();
        _claudeQuota.Dispose();
    }
}
