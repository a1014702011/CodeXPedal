// [AI修改] 使用无第三方依赖的红绿自测覆盖额度分类、缺失值和显示格式。
using System.Text.Json;
using System.Runtime.InteropServices;
using CodeXPedal.Windows.Core.Formatting;
using CodeXPedal.Windows.Core.Models;
using CodeXPedal.Windows.Core.Services;

// [AI修改] 测试进程可模拟官方 stdio app-server，验证客户端跨刷新复用同一子进程。
if (args.Contains("app-server", StringComparer.OrdinalIgnoreCase))
{
    return await RunFakeAppServerAsync();
}

// [AI修改] 官方额度探针验证自动发现的 CLI 和 app-server，只输出额度汇总与诊断。
if (args.Contains("--probe-official", StringComparer.OrdinalIgnoreCase))
{
    using var client = new CodexAppServerClient();
    var quota = await client.ReadRateLimitsAsync(CancellationToken.None);
    Console.WriteLine($"官方读取：{(quota.IsAuthoritative ? "可用" : "不可用")}");
    Console.WriteLine($"5 小时剩余：{UsageFormatter.Percent(quota.FiveHour?.RemainingPercent)}");
    Console.WriteLine($"7 天剩余：{UsageFormatter.Percent(quota.SevenDay?.RemainingPercent)}");
    foreach (var diagnostic in quota.Diagnostics)
    {
        Console.WriteLine($"诊断：{diagnostic}");
    }

    return quota.IsAuthoritative ? 0 : 1;
}

// [AI修改] 可选本地探针只输出汇总和诊断，不泄露线程标题、工作区或会话内容。
if (args.Contains("--probe-local", StringComparer.OrdinalIgnoreCase))
{
    var diagnostics = new List<string>();
    var usage = new CodexLocalUsageReader().Read(DateTimeOffset.Now, diagnostics);
    // [AI修改] 同时验证官方 CLI 不可用时的本地动态额度兜底。
    var quota = new CodexLocalRateLimitReader().Read(DateTimeOffset.Now, diagnostics);
    Console.WriteLine($"本地读取：{(usage is null ? "不可用" : "可用")}");
    Console.WriteLine($"5 小时剩余：{UsageFormatter.Percent(quota?.FiveHour?.RemainingPercent)}");
    Console.WriteLine($"7 天剩余：{UsageFormatter.Percent(quota?.SevenDay?.RemainingPercent)}");
    if (usage is not null)
    {
        Console.WriteLine($"详细事件：{usage.UsesDetailedSessionEvents}");
        Console.WriteLine($"今日/7天/累计：{usage.TodayTokens}/{usage.SevenDayTokens}/{usage.LifetimeTokens}");
        Console.WriteLine($"活跃/待处理：{usage.ActiveTasks}/{usage.PendingTasks}");
    }

    foreach (var diagnostic in diagnostics)
    {
        Console.WriteLine($"诊断：{diagnostic}");
    }

    return 0;
}

var tests = new (string Name, Action Run)[]
{
    ("识别 Pro 单 7 天额度", RecognizesProSevenDay),
    ("识别 Plus 双额度", RecognizesPlusFiveHourAndSevenDay),
    // [AI修改] 先固定跨平台语义差异，重构前必须观察这些用例真实失败。
    ("多桶缺失Codex不回退旧桶", RejectsMissingPreferredBucket),
    ("异常百分比不当成有效额度", RejectsOutOfRangePercent),
    ("单5小时是有效拓扑", AcceptsFiveHourOnly),
    ("明确空窗口是有效响应", AcceptsExplicitEmpty),
    ("真实共享ABI失败保留原时间", SharedFailurePreservesSnapshot),
    ("真实共享ABI成功替换拓扑", SharedSuccessReplacesTopology),
    ("真实共享ABI陈旧边界", SharedStalenessBoundary),
    ("忽略已移除的月窗口", IgnoresRemovedMonthlyWindow),
    ("复用官方 app-server 进程", UsesPersistentOfficialAppServer),
    ("官方失败不能返回缓存成功", () => WithFakeScenario("fail-second", client => { Assert(client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult().IsAuthoritative, "首次成功"); Assert(!client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult().IsAuthoritative, "第二次失败必须失败上报"); })),
    ("官方超长输出失败关闭", () => WithFakeScenario("oversize", client => Assert(!client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult().IsAuthoritative, "超长响应应拒绝"))),
    ("官方错误JSON失败关闭", () => WithFakeScenario("malformed", client => Assert(!client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult().IsAuthoritative, "错误JSON应拒绝"))),
    ("官方用户取消回收进程", CancelsOfficialRead),
    ("重复 7 天窗口失败关闭", RejectsDuplicateWindows),
    ("重复 5 小时窗口失败关闭", RejectsDuplicateFiveHourWindows),
    ("格式错误失败关闭", RejectsMalformedWindow),
    ("本地 Plus 快照识别双额度", ReadsLocalPlusSnapshot),
    ("本地 Pro 新快照不拼接旧额度", ReadsLocalProSnapshotWithoutMixing),
    ("本地畸形快照不导致崩溃", RejectsMalformedLocalSnapshot),
    ("本地过期快照不显示", RejectsExpiredLocalSnapshot),
    ("缺失值显示短横线", FormatsMissingValues),
    ("初始额度来源不可用", InitializesQuotaSource),
    ("更新时间精确到秒", FormatsClockToSeconds),
    ("设置范围归一化", NormalizesSettings),
    ("仅任务栏模式保留入口", NormalizesTaskbarOnlyMode),
    ("任务栏字体随高度放大", ScalesTaskbarFont),
    ("任务栏宽度按 DPI 换算", MeasuresTaskbarWidth),
    ("累计 Token 转换为增量", NormalizesTokenCounter),
    ("Token 计数器重置不重复", HandlesTokenCounterReset),
    ("本地文件读取冻结且有界", ReadsBoundedLocalText),
    ("超大统计文件明确拒绝", RejectsOversizedLocalText),
    // [AI修改] Claude 额度：共享核心解析、状态栏桥接、实时接口开关、路径发现和双行任务栏。
    ("Claude 状态栏额度双窗口", ParsesClaudeStatusLine),
    ("Claude 官方接口ISO时间与超额截断", ParsesClaudeOAuthUsage),
    ("Claude 缺失窗口失败关闭", RejectsClaudeMissingWindows),
    ("Claude 状态栏钩子写缓存并输出", ClaudeHookWritesCache),
    ("Claude 钩子无额度时不覆盖缓存", ClaudeHookKeepsCacheWithoutLimits),
    ("Claude 读取较新缓存且失败保留", ClaudeReaderUsesNewerCache),
    ("Claude 实时读取默认开启且保留用户选择", ClaudeLiveDefaultsOn),
    ("Claude 未开启实时不读取令牌", ClaudeLiveDisabledSkipsNetwork),
    ("Claude 实时接口成功替换快照", ClaudeLiveReadsUsage),
    ("Claude 过期令牌不发请求", ClaudeExpiredTokenSkipsNetwork),
    ("Claude 桥接备份并恢复原状态栏", ClaudeBridgeRoundTrip),
    ("Claude 路径发现顺序", ResolvesClaudeExecutable),
    ("Claude 陈旧规则与 Codex 一致", ClaudeStalenessBoundary),
    ("双行任务栏字号与宽度", ScalesDualTaskbarLayout),
    ("Claude Code 安装登录状态判断", DetectsClaudeCliStatus),
    ("Claude Code 安装脚本路径转义", BuildsClaudeSetupScript),
};

