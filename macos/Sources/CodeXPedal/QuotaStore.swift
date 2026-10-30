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
    init() {
        menuOnly = defaults.bool(forKey: "menuOnly")
        let font = defaults.double(forKey: "fontSize")
        fontSize = font.isFinite && (20...42).contains(font) ? font : 28
        let interval = defaults.double(forKey: "refreshSeconds")
        refreshSeconds = [30.0, 60, 120, 300].contains(interval) ? interval : 60
        cliPath = defaults.string(forKey: "cliPath") ?? ""
    }
    var savedOrigin: NSPoint? {
        guard defaults.object(forKey: "panelX") != nil, defaults.object(forKey: "panelY") != nil else { return nil }
        let x = defaults.double(forKey: "panelX"), y = defaults.double(forKey: "panelY")
        return x.isFinite && y.isFinite ? NSPoint(x: x, y: y) : nil
    }
    func saveOrigin(_ point: NSPoint) { defaults.set(point.x, forKey: "panelX"); defaults.set(point.y, forKey: "panelY") }
}

@MainActor final class QuotaStore: ObservableObject {
    @Published private(set) var state = QuotaDisplayState()
    @Published private(set) var isRefreshing = false
    @Published private(set) var clock = Date()
    @Published private(set) var cliName = "尚未定位"
    let preferences: Preferences
    private var timer: Timer?
    private var client: CodexRPCClient?
    private var stopped = false
    private var generation = 0
    init(preferences: Preferences) { self.preferences = preferences }
    var stale: Bool { state.isStale(at: clock, refreshInterval: preferences.refreshSeconds) }
    var windows: [QuotaWindow] { state.snapshot?.windows ?? [] }
    var updatedAt: String {
        guard let date = state.snapshot?.observedAt else { return "--:--:--" }
        let formatter = DateFormatter(); formatter.locale = Locale(identifier: "en_GB"); formatter.dateFormat = "HH:mm:ss"
        return formatter.string(from: date)
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
            Task { @MainActor in self?.refresh() }
        }
        timer?.tolerance = min(10, interval / 5)
        refresh()
    }
    func refresh() {
        guard !stopped, !isRefreshing else { return }
        isRefreshing = true; clock = Date()
        let explicitPath = preferences.cliPath
        let reader = CodexRPCClient(); client = reader
        let requestGeneration = generation
        DispatchQueue.global(qos: .utility).async { [weak self] in
            let result: Result<(QuotaSnapshot, String), Error>
            do {
                let cli = try CodexLocator.resolve(explicitPath: explicitPath)
                result = .success((try reader.read(executable: cli), cli.lastPathComponent))
            } catch { result = .failure(error) }
            DispatchQueue.main.async {
                guard let self else { return }
                self.client = nil; self.isRefreshing = false
                guard !self.stopped else { return }
                guard requestGeneration == self.generation else { self.refresh(); return }
                self.clock = Date()
                switch result {
                case .success(let (snapshot, name)): self.cliName = name; self.state.accept(snapshot)
                case .failure(let error):
                    // [AI修改] 不显示任意子进程原始输出，避免诊断泄露账户或路径信息。
                    self.state.fail((error as? QuotaReadError)?.localizedDescription ?? "额度读取失败，请检查 CLI 与网络。")
                }
            }
        }
    }
    func stop(force: Bool = false) { generation += 1; stopped = true; timer?.invalidate(); timer = nil; client?.cancel(force: force) }
}
