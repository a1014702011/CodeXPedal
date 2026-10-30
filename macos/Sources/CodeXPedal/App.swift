// [AI修改] 独立原生入口，启动后只创建菜单栏和可选横条，不加载旧窗口或后台统计器。
import AppKit
import Combine
import Foundation
import CodeXPedalCore
import Darwin

@MainActor final class ApplicationDelegate: NSObject, NSApplicationDelegate {
    private let preferences = Preferences()
    private var store: QuotaStore!
    private var auxiliary: AuxiliaryWindows!
    private var statusBar: StatusBarController!
    private var floating: FloatingPanelController!
    private var subscriptions = Set<AnyCancellable>()
    private var terminating = false
    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        store = QuotaStore(preferences: preferences)
        auxiliary = AuxiliaryWindows(store: store, preferences: preferences)
        floating = FloatingPanelController(store: store, preferences: preferences, openDetails: { [weak self] in self?.auxiliary.showDetails() })
        statusBar = StatusBarController(store: store, preferences: preferences, windows: auxiliary)
        store.objectWillChange.receive(on: DispatchQueue.main).sink { [weak self] _ in self?.updatePresentation() }.store(in: &subscriptions)
        preferences.objectWillChange.receive(on: DispatchQueue.main).sink { [weak self] _ in self?.updatePresentation() }.store(in: &subscriptions)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(willSleep), name: NSWorkspace.willSleepNotification, object: nil)
        NSWorkspace.shared.notificationCenter.addObserver(self, selector: #selector(didWake), name: NSWorkspace.didWakeNotification, object: nil)
        NotificationCenter.default.addObserver(self, selector: #selector(screenChanged), name: NSApplication.didChangeScreenParametersNotification, object: nil)
        store.start()
    }
    private func updatePresentation() { guard !terminating else { return }; statusBar.update(); floating.updateVisibility() }
    @objc private func willSleep() { store.stop() }
    @objc private func didWake() { store.start() }
    @objc private func screenChanged() { floating.updateVisibility() }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }
    // [AI修改] 退出时保持事件循环直到自有读取清理完成；最长等待2秒，不让 CLI 因父进程先退出而遗留。
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let store, store.isRefreshing else { return .terminateNow }
        guard !terminating else { return .terminateLater }
        terminating = true; store.stop()
        let deadline = ProcessInfo.processInfo.systemUptime + 2
        func finishWhenReady() {
            if !store.isRefreshing || ProcessInfo.processInfo.systemUptime >= deadline {
                store.stop(force: true); sender.reply(toApplicationShouldTerminate: true)
            } else {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.05) { finishWhenReady() }
            }
        }
        DispatchQueue.main.async { finishWhenReady() }
        return .terminateLater
    }
    func applicationWillTerminate(_ notification: Notification) {
        // [AI修改] 退出清理也兼容启动未完成，停止订阅后不再把浮窗重新显示出来。
        terminating = true; subscriptions.removeAll()
        store?.stop(force: true); floating?.close(); statusBar?.close(); auxiliary?.close()
        NSWorkspace.shared.notificationCenter.removeObserver(self); NotificationCenter.default.removeObserver(self)
    }
}

@main enum CodeXPedalMain {
    @MainActor static func main() {
        // [AI修改] CLI 提前关闭管道时转为可处理的写入错误，不能因 SIGPIPE 直接崩溃。
        signal(SIGPIPE, SIG_IGN)
        let flags = CommandLine.arguments.dropFirst().filter { $0.hasPrefix("--") }
        if flags.contains("--help") {
            print("CodeXPedal：直接启动菜单栏与横条浮窗；--dump-json 只读查询官方额度。")
            return
        }
        guard flags.allSatisfy({ $0 == "--dump-json" }) else {
            // [AI修改] 旧自测参数必须明确失败，不能误启动 GUI 后挂住旧测试脚本。
            FileHandle.standardError.write(Data("不支持此命令；新测试请运行 make test。\n".utf8)); exit(64)
        }
        // [AI修改] 只读诊断供 Mac 集成验证使用；错误不回显原始响应或账户凭据。
        if CommandLine.arguments.contains("--dump-json") {
            do {
                let cli = try CodexLocator.resolve(explicitPath: "")
                let snapshot = try CodexRPCClient().read(executable: cli)
                let windows = snapshot.windows.map { ["windowDurationMins": $0.kind.rawValue, "remainingPercent": $0.remainingPercent] as [String: Any] }
                let json = try JSONSerialization.data(withJSONObject: ["windows": windows, "observedAt": snapshot.observedAt.timeIntervalSince1970], options: [.prettyPrinted, .sortedKeys])
                FileHandle.standardOutput.write(json); FileHandle.standardOutput.write(Data("\n".utf8))
            } catch {
                let message = (error as? QuotaReadError)?.localizedDescription ?? "额度读取失败。"
                FileHandle.standardError.write(Data((message + "\n").utf8)); exit(1)
            }
            return
        }
        let application = NSApplication.shared
        let delegate = ApplicationDelegate(); application.delegate = delegate
        withExtendedLifetime(delegate) { application.run() }
    }
}
