// [AI修改] Claude 剩余额度：默认读取本机登录令牌调用官方 usage 接口；状态栏桥接缓存（官方 statusLine 字段，不读凭据）作为备选。
import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif
#if canImport(Security)
import Security
#endif

public enum ClaudeQuotaSource: Sendable, Equatable {
    case unavailable, live, statusLine
}

// [AI修改] 实时读取默认开启（用户已确认）：没有保存过设置时开启，用户手动关闭后保持关闭。
public enum ClaudeLivePreference {
    public static func resolve(stored: Any?) -> Bool { stored as? Bool ?? true }
}

// [AI修改] 状态栏桥接缓存：只含 rate_limits，不含会话内容；采样时间取文件修改时间。
public enum ClaudeStatusLineCache {
    public static let fileName = "codexpedal-rate-limits.json"
    static let maximumBytes = 1_048_576

    public static func url(in configDirectory: URL) -> URL { configDirectory.appendingPathComponent(fileName) }

    static func read(configDirectory: URL) -> (Data, Date)? {
        let url = url(in: configDirectory)
        guard let attributes = try? FileManager.default.attributesOfItem(atPath: url.path),
              let size = attributes[.size] as? NSNumber, size.intValue <= maximumBytes,
              let modified = attributes[.modificationDate] as? Date,
              let handle = try? FileHandle(forReadingFrom: url) else { return nil }
        defer { try? handle.close() }
        guard let data = try? handle.read(upToCount: maximumBytes + 1), data.count <= maximumBytes else { return nil }
        return (data, modified)
    }
}

public enum ClaudeStatusLineHook {
    public static let argument = "--claude-statusline"
    static let maximumInput = 1_048_576

    // [AI修改] 作为 statusLine 命令运行：只摘取 rate_limits 原子写入缓存；任何错误都返回占位文字，不能破坏 Claude 界面。
    public static func run(input: Data, configDirectory: URL, now: Date = Date()) -> String {
        guard !input.isEmpty, input.count <= maximumInput,
              let root = try? JSONSerialization.jsonObject(with: input) as? [String: Any] else { return "Claude 剩余 --" }
        // [AI修改] 首次 API 响应前或非订阅账号没有该字段，此时不覆盖已有缓存。
        guard let limits = root["rate_limits"] as? [String: Any],
              let payload = try? JSONSerialization.data(withJSONObject: ["rate_limits": limits]) else {
            return "Claude 剩余 --（首次回复后显示）"
        }
        do {
            try FileManager.default.createDirectory(at: configDirectory, withIntermediateDirectories: true)
            try payload.write(to: ClaudeStatusLineCache.url(in: configDirectory), options: .atomic)
        } catch {
            return "Claude 剩余 --"
        }
        return format(payload, now: now)
    }

    // [AI修改] 状态栏文字同样经共享核心计算剩余，和横条口径一致。
    static func format(_ payload: Data, now: Date) -> String {
        guard let snapshot = try? RateLimitParser.parseClaude(payload, observedAt: now), !snapshot.windows.isEmpty else { return "Claude 剩余 --" }
        let parts = snapshot.windows.map { "\($0.kind == .fiveHour ? "5h" : "7d") \($0.remainingText)" }
        return "Claude 剩余 " + parts.joined(separator: " · ")
    }
}

// [AI修改] 官方 usage 接口（Claude Code /usage 同源，未公开文档）。令牌只在内存中使用一次，不记录、不缓存、不代为刷新。
public struct ClaudeLiveUsageClient: Sendable {
    public typealias Fetch = @Sendable (_ token: String) throws -> Data
    static let usageURL = URL(string: "https://api.anthropic.com/api/oauth/usage")!
    private let fetch: Fetch
    private let tokenProvider: @Sendable (_ configDirectory: URL, _ now: Date) throws -> String

    public init(fetch: Fetch? = nil, tokenProvider: (@Sendable (URL, Date) throws -> String)? = nil) {
        self.fetch = fetch ?? ClaudeLiveUsageClient.urlSessionFetch
        self.tokenProvider = tokenProvider ?? { try ClaudeLiveUsageClient.loadToken(configDirectory: $0, now: $1) }
    }

    public func read(configDirectory: URL, now: Date) throws -> Data {
        try fetch(try tokenProvider(configDirectory, now))
    }

