// [AI修改] Windows只负责ABI编组；额度与状态规则全部调用shared/的实际Rust库。
using System.Runtime.InteropServices;
using System.Text;
using CodeXPedal.Windows.Core.Models;

namespace CodeXPedal.Windows.Core.Services;

[StructLayout(LayoutKind.Sequential)]
internal struct NativeQuotaWindow
{
    public uint DurationMinutes;
    public uint HasReset;
    public double UsedPercent;
    public double ResetsAt;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeQuotaSnapshot
{
    public uint AbiVersion;
    public uint WindowCount;
    public double ObservedAt;
    public NativeQuotaWindow First;
    public NativeQuotaWindow Second;
}

public static class SharedQuotaCore
{
    private const string Library = "codexpedal_core";
    static SharedQuotaCore()
    {
        if (AbiVersion() != 1 || Marshal.SizeOf<NativeQuotaWindow>() != 24 || Marshal.SizeOf<NativeQuotaSnapshot>() != 64)
        {
            throw new InvalidOperationException("共享核心版本或数据布局不兼容，请完整重新解压软件包。");
        }
    }
    [DllImport(Library, EntryPoint = "pedal_abi_version", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint AbiVersion();
    [DllImport(Library, EntryPoint = "pedal_parse", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ParseNative(byte[] input, nuint length, double observedAt, uint mode, out NativeQuotaSnapshot output);
    [DllImport(Library, EntryPoint = "pedal_remaining", CallingConvention = CallingConvention.Cdecl)]
    public static extern double Remaining(double usedPercent);
    [DllImport(Library, EntryPoint = "pedal_is_stale", CallingConvention = CallingConvention.Cdecl)]
    private static extern uint IsStaleNative(in NativeQuotaSnapshot snapshot, double now, double refreshSeconds, uint failed);
    [DllImport(Library, EntryPoint = "pedal_apply", CallingConvention = CallingConvention.Cdecl)]
    private static extern int ApplyNative(in NativeQuotaSnapshot previous, in NativeQuotaSnapshot incoming, int status, out NativeQuotaSnapshot output);

    internal static int Parse(string json, DateTimeOffset observedAt, bool local, out NativeQuotaSnapshot snapshot)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        return ParseNative(bytes, (nuint)bytes.Length, observedAt.ToUnixTimeMilliseconds() / 1000D, local ? 1U : 0U, out snapshot);
    }
    internal static NativeQuotaSnapshot Apply(NativeQuotaSnapshot previous, NativeQuotaSnapshot incoming, int status)
    {
        _ = ApplyNative(previous, incoming, status, out var snapshot);
        return snapshot;
    }
    internal static (QuotaWindow? FiveHour, QuotaWindow? SevenDay) Windows(NativeQuotaSnapshot snapshot)
    {
        QuotaWindow? five = null, seven = null;
        foreach (var raw in new[] { snapshot.First, snapshot.Second }.Take((int)snapshot.WindowCount))
        {
            var kind = raw.DurationMinutes == 300 ? QuotaWindowKind.FiveHour : QuotaWindowKind.SevenDay;
            var window = new QuotaWindow(kind, raw.UsedPercent, (int)raw.DurationMinutes,
                raw.HasReset == 1 ? DateTimeOffset.FromUnixTimeMilliseconds((long)(raw.ResetsAt * 1000D)) : null);
            if (kind == QuotaWindowKind.FiveHour) { five = window; } else { seven = window; }
        }
        return (five, seven);
    }
    internal static NativeQuotaSnapshot FromWindows(QuotaWindow? five, QuotaWindow? seven, DateTimeOffset? observedAt)
    {
        if (observedAt is null) { return default; }
        var windows = new[] { five, seven }.Where(window => window is not null).Select(window => new NativeQuotaWindow
        {
            DurationMinutes = (uint)window!.DurationMinutes, UsedPercent = window.UsedPercent,
            HasReset = window.ResetsAt is null ? 0U : 1U,
            ResetsAt = (window.ResetsAt?.ToUnixTimeMilliseconds() ?? 0) / 1000D,
        }).ToArray();
        return new NativeQuotaSnapshot { AbiVersion = 1, WindowCount = (uint)windows.Length,
            ObservedAt = observedAt.Value.ToUnixTimeMilliseconds() / 1000D,
            First = windows.ElementAtOrDefault(0), Second = windows.ElementAtOrDefault(1) };
    }
    public static bool IsStale(UsageSnapshot snapshot, DateTimeOffset now, double refreshSeconds) =>
        IsStaleNative(FromWindows(snapshot.FiveHourQuota, snapshot.SevenDayQuota, snapshot.QuotaObservedAt),
            now.ToUnixTimeMilliseconds() / 1000D, refreshSeconds, snapshot.QuotaReadFailed ? 1U : 0U) != 0;

    internal static string ErrorMessage(int code) => code switch
    {
        2 => "官方响应没有Codex额度桶，本次未更新", 3 => "额度窗口重复，本次未更新",
        4 => "额度百分比超出有效范围，本次未更新", 5 => "额度响应超过安全上限，本次未更新",
        _ => "额度响应格式无效，本次未更新",
    };
}
