// [AI修改] 长期运行官方 Codex app-server，并通过稳定的 stdio JSON-RPC 读取和接收额度更新。
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodeXPedal.Windows.Core.Services;

public sealed class CodexAppServerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private Process? _process;
    private ResolvedCommand? _runningCommand;
    // [AI修改] stderr按固定块排空，不保留账户相关原始输出或无界长行。
    private CancellationTokenSource? _stderrCancellation;
    private int _nextRequestId;
    private bool _disposed;

    public async Task<RateLimitParseResult> ReadRateLimitsAsync(CancellationToken cancellationToken)
    {
        var command = CodexExecutableResolver.Resolve();
        if (command is null)
        {
            // [AI修改] 单独标记 CLI 不可用，便于本地快照成功时把它作为来源差异而非错误。
            return new(false, null, null, ["未找到可执行的官方 Codex CLI；请安装 Windows standalone 或设置 CODEXPEDAL_CODEX_PATH"], true);
        }

        await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                await EnsureStartedAsync(command, timeout.Token).ConfigureAwait(false);
                var requestId = NextRequestId();
                await WriteAsync(new { id = requestId, method = "account/rateLimits/read" }, timeout.Token).ConfigureAwait(false);
                var response = await ReadResponseAsync(requestId, timeout.Token).ConfigureAwait(false);
                var parsed = ParseResponse(response);
                return parsed;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return RecoverFromFailure("Codex app-server 响应超时");
            }
            catch (OperationCanceledException)
            {
                // [AI修改] 用户取消也回收当前会话，防止旧响应进入下一次请求。
                ResetProcess();
                throw;
            }
            catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or Win32Exception)
            {
                return RecoverFromFailure($"Codex 额度读取失败：{exception.Message}");
            }
        }
        finally
        {
            _requestGate.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        ResetProcess();
        // [AI修改] 刷新任务可能仍在 finally 中释放信号量，应用退出时不抢先销毁它。
    }

    private async Task EnsureStartedAsync(ResolvedCommand command, CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false } && Equals(_runningCommand, command))
        {
            return;
        }

        ResetProcess();
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = command.FileName,
                Arguments = command.BuildArguments("app-server --listen stdio://"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // [AI修改] JSONL stdin/stdout 必须使用无 BOM UTF-8，避免首条 initialize 被判为非法 JSON。
                StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            },
        };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("Codex app-server 启动失败");
        }

        _stderrCancellation = new CancellationTokenSource();
        _ = DrainErrorAsync(process, _stderrCancellation.Token);
        _process = process;
        _runningCommand = command;

        var initializeId = NextRequestId();
        await WriteAsync(new
        {
            id = initializeId,
            method = "initialize",
            @params = new
            {
                // [AI修改] 协议版本来自程序集，不再沿用参考项目旧版本。
                clientInfo = new { name = "codexpedal", title = "CodeXPedal", version = typeof(CodexAppServerClient).Assembly.GetName().Version?.ToString() ?? "0.0.0-dev" },
                capabilities = new { experimentalApi = false, optOutNotificationMethods = Array.Empty<string>() },
            },
        }, cancellationToken).ConfigureAwait(false);
        var initializeResponse = await ReadResponseAsync(initializeId, cancellationToken).ConfigureAwait(false);
        ThrowIfError(initializeResponse, "Codex app-server 初始化失败");
        await WriteAsync(new { method = "initialized" }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonElement> ReadResponseAsync(int expectedId, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex app-server 尚未启动");
        var totalBytes = 0;
        while (true)
        {
            var line = await ReadBoundedLineAsync(process.StandardOutput, cancellationToken).ConfigureAwait(false);
            totalBytes += Encoding.UTF8.GetByteCount(line) + 1;
            if (totalBytes > 1_048_576) { throw new IOException("官方响应累计输出超过安全上限"); }
            if (line.Length == 0) { continue; }

            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (TryHandleRateLimitNotification(root))
            {
                continue;
            }

            if (root.TryGetProperty("id", out var idElement)
                && idElement.TryGetInt32(out var id)
                && id == expectedId)
            {
                return root.Clone();
            }
        }
    }

    private bool TryHandleRateLimitNotification(JsonElement root)
    {
        if (!root.TryGetProperty("method", out var method)
            || method.GetString() != "account/rateLimits/updated"
            || !root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // [AI修改] 读取以匹配请求ID的完整响应为准，不把稀疏通知缓存伪装为新读取成功。
        return true;
    }

    private RateLimitParseResult ParseResponse(JsonElement response)
    {
        ThrowIfError(response, "Codex 额度读取失败");
        if (!response.TryGetProperty("result", out var result))
        {
            return new(false, null, null, ["Codex 额度响应缺少 result"]);
        }

        return RateLimitParser.Parse(result);
    }

    private static void ThrowIfError(JsonElement response, string prefix)
    {
        if (!response.TryGetProperty("error", out var error))
        {
            return;
        }

        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : "未知错误";
        throw new InvalidOperationException($"{prefix}：{message}");
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new InvalidOperationException("Codex app-server 尚未启动");
        var json = JsonSerializer.Serialize(message, JsonOptions);
        await process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private RateLimitParseResult RecoverFromFailure(string message)
    {
        ResetProcess();
        // [AI修改] 失败必须原样上报，由统一快照层保留旧值和旧时间。
        return new(false, null, null, [message]);
    }

    private string BuildExitMessage(Process process)
    {
        return process.HasExited
            ? $"Codex app-server已退出（{process.ExitCode}）"
            : "Codex app-server已关闭输出";
    }

    // [AI修改] 一行最多32768个UTF-16字符，最坏UTF-8也不超过128KiB；取消可中断半行。
    private static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var line = new StringBuilder();
        while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
        {
            if (buffer[0] == '\n') { return line.ToString().TrimEnd('\r'); }
            line.Append(buffer[0]);
            if (line.Length > 32768) { throw new IOException("官方响应单行超过安全上限"); }
        }
        throw new IOException("官方CLI提前关闭输出");
    }

    private static async Task DrainErrorAsync(Process process, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        try { while (await process.StandardError.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private void ResetProcess()
    {
        _stderrCancellation?.Cancel();
        _stderrCancellation?.Dispose();
        _stderrCancellation = null;
        var process = _process;
        _process = null;
        _runningCommand = null;
        if (process is null)
        {
            return;
        }

        try
        {
            process.StandardInput.Close();
            if (!process.HasExited && !process.WaitForExit(500))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            // [AI修改] 进程已退出或正在退出时无需再次终止。
        }
        finally
        {
            process.Dispose();
        }
    }
}