var failures = new List<string>();
// [AI修改] 记录真实执行数/失败数和逐项耗时；CI留存结构化报告。
var results = new List<object>();
foreach (var test in tests)
{
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    try
    {
        test.Run();
        Console.WriteLine($"通过：{test.Name}");
        results.Add(new { name = test.Name, passed = true, elapsedMs = stopwatch.ElapsedMilliseconds });
    }
    catch (Exception exception)
    {
        failures.Add($"失败：{test.Name} - {exception.Message}");
        results.Add(new { name = test.Name, passed = false, elapsedMs = stopwatch.ElapsedMilliseconds, error = exception.Message });
    }
}

var reportPath = Environment.GetEnvironmentVariable("CODEXPEDAL_TEST_REPORT");
if (!string.IsNullOrWhiteSpace(reportPath))
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(new { executed = tests.Length, passed = tests.Length - failures.Count, failed = failures.Count, skipped = 0, results }, new JsonSerializerOptions { WriteIndented = true }));
}

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine($"全部通过：{tests.Length} 项");
return 0;

static void RecognizesProSevenDay()
{
    var result = Parse("""
        {"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":22,"windowDurationMins":10080,"resetsAt":1770000000},"secondary":null}}}
        """);
    Assert(result.IsAuthoritative, "7 天窗口应该可信");
    Assert(result.FiveHour is null, "Pro 单窗口不能显示 5 小时空占位");
    AssertEqual(78D, result.SevenDay?.RemainingPercent, "7 天剩余额度");
}

// [AI修改] 覆盖两端已确认的分歧，测试调用实际生产解析器，不复制业务实现。
static void RejectsMissingPreferredBucket()
{
    var result = Parse("""{"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":10080}},"rateLimitsByLimitId":{"other":{"primary":null}}}""");
    Assert(!result.IsAuthoritative, "多桶视图存在时不能从旧桶伪造Codex额度");
}

static void RejectsOutOfRangePercent()
{
    foreach (var used in new[] { -1, 101 })
    {
        var result = Parse(JsonSerializer.Serialize(new { rateLimits = new { primary = new { usedPercent = used, windowDurationMins = 10080 } } }));
        Assert(!result.IsAuthoritative, "超出0至100必须拒绝整份快照");
    }
}

static void AcceptsFiveHourOnly()
{
    var result = Parse("""{"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":300},"secondary":null}}""");
    Assert(result.IsAuthoritative && result.FiveHour is not null && result.SevenDay is null, "不能强制要求7天窗口");
}

static void AcceptsExplicitEmpty()
{
    var result = Parse("""{"rateLimits":{"primary":null,"secondary":null}}""");
    Assert(result.IsAuthoritative && result.FiveHour is null && result.SevenDay is null, "官方明确空响应不能当连接错误");
}

