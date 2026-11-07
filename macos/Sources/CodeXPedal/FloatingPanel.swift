// [AI修改] A 方向原生横条；额度、时间是唯一常驻内容，不包含品牌图标和标题。
import AppKit
import SwiftUI
import CodeXPedalCore

private struct PanelMaterial: NSViewRepresentable {
    func makeNSView(context: Context) -> NSVisualEffectView {
        let view = NSVisualEffectView(); view.material = .hudWindow; view.blendingMode = .behindWindow; view.state = .active
        return view
    }
    func updateNSView(_ view: NSVisualEffectView, context: Context) {}
}

// [AI修改] 上报横条实际渲染尺寸：数据变化时窗口跟随渲染结果调整，避免读到 SwiftUI 刷新前的旧尺寸而裁切内容。
private struct StripSizeKey: PreferenceKey {
    static let defaultValue = CGSize.zero
    static func reduce(value: inout CGSize, nextValue: () -> CGSize) { value = nextValue() }
}

private extension View {
    // [AI修改] 文字宽度只比可用宽度差一点时，SwiftUI 会把“97%”折成两行；读数和标签一律固定单行。
    func singleLine() -> some View { lineLimit(1).fixedSize(horizontal: true, vertical: false) }
}

@MainActor struct CompactStrip: View {
    @ObservedObject var store: QuotaStore
    @ObservedObject var preferences: Preferences
    let openDetails: () -> Void
    var onLayoutChange: () -> Void = {}
    private let quotaColor = Color(red: 0.34, green: 0.84, blue: 0.71)
    // [AI修改] 两项都启用时上下两行；单项使用完整字号，空态不用空白行。
    private func rowView(_ row: ProviderRow, size: Double) -> some View {
        HStack(spacing: 14) {
            if row.showLabel {
                Text(row.name).font(.system(size: 12, weight: .semibold)).foregroundStyle(.white).singleLine().frame(minWidth: 44, alignment: .leading)
            }
            if row.windows.isEmpty {
                Text("额度").font(.system(size: 12)).foregroundStyle(.white.opacity(0.8)).singleLine()
                Text("--").font(.system(size: size, weight: .semibold, design: .rounded)).foregroundStyle(quotaColor).singleLine()
            } else {
                ForEach(row.windows, id: \.kind) { window in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(window.kind.label).font(.system(size: 12)).foregroundStyle(.white.opacity(0.8)).singleLine()
                        Text(row.value(for: window))
                            .font(.system(size: size, weight: .semibold, design: .rounded))
                            .monospacedDigit().foregroundStyle(row.stale ? Color.white.opacity(0.85) : quotaColor).singleLine()
                    }
                }
            }
            Spacer(minLength: 0)
            Rectangle().fill(.white.opacity(0.3)).frame(width: 1, height: size)
            // [AI修改] 重置模式包含周期及可能的日期，时间列适度减小字号。
            Text(row.stripTime).font(.system(size: size * (row.timeMode == .nextReset ? 0.60 : 0.72), weight: .medium, design: .monospaced))
                .foregroundStyle(.white).singleLine()
        }
    }
    var body: some View {
        let rows = store.rows
        let size = rows.count > 1 ? max(20, preferences.fontSize * 0.8) : preferences.fontSize
        VStack(alignment: .leading, spacing: 8) {
            if rows.isEmpty { Text(QuotaTimePresentation.empty).font(.system(size: size * 0.72)).foregroundStyle(.white).singleLine() }
            ForEach(rows) { row in rowView(row, size: size) }
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
        .background(Color.black.opacity(0.72)).background(PanelMaterial())
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.white.opacity(0.23), lineWidth: 1))
        .fixedSize()
        .background(GeometryReader { proxy in Color.clear.preference(key: StripSizeKey.self, value: proxy.size) })
        .onPreferenceChange(StripSizeKey.self) { _ in onLayoutChange() }
        .help((rows.isEmpty ? QuotaTimePresentation.empty : rows.map { "\($0.name)：\($0.status)，\($0.timeLabel) \($0.timeValue)" }.joined(separator: "\n")) + "\n双击查看详情，拖动调整位置。")
        .accessibilityElement(children: .combine)
        .accessibilityLabel(rows.isEmpty ? QuotaTimePresentation.empty : rows.map { "\($0.name)\($0.status)，\($0.timeLabel) \($0.timeValue)" }.joined(separator: "；"))
    }
}

