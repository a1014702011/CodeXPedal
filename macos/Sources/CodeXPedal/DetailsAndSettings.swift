// [AI修改] 详情与设置为按需打开的小窗口，不恢复旧仪表盘或估算统计。
import AppKit
import SwiftUI
import CodeXPedalCore

@MainActor struct DetailsView: View {
    @ObservedObject var store: QuotaStore
    // [AI修改] 时间偏好变化立即重绘详情，不触发后台读取。
    @ObservedObject var preferences: Preferences
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Text("额度详情").font(.title2.bold())
            // [AI修改] 只展示启用项；两家都关闭时仍保留详情和设置入口。
            if !preferences.providers.hasEnabled { Text(QuotaTimePresentation.empty).foregroundStyle(.secondary) }
            ForEach(store.rows) { row in
                if row.showLabel { Text(row.name).font(.headline) }
                if row.windows.isEmpty { Text("暂无可显示额度").foregroundStyle(.secondary) }
                ForEach(row.windows, id: \.kind) { window in
                    VStack(alignment: .leading, spacing: 6) {
                        HStack { Text(window.kind.label); Spacer(); Text(row.value(for: window)).font(.title2.monospacedDigit().bold()) }
                        ProgressView(value: window.remainingPercent, total: 100)
                        if row.timeMode == .nextReset {
                            Text("重置时间：\(QuotaTimePresentation.reset(window.resetsAt, now: Date()))").font(.caption).foregroundStyle(.secondary)
                        }
                    }
                }
                if row.timeMode == .observed || row.windows.isEmpty {
                    LabeledContent(row.timeLabel, value: row.timeValue)
                }
                LabeledContent("额度来源", value: row.source)
                if row.id == "codex" { LabeledContent("CLI", value: store.cliName) }
                Text(row.status).font(.callout).foregroundStyle(row.stale ? Color.orange : Color.secondary).textSelection(.enabled)
                Divider()
            }
            Text("≈ 表示旧快照，不代表当前实时额度。百分比始终表示剩余。\n本工具不读取会话正文，不上传账户和用量。")
                .font(.caption).foregroundStyle(.secondary)
            Button(store.isRefreshing ? "正在刷新…" : "立即刷新") { store.refresh() }.disabled(!store.canRefresh)
        }.padding(24).frame(width: 430).fixedSize(horizontal: false, vertical: true)
    }
}

