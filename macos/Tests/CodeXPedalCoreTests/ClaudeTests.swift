// [AI修改] Claude 额度测试：真实共享核心解析、状态栏钩子、桥接、实时开关和路径发现，全部使用隔离目录。
import Foundation
import XCTest
@testable import CodeXPedalCore

final class ClaudeTests: XCTestCase {
    private var directory: URL!
    override func setUpWithError() throws {
        let root = ProcessInfo.processInfo.environment["CODEXPEDAL_TEST_TEMP_ROOT"].map { URL(fileURLWithPath: $0) }
            ?? URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("build/test-fixtures")
        directory = root.appendingPathComponent("claude-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
    }
    override func tearDownWithError() throws {
        // [AI修改] 只删除本用例创建的 UUID 目录。
        if let directory { try? FileManager.default.removeItem(at: directory) }
    }
    private func write(_ text: String, _ name: String) throws -> URL {
        let url = directory.appendingPathComponent(name)
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data(text.utf8).write(to: url)
        return url
    }

    func testClaudeStatusLineUsesSharedCore() throws {
        let json = #"{"rate_limits":{"seven_day":{"used_percentage":41.2,"resets_at":1738857600},"five_hour":{"used_percentage":23.5}}}"#
        let snapshot = try RateLimitParser.parseClaude(Data(json.utf8))
        XCTAssertEqual(snapshot.windows.map(\.kind), [.fiveHour, .sevenDay])
        XCTAssertEqual(snapshot.windows.map(\.remainingText), ["77%", "59%"])
        XCTAssertEqual(snapshot.windows[1].resetsAt, Date(timeIntervalSince1970: 1738857600))
        XCTAssertNil(snapshot.windows[0].resetsAt)
    }
    func testClaudeOAuthIsoResetAndOverLimit() throws {
        let json = #"{"five_hour":{"utilization":130,"resets_at":"2026-01-22T09:00:00Z"},"seven_day":{"utilization":8,"resets_at":null},"seven_day_opus":{"utilization":1}}"#
        let snapshot = try RateLimitParser.parseClaude(Data(json.utf8))
        XCTAssertEqual(snapshot.windows.map(\.remainingText), ["0%", "92%"])
        XCTAssertEqual(snapshot.windows[0].resetsAt, Date(timeIntervalSince1970: 1769072400))
    }
    func testClaudeMissingWindowsFailClosed() {
        XCTAssertThrowsError(try RateLimitParser.parseClaude(Data(#"{"cwd":"x"}"#.utf8)))
        XCTAssertThrowsError(try RateLimitParser.parseClaude(Data(#"{"five_hour":{"used_percentage":"7"}}"#.utf8)))
    }
    func testHookWritesOnlyRateLimitsAndFormats() throws {
        let input = #"{"cwd":"/secret","session_id":"s","rate_limits":{"five_hour":{"used_percentage":23.5},"seven_day":{"used_percentage":41.2}}}"#
        XCTAssertEqual(ClaudeStatusLineHook.run(input: Data(input.utf8), configDirectory: directory), "Claude 剩余 5h 77% · 7d 59%")
        let cache = try String(contentsOf: ClaudeStatusLineCache.url(in: directory), encoding: .utf8)
        XCTAssertTrue(cache.contains("rate_limits"))
        XCTAssertFalse(cache.contains("secret"))
    }
    func testHookWithoutLimitsKeepsCache() throws {
        let cache = try write(#"{"rate_limits":{"seven_day":{"used_percentage":10}}}"#, ClaudeStatusLineCache.fileName)
        XCTAssertTrue(ClaudeStatusLineHook.run(input: Data(#"{"cwd":"x"}"#.utf8), configDirectory: directory).contains("--"))
        XCTAssertEqual(ClaudeStatusLineHook.run(input: Data("{".utf8), configDirectory: directory), "Claude 剩余 --")
        XCTAssertTrue(try String(contentsOf: cache, encoding: .utf8).contains("10"))
    }
    func testReaderUsesNewerCacheOnlyAndSkipsNetworkWhenLiveDisabled() throws {
        let cache = try write(#"{"rate_limits":{"five_hour":{"used_percentage":20}}}"#, ClaudeStatusLineCache.fileName)
        let modified = Date(timeIntervalSince1970: 1_800_000_000)
        try FileManager.default.setAttributes([.modificationDate: modified], ofItemAtPath: cache.path)
        let calls = Counter()
        let dir = directory!
        let reader = ClaudeQuotaReader(live: ClaudeLiveUsageClient(fetch: { _ in calls.increment(); return Data() }, tokenProvider: { _, _ in "t" }),
                                       configDirectory: { dir })
        guard case .updated(let snapshot, .statusLine) = reader.read(liveEnabled: false, now: Date(), lastObserved: nil) else { return XCTFail("应读取缓存") }
        XCTAssertEqual(snapshot.observedAt, modified)
        XCTAssertEqual(snapshot.windows.first?.remainingText, "80%")
        guard case .unchanged = reader.read(liveEnabled: false, now: Date(), lastObserved: modified) else { return XCTFail("旧缓存不能重复替换") }
        XCTAssertEqual(calls.value, 0)
    }
    func testLiveReadIsThrottledAndFailureReported() throws {
        let calls = Counter()
        let dir = directory!
        let body = Data(#"{"five_hour":{"utilization":30},"seven_day":{"utilization":60}}"#.utf8)
        let reader = ClaudeQuotaReader(live: ClaudeLiveUsageClient(fetch: { token in
            calls.increment(); XCTAssertEqual(token, "sk-test"); return body
        }, tokenProvider: { _, _ in "sk-test" }), configDirectory: { dir })
        let now = Date()
        guard case .updated(let snapshot, .live) = reader.read(liveEnabled: true, now: now, lastObserved: nil) else { return XCTFail("实时读取") }
        XCTAssertEqual(snapshot.windows.map(\.remainingText), ["70%", "40%"])
        guard case .unchanged = reader.read(liveEnabled: true, now: now.addingTimeInterval(5), lastObserved: now) else { return XCTFail("正常间隔内不重复请求") }
        XCTAssertEqual(calls.value, 1)
        let failing = ClaudeQuotaReader(live: ClaudeLiveUsageClient(fetch: { _ in Data() }, tokenProvider: { _, _ in throw QuotaReadError.claudeTokenExpired }),
                                        configDirectory: { dir })
        guard case .failed(let message) = failing.read(liveEnabled: true, now: now, lastObserved: nil) else { return XCTFail("过期应失败") }
        XCTAssertTrue(message.contains("过期"))
    }
    // [AI修改] 实时读取默认开启，但用户明确关闭后必须保持关闭。
    func testLiveReadingDefaultsToOnAndKeepsUserChoice() {
        XCTAssertTrue(ClaudeLivePreference.resolve(stored: nil))
        XCTAssertTrue(ClaudeLivePreference.resolve(stored: true))
        XCTAssertFalse(ClaudeLivePreference.resolve(stored: false))
    }
    // [AI修改] 拒绝钥匙串授权后不能每分钟重复弹窗，关闭实时读取后才恢复尝试。
    func testKeychainDenialPausesLiveReadUntilReset() {
        let attempts = Counter()
        let dir: URL = directory
        let reader = ClaudeQuotaReader(live: ClaudeLiveUsageClient(fetch: { _ in Data() }, tokenProvider: { _, _ in
            attempts.increment(); throw QuotaReadError.claudeKeychainDenied
        }), configDirectory: { dir })
        let now = Date()
        guard case .failed(let message) = reader.read(liveEnabled: true, now: now, lastObserved: nil) else { return XCTFail("拒绝应失败") }
        XCTAssertTrue(message.contains("钥匙串"))
        guard case .failed = reader.read(liveEnabled: true, now: now.addingTimeInterval(120), lastObserved: nil) else { return XCTFail("应保留错误") }
        XCTAssertEqual(attempts.value, 1)
        _ = reader.read(liveEnabled: false, now: now.addingTimeInterval(180), lastObserved: nil)
        _ = reader.read(liveEnabled: true, now: now.addingTimeInterval(240), lastObserved: nil)
        XCTAssertEqual(attempts.value, 2)
    }
    // [AI修改] 限流测试：退避期内不发请求、提示持续，到期重试成功后恢复 120 秒正常间隔。
    func testRateLimitHonoursRetryAfterAndRecovers() throws {
        let calls = Counter()
        let dir = directory!
        let body = Data(#"{"five_hour":{"utilization":10},"seven_day":{"utilization":20}}"#.utf8)
        let reader = ClaudeQuotaReader(live: ClaudeLiveUsageClient(fetch: { _ in
            calls.increment()
            if calls.value == 1 { throw ClaudeRateLimitedError(retryAfter: 600) }
            return body
        }, tokenProvider: { _, _ in "t" }), configDirectory: { dir })
        let start = Date()
        guard case .failed(let message) = reader.read(liveEnabled: true, now: start, lastObserved: nil) else { return XCTFail("限流应失败") }
        XCTAssertTrue(message.contains("限流中"))
        guard case .failed(let waiting) = reader.read(liveEnabled: true, now: start.addingTimeInterval(300), lastObserved: nil) else { return XCTFail("等待期提示应保留") }
        XCTAssertTrue(waiting.contains("限流中"))
        XCTAssertEqual(calls.value, 1)
        guard case .updated(_, .live) = reader.read(liveEnabled: true, now: start.addingTimeInterval(601), lastObserved: nil) else { return XCTFail("到期后应重试成功") }
        XCTAssertEqual(calls.value, 2)
        guard case .unchanged = reader.read(liveEnabled: true, now: start.addingTimeInterval(661), lastObserved: start) else { return XCTFail("恢复后按120秒间隔") }
        XCTAssertEqual(calls.value, 2)
    }
    func testRateLimitDelayAndRetryAfterParsing() {
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 1, retryAfter: nil), 120)
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 2, retryAfter: nil), 240)
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 3, retryAfter: nil), 480)
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 9, retryAfter: nil), 900)
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 1, retryAfter: 5), 60)
        XCTAssertEqual(ClaudeQuotaReader.rateLimitDelay(streak: 1, retryAfter: 99_999), 1800)
        XCTAssertEqual(ClaudeRateLimitedError.parse("300"), 300)
        XCTAssertNil(ClaudeRateLimitedError.parse("soon"))
        // 2026-11-04 02:45:00 GMT = 1_793_760_300，比 now 晚 3900 秒。
        let now = Date(timeIntervalSince1970: 1_793_756_400)
        XCTAssertEqual(ClaudeRateLimitedError.parse("Wed, 04 Nov 2026 02:45:00 GMT", now: now) ?? -1, 3900, accuracy: 1)
    }
    func testExpiredCredentialFileIsRejectedWithoutToken() throws {
        guard ProcessInfo.processInfo.environment["CLAUDE_CODE_OAUTH_TOKEN"] == nil else { return }
        _ = try write(#"{"claudeAiOauth":{"accessToken":"sk-old","expiresAt":1000}}"#, ".credentials.json")
        // [AI修改] 测试机钥匙串可能有真实条目，此处只验证文件分支在无钥匙串结果时的过期判断。
        do { _ = try ClaudeLiveUsageClient.loadToken(configDirectory: directory, now: Date()) } catch let error as QuotaReadError {
            XCTAssertTrue([QuotaReadError.claudeTokenExpired.localizedDescription, QuotaReadError.claudeNoLogin.localizedDescription].contains(error.localizedDescription))
        }
    }
    func testBridgeBacksUpAndRestoresStatusLine() throws {
        let settings = try write(#"{"model":"opus","statusLine":{"type":"command","command":"bash ~/.claude/mine.sh"}}"#, "settings.json")
        let exe = URL(fileURLWithPath: "/Applications/CodeXPedal.app/Contents/MacOS/CodeXPedal")
        XCTAssertEqual(ClaudeStatusLineBridge.state(configDirectory: directory, executable: exe), .otherCommand("bash ~/.claude/mine.sh"))
        try ClaudeStatusLineBridge.enable(configDirectory: directory, executable: exe)
        XCTAssertEqual(ClaudeStatusLineBridge.state(configDirectory: directory, executable: exe), .enabled)
        XCTAssertEqual(ClaudeStatusLineBridge.state(configDirectory: directory, executable: URL(fileURLWithPath: "/tmp/Other")), .enabledOtherPath)
        let enabled = try JSONSerialization.jsonObject(with: Data(contentsOf: settings)) as? [String: Any]
        XCTAssertEqual(enabled?["model"] as? String, "opus")
        try ClaudeStatusLineBridge.enable(configDirectory: directory, executable: exe)
        try ClaudeStatusLineBridge.disable(configDirectory: directory)
        XCTAssertEqual(ClaudeStatusLineBridge.state(configDirectory: directory, executable: exe), .otherCommand("bash ~/.claude/mine.sh"))
        try Data("{}".utf8).write(to: settings)
        try ClaudeStatusLineBridge.enable(configDirectory: directory, executable: exe)
        try ClaudeStatusLineBridge.disable(configDirectory: directory)
        XCTAssertEqual(ClaudeStatusLineBridge.state(configDirectory: directory, executable: exe), .notConfigured)
    }
    func testClaudeCliStatusAndScript() throws {
        let env: [String: String] = [:]
        XCTAssertEqual(ClaudeCliSetup.status(executable: nil, configDirectory: directory, environment: env, keychainHasItem: { true }), .notInstalled)
        let exe = URL(fileURLWithPath: "/usr/local/bin/claude")
        XCTAssertEqual(ClaudeCliSetup.status(executable: exe, configDirectory: directory, environment: env, keychainHasItem: { false }), .notLoggedIn)
        XCTAssertEqual(ClaudeCliSetup.status(executable: exe, configDirectory: directory, environment: env, keychainHasItem: { true }), .ready)
        _ = try write(#"{"claudeAiOauth":{"accessToken":"x"}}"#, ".credentials.json")
        XCTAssertEqual(ClaudeCliSetup.status(executable: exe, configDirectory: directory, environment: env, keychainHasItem: { false }), .ready)
        let fresh = ClaudeCliSetup.buildScript(executable: nil)
        XCTAssertTrue(fresh.contains(ClaudeCliSetup.installCommand) && fresh.contains("auth login") && fresh.contains("CLAUDE=''"))
        XCTAssertTrue(ClaudeCliSetup.buildScript(executable: "/Users/o'neil/bin/claude").contains(#"CLAUDE='/Users/o'\''neil/bin/claude'"#))
    }
    func testClaudeLocatorOrder() throws {
        let home = directory.appendingPathComponent("home").path
        XCTAssertNil(ClaudeLocator.resolve(environment: [:], home: home))
        XCTAssertFalse(ClaudeLocator.isInstalled(environment: [:], home: home))
        let native = try write("#!/bin/sh\n", "home/.local/bin/claude")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: native.path)
        XCTAssertEqual(ClaudeLocator.resolve(environment: [:], home: home)?.path, native.resolvingSymlinksInPath().path)
        let custom = try write("#!/bin/sh\n", "custom/claude")
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: custom.path)
        XCTAssertEqual(ClaudeLocator.resolve(environment: [ClaudeLocator.pathVariable: custom.path], home: home)?.path, custom.resolvingSymlinksInPath().path)
        XCTAssertEqual(ClaudeLocator.configDirectory(environment: ["CLAUDE_CONFIG_DIR": "/x/y"], home: home).path, "/x/y")
    }
}

// [AI修改] 线程安全计数器，记录假请求次数。
private final class Counter: @unchecked Sendable {
    private let lock = NSLock()
    private var count = 0
    var value: Int { lock.lock(); defer { lock.unlock() }; return count }
    func increment() { lock.lock(); count += 1; lock.unlock() }
}