// [AI修改] 显式处理无边框面板的鼠标拖拽，避免依赖窗口背景拖动在 SwiftUI 内容视图中失效。
private final class DraggablePanel: NSPanel {
    var onDoubleClick: (() -> Void)?
    var onMove: ((NSPoint) -> Void)?
    private var dragStartMouse: NSPoint?
    private var dragStartOrigin: NSPoint?

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 {
            onDoubleClick?()
            return
        }
        dragStartMouse = NSEvent.mouseLocation
        dragStartOrigin = frame.origin
    }

    override func mouseDragged(with event: NSEvent) {
        guard let dragStartMouse, let dragStartOrigin else { return }
        let mouse = NSEvent.mouseLocation
        onMove?(NSPoint(x: dragStartOrigin.x + mouse.x - dragStartMouse.x,
                        y: dragStartOrigin.y + mouse.y - dragStartMouse.y))
    }

    override func mouseUp(with event: NSEvent) {
        dragStartMouse = nil
        dragStartOrigin = nil
        onMove?(frame.origin)
    }
}

@MainActor final class FloatingPanelController: NSObject, NSWindowDelegate {
    private let panel: DraggablePanel
    private let host: NSHostingView<CompactStrip>
    private let preferences: Preferences
    private var movingProgrammatically = false
    init(store: QuotaStore, preferences: Preferences, openDetails: @escaping () -> Void) {
        self.preferences = preferences
        host = NSHostingView(rootView: CompactStrip(store: store, preferences: preferences, openDetails: openDetails))
        panel = DraggablePanel(contentRect: .zero, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        super.init()
        // [AI修改] self 可用后再接入尺寸回调；异步执行，避免在 SwiftUI 布局过程中调整窗口。
        host.rootView = CompactStrip(store: store, preferences: preferences, openDetails: openDetails,
                                     onLayoutChange: { [weak self] in DispatchQueue.main.async { self?.updateVisibility() } })
        panel.title = "CodeXPedal"; panel.isFloatingPanel = true; panel.level = .floating
        panel.isOpaque = false; panel.backgroundColor = .clear; panel.hasShadow = true
        panel.hidesOnDeactivate = false; panel.isMovableByWindowBackground = true
        panel.isReleasedWhenClosed = false; panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.contentView = host; panel.delegate = self
        panel.onDoubleClick = openDetails
        panel.onMove = { [weak self] origin in self?.movePanel(to: origin) }
        let size = host.fittingSize
        let screen = NSScreen.main?.visibleFrame ?? NSRect(x: 0, y: 0, width: 1280, height: 800)
        let origin = preferences.savedOrigin ?? NSPoint(x: screen.maxX - size.width - 24, y: screen.maxY - size.height - 24)
        panel.setFrame(NSRect(origin: origin, size: size), display: false)
        updateVisibility()
    }
    // [AI修改] 拖动时限制浮窗保留在当前屏幕可见区域，并统一保存用户最终位置。
    private func movePanel(to origin: NSPoint) {
        // [AI修改] 统一取屏幕可见区域，修复 NSScreen 与 NSRect 类型不一致导致的编译失败。
        let screen = NSScreen.screens.first(where: { $0.frame.contains(origin) })?.visibleFrame ?? NSScreen.main?.visibleFrame ?? .zero
        let maxX = max(screen.minX, screen.maxX - panel.frame.width)
        let maxY = max(screen.minY, screen.maxY - panel.frame.height)
        let bounded = NSPoint(x: min(max(origin.x, screen.minX), maxX), y: min(max(origin.y, screen.minY), maxY))
        panel.setFrameOrigin(bounded)
        preferences.saveOrigin(bounded)
    }
    func updateVisibility() {
        movingProgrammatically = true
        defer { movingProgrammatically = false }
        host.layoutSubtreeIfNeeded()
        let size = host.fittingSize
        let screens = NSScreen.screens.map(\.visibleFrame)
        let screen = screens.first(where: { $0.intersects(panel.frame) }) ?? NSScreen.main?.visibleFrame ?? .zero
        let safeSize = NSSize(width: min(size.width, max(100, screen.width)), height: min(size.height, max(60, screen.height)))
        let origin = NSPoint(x: max(screen.minX, min(panel.frame.minX, screen.maxX - safeSize.width)), y: max(screen.minY, min(panel.frame.minY, screen.maxY - safeSize.height)))
        panel.setFrame(NSRect(origin: origin, size: safeSize), display: true)
        if preferences.menuOnly { panel.orderOut(nil) } else { panel.orderFrontRegardless() }
    }
    func windowDidMove(_ notification: Notification) {
        guard !movingProgrammatically else { return }
        preferences.saveOrigin(panel.frame.origin)
    }
    func close() { panel.orderOut(nil) }
}
