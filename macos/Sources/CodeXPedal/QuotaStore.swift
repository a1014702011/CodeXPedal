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
    // [AI修改] 实时读取是默认的 Claude 额度来源，需要读取钥匙串中的 Claude 登录令牌；用户可在设置中关闭。
    @Published var claudeLive: Bool { didSet { defaults.set(claudeLive, forKey: "claudeLive") } }
    // [AI修改] 用户在“安装并登录 Claude Code”弹窗中选择“不再提示”后不再在启动时询问。
    @Published var claudeSetupDismissed: Bool { didSet { defaults.set(claudeSetupDismissed, forKey: "claudeSetupDismissed") } }
    // [AI修改] 保存两个独立开关；旧配置缺项时默认开启。时间样式只用于展示。
    @Published var providers: QuotaProviders {
        didSet {
            guard providers != oldValue else { return }
            defaults.set(providers.codex, forKey: "codexEnabled")
            defaults.set(providers.claude, forKey: "claudeEnabled")
            onProvidersChanged?()
        }
    }
    @Published var quotaTimeMode: QuotaTimeMode { didSet { defaults.set(quotaTimeMode.rawValue, forKey: "quotaTimeMode") } }
    var onProvidersChanged: (() -> Void)?
    init() {
        providers = QuotaProviders(storedCodex: defaults.object(forKey: "codexEnabled"),
                                  storedClaude: defaults.object(forKey: "claudeEnabled"))
        quotaTimeMode = QuotaTimeMode(rawValue: defaults.string(forKey: "quotaTimeMode") ?? "") ?? .observed
        menuOnly = defaults.bool(forKey: "menuOnly")
        let font = defaults.double(forKey: "fontSize")
        fontSize = font.isFinite && (20...42).contains(font) ? font : 28
        let interval = defaults.double(forKey: "refreshSeconds")
        refreshSeconds = [30.0, 60, 120, 300].contains(interval) ? interval : 60
        cliPath = defaults.string(forKey: "cliPath") ?? ""
        claudeLive = ClaudeLivePreference.resolve(stored: defaults.object(forKey: "claudeLive"))
        claudeSetupDismissed = defaults.bool(forKey: "claudeSetupDismissed")
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
    // [AI修改] 紧凑视图用选定的时间，详情按每个窗口展示重置时间。
    let timeLabel: String
    let timeValue: String
    let timeMode: QuotaTimeMode
    var stripTime: String { timeMode == .nextReset ? "\(timeLabel) \(timeValue)" : timeValue }
    let status: String
    let source: String
    func value(for window: QuotaWindow) -> String { (stale ? "≈" : "") + window.remainingText }
}

@MainActor final class QuotaStore: ObservableObject {
    @Published private(set) var state = QuotaDisplayState()
    // [AI修改] Claude 额度独立保存快照、错误与来源；安装探测只影响状态提示。
    @Published private(set) var claudeState = QuotaDisplayState()
    @Published private(set) var claudeSource: ClaudeQuotaSource = .unavailable
    @Published private(set) var claudeDetected: Bool?
    @Published private(set) var isRefreshing = false
    @Published private(set) var clock = Date()
    @Published private(set) var cliName = "尚未定位"
    let preferences: Preferences
    private var timer: Timer?
    private var client: CodexRPCClient?
    private let claudeReader = ClaudeQuotaReader()
    private var claudeSetupTimer: Timer?
    private var claudeSetupDeadline = Date.distantPast
    private var stopped = false
    // [AI修改] 同一轮请求用于后台门禁和主线程旧结果隔离。
    private var activeRequest: QuotaRefreshRequest?
    private var resetClaudeThrottle = false
    init(preferences: Preferences) {
        self.preferences = preferences
        preferences.onProvidersChanged = { [weak self] in self?.providersChanged() }
    }
    var hasProviders: Bool { preferences.providers.hasEnabled }
    var canRefresh: Bool { hasProviders && !isRefreshing && !stopped }

