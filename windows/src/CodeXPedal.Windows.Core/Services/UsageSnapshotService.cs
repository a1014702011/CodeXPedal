// [AI修改] 单一快照出口：平台只读取数据，成功/失败替换与时间保留调用共享核心。
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class UsageSnapshotService : IDisposable
{
    private readonly CodexAppServerClient _appServer = new();
    private readonly CodexLocalRateLimitReader _localRateLimits = new();
    private readonly CodexLocalUsageReader _codexLocal = new();
    private readonly ClaudeLocalUsageReader _claudeLocal = new();
    private readonly ClaudeQuotaReader _claudeQuota = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private NativeQuotaSnapshot _lastQuota;
    private QuotaDataSource _lastSource;
    private bool _resetClaudeThrottle;

    public Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken) => RefreshAsync(false, cancellationToken);

    // [AI修改] 每次刷新先探测安装情况：未安装的工具不启动进程、不读取数据，界面也不显示对应分组。
    public async Task<UsageSnapshot> RefreshAsync(bool claudeLiveQuota, CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _resetClaudeThrottle, false)) { _claudeQuota.ResetLiveThrottle(); }
            var (codexDetected, claudeDetected) = ProviderDetection.Detect();
            var diagnostics = new List<string>();
            var claude = claudeDetected
                ? await _claudeQuota.ReadAsync(claudeLiveQuota, DateTimeOffset.UtcNow, diagnostics, cancellationToken).ConfigureAwait(false)
                : ClaudeQuotaSnapshot.Empty;
            cancellationToken.ThrowIfCancellationRequested();
            var showCodex = codexDetected || !claudeDetected;
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
            _lastQuota = SharedQuotaCore.Apply(_lastQuota, incoming, incoming.AbiVersion == 1 ? 0 : 1);
            var windows = SharedQuotaCore.Windows(_lastQuota);
            var observed = _lastQuota.AbiVersion == 1
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)(_lastQuota.ObservedAt * 1000D)) : (DateTimeOffset?)null;
            if (failed && observed is not null) { diagnostics.Add("显示上次有效快照，≈不代表实时额度"); }
            var now = DateTimeOffset.Now;
            return new UsageSnapshot(now, windows.FiveHour, windows.SevenDay,
                observed is null ? QuotaDataSource.Unavailable : _lastSource, observed,
                showCodex ? _codexLocal.Read(now, diagnostics) : null,
                claudeDetected ? _claudeLocal.Read(now, diagnostics) : null, diagnostics)
            {
                QuotaReadFailed = failed,
                ClaudeQuota = claude,
                CodexDetected = codexDetected,
                ClaudeDetected = claudeDetected,
            };
        }
        finally { _refreshGate.Release(); }
    }
    // [AI修改] Claude 登录完成后由界面调用，使下一次刷新立即读取实时额度；只置标志，实际重置在刷新锁内完成，避免跨线程改时间。
    public void ResetClaudeLiveThrottle() => Volatile.Write(ref _resetClaudeThrottle, true);

    public void Dispose()
    {
        // [AI修改] 退出只回收本应用CLI；异步finally仍可能释放信号量，不提前销毁。
        _appServer.Dispose();
        _claudeQuota.Dispose();
    }
}