// [AI修改] 从生产解析器获得真实Rust快照，再通过生产P/Invoke调用状态接口。
static void SharedFailurePreservesSnapshot()
{
    var initial = Parse("""{"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":10080}}}""");
    var kept = SharedQuotaCore.Apply(initial.Native, default, 1);
    AssertEqual(initial.Native.ObservedAt, kept.ObservedAt, "失败不能刷新采样时间");
    AssertEqual(7D, kept.First.UsedPercent, "失败保留同一整份旧快照");
}
static void SharedSuccessReplacesTopology()
{
    var old = Parse("""{"rateLimits":{"primary":{"usedPercent":24,"windowDurationMins":300},"secondary":{"usedPercent":7,"windowDurationMins":10080}}}""");
    var fresh = Parse("""{"rateLimits":{"primary":{"usedPercent":8,"windowDurationMins":10080}}}""");
    var replaced = SharedQuotaCore.Apply(old.Native, fresh.Native, 0);
    AssertEqual(1U, replaced.WindowCount, "新成功结果移除不存在的5小时窗口");
    AssertEqual(10080U, replaced.First.DurationMinutes, "保留7天窗口");
}
static void SharedStalenessBoundary()
{
    var now = DateTimeOffset.UtcNow;
    var snapshot = UsageSnapshot.Empty(now) with { SevenDayQuota = new(QuotaWindowKind.SevenDay, 7, 10080, null), QuotaObservedAt = now };
    Assert(!SharedQuotaCore.IsStale(snapshot, now.AddSeconds(179), 60), "179秒未过期");
    Assert(SharedQuotaCore.IsStale(snapshot, now.AddSeconds(180), 60), "180秒边界过期");
    Assert(SharedQuotaCore.IsStale(snapshot with { QuotaReadFailed = true }, now, 60), "失败立即标旧值");
}

static void RecognizesPlusFiveHourAndSevenDay()
{
    // [AI修改] 300 分钟与 10080 分钟窗口同时存在时，按 Plus/Standard Business 拓扑显示双额度。
    var result = Parse("""
        {"rateLimits":{"primary":{"usedPercent":9,"windowDurationMins":300},"secondary":{"usedPercent":31,"windowDurationMins":10080}}}
        """);
    Assert(result.IsAuthoritative, "双额度拓扑应该可信");
    AssertEqual(91D, result.FiveHour?.RemainingPercent, "5 小时剩余额度");
    AssertEqual(69D, result.SevenDay?.RemainingPercent, "7 天剩余额度");
}

static void IgnoresRemovedMonthlyWindow()
{
    // [AI修改] 历史月窗口即使仍出现在旧响应中，也不能再进入当前额度模型。
    var result = Parse("""
        {"rateLimits":{"primary":{"usedPercent":7,"windowDurationMins":10080},"secondary":{"usedPercent":57,"windowDurationMins":43800}}}
        """);
    Assert(result.IsAuthoritative, "历史月窗口不应污染 7 天额度");
    AssertEqual(93D, result.SevenDay?.RemainingPercent, "7 天剩余额度");
}

static void RejectsDuplicateWindows()
{
    var result = Parse("""
        {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":10080},"secondary":{"usedPercent":20,"windowDurationMins":10080}}}
        """);
    Assert(!result.IsAuthoritative, "重复窗口必须失败关闭");
    Assert(result.SevenDay is null, "失败关闭不能残留部分额度");
}

static void RejectsDuplicateFiveHourWindows()
{
    // [AI修改] 重复短窗口属于协议异常，禁止挑一个值继续显示。
    var result = Parse("""
        {"rateLimits":{"primary":{"usedPercent":10,"windowDurationMins":300},"secondary":{"usedPercent":20,"windowDurationMins":300}}}
        """);
    Assert(!result.IsAuthoritative, "重复 5 小时窗口必须失败关闭");
    Assert(result.FiveHour is null, "失败关闭不能残留部分额度");
}

static void RejectsMalformedWindow()
{
    var result = Parse("""
        {"rateLimits":{"primary":{"windowDurationMins":10080},"secondary":null}}
        """);
    Assert(!result.IsAuthoritative, "缺少 usedPercent 必须失败关闭");
}

static void UsesPersistentOfficialAppServer()
{
    var previousPath = Environment.GetEnvironmentVariable("CODEXPEDAL_CODEX_PATH");
    var previousDotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
    var executable = Path.Combine(AppContext.BaseDirectory, "CodeXPedal.Windows.Tests.exe");
    var runtimeRoot = new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
    Assert(File.Exists(executable), "测试 apphost 不存在");
    Assert(!string.IsNullOrWhiteSpace(runtimeRoot), "无法定位测试使用的 .NET Runtime");

    try
    {
        Environment.SetEnvironmentVariable("CODEXPEDAL_CODEX_PATH", executable);
        Environment.SetEnvironmentVariable("DOTNET_ROOT", runtimeRoot);
        using var client = new CodexAppServerClient();
        var first = client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult();
        var second = client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult();
        Assert(
            first.IsAuthoritative && second.IsAuthoritative,
            $"两次官方响应都应可信；首次：{string.Join("；", first.Diagnostics)}；再次：{string.Join("；", second.Diagnostics)}");
        AssertEqual(93D, first.SevenDay?.RemainingPercent, "首次官方剩余额度");
        AssertEqual(92D, second.SevenDay?.RemainingPercent, "复用进程后的官方剩余额度");
    }
    finally
    {
        // [AI修改] 测试结束恢复用户环境，避免影响后续本地探针。
        Environment.SetEnvironmentVariable("CODEXPEDAL_CODEX_PATH", previousPath);
        Environment.SetEnvironmentVariable("DOTNET_ROOT", previousDotnetRoot);
    }
}

