// [AI修改] 将 Codex token_count 累计计数器转换为单次增量，避免重复累计整段会话。
namespace CodeXPedal.Windows.Core.Services;

public sealed record CodexTokenSample(long? InputTokens, long? OutputTokens, long? TotalTokens)
{
    public bool HasNegativeValue => new[] { InputTokens, OutputTokens, TotalTokens }
        .Where(value => value.HasValue)
        .Any(value => value < 0);

    public long VisibleTotal => Math.Max(TotalTokens ?? 0, (InputTokens ?? 0) + (OutputTokens ?? 0));
}

public sealed class CodexTokenCounterState
{
    public CodexTokenSample? Cumulative { get; set; }
}

public static class CodexTokenCounterNormalizer
{
    public static long? Consume(CodexTokenSample? cumulative, CodexTokenSample? lastUsage, CodexTokenCounterState state)
    {
        var lastDelta = Valid(lastUsage) ? lastUsage!.VisibleTotal : 0;
        if (!Valid(cumulative))
        {
            return lastDelta > 0 ? lastDelta : null;
        }

        var current = cumulative!;
        if (state.Cumulative is null)
        {
            state.Cumulative = current;
            var first = lastDelta > 0 ? lastDelta : current.VisibleTotal;
            return first > 0 ? first : null;
        }

        var previous = state.Cumulative;
        var isReset = current.TotalTokens.HasValue
            && current.InputTokens.HasValue
            && previous.TotalTokens.HasValue
            && previous.InputTokens.HasValue
            && current.TotalTokens < previous.TotalTokens
            && current.InputTokens < previous.InputTokens;
        if (isReset)
        {
            state.Cumulative = current;
            var resetDelta = lastDelta > 0 ? lastDelta : current.VisibleTotal;
            return resetDelta > 0 ? resetDelta : null;
        }

        var highWater = new CodexTokenSample(
            Math.Max(previous.InputTokens ?? 0, current.InputTokens ?? previous.InputTokens ?? 0),
            Math.Max(previous.OutputTokens ?? 0, current.OutputTokens ?? previous.OutputTokens ?? 0),
            Math.Max(previous.TotalTokens ?? 0, current.TotalTokens ?? previous.TotalTokens ?? 0));
        var fallback = Math.Max(0, highWater.VisibleTotal - previous.VisibleTotal);
        state.Cumulative = highWater;
        if (fallback == 0)
        {
            return null;
        }

        return lastDelta > 0 ? lastDelta : fallback;
    }

    private static bool Valid(CodexTokenSample? sample) => sample is not null && !sample.HasNegativeValue;
}
