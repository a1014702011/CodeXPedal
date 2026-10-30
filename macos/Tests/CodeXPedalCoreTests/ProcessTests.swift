// [AI修改] 真实子进程集成测试：覆盖部分响应、EOF、输出上限、超时、取消和请求ID隔离。
import Foundation
import XCTest
@testable import CodeXPedalCore

final class ProcessTests: XCTestCase {
    private func withCLI(_ body: String, run: (URL) throws -> Void) throws {
        let root = ProcessInfo.processInfo.environment["CODEXPEDAL_TEST_TEMP_ROOT"]
            ?? URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("build/test-fixtures").path
        let directory = URL(fileURLWithPath: root).appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let cli = directory.appendingPathComponent("fake-cli")
        try Data(("#!/bin/sh\n" + body + "\n").utf8).write(to: cli)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: cli.path)
        defer { try? FileManager.default.removeItem(at: directory) }
        try run(cli)
    }
    private let success = """
    IFS= read -r first
    printf '%s\\n' '{"id":1,"result":{}}'
    IFS= read -r initialized
    IFS= read -r request
    printf '%s' '{"id":2,"result":{"rateLimits":{"primary":'
    printf '%s\\n' '{"usedPercent":7,"windowDurationMins":10080}}}}'
    """
    func testPartialOutputUsesRealSharedCore() throws {
        try withCLI(success) { cli in
            XCTAssertEqual(try CodexRPCClient().read(executable: cli).windows.first?.remainingText, "93%")
        }
    }
    func testEOFDoesNotWaitForTimeout() throws {
        try withCLI("exit 0") { cli in
            let start = Date()
            XCTAssertThrowsError(try CodexRPCClient().read(executable: cli, timeout: 2))
            XCTAssertLessThan(Date().timeIntervalSince(start), 2)
        }
    }
    func testTimeoutIsBounded() throws {
        try withCLI("while IFS= read -r line; do :; done") { cli in
            let start = Date()
            XCTAssertThrowsError(try CodexRPCClient().read(executable: cli, timeout: 0.2))
            XCTAssertLessThan(Date().timeIntervalSince(start), 2)
        }
    }
    func testOversizedLineRejected() throws {
        try withCLI("/usr/bin/yes x | /usr/bin/tr -d '\\n' | /usr/bin/head -c 150000") { cli in
            XCTAssertThrowsError(try CodexRPCClient().read(executable: cli, timeout: 2))
        }
    }
    func testCancelledBeforeStartNeverLaunches() throws {
        try withCLI(success) { cli in
            let reader = CodexRPCClient(); reader.cancel()
            XCTAssertThrowsError(try reader.read(executable: cli)) { error in
                guard let readError = error as? QuotaReadError, case .cancelled = readError else { return XCTFail("应返回取消") }
            }
        }
    }
    func testUnrelatedErrorIDIsIgnored() throws {
        let body = "printf '%s\\n' '{\"id\":999,\"error\":{\"code\":-1}}'\n" + success
        try withCLI(body) { cli in XCTAssertEqual(try CodexRPCClient().read(executable: cli).windows.count, 1) }
    }
    func testMalformedOutputIsRejected() throws {
        try withCLI("printf '%s\\n' '{broken'") { cli in
            XCTAssertThrowsError(try CodexRPCClient().read(executable: cli, timeout: 2))
        }
    }
}