// [AI修改] 子进程是真实测试宿主，始终恢复测试注入环境，不改写用户配置。
static void WithFakeScenario(string scenario, Action<CodexAppServerClient> test)
{
    var names = new[] { "CODEXPEDAL_CODEX_PATH", "DOTNET_ROOT", "CODEXPEDAL_FAKE_SCENARIO" };
    var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
    try
    {
        Environment.SetEnvironmentVariable(names[0], Path.Combine(AppContext.BaseDirectory, "CodeXPedal.Windows.Tests.exe"));
        Environment.SetEnvironmentVariable(names[1], new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName);
        Environment.SetEnvironmentVariable(names[2], scenario);
        using var client = new CodexAppServerClient();
        test(client);
    }
    finally { for (var i = 0; i < names.Length; i++) { Environment.SetEnvironmentVariable(names[i], old[i]); } }
}
static void CancelsOfficialRead() => WithFakeScenario("silent", client =>
{
    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
    try { _ = client.ReadRateLimitsAsync(cancellation.Token).GetAwaiter().GetResult(); throw new InvalidOperationException("应收到取消"); }
    catch (OperationCanceledException) { }
});

// [AI修改] 本地额度回归使用隔离的真实 JSONL 文件，直接调用生产读取器而非复制解析逻辑。
static void ReadsLocalPlusSnapshot()
{
    WithLocalQuotaFixture((directory, now) =>
    {
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), LocalQuotaEvent(now, 12, 300, 7, 10080));
        var quota = new CodexLocalRateLimitReader(directory).Read(now, new List<string>());
        AssertEqual(88D, quota?.FiveHour?.RemainingPercent, "本地 5 小时剩余");
        AssertEqual(93D, quota?.SevenDay?.RemainingPercent, "本地 7 天剩余");
    });
}

static void ReadsLocalProSnapshotWithoutMixing()
{
    WithLocalQuotaFixture((directory, now) =>
    {
        var events = LocalQuotaEvent(now.AddMinutes(-1), 12, 300, 7, 10080)
            + Environment.NewLine + LocalQuotaEvent(now, 18, 10080, null, null);
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), events);
        var quota = new CodexLocalRateLimitReader(directory).Read(now, new List<string>());
        Assert(quota is not null, "最新 Pro 快照应可用");
        Assert(quota!.FiveHour is null, "不能从旧 Plus 事件补回 5 小时额度");
        AssertEqual(82D, quota.SevenDay?.RemainingPercent, "应选择最新事件");
    });
}

static void RejectsMalformedLocalSnapshot()
{
    WithLocalQuotaFixture((directory, now) =>
    {
        var valid = LocalQuotaEvent(now, 18, 10080, null, null);
        var malformed = valid.Replace("\"secondary\":null", "\"secondary\":\"bad\"");
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), malformed);
        var quota = new CodexLocalRateLimitReader(directory).Read(now, new List<string>());
        Assert(quota is null, "畸形拓扑必须拒绝，不能当作无 5 小时额度");
    });
}

static void RejectsExpiredLocalSnapshot()
{
    WithLocalQuotaFixture((directory, now) =>
    {
        File.WriteAllText(Path.Combine(directory, "session.jsonl"), LocalQuotaEvent(now.AddDays(-8), 18, 10080, null, null));
        var diagnostics = new List<string>();
        var quota = new CodexLocalRateLimitReader(directory).Read(now, diagnostics);
        Assert(quota is null, "过期额度不能显示为当前值");
        Assert(diagnostics.Any(message => message.Contains("已过期")), "应解释快照已过期");
    });
}

static string LocalQuotaEvent(DateTimeOffset timestamp, double firstUsed, int firstMinutes, double? secondUsed, int? secondMinutes)
{
    object Window(double used, int minutes) => new
    {
        used_percent = used,
        window_minutes = minutes,
        resets_at = timestamp.AddMinutes(minutes).ToUnixTimeSeconds(),
    };
    return JsonSerializer.Serialize(new
    {
        timestamp = timestamp.ToString("O"),
        type = "event_msg",
        payload = new
        {
            type = "token_count",
            rate_limits = new
            {
                limit_id = "codex",
                primary = Window(firstUsed, firstMinutes),
                secondary = secondUsed is { } used && secondMinutes is { } minutes ? Window(used, minutes) : null,
            },
        },
    });
}

static void WithLocalQuotaFixture(Action<string, DateTimeOffset> test)
{
    // [AI修改] 本机默认统一使用 F 盘临时目录；CI 可显式注入 runner 专用临时路径。
    var tempRoot = Environment.GetEnvironmentVariable("CODEXPEDAL_TEST_TEMP_ROOT") ?? @"F:\codex_tmp\codexpedal-tests";
    var directory = Path.Combine(tempRoot, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        test(directory, DateTimeOffset.UtcNow);
    }
    finally
    {
        // [AI修改] 仅清理本次测试生成的固定文件和空目录，不递归删除其他资料。
        File.Delete(Path.Combine(directory, "session.jsonl"));
        Directory.Delete(directory, recursive: false);
    }
}

static void FormatsMissingValues()
{
    AssertEqual("--", UsageFormatter.Percent(null), "缺失百分比");
    AssertEqual("--", UsageFormatter.Tokens(null), "缺失 Token");
}

// [AI修改] 用隔离文件直接验证生产读取器，而非仅检查常量存在。
static void ReadsBoundedLocalText() => WithLocalQuotaFixture((directory, _) =>
{
    var path = Path.Combine(directory, "session.jsonl");
    File.WriteAllText(path, "测试\nsecond");
    AssertEqual("测试\nsecond", BoundedLocalText.ReadSnapshot(path), "读取原文且不额外追加内容");
});
static void RejectsOversizedLocalText() => WithLocalQuotaFixture((directory, fixtureTime) =>
{
    var path = Path.Combine(directory, "session.jsonl");
    File.WriteAllText(path, new string('x', BoundedLocalText.MaximumBytes + 1));
    try { _ = BoundedLocalText.ReadSnapshot(path); throw new InvalidOperationException("超大文件应被拒绝"); }
    catch (IOException) { }
});

