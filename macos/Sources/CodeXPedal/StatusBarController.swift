// [AI修改] 原生 NSStatusItem 与 NSMenu；宽度贴合实际读数，不覆盖其他应用的系统栏区域。
import AppKit
import CodeXPedalCore

@MainActor final class StatusBarController: NSObject, NSMenuDelegate {
    private let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let menu = NSMenu()
    private let store: QuotaStore
    private let preferences: Preferences
    private let windows: AuxiliaryWindows
    // [AI修改] 系统菜单文字左缩进实测29pt，标签自带2pt内边距，所以视图内取27pt。
    private static let textRowInset: CGFloat = 27
    private static let textRowHeight: CGFloat = 24
    private static let textRowTrailing: CGFloat = 14
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
        // [AI修改] 单项只占实际宽度；两项关闭时保留可打开菜单的空态入口。
        let text = rows.isEmpty ? QuotaTimePresentation.empty : rows.map { "\(title(for: $0)) · \($0.stripTime)" }.joined(separator: "  ")
        // [AI修改] 删除按“≈100%”预留宽度的逻辑，避免空白浪费；保持 variableLength 贴合实际文字，等宽数字保证同位数刷新不抖动。
        let attributes: [NSAttributedString.Key: Any] = [.font: NSFont.monospacedDigitSystemFont(ofSize: 13, weight: .semibold), .foregroundColor: NSColor.labelColor]
        item.button?.attributedTitle = NSAttributedString(string: text, attributes: attributes)
        let order = rows.count > 1 ? "\n斜杠前为5小时、后为7天剩余" : ""
        item.button?.toolTip = rows.isEmpty ? QuotaTimePresentation.empty : rows.map {
            "\($0.name)：\($0.status)，\($0.timeLabel) \($0.timeValue)"
        }.joined(separator: "\n") + order
        item.button?.setAccessibilityLabel("CodeXPedal 剩余额度，\(text)")
        rebuildMenu()
    }
    func menuWillOpen(_ menu: NSMenu) { rebuildMenu() }
    private func rebuildMenu() {
        menu.removeAllItems()
        addText("CodeXPedal · 剩余额度", bold: true)
        // [AI修改] 只展示已启用的分组；重置模式在菜单中也按周期显示。
        if !store.hasProviders { addText(QuotaTimePresentation.empty) }
        for row in store.rows {
            menu.addItem(.separator())
            if row.showLabel { addText(row.name, bold: true) }
            if row.windows.isEmpty { addText("暂无可显示额度") }
            for window in row.windows {
                addText("\(window.kind.label)    \(row.value(for: window))")
                if row.timeMode == .nextReset { addText("重置：\(QuotaTimePresentation.reset(window.resetsAt, now: Date()))") }
            }
            if row.timeMode == .observed || row.windows.isEmpty { addText("\(row.timeLabel)：\(row.timeValue)") }
            addText(row.status)
        }
        menu.addItem(.separator())
        let refresh = addAction(store.isRefreshing ? "正在刷新…" : "立即刷新", #selector(refreshNow), "r")
        refresh.isEnabled = store.canRefresh
        let visibility = addAction("只显示菜单栏", #selector(toggleFloating))
        visibility.state = preferences.menuOnly ? .on : .off
        addAction("额度详情…", #selector(showDetails), "d")
        addAction("设置…", #selector(showSettings), ",")
        menu.addItem(.separator())
        addAction("退出 CodeXPedal", #selector(quit), "q")
    }
    // [AI修改] 真机验证：禁用项即使用属性标题指定颜色，系统仍画成灰色，所以说明行改用自定义视图。
    // 视图行不响应点击、无悬停高亮，文字色随深浅色模式变化；行高和左缩进按系统菜单项实测值对齐。
    private func addText(_ text: String, bold: Bool = false) {
        let label = NSTextField(labelWithString: text)
        label.font = .systemFont(ofSize: NSFont.menuFont(ofSize: 0).pointSize, weight: bold ? .semibold : .regular)
        label.textColor = .labelColor
        label.sizeToFit()
        label.setFrameOrigin(NSPoint(x: Self.textRowInset, y: ((Self.textRowHeight - label.frame.height) / 2).rounded()))
        let row = NSView(frame: NSRect(x: 0, y: 0, width: label.frame.maxX + Self.textRowTrailing, height: Self.textRowHeight))
        row.addSubview(label)
        let item = NSMenuItem(title: text, action: nil, keyEquivalent: "")
        item.view = row
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
