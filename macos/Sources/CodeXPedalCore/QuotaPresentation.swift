// [AI修改] 集中管理额度显示偏好及本轮读取门禁，菜单、浮窗和详情使用同一时间口径。
import Foundation
#if canImport(FoundationNetworking)
import FoundationNetworking
#endif

public struct QuotaProviders: Equatable, Sendable {
    public var codex: Bool
    public var claude: Bool
    public var hasEnabled: Bool { codex || claude }

    public init(codex: Bool, claude: Bool) { self.codex = codex; self.claude = claude }

    public init(storedCodex: Any?, storedClaude: Any?) {
        self.init(codex: storedCodex as? Bool ?? true, claude: storedClaude as? Bool ?? true)
    }
}

public enum QuotaTimeMode: String, CaseIterable, Hashable, Sendable {
    case observed, nextReset
}

public enum QuotaTimePresentation {
    public static let empty = "没有可获取的 AI"

    // [AI修改] 仅展示未来的真实重置时间，跨天带日期，不用查询间隔猜测。
    public static func reset(_ date: Date?, now: Date, zone: TimeZone = .current) -> String {
        guard let date, date > now else { return "--" }
        var calendar = Calendar(identifier: .gregorian); calendar.timeZone = zone
        let format = calendar.isDate(date, inSameDayAs: now) ? "HH:mm"
            : calendar.isDate(date, equalTo: now, toGranularity: .year) ? "MM/dd HH:mm" : "yyyy/MM/dd HH:mm"
        return formatted(date, format: format, zone: zone)
    }

    public static func selected(mode: QuotaTimeMode, observed: Date?, windows: [QuotaWindow], now: Date) -> (label: String, value: String) {
        if mode == .observed {
            return ("最近获取", observed.map { formatted($0, format: "HH:mm:ss", zone: .current) } ?? "--:--:--")
        }
        let next = windows.compactMap { window -> (Date, QuotaKind)? in
            guard let reset = window.resetsAt, reset > now else { return nil }
            return (reset, window.kind)
        }.min { lhs, rhs in lhs.0 == rhs.0 ? lhs.1.rawValue < rhs.1.rawValue : lhs.0 < rhs.0 }
        guard let next else { return ("下次重置", "--") }
        return ("\(next.1 == .fiveHour ? "5时" : "7天")重置", reset(next.0, now: now))
    }

    private static func formatted(_ date: Date, format: String, zone: TimeZone) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = zone; formatter.dateFormat = format
        return formatter.string(from: date)
    }
}

// [AI修改] 切换设置可立即封锁后续读取，取消自有网络任务，并丢弃已返回的旧代次结果。
public final class QuotaRefreshRequest: @unchecked Sendable {
    private let lock = NSLock()
    private let providers: QuotaProviders
    private var cancelled = false
    private var networkTask: URLSessionTask?

    public init(providers: QuotaProviders) { self.providers = providers }
    public var isCancelled: Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    public var canReadCodex: Bool { providers.codex && !isCancelled }
    public var canReadClaude: Bool { providers.claude && !isCancelled }

    public func cancel() {
        lock.lock()
        cancelled = true
        let task = networkTask
        networkTask = nil
        lock.unlock()
        task?.cancel()
    }

    func startClaudeNetwork(_ task: URLSessionTask) throws {
        lock.lock(); defer { lock.unlock() }
        guard providers.claude, !cancelled else { task.cancel(); throw QuotaReadError.cancelled }
        networkTask = task
        task.resume()
    }
}