static void InitializesQuotaSource()
{
    // [AI修改] 首次刷新前不能把未知额度误标为官方或本地来源。
    var snapshot = UsageSnapshot.Empty(DateTimeOffset.Now);
    AssertEqual(QuotaDataSource.Unavailable, snapshot.QuotaSource, "初始额度来源");
    Assert(snapshot.QuotaObservedAt is null, "初始额度不能带采样时间");
}

static void FormatsClockToSeconds()
{
    // [AI修改] 用本地墙钟时间构造样例，避免先当 UTC 再换偏移导致小时变化。
    var localTime = new DateTime(2026, 7, 27, 16, 25, 42, DateTimeKind.Unspecified);
    var value = new DateTimeOffset(localTime, TimeZoneInfo.Local.GetUtcOffset(localTime));
    AssertEqual("16:25:42", UsageFormatter.Clock(value), "更新时间格式");
}

static void NormalizesSettings()
{
    AssertEqual(15, new AppSettings { RefreshIntervalSeconds = 1 }.Normalize().RefreshIntervalSeconds, "最短刷新间隔");
    AssertEqual(3600, new AppSettings { RefreshIntervalSeconds = 9000 }.Normalize().RefreshIntervalSeconds, "最长刷新间隔");
}

static void NormalizesTaskbarOnlyMode()
{
    // [AI修改] 即使配置文件手工关闭任务栏栏位，仅任务栏模式也必须自动恢复可见入口。
    var settings = new AppSettings { TaskbarOnlyMode = true, ShowTaskbarStrip = false }.Normalize();
    Assert(settings.TaskbarOnlyMode, "仅任务栏模式应保持启用");
    Assert(settings.ShowTaskbarStrip, "仅任务栏模式必须显示任务栏栏位");
}

static void ScalesTaskbarFont()
{
    // [AI修改] 常见 40 像素任务栏应使用明显大于旧 16 DIP 上限的字号，并守住放大范围。
    AssertEqual(23.2D, TaskbarStripLayout.CalculateFontSize(40D), "40 像素任务栏字号");
    AssertEqual(18D, TaskbarStripLayout.CalculateFontSize(20D), "低高度字号下限");
    AssertEqual(26D, TaskbarStripLayout.CalculateFontSize(60D), "高任务栏字号上限");
}

static void MeasuresTaskbarWidth()
{
    // [AI修改] 内容宽度只增加细边界，并按任务栏显示器 DPI 换算为 Win32 物理像素。
    AssertEqual(130, TaskbarStripLayout.CalculatePhysicalWidth(127.1D, 1D), "100% DPI 内容宽度");
    AssertEqual(162, TaskbarStripLayout.CalculatePhysicalWidth(127.1D, 1.25D), "125% DPI 内容宽度");
}

static void NormalizesTokenCounter()
{
    var state = new CodexTokenCounterState();
    var first = CodexTokenCounterNormalizer.Consume(
        new CodexTokenSample(100, 20, 120),
        new CodexTokenSample(10, 2, 12),
        state);
    var growth = CodexTokenCounterNormalizer.Consume(
        new CodexTokenSample(140, 25, 165),
        new CodexTokenSample(40, 5, 45),
        state);
    var duplicate = CodexTokenCounterNormalizer.Consume(
        new CodexTokenSample(140, 25, 165),
        new CodexTokenSample(40, 5, 45),
        state);
    AssertEqual(12L, first, "首个事件优先使用 last_token_usage");
    AssertEqual(45L, growth, "增长事件使用单次增量");
    Assert(duplicate is null, "相同累计样本不能重复计数");
}

static void HandlesTokenCounterReset()
{
    var state = new CodexTokenCounterState();
    _ = CodexTokenCounterNormalizer.Consume(new CodexTokenSample(1_000, 100, 1_100), null, state);
    var reset = CodexTokenCounterNormalizer.Consume(
        new CodexTokenSample(100, 20, 120),
        new CodexTokenSample(10, 2, 12),
        state);
    AssertEqual(12L, reset, "累计计数器重置后只能记录单次增量");
}

static RateLimitParseResult Parse(string json)
{
    using var document = JsonDocument.Parse(json);
    return RateLimitParser.Parse(document.RootElement);
}

static async Task<int> RunFakeAppServerAsync()
{
    var reads = 0;
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        if (!root.TryGetProperty("method", out var methodElement))
        {
            continue;
        }

        var method = methodElement.GetString();
        if (method == "initialize" && root.TryGetProperty("id", out var initializeId))
        {
            await WriteFakeResponseAsync(new { id = initializeId.GetInt32(), result = new { } });
            continue;
        }

        if (method == "account/rateLimits/read" && root.TryGetProperty("id", out var requestId))
        {
            reads++;
            // [AI修改] 只在测试宿主中模拟协议异常，生产客户端没有此环境入口。
            var scenario = Environment.GetEnvironmentVariable("CODEXPEDAL_FAKE_SCENARIO");
            if (scenario == "silent") { await Task.Delay(30000); continue; }
            if (scenario == "oversize") { await Console.Out.WriteAsync(new string('x', 40000)); await Console.Out.FlushAsync(); continue; }
            if (scenario == "malformed" || (scenario == "fail-second" && reads == 2)) { await Console.Out.WriteLineAsync("{broken"); await Console.Out.FlushAsync(); continue; }
            await WriteFakeResponseAsync(new
            {
                id = requestId.GetInt32(),
                result = new
                {
                    rateLimits = new
                    {
                        primary = new
                        {
                            usedPercent = reads == 1 ? 7D : 8D,
                            windowDurationMins = RateLimitParser.SevenDayMinutes,
                            resetsAt = DateTimeOffset.Now.AddDays(6).ToUnixTimeSeconds(),
                        },
                        secondary = (object?)null,
                    },
                },
            });
        }
    }

    return 0;
}

