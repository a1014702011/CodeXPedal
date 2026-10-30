// [AI修改] 详情与设置为按需打开的小窗口，不恢复旧仪表盘或估算统计。
import AppKit
import SwiftUI

@MainActor struct DetailsView: View {
    @ObservedObject var store: QuotaStore
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("额度详情").font(.title2.bold())
            if store.windows.isEmpty { Text("暂无可显示额度").foregroundStyle(.secondary) }
            ForEach(store.windows, id: \.kind) { window in
                VStack(alignment: .leading, spacing: 6) {
                    HStack { Text(window.kind.label); Spacer(); Text(store.value(for: window)).font(.title2.monospacedDigit().bold()) }
                    ProgressView(value: window.remainingPercent, total: 100)
                    if let reset = window.resetsAt {
                        Text("重置时间：\(reset.formatted(date: .abbreviated, time: .standard))").font(.caption).foregroundStyle(.secondary)
                    } else { Text("官方未提供重置时间").font(.caption).foregroundStyle(.secondary) }
                }
            }
            Divider()
            LabeledContent("最近成功读取", value: store.updatedAt)
            LabeledContent("额度来源", value: "官方 Codex app-server")
            LabeledContent("CLI", value: store.cliName)
            Text(store.status).font(.callout).foregroundStyle(store.stale ? Color.orange : Color.secondary).textSelection(.enabled)
            Text("≈ 表示旧快照，不代表当前实时额度。百分比始终表示剩余。\n本工具不读取会话正文，不上传账户和用量。")
                .font(.caption).foregroundStyle(.secondary)
            Button(store.isRefreshing ? "正在刷新…" : "立即刷新") { store.refresh() }.disabled(store.isRefreshing)
        }.padding(24).frame(width: 430).fixedSize(horizontal: false, vertical: true)
    }
}

@MainActor struct PreferencesView: View {
    @ObservedObject var preferences: Preferences
    let apply: () -> Void
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("显示与连接").font(.title2.bold())
            Toggle("只显示菜单栏，不显示浮窗", isOn: $preferences.menuOnly)
            VStack(alignment: .leading) {
                HStack { Text("浮窗字号"); Spacer(); Text("\(Int(preferences.fontSize)) pt").monospacedDigit() }
                Slider(value: $preferences.fontSize, in: 20...42, step: 1)
            }
            Picker("刷新间隔", selection: $preferences.refreshSeconds) {
                Text("30秒").tag(30.0); Text("1分钟").tag(60.0); Text("2分钟").tag(120.0); Text("5分钟").tag(300.0)
            }
            Divider()
            Text("Codex CLI 路径").font(.headline)
            HStack {
                TextField("留空自动查找", text: $preferences.cliPath)
                Button("选择…") { chooseCLI() }
            }
            Text("可复用 Codex.app 内的官方 CLI，或选择已安装的 standalone／Homebrew CLI。需要先用官方 Codex 登录；本软件不会代替你安装或登录。")
                .font(.caption).foregroundStyle(.secondary)
            HStack { Spacer(); Button("应用并刷新", action: apply).keyboardShortcut(.defaultAction) }
        }.padding(24).frame(width: 470).fixedSize(horizontal: false, vertical: true)
    }
    private func chooseCLI() {
        let picker = NSOpenPanel(); picker.canChooseFiles = true; picker.canChooseDirectories = false
        picker.allowsMultipleSelection = false; picker.title = "选择官方 Codex CLI"
        if picker.runModal() == .OK, let url = picker.url { preferences.cliPath = url.path }
    }
}

@MainActor final class AuxiliaryWindows {
    private var details: NSWindow?
    private var settings: NSWindow?
    private let store: QuotaStore
    private let preferences: Preferences
    init(store: QuotaStore, preferences: Preferences) { self.store = store; self.preferences = preferences }
    func showDetails() {
        if details == nil { details = makeWindow("CodeXPedal · 额度详情", view: DetailsView(store: store)) }
        show(details)
    }
    func showSettings() {
        if settings == nil { settings = makeWindow("CodeXPedal · 设置", view: PreferencesView(preferences: preferences, apply: { [weak self] in self?.store.start() })) }
        show(settings)
    }
    private func makeWindow<Content: View>(_ title: String, view: Content) -> NSWindow {
        let host = NSHostingView(rootView: view)
        let window = NSWindow(contentRect: NSRect(origin: .zero, size: host.fittingSize), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = title; window.contentView = host; window.isReleasedWhenClosed = false; window.center()
        return window
    }
    private func show(_ window: NSWindow?) { NSApp.activate(ignoringOtherApps: true); window?.makeKeyAndOrderFront(nil) }
    func close() { details?.close(); settings?.close() }
}
