// [AI修改] 原生 NSStatusItem 与 NSMenu；宽度贴合实际读数，不覆盖其他应用的系统栏区域。
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
    // [AI修改] 只装一个工具时保持原格式；两者都装时缩写为「名称 5小时%/7天%」，避免菜单栏过长。
    private func title(for row: ProviderRow) -> String {
        guard row.showLabel else {
            return row.windows.isEmpty ? "额度 --" : row.windows.map { "\($0.kind.label) \(row.value(for: $0))" }.joined(separator: "  ")
        }
        return "\(row.name) " + (row.windows.isEmpty ? "--" : row.windows.map(row.value(for:)).joined(separator: "/"))
    }
    func update() {
        let rows = store.rows
        let text = rows.map { title(for: $0) }.joined(separator: "  ")
        // [AI修改] 删除按“≈100%”预留宽度的逻辑，避免空白浪费；保持 variableLength 贴合实际文字，等宽数字保证同位数刷新不抖动。
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold), .foregroundColor: NSColor.labelColor]
        item.button?.attributedTitle = NSAttributedString(string: text, attributes: attributes)
        let order = rows.count > 1 ? "\n斜杠前为5小时、后为7天剩余" : ""
        item.button?.toolTip = rows.map { "\($0.name)：\($0.status)，最近成功读取 \($0.updatedAt)" }.joined(separator: "\n") + order
        item.button?.setAccessibilityLabel("CodeXPedal 剩余额度，\(text)")
        rebuildMenu()
    }
    func menuWillOpen(_ menu: NSMenu) { rebuildMenu() }
    private func rebuildMenu() {
        menu.removeAllItems()
        addText("CodeXPedal · 剩余额度", bold: true)
        // [AI修改] 每个已安装工具一段：名称、各窗口剩余与重置时间、最近成功时间和状态。
        for row in store.rows {
            menu.addItem(.separator())
            if row.showLabel { addText(row.name, bold: true) }
            if row.windows.isEmpty { addText("暂无可显示额度") }
            for window in row.windows {
                addText("\(window.kind.label)    \(row.value(for: window))")
                if let reset = window.resetsAt { addText("重置：\(reset.formatted(date: .abbreviated, time: .shortened))") }
            }
            addText("最近成功：\(row.updatedAt)")
            addText(row.status)
        }
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
    // [AI修改] 说明行仍禁用以保持不可点击、无悬停高亮；用属性标题指定文字色，避免系统灰字，颜色随深浅色模式变化。
    private func addText(_ text: String, bold: Bool = false) {
        let size = NSFont.menuFont(ofSize: 0).pointSize
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.attributedTitle = NSAttributedString(string: text, attributes: [
            .font: NSFont.systemFont(ofSize: size, weight: bold ? .semibold : .regular),
            .foregroundColor: NSColor.labelColor
        ])
        item.isEnabled = false
        menu.addItem(item)
    }
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