static async Task WriteFakeResponseAsync(object response)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response));
    await Console.Out.FlushAsync();
}

// [AI修改] Claude 测试统一使用隔离目录，结束时只删除本次创建的 GUID 目录。
static void WithTempDirectory(Action<string> test)
{
    var tempRoot = Environment.GetEnvironmentVariable("CODEXPEDAL_TEST_TEMP_ROOT") ?? @"F:\codex_tmp\codexpedal-tests";
    var directory = Path.Combine(tempRoot, "claude-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { test(directory); }
    finally { Directory.Delete(directory, recursive: true); }
}

static void ParsesClaudeStatusLine()
{
    var json = """{"rate_limits":{"five_hour":{"used_percentage":23.5,"resets_at":1738425600},"seven_day":{"used_percentage":41.2,"resets_at":1738857600}}}""";
    AssertEqual(0, SharedQuotaCore.ParseClaude(json, DateTimeOffset.UtcNow, out var native), "解析状态");
    var (five, seven) = SharedQuotaCore.Windows(native);
    AssertEqual(76.5D, five!.RemainingPercent, "5小时剩余");
    AssertEqual("77%", UsageFormatter.Percent(five.RemainingPercent), "四舍五入");
    AssertEqual(DateTimeOffset.FromUnixTimeSeconds(1738857600), seven!.ResetsAt, "7天重置时间");
}

static void ParsesClaudeOAuthUsage()
{
    var json = """{"five_hour":{"utilization":120.0,"resets_at":"2026-01-22T09:00:00Z"},"seven_day":{"utilization":8,"resets_at":null},"seven_day_opus":{"utilization":50}}""";
    AssertEqual(0, SharedQuotaCore.ParseClaude(json, DateTimeOffset.UtcNow, out var native), "解析状态");
    var (five, seven) = SharedQuotaCore.Windows(native);
    AssertEqual(0D, five!.RemainingPercent, "超额剩余0");
    AssertEqual(DateTimeOffset.Parse("2026-01-22T09:00:00Z"), five.ResetsAt, "ISO重置时间");
    Assert(seven!.ResetsAt is null, "空重置时间不编造");
    AssertEqual(92D, seven.RemainingPercent, "7天剩余");
}

static void RejectsClaudeMissingWindows()
{
    AssertEqual(2, SharedQuotaCore.ParseClaude("""{"cwd":"x"}""", DateTimeOffset.UtcNow, out _), "缺失窗口");
    AssertEqual(1, SharedQuotaCore.ParseClaude("""{"five_hour":{"used_percentage":"7"}}""", DateTimeOffset.UtcNow, out _), "字符串百分比");
}

static void ClaudeHookWritesCache() => WithTempDirectory(directory =>
{
    var input = """{"session_id":"s","cwd":"C:/secret","rate_limits":{"five_hour":{"used_percentage":23.5},"seven_day":{"used_percentage":41.2}}}""";
    var text = ClaudeStatusLineHook.Run(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(input)), directory, DateTimeOffset.UtcNow);
    AssertEqual("Claude 剩余 5h 77% · 7d 59%", text, "状态栏文字");
    var cache = File.ReadAllText(ClaudeStatusLineCache.PathFor(directory));
    Assert(cache.Contains("rate_limits") && !cache.Contains("secret") && !cache.Contains("session_id"), "缓存只保存额度字段");
    Assert(Directory.GetFiles(directory, "*.tmp").Length == 0, "不残留临时文件");
});

static void ClaudeHookKeepsCacheWithoutLimits() => WithTempDirectory(directory =>
{
    var path = ClaudeStatusLineCache.PathFor(directory);
    File.WriteAllText(path, """{"rate_limits":{"seven_day":{"used_percentage":10}}}""");
    var text = ClaudeStatusLineHook.Run(new MemoryStream("""{"cwd":"x"}"""u8.ToArray()), directory, DateTimeOffset.UtcNow);
    Assert(text.Contains("--"), "无额度显示短横线");
    Assert(File.ReadAllText(path).Contains("10"), "已有缓存不被覆盖");
    AssertEqual("Claude 剩余 --", ClaudeStatusLineHook.Run(new MemoryStream("{"u8.ToArray()), directory, DateTimeOffset.UtcNow), "畸形输入不抛出");
});

