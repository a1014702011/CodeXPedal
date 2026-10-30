// [AI修改] 测试直接调用新生产解析器与快照状态，覆盖主路径、边界和失败出口。
import Foundation
import XCTest
@testable import CodeXPedalCore

final class QuotaTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_800_000_000)
    private func read(_ json: String) throws -> QuotaSnapshot {
        try RateLimitParser.parse(Data(json.utf8), observedAt: now)
    }
    private func response(_ primary: String, _ secondary: String = "null") -> String {
        "{\"rateLimits\":{\"primary\":\(primary),\"secondary\":\(secondary)}}"
    }
    private func window(_ minutes: Int, _ used: Double = 7) -> String {
        "{\"usedPercent\":\(used),\"windowDurationMins\":\(minutes),\"resetsAt\":1800003600}"
    }

    func testUsedSevenMeansRemainingNinetyThree() throws {
        XCTAssertEqual(try read(response(window(10080))).windows.first?.remainingText, "93%")
    }
    func testDualWindowsAreOrderedByDurationNotPayloadPosition() throws {
        let result = try read(response(window(10080), window(300, 24)))
        XCTAssertEqual(result.windows.map(\.kind), [.fiveHour, .sevenDay])
        XCTAssertEqual(result.windows.map(\.remainingText), ["76%", "93%"])
    }
    func testSingleSevenDayHasNoFiveHourPlaceholder() throws {
        XCTAssertEqual(try read(response(window(10080))).windows.map(\.kind), [.sevenDay])
    }
    func testFiveHourOnlyIsSupported() throws {
        XCTAssertEqual(try read(response(window(300))).windows.map(\.kind), [.fiveHour])
    }
    func testMonthlyAndUnknownWindowsAreNotDisplayed() throws {
        XCTAssertEqual(try read(response(window(43800), window(10080))).windows.map(\.kind), [.sevenDay])
        XCTAssertTrue(try read(response(window(60))).windows.isEmpty)
    }
    func testExplicitEmptyWindowsAreAValidResponse() throws {
        XCTAssertTrue(try read(response("null")).windows.isEmpty)
    }
    func testMissingWindowFieldsFailClosed() {
        XCTAssertThrowsError(try read("{\"rateLimits\":{}}"))
        XCTAssertThrowsError(try read("{}"))
    }
    func testDuplicateKnownWindowIsRejected() {
        XCTAssertThrowsError(try read(response(window(10080), window(10080, 20))))
    }
    func testPreferredCodexBucketWinsOverLegacyBucket() throws {
        let json = "{\"rateLimits\":{\"primary\":\(window(300, 90))},\"rateLimitsByLimitId\":{\"codex\":{\"primary\":\(window(10080))}}}"
        XCTAssertEqual(try read(json).windows.map(\.remainingText), ["93%"])
    }
    func testOtherBucketIsNeverMislabelledAsCodex() {
        XCTAssertThrowsError(try read("{\"rateLimitsByLimitId\":{\"other\":{\"primary\":\(window(10080))}}}"))
    }
    func testMalformedWindowIsRejectedWithoutMixingOtherWindow() {
        XCTAssertThrowsError(try read(response("{\"usedPercent\":true,\"windowDurationMins\":300}", window(10080))))
        XCTAssertThrowsError(try read(response("{\"usedPercent\":7,\"windowDurationMins\":\"10080\"}")))
    }
    func testPercentBoundariesAndInvalidNumbers() throws {
        XCTAssertEqual(try read(response(window(10080, 0))).windows.first?.remainingText, "100%")
        XCTAssertEqual(try read(response(window(10080, 100))).windows.first?.remainingText, "0%")
        XCTAssertThrowsError(try read(response(window(10080, -1))))
        XCTAssertThrowsError(try read(response(window(10080, 101))))
    }
    func testOptionalResetDateIsNotInvented() throws {
        let result = try read(response("{\"usedPercent\":7,\"windowDurationMins\":10080}"))
        XCTAssertNil(result.windows.first?.resetsAt)
    }
    func testFailurePreservesSnapshotAndTimeButMarksItStale() throws {
        var state = QuotaDisplayState()
        let snapshot = try read(response(window(10080)))
        state.accept(snapshot)
        state.fail("连接超时")
        XCTAssertEqual(state.snapshot, snapshot)
        XCTAssertTrue(state.isStale(at: now, refreshInterval: 60))
        XCTAssertEqual(state.snapshot?.observedAt, now)
    }
    func testSuccessCanRemoveFiveHourAndClearAnError() throws {
        var state = QuotaDisplayState()
        state.accept(try read(response(window(300), window(10080))))
        state.fail("连接失败")
        state.accept(try read(response(window(10080))))
        XCTAssertEqual(state.snapshot?.windows.map(\.kind), [.sevenDay])
        XCTAssertNil(state.errorMessage)
        XCTAssertFalse(state.isStale(at: now, refreshInterval: 60))
    }
    func testAgeAndResetBoundaryMakeSnapshotStale() throws {
        var state = QuotaDisplayState()
        state.accept(try read(response(window(10080))))
        XCTAssertTrue(state.isStale(at: now.addingTimeInterval(181), refreshInterval: 60))
        let expired = try read(response("{\"usedPercent\":7,\"windowDurationMins\":10080,\"resetsAt\":1799999999}"))
        state.accept(expired)
        XCTAssertTrue(state.isStale(at: now, refreshInterval: 60))
    }
    func testNoSnapshotNeverPretendsToBeZero() {
        let state = QuotaDisplayState()
        XCTAssertNil(state.snapshot)
        XCTAssertTrue(state.isStale(at: now, refreshInterval: 60))
    }
    // [AI修改] CLI 定位覆盖显式错误、目录误判和带空格路径，不创建或启动任何外部进程。
    func testExplicitMissingCLIAndDirectoryAreRejected() {
        XCTAssertThrowsError(try CodexLocator.resolve(explicitPath: "/nonexistent-CodeXPedal-test-cli", environment: [:]))
        XCTAssertThrowsError(try CodexLocator.resolve(explicitPath: "/usr/bin", environment: [:]))
    }
    func testInvalidEnvironmentOverrideDoesNotSilentlyFallBack() {
        XCTAssertThrowsError(try CodexLocator.resolve(explicitPath: "", environment: ["CODEXPEDAL_CODEX_PATH": "/nonexistent folder/codex", "PATH": "/usr/bin"]))
    }
    func testExplicitPathWinsOverEnvironmentOverride() throws {
        let path = try CodexLocator.resolve(explicitPath: "/usr/bin/true", environment: ["CODEXPEDAL_CODEX_PATH": "/nonexistent-codex"])
        XCTAssertEqual(path.lastPathComponent, "true")
    }

    // [AI修改] 覆盖 ChatGPT.app 内嵌 CodexCLI.app 的自动发现路径，避免依赖用户手动设置。
    func testFindExecutableRecursivelyDiscoversNestedCodexCLI() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let executable = root.appendingPathComponent("Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex")
        try FileManager.default.createDirectory(at: executable.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("#!/bin/sh\n".utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: executable.path)
        defer { try? FileManager.default.removeItem(at: root) }

        XCTAssertEqual(try CodexLocator.findExecutable(in: [root]), executable)
    }
}
