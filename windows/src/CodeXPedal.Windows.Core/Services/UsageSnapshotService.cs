// [AI修改] 单一快照出口：平台只读取数据，成功/失败替换与时间保留调用共享核心。
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

public sealed class UsageSnapshotService : IDisposable
{
    private readonly CodexAppServerClient _appServer = new();
    private readonly CodexLocalRateLimitReader _localRateLimits = new();
    private readonly CodexLocalUsageReader _codexLocal = new();
    private readonly ClaudeLocalUsageReader _claudeLocal = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private NativeQuotaSnapshot _lastQuota;
    private QuotaDataSource _lastSource;

    public async Task<UsageSnapshot> RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var quota = await _appServer.ReadRateLimitsAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostics = new List<string>(quota.Diagnostics);
            var incoming = quota.Native;
            var failed = !quota.IsAuthoritative;
            if (!failed) { _lastSource = QuotaDataSource.OfficialAppServer; }
            else
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
                _codexLocal.Read(now, diagnostics), _claudeLocal.Read(now, diagnostics), diagnostics)
            { QuotaReadFailed = failed };
        }
        finally { _refreshGate.Release(); }
    }
    public void Dispose()
    {
        // [AI修改] 退出只回收本应用CLI；异步finally仍可能释放信号量，不提前销毁。
        _appServer.Dispose();
    }
}
