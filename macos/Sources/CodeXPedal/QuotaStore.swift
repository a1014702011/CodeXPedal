// [AI修改] 主线程统一发布状态，后台串行读取，禁止同频刷新重叠与退出后回写。
import AppKit
import Combine
import CodeXPedalCore

@MainActor final class Preferences: ObservableObject {
    private let defaults = UserDefaults(suiteName: "com.codexpedal.app") ?? .standard
    @Published var menuOnly: Bool { didSet { defaults.set(menuOnly, forKey: "menuOnly") } }
    @Published var fontSize: Double { didSet { defaults.set(fontSize, forKey: "fontSize") } }
    @Published var refreshSeconds: Double { didSet { defaults.set(refreshSeconds, forKey: "refreshSeconds") } }
    @Published var cliPath: String { didSet { defaults.set(cliPath, forKey: "cliPath") } }
    // [AI修改] 实时读取 Claude 额度需要读取钥匙串中的 Claude 登录令牌，默认关闭。
    @Published var claudeLive: Bool { didSet { defaults.set(claudeLive, forKey: "claudeLive") } }
    init() {
        menuOnly = defaults.bool(forKey: "menuOnly")
        let font = defaults.double(forKey: "fontSize")
        fontSize = font.isFinite && (20...42).contains(font) ? font : 28
        let interval = defaults.double(forKey: "refreshSeconds")
        refreshSeconds = [30.0, 60, 120, 300].contains(interval) ? interval : 60
        cliPath = defaults.string(forKey: "cliPath") ?? ""
        claudeLive = defaults.bool(forKey: "claudeLive")
    }
    var savedOrigin: NSPoint? {
        guard defaults.object(forKey: "panelX") != nil, defaults.object(forKey: "panelY") != nil else { return nil }
        let x = defaults.double(forKey: "panelX"), y = defaults.double(forKey: "panelY")
        return x.isFinite && y.isFinite ? NSPoint(x: x, y: y) : nil
    }
    func saveOrigin(_ point: NSPoint) { defaults.set(point.x, forKey: "panelX"); defaults.set(point.y, forKey: "panelY") }
}

// [AI修改] 一行额度的显示数据，菜单栏、横条与详情共用同一份结果。
struct ProviderRow: Identifiable {
    let id: String
    let name: String
    let showLabel: Bool
    let windows: [QuotaWindow]
    let stale: Bool
    let updatedAt: String
    let status: String
    let source: String
    func value(for window: QuotaWindow) -> String { (stale ? "≈" : "") + window.remainingText }
}

