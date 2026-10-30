// [AI修改] 独立短生命周期 stdio 会话，只握手和读取额度；所有等待、输出和退出都有上限。
import Foundation
import Darwin

public final class CodexRPCClient: @unchecked Sendable {
    private let lock = NSLock()
    private var cancelled = false
    private var activeProcess: Process?
    public init() {}
    // [AI修改] 取消直接通知自有子进程，应用退出前可强制回收，不等待下次轮询才发信号。
    public func cancel(force: Bool = false) {
        lock.lock(); defer { lock.unlock() }
        cancelled = true
        if let process = activeProcess, process.isRunning {
            if force { Darwin.kill(process.processIdentifier, SIGKILL) } else { process.terminate() }
        }
    }
    private var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }

    public func read(executable: URL, timeout: TimeInterval = 12) throws -> QuotaSnapshot {
        // [AI修改] 核心层也保护断管道，测试或命令行调用不依赖 UI 入口先设置。
        signal(SIGPIPE, SIG_IGN)
        let process = Process()
        let input = Pipe(), output = Pipe()
        process.executableURL = executable
        process.arguments = ["app-server", "--listen", "stdio://"]
        process.standardInput = input
        process.standardOutput = output
        process.standardError = FileHandle.nullDevice
        var environment = ProcessInfo.processInfo.environment
        environment["PATH"] = "/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin:" + (environment["PATH"] ?? "")
        process.environment = environment
        let outFD = output.fileHandleForReading.fileDescriptor
        // [AI修改] 启动和登记由同一锁保护，取消不能错过刚启动但尚未登记的进程。
        lock.lock()
        guard !cancelled else { lock.unlock(); throw QuotaReadError.cancelled }
        do { try process.run(); activeProcess = process; lock.unlock() }
        catch { lock.unlock(); throw QuotaReadError.invalidCLI }
        // [AI修改] 子进程只影响本次请求；超时先正常终止，未退出再强制回收，不能无限 wait。
        defer {
            try? input.fileHandleForWriting.close()
            if process.isRunning { process.terminate() }
            let until = ProcessInfo.processInfo.systemUptime + 0.4
            while process.isRunning && ProcessInfo.processInfo.systemUptime < until { Thread.sleep(forTimeInterval: 0.01) }
            if process.isRunning { Darwin.kill(process.processIdentifier, SIGKILL) }
            let killDeadline = ProcessInfo.processInfo.systemUptime + 0.4
            while process.isRunning && ProcessInfo.processInfo.systemUptime < killDeadline { Thread.sleep(forTimeInterval: 0.01) }
            try? output.fileHandleForReading.close()
            lock.lock(); activeProcess = nil; lock.unlock()
        }
        guard fcntl(outFD, F_SETFL, fcntl(outFD, F_GETFL) | O_NONBLOCK) != -1 else { throw QuotaReadError.processExited }
        func send(_ message: [String: Any]) throws {
            var data = try JSONSerialization.data(withJSONObject: message)
            data.append(10)
            try input.fileHandleForWriting.write(contentsOf: data)
        }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0-dev"
        try send(["id": 1, "method": "initialize", "params": ["clientInfo": ["name": "codexpedal", "title": "CodeXPedal", "version": version]]])
        let deadline = ProcessInfo.processInfo.systemUptime + max(0.1, min(timeout, 30))
        var buffer = Data(), total = 0, initialized = false
        var bytes = [UInt8](repeating: 0, count: 8192)
        while ProcessInfo.processInfo.systemUptime < deadline {
            if isCancelled { throw QuotaReadError.cancelled }
            var pollFD = pollfd(fd: outFD, events: Int16(POLLIN), revents: 0)
            let ready = Darwin.poll(&pollFD, 1, 100)
            if ready < 0 { if errno == EINTR { continue }; throw QuotaReadError.processExited }
            if ready == 0 { if !process.isRunning { throw QuotaReadError.processExited }; continue }
            let count = Darwin.read(outFD, &bytes, bytes.count)
            if count < 0 { if errno == EAGAIN || errno == EINTR { continue }; throw QuotaReadError.processExited }
            if count == 0 { throw QuotaReadError.processExited }
            buffer.append(contentsOf: bytes.prefix(count)); total += count
            guard total <= 1_048_576, buffer.count <= 131_072 else { throw QuotaReadError.outputTooLarge }
            while let newline = buffer.firstIndex(of: 10) {
                let line = buffer.prefix(upTo: newline)
                buffer.removeSubrange(...newline)
                if line.isEmpty { continue }
                guard let object = try JSONSerialization.jsonObject(with: line) as? [String: Any] else { throw QuotaReadError.invalidResponse }
                guard let id = object["id"] as? Int else { continue }
                // [AI修改] 只处理当前在途请求，其他ID的错误不能污染当前结果。
                guard id == (initialized ? 2 : 1) else { continue }
                if let error = object["error"] as? [String: Any] {
                    throw QuotaReadError.serverRejected(error["code"] as? Int ?? -1)
                }
                guard let result = object["result"] as? [String: Any] else { throw QuotaReadError.invalidResponse }
                if id == 1 && !initialized {
                    initialized = true
                    try send(["method": "initialized", "params": [String: Any]()])
                    try send(["id": 2, "method": "account/rateLimits/read"])
                } else if id == 2 && initialized {
                    return try RateLimitParser.parse(JSONSerialization.data(withJSONObject: result))
                }
            }
        }
        throw QuotaReadError.timeout
    }
}