static void ClaudeReaderUsesNewerCache() => WithTempDirectory(directory =>
{
    var path = ClaudeStatusLineCache.PathFor(directory);
    File.WriteAllText(path, """{"rate_limits":{"five_hour":{"used_percentage":20},"seven_day":{"used_percentage":40}}}""");
    var first = DateTimeOffset.UtcNow.AddSeconds(-30);
    File.SetLastWriteTimeUtc(path, first.UtcDateTime);
    using var reader = new ClaudeQuotaReader(new ClaudeOAuthUsageClient(new CountingHandler()), () => directory);
    var diagnostics = new List<string>();
    var snapshot = reader.ReadAsync(false, DateTimeOffset.UtcNow, diagnostics, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(ClaudeQuotaSource.StatusLine, snapshot.Source, "来源");
    AssertEqual(80D, snapshot.FiveHourQuota!.RemainingPercent, "5小时");
    AssertEqual(first.ToUnixTimeSeconds(), snapshot.ObservedAt!.Value.ToUnixTimeSeconds(), "采样时间取缓存修改时间");
    File.WriteAllText(path, "{broken");
    File.SetLastWriteTimeUtc(path, first.AddSeconds(10).UtcDateTime);
    var kept = reader.ReadAsync(false, DateTimeOffset.UtcNow, diagnostics, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(80D, kept.FiveHourQuota!.RemainingPercent, "畸形新缓存保留旧值");
    AssertEqual(first.ToUnixTimeSeconds(), kept.ObservedAt!.Value.ToUnixTimeSeconds(), "失败不更新时间");
});

// [AI修改] 实时读取默认开启：旧配置没有该字段时开启，用户保存过的关闭选择保持关闭。
static void ClaudeLiveDefaultsOn()
{
    var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
    Assert(new AppSettings().ClaudeLiveQuota, "新配置默认开启");
    Assert(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{}", options)!.ClaudeLiveQuota, "缺少字段时默认开启");
    Assert(!System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"claudeLiveQuota\":false}", options)!.ClaudeLiveQuota, "用户关闭后保持关闭");
}

static void ClaudeLiveDisabledSkipsNetwork() => WithTempDirectory(directory =>
{
    File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"sk-test-token","expiresAt":32503680000000}}""");
    var handler = new CountingHandler();
    using var reader = new ClaudeQuotaReader(new ClaudeOAuthUsageClient(handler), () => directory);
    var snapshot = reader.ReadAsync(false, DateTimeOffset.UtcNow, new List<string>(), CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(0, handler.Calls, "未开启实时不能发请求");
    AssertEqual(ClaudeQuotaSource.Unavailable, snapshot.Source, "无缓存时不可用");
});

static void ClaudeLiveReadsUsage() => WithTempDirectory(directory =>
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"))) { return; }
    File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"sk-test-token","expiresAt":32503680000000}}""");
    var handler = new CountingHandler { Body = """{"five_hour":{"utilization":30,"resets_at":"2026-11-03T12:00:00+00:00"},"seven_day":{"utilization":60,"resets_at":"2026-11-08T00:00:00Z"}}""" };
    using var reader = new ClaudeQuotaReader(new ClaudeOAuthUsageClient(handler), () => directory);
    var diagnostics = new List<string>();
    var snapshot = reader.ReadAsync(true, DateTimeOffset.UtcNow, diagnostics, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(1, handler.Calls, "请求一次");
    AssertEqual("Bearer sk-test-token", handler.Authorization, "使用令牌");
    AssertEqual(ClaudeQuotaSource.LiveOAuth, snapshot.Source, "来源");
    AssertEqual(70D, snapshot.FiveHourQuota!.RemainingPercent, "5小时剩余");
    Assert(!snapshot.ReadFailed && diagnostics.All(item => !item.Contains("sk-test")), "诊断不含令牌");
    _ = reader.ReadAsync(true, DateTimeOffset.UtcNow.AddSeconds(5), diagnostics, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(1, handler.Calls, "60秒内不重复请求");
});

static void ClaudeExpiredTokenSkipsNetwork() => WithTempDirectory(directory =>
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"))) { return; }
    File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"sk-old","expiresAt":1000}}""");
    var handler = new CountingHandler();
    using var reader = new ClaudeQuotaReader(new ClaudeOAuthUsageClient(handler), () => directory);
    var diagnostics = new List<string>();
    var snapshot = reader.ReadAsync(true, DateTimeOffset.UtcNow, diagnostics, CancellationToken.None).GetAwaiter().GetResult();
    AssertEqual(0, handler.Calls, "过期令牌不发请求");
    Assert(snapshot.ReadFailed && diagnostics.Any(item => item.Contains("过期")), "提示过期");
});

static void ClaudeBridgeRoundTrip() => WithTempDirectory(directory =>
{
    var settingsPath = ClaudeStatusLineBridge.SettingsPath(directory);
    File.WriteAllText(settingsPath, """{"model":"opus","statusLine":{"type":"command","command":"bash ~/.claude/mine.sh"}}""");
    var exe = Path.Combine(directory, "app", "CodeXPedal.exe");
    AssertEqual(ClaudeBridgeState.OtherCommand, ClaudeStatusLineBridge.GetState(directory, exe).State, "识别自定义状态栏");
    ClaudeStatusLineBridge.Enable(directory, exe);
    AssertEqual(ClaudeBridgeState.Enabled, ClaudeStatusLineBridge.GetState(directory, exe).State, "启用后");
    var enabled = File.ReadAllText(settingsPath);
    using (var parsed = JsonDocument.Parse(enabled))
    {
        var command = parsed.RootElement.GetProperty("statusLine").GetProperty("command").GetString()!;
        Assert(parsed.RootElement.GetProperty("model").GetString() == "opus", "保留其他设置");
        Assert(command.EndsWith(ClaudeStatusLineHook.Argument, StringComparison.Ordinal) && !command.Contains('\\'), "命令使用正斜杠路径");
    }
    AssertEqual(ClaudeBridgeState.EnabledOtherPath, ClaudeStatusLineBridge.GetState(directory, exe + "x").State, "程序移动后提示重新启用");
    ClaudeStatusLineBridge.Enable(directory, exe);
    ClaudeStatusLineBridge.Disable(directory);
    var restored = File.ReadAllText(settingsPath);
    Assert(restored.Contains("mine.sh") && !restored.Contains(ClaudeStatusLineHook.Argument), "停用后恢复原状态栏");
    Assert(File.Exists(settingsPath + ".codexpedal.bak"), "首次修改前备份");
    File.WriteAllText(settingsPath, "{}");
    ClaudeStatusLineBridge.Enable(directory, exe);
    ClaudeStatusLineBridge.Disable(directory);
    Assert(!File.ReadAllText(settingsPath).Contains("statusLine"), "原本没有状态栏时停用即删除");
});

static void ResolvesClaudeExecutable() => WithTempDirectory(directory =>
{
    var home = Path.Combine(directory, "home");
    var appData = Path.Combine(directory, "roaming");
    var local = Path.Combine(directory, "local");
    var env = new Dictionary<string, string?>();
    Assert(ClaudeExecutableResolver.Resolve(name => env.GetValueOrDefault(name), home, appData, local) is null, "未安装");
    var managed = Path.Combine(appData, "Claude", "claude-code", "2.1.300", "claude.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(managed)!);
    File.WriteAllText(managed, string.Empty);
    AssertEqual(Path.GetFullPath(managed), ClaudeExecutableResolver.Resolve(name => env.GetValueOrDefault(name), home, appData, local), "桌面版托管");
    var native = Path.Combine(home, ".local", "bin", "claude.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(native)!);
    File.WriteAllText(native, string.Empty);
    AssertEqual(Path.GetFullPath(native), ClaudeExecutableResolver.Resolve(name => env.GetValueOrDefault(name), home, appData, local), "原生安装优先");
    var explicitPath = Path.Combine(directory, "custom-claude.exe");
    File.WriteAllText(explicitPath, string.Empty);
    env[ClaudeExecutableResolver.PathVariable] = explicitPath;
    AssertEqual(Path.GetFullPath(explicitPath), ClaudeExecutableResolver.Resolve(name => env.GetValueOrDefault(name), home, appData, local), "显式变量最优先");
});

static void ClaudeStalenessBoundary()
{
    var now = DateTimeOffset.UtcNow;
    var window = new QuotaWindow(QuotaWindowKind.SevenDay, 10, RateLimitParser.SevenDayMinutes, null);
    var snapshot = new ClaudeQuotaSnapshot(null, window, ClaudeQuotaSource.StatusLine, now, false);
    Assert(!SharedQuotaCore.IsStale(snapshot, now.AddSeconds(179), 60), "179秒未过期");
    Assert(SharedQuotaCore.IsStale(snapshot, now.AddSeconds(180), 60), "180秒过期");
    Assert(SharedQuotaCore.IsStale(snapshot with { ReadFailed = true }, now, 60), "失败立即标旧值");
}

static void DetectsClaudeCliStatus() => WithTempDirectory(directory =>
{
    if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN"))) { return; }
    AssertEqual(ClaudeCliStatus.NotInstalled, ClaudeCliSetup.GetStatus(null, directory), "未找到 CLI");
    var exe = Path.Combine(directory, "claude.exe");
    AssertEqual(ClaudeCliStatus.NotLoggedIn, ClaudeCliSetup.GetStatus(exe, directory), "无登录文件");
    File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"other":1}""");
    AssertEqual(ClaudeCliStatus.NotLoggedIn, ClaudeCliSetup.GetStatus(exe, directory), "登录文件无令牌");
    File.WriteAllText(Path.Combine(directory, ".credentials.json"), """{"claudeAiOauth":{"accessToken":"x"}}""");
    AssertEqual(ClaudeCliStatus.Ready, ClaudeCliSetup.GetStatus(exe, directory), "已登录");
});