    private func providersChanged() {
        if !preferences.providers.claude {
            claudeSetupTimer?.invalidate(); claudeSetupTimer = nil
        }
        if !stopped { start() }
    }
    var stale: Bool { state.isStale(at: clock, refreshInterval: preferences.refreshSeconds) }
    var claudeStale: Bool { claudeState.isStale(at: clock, refreshInterval: preferences.refreshSeconds) }
    var windows: [QuotaWindow] { state.snapshot?.windows ?? [] }
    // [AI修改] 未安装只影响已勾选分组的提示，不再决定显示哪一家。
    var claudeStatus: String {
        if isRefreshing { return "正在读取 Claude 额度…" }
        if claudeDetected == false { return "未检测到 Claude Code，请安装并登录。" }
        if let error = claudeState.errorMessage { return error }
        guard let snapshot = claudeState.snapshot else {
            // [AI修改] 实时读取改为默认后，无数据时优先引导开启实时读取，桥接作为备选。
            return preferences.claudeLive ? "等待 Claude 官方额度" : "尚无数据：请开启实时读取，或按设置页步骤使用状态栏桥接"
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
    // [AI修改] 所有界面只遍历当前启用的来源，并复用同一时间标签和值。
    var rows: [ProviderRow] {
        let mode = preferences.quotaTimeMode
        let now = Date()
        let enabled = preferences.providers
        var result: [ProviderRow] = []
        if enabled.codex {
            let time = QuotaTimePresentation.selected(mode: mode, observed: state.snapshot?.observedAt, windows: windows, now: now)
            result.append(ProviderRow(id: "codex", name: "Codex", showLabel: enabled.claude, windows: windows, stale: stale,
                                      timeLabel: time.label, timeValue: time.value, timeMode: mode, status: status, source: "官方 Codex app-server"))
        }
        if enabled.claude {
            let quota = claudeState.snapshot
            let windows = quota?.windows ?? []
            let time = QuotaTimePresentation.selected(mode: mode, observed: quota?.observedAt, windows: windows, now: now)
            result.append(ProviderRow(id: "claude", name: "Claude", showLabel: true, windows: windows, stale: claudeStale,
                                      timeLabel: time.label, timeValue: time.value, timeMode: mode, status: claudeStatus, source: claudeSourceText))
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
        activeRequest?.cancel()
        client?.cancel()
        stopped = false
        timer?.invalidate(); timer = nil
        // [AI修改] 两项均关闭时不创建刷新计时器。
        guard hasProviders else { return }
        let interval = preferences.refreshSeconds
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { [weak self] _ in
            // [AI修改] 每层并发闭包独立弱捕获，避免跨执行器共享可变weak绑定。
            Task { @MainActor [weak self] in self?.refresh() }
        }
        timer?.tolerance = min(10, interval / 5)
        refresh()
    }
    func refresh() {
        guard canRefresh else { return }
        isRefreshing = true; clock = Date()
        let explicitPath = preferences.cliPath
        let claudeLive = preferences.claudeLive
        let lastClaude = claudeState.snapshot?.observedAt
        let enabled = preferences.providers
        let request = QuotaRefreshRequest(providers: enabled)
        activeRequest = request
        let reader = enabled.codex ? CodexRPCClient() : nil
        client = reader
        let claudeReader = self.claudeReader
        let shouldResetClaudeThrottle = enabled.claude && resetClaudeThrottle
        if shouldResetClaudeThrottle { resetClaudeThrottle = false }
        DispatchQueue.global(qos: .utility).async { [weak self] in
            // [AI修改] 关闭来源时不探测、不启动进程，也不进入 Claude 额度读取器。
            let result: Result<(QuotaSnapshot, String), Error>?
            if let reader, request.canReadCodex {
                do {
                    let cli = try CodexLocator.resolve(explicitPath: explicitPath)
                    result = .success((try reader.read(executable: cli), cli.lastPathComponent))
                } catch { result = .failure(error) }
            } else { result = nil }
            let claudeDetected: Bool? = request.canReadClaude ? ClaudeLocator.isInstalled() : nil
            let claudeOutcome: ClaudeReadOutcome
            if claudeDetected == true, request.canReadClaude {
                if shouldResetClaudeThrottle { claudeReader.resetLiveThrottle() }
                claudeOutcome = claudeReader.read(liveEnabled: claudeLive, now: Date(), lastObserved: lastClaude, request: request)
            } else { claudeOutcome = .unchanged }
            DispatchQueue.main.async { [weak self] in
                guard let self else { return }
                self.client = nil; self.activeRequest = nil; self.isRefreshing = false
                guard !self.stopped else { return }
                guard !request.isCancelled else { self.refresh(); return }
                self.clock = Date()
                self.claudeDetected = claudeDetected
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
    // [AI修改] 打开“终端”执行官方安装与登录，并每 5 秒检查登录状态（最多 10 分钟）；登录成功后自动开启实时读取并立即刷新。
    @discardableResult func startClaudeSetup() -> Bool {
        do { try ClaudeCliSetup.launch() } catch {
            let alert = NSAlert()
            alert.messageText = "无法打开“终端”"
            alert.informativeText = "也可以手动在终端中运行：\n\(ClaudeCliSetup.installCommand)\n然后运行 claude auth login"
            alert.runModal()
            return false
        }
        claudeSetupDeadline = Date().addingTimeInterval(600)
        claudeSetupTimer?.invalidate()
        claudeSetupTimer = Timer.scheduledTimer(withTimeInterval: 5, repeats: true) { [weak self] _ in
            Task { @MainActor [weak self] in self?.checkClaudeSetup() }
        }
        return true
    }
    private func checkClaudeSetup() {
        guard preferences.providers.claude, Date() < claudeSetupDeadline else { claudeSetupTimer?.invalidate(); claudeSetupTimer = nil; return }
        guard ClaudeCliSetup.status() == .ready else { return }
        claudeSetupTimer?.invalidate(); claudeSetupTimer = nil
        // [AI修改] 将重置交给后台，不在主线程等待读取器锁。
        resetClaudeThrottle = true
        preferences.claudeLive = true
        claudeDetected = true
        start()
    }
    func stop(force: Bool = false) {
        // [AI修改] 睡眠和退出复用取消逻辑，停用后的结果不能回写。
        stopped = true; timer?.invalidate(); timer = nil
        activeRequest?.cancel(); client?.cancel(force: force)
    }
}
