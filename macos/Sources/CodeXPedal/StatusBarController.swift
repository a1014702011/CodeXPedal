// [AI修改] 原生 NSStatusItem 与 NSMenu；固定读数空间，不覆盖其他应用的系统栏区域。
import AppKit
import CodeXPedalCore

@MainActor final class StatusBarController: NSObject, NSMenuDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()
    private let store: QuotaStore
    private let preferences: Preferences
    private let windows: AuxiliaryWindows
    init(store: QuotaStore, preferences: Preferences, windows: AuxiliaryWindows) {
        self.store = store; self.preferences = preferences; self.windows = windows
        super.init(); menu.delegate = self; menu.autoenablesItems = false; item.menu = menu; update()
    }
    func update() {
        let entries = store.windows
        let text = entries.isEmpty ? "额度 --" : entries.map { "\($0.kind.label) \(store.value(for: $0))" }.joined(separator: "  ")
        // [AI修改] 只按已确认拓扑和最宽读数预留空间，避免固定宽度浪费或百分比刷新挤动邻项。
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold), .foregroundColor: NSColor.labelColor]
        let maximum = entries.isEmpty ? "额度 --" : entries.map { "\($0.kind.label) ≈100%" }.joined(separator: "  ")
        item.length = ceil(NSAttributedString(string: maximum, attributes: attributes).size().width) + 16
        item.button?.attributedTitle = NSAttributedString(string: text, attributes: attributes)
        item.button?.toolTip = "\(store.status)\n最近成功读取：\(store.updatedAt)"
        item.button?.setAccessibilityLabel("CodeXPedal 剩余额度，\(text)，\(store.status)")
        rebuildMenu()
    }
    func menuWillOpen(_ menu: NSMenu) { rebuildMenu() }
    private func rebuildMenu() {
        menu.removeAllItems()
        addText("CodeXPedal · 剩余额度")
        menu.addItem(.separator())
        if store.windows.isEmpty { addText("暂无可显示额度") }
        for window in store.windows {
            addText("\(window.kind.label)    \(store.value(for: window))")
            if let reset = window.resetsAt { addText("重置：\(reset.formatted(date: .abbreviated, time: .shortened))") }
        }
        addText("最近成功：\(store.updatedAt)")
        addText(store.status)
        menu.addItem(.separator())
        let refresh = addAction(store.isRefreshing ? "正在刷新…" : "立即刷新", #selector(refreshNow), "r")
        refresh.isEnabled = !store.isRefreshing
        let visibility = addAction("只显示菜单栏", #selector(toggleFloating))
        visibility.state = preferences.menuOnly ? .on : .off
        addAction("额度详情…", #selector(showDetails), "d")
        addAction("设置…", #selector(showSettings), ",")
        menu.addItem(.separator())
        addAction("退出 CodeXPedal", #selector(quit), "q")
    }
    private func addText(_ text: String) { let item = NSMenuItem(title: text, action: nil, keyEquivalent: ""); item.isEnabled = false; menu.addItem(item) }
    @discardableResult private func addAction(_ title: String, _ action: Selector, _ key: String = "") -> NSMenuItem {
        let actionItem = NSMenuItem(title: title, action: action, keyEquivalent: key); actionItem.target = self; menu.addItem(actionItem); return actionItem
    }
    @objc private func refreshNow() { store.refresh() }
    @objc private func toggleFloating() { preferences.menuOnly.toggle() }
    @objc private func showDetails() { windows.showDetails() }
    @objc private func showSettings() { windows.showSettings() }
    @objc private func quit() { NSApp.terminate(nil) }
    func close() { NSStatusBar.system.removeStatusItem(item) }
}