    // [AI修改] 环境变量优先，其次 macOS 钥匙串「Claude Code-credentials」（系统会询问授权），最后兼容 .credentials.json。
    public static func loadToken(configDirectory: URL, now: Date) throws -> String {
        if let token = ProcessInfo.processInfo.environment["CLAUDE_CODE_OAUTH_TOKEN"]?.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty {
            return token
        }
        var raw: Data?
        #if canImport(Security)
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Claude Code-credentials",
                                    kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        let status = SecItemCopyMatching(query as CFDictionary, &item)
        // [AI修改] 区分“拒绝授权”和“未登录”；拒绝后由读取器暂停，避免默认开启时每分钟重复弹窗。
        if status == errSecUserCanceled || status == errSecAuthFailed { throw QuotaReadError.claudeKeychainDenied }
        if status == errSecSuccess, let data = item as? Data, data.count <= 65_536 { raw = data }
        #endif
        if raw == nil {
            let file = configDirectory.appendingPathComponent(".credentials.json")
            if let size = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber, size.intValue <= 65_536 {
                raw = try? Data(contentsOf: file)
            }
        }
        guard let raw, let root = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let oauth = root["claudeAiOauth"] as? [String: Any],
              let token = (oauth["accessToken"] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !token.isEmpty
        else { throw QuotaReadError.claudeNoLogin }
        // [AI修改] 不使用刷新令牌，避免与 Claude Code 争用导致对方登录失效。
        if let expires = oauth["expiresAt"] as? NSNumber, Date(timeIntervalSince1970: expires.doubleValue / 1000) <= now {
            throw QuotaReadError.claudeTokenExpired
        }
        return token
    }

    static let urlSessionFetch: Fetch = { token in
        var request = URLRequest(url: usageURL, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization")
        request.setValue("oauth-2025-04-20", forHTTPHeaderField: "anthropic-beta")
        request.setValue("application/json", forHTTPHeaderField: "Accept")
        request.setValue("CodeXPedal/1.0", forHTTPHeaderField: "User-Agent")
        let box = FetchBox()
        let semaphore = DispatchSemaphore(value: 0)
        let task = URLSession.shared.dataTask(with: request) { data, response, error in
            let http = response as? HTTPURLResponse
            box.set(data: data, status: http?.statusCode, failed: error != nil, retryAfter: http?.value(forHTTPHeaderField: "Retry-After"))
            semaphore.signal()
        }
        task.resume()
        // [AI修改] 等待有上限；超时取消请求，不留后台任务。
        if semaphore.wait(timeout: .now() + 20) == .timedOut { task.cancel(); throw QuotaReadError.claudeNetwork }
        let (data, status, failed, retryAfter) = box.get()
        guard !failed, let status else { throw QuotaReadError.claudeNetwork }
        if status == 401 || status == 403 { throw QuotaReadError.claudeUnauthorized }
        if status == 429 { throw ClaudeRateLimitedError(retryAfter: ClaudeRateLimitedError.parse(retryAfter)) }
        guard (200..<300).contains(status) else { throw QuotaReadError.claudeHTTP(status) }
        guard let data, data.count <= 1_048_576 else { throw QuotaReadError.outputTooLarge }
        return data
    }
}

// [AI修改] 429 限流错误，携带服务器 Retry-After 建议的等待秒数（可能没有），供读取器退避。
public struct ClaudeRateLimitedError: Error, LocalizedError, Sendable {
    public let retryAfter: TimeInterval?
    public init(retryAfter: TimeInterval?) { self.retryAfter = retryAfter }
    public var errorDescription: String? { QuotaReadError.claudeRateLimited.localizedDescription }

    // [AI修改] Retry-After 可为秒数或 HTTP 日期；无法识别时返回 nil，按逐级退避处理。
    static func parse(_ header: String?, now: Date = Date()) -> TimeInterval? {
        guard let text = header?.trimmingCharacters(in: .whitespaces), !text.isEmpty else { return nil }
        if let seconds = TimeInterval(text) { return seconds }
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX"); formatter.timeZone = TimeZone(identifier: "GMT")
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        return formatter.date(from: text).map { $0.timeIntervalSince(now) }
    }
}

private final class FetchBox: @unchecked Sendable {
    private let lock = NSLock()
    private var data: Data?, status: Int?, failed = false, retryAfter: String?
    func set(data: Data?, status: Int?, failed: Bool, retryAfter: String?) {
        lock.lock(); self.data = data; self.status = status; self.failed = failed; self.retryAfter = retryAfter; lock.unlock()
    }
    func get() -> (Data?, Int?, Bool, String?) { lock.lock(); defer { lock.unlock() }; return (data, status, failed, retryAfter) }
}

public enum ClaudeReadOutcome: Sendable {
    case updated(QuotaSnapshot, ClaudeQuotaSource)
    case failed(String)
    case unchanged
}

// [AI修改] 读取策略与 Windows 一致：实时接口正常 120 秒一次、被限流时退避；未开启或失败时只接受比当前更新的状态栏缓存。
public final class ClaudeQuotaReader: @unchecked Sendable {
    private let lock = NSLock()
    private let live: ClaudeLiveUsageClient
    private let configDirectory: @Sendable () -> URL
    private var lastLiveAttempt: Date?
    // [AI修改] 下一次允许请求的时间；被限流时推迟到退避结束，期间完全不发请求。
    private var nextLiveAttempt: Date?
    private var rateLimitStreak = 0
    private var lastLiveError: String?
    // [AI修改] 正常间隔 120 秒：额度两分钟更新足够，且小于 180 秒的过期判定，不会无故显示 ≈。
    static let normalLiveInterval: TimeInterval = 120

    // [AI修改] 限流退避：有 Retry-After 就照办（限制在 1–30 分钟）；否则第 1/2/3 次依次等 2/4/8 分钟，之后每次 15 分钟。
    static func rateLimitDelay(streak: Int, retryAfter: TimeInterval?) -> TimeInterval {
        if let retryAfter { return min(max(retryAfter, 60), 1800) }
        switch streak {
        case ...1: return 120
        case 2: return 240
        case 3: return 480
        default: return 900
        }
    }
    private var liveDenied = false

    public init(live: ClaudeLiveUsageClient = ClaudeLiveUsageClient(), configDirectory: (@Sendable () -> URL)? = nil) {
        self.live = live
        self.configDirectory = configDirectory ?? { ClaudeLocator.configDirectory() }
    }

    // [AI修改] 刚完成登录时立即允许下一次实时读取，不等正常间隔并清除限流退避。
    public func resetLiveThrottle() {
        lock.lock(); lastLiveAttempt = nil; nextLiveAttempt = nil; rateLimitStreak = 0; lastLiveError = nil; liveDenied = false; lock.unlock()
    }

    public func read(liveEnabled: Bool, now: Date, lastObserved: Date?) -> ClaudeReadOutcome {
        lock.lock(); defer { lock.unlock() }
        let directory = configDirectory()
        // [AI修改] 钥匙串被拒绝后暂停到下次启动或用户关闭实时读取，期间继续接受状态栏桥接数据。
        if !liveEnabled { liveDenied = false }
        if liveEnabled, !liveDenied {
            // [AI修改] 时钟回拨（上次请求时间晚于现在）时立即允许请求，避免永久卡住。
            let due = nextLiveAttempt.map { now >= $0 } ?? true
            if due || (lastLiveAttempt.map { $0 > now } ?? false) {
                lastLiveAttempt = now
                nextLiveAttempt = now.addingTimeInterval(Self.normalLiveInterval)
                do {
                    let snapshot = try RateLimitParser.parseClaude(try live.read(configDirectory: directory, now: now), observedAt: now)
                    lastLiveError = nil
                    rateLimitStreak = 0
                    return .updated(snapshot, .live)
                } catch let limited as ClaudeRateLimitedError {
                    // [AI修改] 限流时按服务器建议或逐级翻倍退避，提示写明自动重试时间，恢复前一直保留该提示。
                    rateLimitStreak += 1
                    let retryAt = now.addingTimeInterval(Self.rateLimitDelay(streak: rateLimitStreak, retryAfter: limited.retryAfter))
                    nextLiveAttempt = retryAt
                    let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_GB"); formatter.dateFormat = "HH:mm:ss"
                    lastLiveError = "Claude 额度接口限流中，\(formatter.string(from: retryAt)) 后自动重试。"
                } catch {
                    lastLiveError = (error as? QuotaReadError)?.localizedDescription ?? "Claude 额度读取失败。"
                    if case QuotaReadError.claudeKeychainDenied = error { liveDenied = true }
                }
            }
        }
        if let cached = ClaudeStatusLineCache.read(configDirectory: directory),
           cached.1 > (lastObserved ?? .distantPast),
           let snapshot = try? RateLimitParser.parseClaude(cached.0, observedAt: cached.1) {
            return .updated(snapshot, .statusLine)
        }
        if liveEnabled, let lastLiveError { return .failed(lastLiveError) }
        return .unchanged
    }
}

public enum ClaudeBridgeState: Sendable, Equatable {
    case notConfigured, enabled, enabledOtherPath, otherCommand(String), settingsUnreadable
}

// [AI修改] 只在用户点击“启用/停用”时修改 Claude 的 settings.json；原 statusLine 备份后可完整恢复。
public enum ClaudeStatusLineBridge {
    public static let backupFileName = "codexpedal-statusline-backup.json"

    public static func command(for executable: URL) -> String { "\"\(executable.path)\" \(ClaudeStatusLineHook.argument)" }

    static func settingsURL(_ directory: URL) -> URL { directory.appendingPathComponent("settings.json") }

    private static func load(_ directory: URL) -> [String: Any]? {
        let url = settingsURL(directory)
        guard FileManager.default.fileExists(atPath: url.path) else { return [:] }
        guard let size = (try? FileManager.default.attributesOfItem(atPath: url.path))?[.size] as? NSNumber, size.intValue <= 1_048_576,
              let data = try? Data(contentsOf: url) else { return nil }
        if data.allSatisfy({ $0 == 32 || $0 == 10 || $0 == 13 || $0 == 9 }) { return [:] }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    private static func save(_ object: [String: Any], to url: URL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        var data = try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .withoutEscapingSlashes])
        data.append(10)
        try data.write(to: url, options: .atomic)
    }

    private static func currentCommand(_ root: [String: Any]) -> String? {
        guard let statusLine = root["statusLine"] else { return nil }
        return (statusLine as? [String: Any])?["command"] as? String ?? ""
    }

    public static func state(configDirectory: URL, executable: URL) -> ClaudeBridgeState {
        guard let root = load(configDirectory) else { return .settingsUnreadable }
        guard let command = currentCommand(root) else { return .notConfigured }
        guard command.contains(ClaudeStatusLineHook.argument) else { return .otherCommand(command) }
        return command == self.command(for: executable) ? .enabled : .enabledOtherPath
    }

    public static func enable(configDirectory: URL, executable: URL) throws {
        guard var root = load(configDirectory) else { throw QuotaReadError.invalidResponse }
        let settings = settingsURL(configDirectory)
        // [AI修改] 只备份用户自己的状态栏；重复启用或路径变更时不覆盖首次备份。
        if let existing = root["statusLine"], !(currentCommand(root) ?? "").contains(ClaudeStatusLineHook.argument) {
            try save(["statusLine": existing], to: configDirectory.appendingPathComponent(backupFileName))
        }
        let backup = settings.appendingPathExtension("codexpedal.bak")
        if FileManager.default.fileExists(atPath: settings.path), !FileManager.default.fileExists(atPath: backup.path) {
            try FileManager.default.copyItem(at: settings, to: backup)
        }
        root["statusLine"] = ["type": "command", "command": command(for: executable), "padding": 0] as [String: Any]
        try save(root, to: settings)
    }

    public static func disable(configDirectory: URL) throws {
        guard var root = load(configDirectory) else { throw QuotaReadError.invalidResponse }
        guard let command = currentCommand(root), command.contains(ClaudeStatusLineHook.argument) else { return }
        let backupURL = configDirectory.appendingPathComponent(backupFileName)
        if let data = try? Data(contentsOf: backupURL), data.count <= 1_048_576,
           let backup = try? JSONSerialization.jsonObject(with: data) as? [String: Any], let original = backup["statusLine"] {
            root["statusLine"] = original
        } else {
            root.removeValue(forKey: "statusLine")
        }
        try save(root, to: settingsURL(configDirectory))
        try? FileManager.default.removeItem(at: backupURL)
    }
}