static void BuildsClaudeSetupScript()
{
    var fresh = ClaudeCliSetup.BuildScript(null);
    Assert(fresh.Contains(ClaudeCliSetup.InstallCommand) && fresh.Contains("auth login") && fresh.Contains("$claude = ''"), "未安装时先安装再登录");
    var known = ClaudeCliSetup.BuildScript(@"C:\Users\o'neil\.local\bin\claude.exe");
    Assert(known.Contains(@"$claude = 'C:\Users\o''neil\.local\bin\claude.exe'"), "路径单引号转义");
}

static void ScalesDualTaskbarLayout()
{
    AssertEqual(11.6D, TaskbarStripLayout.CalculateDualFontSize(40D), "40像素两行字号");
    AssertEqual(10D, TaskbarStripLayout.CalculateDualFontSize(20D), "两行字号下限");
    AssertEqual(14D, TaskbarStripLayout.CalculateDualFontSize(60D), "两行字号上限");
    AssertEqual(320, TaskbarStripLayout.CalculatePhysicalWidth(400D, 1D), "单行宽度上限不变");
    AssertEqual(402, TaskbarStripLayout.CalculatePhysicalWidth(400D, 1D, TaskbarStripLayout.MaximumDualPhysicalWidth), "两行宽度上限放宽");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
    {
        throw new InvalidOperationException($"{message}：期望 {expected}，实际 {actual}");
    }
}

// [AI修改] 假 HTTP 处理器：记录请求次数与授权头，不访问真实网络。
sealed class CountingHandler : HttpMessageHandler
{
    public int Calls { get; private set; }
    public string? Authorization { get; private set; }
    public string Body { get; init; } = "{}";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        Authorization = request.Headers.Authorization?.ToString();
        return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(Body) });
    }
}
