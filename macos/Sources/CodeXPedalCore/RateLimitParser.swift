// [AI修改] 删除Swift重复业务解析，仅映射共享核心错误到原生界面。
import Foundation

public enum QuotaReadError: Error, LocalizedError {
    case invalidResponse, missingBucket, duplicateWindow, invalidPercent, cliMissing, invalidCLI
    case timeout, processExited, outputTooLarge, cancelled, serverRejected(Int)
    // [AI修改] Claude 额度相关错误，文案不回显令牌或原始响应。
    case claudeMissing, claudeNoLogin, claudeKeychainDenied, claudeTokenExpired, claudeUnauthorized, claudeRateLimited, claudeHTTP(Int), claudeNetwork
    public var errorDescription: String? {
        switch self {
        case .invalidResponse: return "官方额度响应格式无法识别，请检查CLI版本。"
        case .missingBucket: return "官方响应没有Codex额度，请检查登录账户。"
        case .duplicateWindow: return "官方响应包含重复的额度窗口，本次未更新。"
        case .invalidPercent: return "官方额度百分比无效，本次未更新。"
        case .cliMissing: return "未找到官方Codex CLI，请安装并登录，或在设置中指定路径。"
        case .invalidCLI: return "指定的CLI路径不可执行，请重新选择。"
        case .timeout: return "读取官方额度超时，请检查网络和Codex登录状态。"
        case .processExited: return "Codex CLI提前退出，请检查版本与登录状态。"
        case .outputTooLarge: return "CLI输出超过安全上限，本次读取已停止。"
        case .cancelled: return "额度读取已取消。"
        case .serverRejected(let code): return "官方CLI拒绝额度读取（错误码\(code)），请检查登录与版本。"
        case .claudeMissing: return "Claude 响应没有5小时或7天额度，本次未更新。"
        case .claudeNoLogin: return "未找到 Claude 订阅登录信息，请先用 Claude Code 登录 Pro/Max 账号。"
        case .claudeKeychainDenied: return "未获得钥匙串授权，Claude 实时读取已暂停。重启 CodeXPedal 后可重新授权，或改用状态栏桥接。"
        case .claudeTokenExpired: return "Claude 登录令牌已过期，请打开一次 Claude Code 让其自动刷新。"
        case .claudeUnauthorized: return "Claude 登录令牌无效或已过期，请打开一次 Claude Code 后重试。"
        case .claudeRateLimited: return "Claude 额度接口请求过于频繁，稍后自动重试。"
        case .claudeHTTP(let code): return "Claude 额度接口返回 HTTP \(code)。"
        case .claudeNetwork: return "Claude 额度接口网络失败或超时，请检查网络。"
        }
    }
}
public enum RateLimitParser {
    public static func parse(_ data: Data, observedAt: Date = Date()) throws -> QuotaSnapshot {
        try SharedQuotaCore.parse(data, observedAt: observedAt)
    }
    // [AI修改] Claude 额度同样只交给共享核心解析。
    public static func parseClaude(_ data: Data, observedAt: Date = Date()) throws -> QuotaSnapshot {
        try SharedQuotaCore.parse(data, observedAt: observedAt, mode: SharedQuotaCore.claudeMode)
    }
}
