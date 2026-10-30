// [AI修改] 只建模本产品支持的真实周期，不携带账户凭据、会话正文或月额度。
import Foundation

public enum QuotaKind: Int, Codable, Hashable, Sendable {
    case fiveHour = 300
    case sevenDay = 10080
    public var label: String { self == .fiveHour ? "5小时" : "7天" }
}

public struct QuotaWindow: Equatable, Sendable {
    public let kind: QuotaKind
    public let usedPercent: Double
    public let resetsAt: Date?
    // [AI修改] 不在宿主复制剩余计算，直接使用共享核心。
    public var remainingPercent: Double { SharedQuotaCore.remaining(usedPercent) }
    public var remainingText: String { "\(Int(remainingPercent.rounded()))%" }
}

public struct QuotaSnapshot: Equatable, Sendable {
    public let windows: [QuotaWindow]
    public let observedAt: Date
}

// [AI修改] 失败保留同一成功快照；仅新的成功响应可以改变窗口拓扑和成功时间。
public struct QuotaDisplayState: Equatable, Sendable {
    public private(set) var snapshot: QuotaSnapshot?
    public private(set) var errorMessage: String?
    public init() {}
    // [AI修改] 状态替换由共享核心处理，宿主只保留界面错误文字。
    public mutating func accept(_ value: QuotaSnapshot) { snapshot = SharedQuotaCore.apply(previous: snapshot, incoming: value, failed: false); errorMessage = nil }
    public mutating func fail(_ message: String) { snapshot = SharedQuotaCore.apply(previous: snapshot, incoming: nil, failed: true); errorMessage = message }
    public func isStale(at now: Date, refreshInterval: TimeInterval) -> Bool {
        SharedQuotaCore.isStale(snapshot, at: now, interval: refreshInterval, failed: errorMessage != nil)
    }
}