@MainActor final class QuotaStore: ObservableObject {
    @Published private(set) var state = QuotaDisplayState()
    // [AI修改] Claude 额度独立保存快照、错误与来源；安装探测决定显示哪些分组。
    @Published private(set) var claudeState = QuotaDisplayState()
    @Published private(set) var claudeSource: ClaudeQuotaSource = .unavailable
    @Published private(set) var codexDetected = true
    @Published private(set) var claudeDetected = false
    @Published private(set) var isRefreshing = false
    @Published private(set) var clock = Date()
    @Published private(set) var cliName = "尚未定位"
    let preferences: Preferences
    private var timer: Timer?
    private var client: CodexRPCClient?
    private let claudeReader = ClaudeQuotaReader()
    private var stopped = false
    private var generation = 0
    init(preferences: Preferences) {
        self.preferences = preferences
        // [AI修改] 启动时先探测安装情况，避免只装 Claude 的用户首帧看到空的 Codex 行。
        let detected = ProviderDetection.detect(codexPath: preferences.cliPath)
        codexDetected = detected.codex; claudeDetected = detected.claude
    }
    var showCodex: Bool { codexDetected || !claudeDetected }
    var showClaude: Bool { claudeDetected }
    var stale: Bool { state.isStale(at: clock, refreshInterval: preferences.refreshSeconds) }
    var claudeStale: Bool { claudeState.isStale(at: clock, refreshInterval: preferences.refreshSeconds) }
    var windows: [QuotaWindow] { state.snapshot?.windows ?? [] }
    var updatedAt: String { Self.clockText(state.snapshot?.observedAt) }
    private static func clockText(_ date: Date?) -> String {
        guard let date else { return "--:--:--" }
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_GB"); formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: date)
    }
    var claudeStatus: String {
        if isRefreshing { return "正在读取 Claude 额度…" }
        if let error = claudeState.errorMessage { return error }
        guard let snapshot = claudeState.snapshot else {
            return preferences.claudeLive ? "等待 Claude 官方额度" : "尚无数据：请在设置中启用 Claude 状态栏桥接后在 Claude Code 发一条消息，或开启实时读取"
        }
        if snapshot.windows.isEmpty { return "Claude 未返回5小时或7天窗口" }
        if !claudeStale { return "Claude 额度" }
        return claudeSource == .statusLine ? "状态栏快照已过期，在 Claude Code 发消息后更新" : "快照已过期，显示上次成功值"
    }
    var claudeSourceText: String {
        switch claudeSource {
        case .live: return "官方实时接口"
        case .statusLine: return "Claude Code 状态栏"
        case .unavailable: return "不可用"
        }
    }
    // [AI修改] 只显示 Codex 时保持原外观；出现 Claude 后两行都带名称。
    var rows: [ProviderRow] {
        var result: [ProviderRow] = []
        if showCodex {
            result.append(ProviderRow(id: "codex", name: "Codex", showLabel: showClaude, windows: windows, stale: stale,
                                      updatedAt: updatedAt, status: status, source: "官方 Codex app-server"))
        }
        if showClaude {
            result.append(ProviderRow(id: "claude", name: "Claude", showLabel: true, windows: claudeState.snapshot?.windows ?? [],
                                      stale: claudeStale, updatedAt: Self.clockText(claudeState.snapshot?.observedAt),
                                      status: claudeStatus, source: claudeSourceText))
        }
        return result
    }
    var status: String {
        if isRefreshing { return "正在读取官方额度…" }
        if let error = state.errorMessage { return error }
        guard let snapshot = state.snapshot else { return "等待官方额度" }
        if snapshot.windows.isEmpty { return "官方未返回支持的5小时或7天窗口" }
        return stale ? "快照已过期，显示上次成功值" : "官方额度"
    }
    func value(for window: QuotaWindow) -> String { (stale ? "≈" : "") + window.remainingText }
    func start() {
        // [AI修改] 重启设置或唤醒时取消旧代次，等旧进程清理后再发下一次读取。
        generation += 1
        client?.cancel()
        stopped = false
        timer?.invalidate()
        let interval = preferences.refreshSeconds
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            // [AI修改] 每层并发闭包独立弱捕获，避免跨执行器共享可变weak绑定。
            Task { @MainActor [weak self] in self?.refresh() }
        }
        timer?.tolerance = min(10, interval / 5)
        refresh()
    }
    func refresh() {
        guard !stopped, !isRefreshing else { return }
        isRefreshing = true; clock = Date()
        let explicitPath = preferences.cliPath
        let claudeLive = preferences.claudeLive
        let lastClaude = claudeState.snapshot?.observedAt
        let reader = CodexRPCClient(); client = reader
        let claudeReader = self.claudeReader
        let requestGeneration = generation
        DispatchQueue.global(qos: .utility).async { [weak self] in
            // [AI修改] 未安装的工具不启动进程、不读取数据；两者都没探测到时仍读取 Codex 以显示定位提示。
            let detected = ProviderDetection.detect(codexPath: explicitPath)
            let result: Result<(QuotaSnapshot, String), Error>?
            if detected.codex || !detected.claude {
                do {
                    let cli = try CodexLocator.resolve(explicitPath: explicitPath)
                    result = .success((try reader.read(executable: cli), cli.lastPathComponent))
                } catch { result = .failure(error) }
            } else { result = nil }
            let claudeOutcome: ClaudeReadOutcome = detected.claude
                ? claudeReader.read(liveEnabled: claudeLive, now: Date(), lastObserved: lastClaude) : .unchanged
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.client = nil; self.isRefreshing = false
                guard !self.stopped else { return }
                guard requestGeneration == self.generation else { self.refresh(); return }
                self.clock = Date()
                self.codexDetected = detected.codex; self.claudeDetected = detected.claude
                switch result {
                case .some(.success(let (snapshot, name))): self.cliName = name; self.state.accept(snapshot)
                case .some(.failure(let error)):
                    // [AI修改] 不显示任意子进程原始输出，避免诊断泄露账户或路径信息。
                    self.state.fail((error as? QuotaReadError)?.localizedDescription ?? "额度读取失败，请检查 CLI 与网络。")
                case .none: break
                }
                switch claudeOutcome {
                case .updated(let snapshot, let source): self.claudeState.accept(snapshot); self.claudeSource = source
                case .failed(let message): self.claudeState.fail(message)
                case .unchanged: if !claudeLive { self.claudeState.clearError() }
                }
            }
        }
    }
    func stop(force: Bool = false) { generation += 1; stopped = true; timer?.invalidate(); timer = nil; client?.cancel(force: force) }
}