@MainActor struct PreferencesView: View {
    @ObservedObject var preferences: Preferences
    let apply: () -> Void
    // [AI修改] 设置页“安装并登录 Claude Code”按钮复用存储层的同一入口。
    let startClaudeSetup: () -> Bool
    @State private var bridgeState: ClaudeBridgeState = .notConfigured
    @State private var cliStatus: ClaudeCliStatus = .notInstalled
    @State private var setupStarted = false
    private var cliText: (button: String, status: String) {
        if setupStarted { return ("安装并登录 Claude Code", "已打开“终端”，请在浏览器中授权") }
        switch cliStatus {
        case .ready: return ("Claude Code 已就绪", "已安装并登录")
        case .notLoggedIn: return ("登录 Claude Code", "已安装，未登录")
        case .notInstalled: return ("安装并登录 Claude Code", "未安装")
        }
    }
    private var executable: URL { Bundle.main.executableURL ?? URL(fileURLWithPath: CommandLine.arguments[0]) }
    private var bridgeText: (button: String, status: String) {
        switch bridgeState {
        case .enabled: return ("停用 Claude 状态栏桥接", "已启用")
        case .enabledOtherPath: return ("重新启用（指向本程序）", "已启用，但指向其他位置的 CodeXPedal")
        case .otherCommand: return ("启用 Claude 状态栏桥接", "当前使用自定义状态栏，启用时会先备份")
        case .settingsUnreadable: return ("启用 Claude 状态栏桥接", "Claude settings.json 无法解析，请先手动修复")
        case .notConfigured: return ("启用 Claude 状态栏桥接", "未启用")
        }
    }
    var body: some View {
        // [AI修改] 增加设置后允许滚动，小屏幕不裁切操作按钮。
        ScrollView {
        VStack(alignment: .leading, spacing: 18) {
            Text("显示与连接").font(.title2.bold())
            // [AI修改] 独立控制每家额度，并解释时间样式不会改变刷新间隔。
            VStack(alignment: .leading, spacing: 6) {
                Toggle("显示并获取 Codex 额度", isOn: $preferences.providers.codex)
                Text("取消后不显示 Codex 额度，也不在后台获取。").font(.caption).foregroundStyle(.secondary)
                Toggle("显示并获取 Claude 额度", isOn: $preferences.providers.claude)
                Text("取消后不显示 Claude 额度，也不在后台获取。").font(.caption).foregroundStyle(.secondary)
                Picker("额度时间显示", selection: $preferences.quotaTimeMode) {
                    Text("最近获取时间").tag(QuotaTimeMode.observed)
                    Text("下次额度重置时间").tag(QuotaTimeMode.nextReset)
                }
                Text("重置时间由额度来源提供，不是软件下一次查询的时间。").font(.caption).foregroundStyle(.secondary)
            }
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
            // [AI修改] Claude 额度：默认实时读取，状态栏桥接作为备选并写明使用步骤；单独分组避免超出视图构建上限。
            VStack(alignment: .leading, spacing: 10) {
                Divider()
                Text("Claude 额度").font(.headline)
                HStack {
                    Button(cliText.button) { if startClaudeSetup() { setupStarted = true } }.disabled(!preferences.providers.claude || cliStatus == .ready)
                    Text(cliText.status).font(.caption).foregroundStyle(.secondary)
                }
                Toggle("实时读取 Claude 额度（默认，读取钥匙串中的 Claude 登录令牌）", isOn: $preferences.claudeLive)
                    .disabled(!preferences.providers.claude)
                Text("首次读取时系统会询问是否允许访问「Claude Code-credentials」；约每 2 分钟一次（被限流时自动退避）发送到 Anthropic 官方接口，不保存、不上传到其他地方。令牌过期时请打开一次 Claude Code。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                Text("备选方式：Claude 状态栏桥接").font(.subheadline.weight(.semibold)).padding(.top, 4)
                Text("不想授权读取令牌，或实时读取失败时使用，需要 Pro/Max 订阅：\n1. 点击下方按钮启用桥接，原状态栏会备份，停用后恢复。\n2. 在终端运行 claude 并发送一条消息；桌面端可能不会更新额度。\n3. 回到本程序等待下次刷新。之后只在发消息时更新，空闲约3分钟后数字带 ≈。\n如需完全不读取令牌，请同时关闭上方的实时读取。")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button(bridgeText.button) { toggleBridge() }.disabled(bridgeState == .settingsUnreadable)
                    Text(bridgeText.status).font(.caption).foregroundStyle(.secondary)
                }
            }
            HStack { Spacer(); Button("应用并刷新", action: apply).keyboardShortcut(.defaultAction) }
        }.padding(24)
        }.frame(width: 490, height: min(700, (NSScreen.main?.visibleFrame.height ?? 760) - 60))
        .onAppear {
            bridgeState = ClaudeStatusLineBridge.state(configDirectory: ClaudeLocator.configDirectory(), executable: executable)
            if preferences.providers.claude { cliStatus = ClaudeCliSetup.status() }
        }
        .onChange(of: preferences.providers.claude) { enabled in
            if enabled { cliStatus = ClaudeCliSetup.status() }
            else { setupStarted = false }
        }
    }
    // [AI修改] 替换用户自定义状态栏前必须明确确认；失败时提示原因且不做部分修改。
    private func toggleBridge() {
        let directory = ClaudeLocator.configDirectory()
        do {
            switch bridgeState {
            case .enabled: try ClaudeStatusLineBridge.disable(configDirectory: directory)
            case .otherCommand(let command):
                let alert = NSAlert()
                alert.messageText = "替换 Claude Code 状态栏？"
                alert.informativeText = "当前状态栏命令：\n\(command)\n\n启用后会被替换为 CodeXPedal 额度显示，原配置会备份，停用时自动恢复。"
                alert.addButton(withTitle: "继续"); alert.addButton(withTitle: "取消")
                guard alert.runModal() == .alertFirstButtonReturn else { return }
                try ClaudeStatusLineBridge.enable(configDirectory: directory, executable: executable)
            default: try ClaudeStatusLineBridge.enable(configDirectory: directory, executable: executable)
            }
        } catch {
            let alert = NSAlert(); alert.messageText = "修改 Claude 设置失败"; alert.informativeText = error.localizedDescription; alert.runModal()
        }
        bridgeState = ClaudeStatusLineBridge.state(configDirectory: directory, executable: executable)
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
        if details == nil { details = makeWindow("CodeXPedal · 额度详情", view: DetailsView(store: store, preferences: preferences)) }
        show(details)
    }
    func showSettings() {
        if settings == nil { settings = makeWindow("CodeXPedal · 设置", view: PreferencesView(preferences: preferences, apply: { [weak self] in self?.store.start() },
                                                                                 startClaudeSetup: { [weak self] in self?.store.startClaudeSetup() ?? false })) }
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
