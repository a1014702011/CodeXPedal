// [AI修改] 删除Swift重复业务解析，仅映射共享核心错误到原生界面。
import Foundation

public enum QuotaReadError: Error, LocalizedError {
    case invalidResponse, missingBucket, duplicateWindow, invalidPercent, cliMissing, invalidCLI
    case timeout, processExited, outputTooLarge, cancelled, serverRejected(Int)
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
        }
    }
}
public enum RateLimitParser {
    public static func parse(_ data: Data, observedAt: Date = Date()) throws -> QuotaSnapshot {
        try SharedQuotaCore.parse(data, observedAt: observedAt)
    }
}
