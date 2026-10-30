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

@MainActor struct CompactStrip: View {
    @ObservedObject var store: QuotaStore
    @ObservedObject var preferences: Preferences
    let openDetails: () -> Void
    private let quotaColor = Color(red: 0.34, green: 0.84, blue: 0.71)
    var body: some View {
        HStack(spacing: 14) {
            if store.windows.isEmpty {
                Text("额度").font(.system(size: 12)).foregroundStyle(.white.opacity(0.8))
                Text("--").font(.system(size: preferences.fontSize, weight: .semibold, design: .rounded)).foregroundStyle(quotaColor)
            } else {
                ForEach(store.windows, id: \.kind) { window in
                    HStack(alignment: .firstTextBaseline, spacing: 6) {
                        Text(window.kind.label).font(.system(size: 12)).foregroundStyle(.white.opacity(0.8))
                        Text(store.value(for: window))
                            .font(.system(size: preferences.fontSize, weight: .semibold, design: .rounded))
                            .monospacedDigit().foregroundStyle(store.stale ? Color.white.opacity(0.85) : quotaColor)
                    }
                }
            }
            Rectangle().fill(.white.opacity(0.3)).frame(width: 1, height: preferences.fontSize)
            Text(store.updatedAt).font(.system(size: preferences.fontSize * 0.72, weight: .medium, design: .monospaced)).foregroundStyle(.white)
        }
        .padding(.horizontal, 16).padding(.vertical, 12)
        .background(Color.black.opacity(0.72)).background(PanelMaterial())
        .clipShape(RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).stroke(.white.opacity(0.23), lineWidth: 1))
        .fixedSize()
        .help("\(store.status)\n最近成功读取：\(store.updatedAt)\n双击查看详情，拖动调整位置。")
        .accessibilityElement(children: .combine)
        .accessibilityLabel("剩余额度，\(store.status)，最近成功读取\(store.updatedAt)")
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
        let screen = NSScreen.screens.first(where: { $0.frame.contains(origin) }) ?? NSScreen.main?.visibleFrame ?? .zero
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
