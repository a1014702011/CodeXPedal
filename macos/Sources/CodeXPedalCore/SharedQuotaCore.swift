// [AI修改] Swift只编组共享C ABI，数据规则与Windows使用同一份Rust实现。
import Foundation
import CSharedCore

enum SharedQuotaCore {
    static func checkABI() throws {
        guard pedal_abi_version() == 1, MemoryLayout<PedalSnapshot>.size == 64,
              MemoryLayout<PedalWindow>.size == 24 else { throw QuotaReadError.invalidResponse }
    }
    static func parse(_ data: Data, observedAt: Date) throws -> QuotaSnapshot {
        try checkABI()
        var native = PedalSnapshot()
        let status = data.withUnsafeBytes { bytes in
            pedal_parse(bytes.bindMemory(to: UInt8.self).baseAddress, bytes.count, observedAt.timeIntervalSince1970, 0, &native)
        }
        switch status {
        case 0: return decode(native)
        case 2: throw QuotaReadError.missingBucket
        case 3: throw QuotaReadError.duplicateWindow
        case 4: throw QuotaReadError.invalidPercent
        case 5: throw QuotaReadError.outputTooLarge
        default: throw QuotaReadError.invalidResponse
        }
    }
    static func decode(_ native: PedalSnapshot) -> QuotaSnapshot {
        let windows = [native.first, native.second].prefix(Int(native.window_count)).compactMap { raw -> QuotaWindow? in
            guard let kind = QuotaKind(rawValue: Int(raw.duration_minutes)) else { return nil }
            return QuotaWindow(kind: kind, usedPercent: raw.used_percent,
                resetsAt: raw.has_reset == 1 ? Date(timeIntervalSince1970: raw.resets_at) : nil)
        }
        return QuotaSnapshot(windows: windows, observedAt: Date(timeIntervalSince1970: native.observed_at))
    }
    static func encode(_ snapshot: QuotaSnapshot?) -> PedalSnapshot {
        guard let snapshot else { return PedalSnapshot() }
        let windows = snapshot.windows.map { window in
            PedalWindow(duration_minutes: UInt32(window.kind.rawValue), has_reset: window.resetsAt == nil ? 0 : 1,
                used_percent: window.usedPercent, resets_at: window.resetsAt?.timeIntervalSince1970 ?? 0)
        }
        return PedalSnapshot(abi_version: 1, window_count: UInt32(windows.count), observed_at: snapshot.observedAt.timeIntervalSince1970,
            first: windows.first ?? PedalWindow(), second: windows.count > 1 ? windows[1] : PedalWindow())
    }
    static func apply(previous: QuotaSnapshot?, incoming: QuotaSnapshot?, failed: Bool) -> QuotaSnapshot? {
        var old = encode(previous), new = encode(incoming), output = PedalSnapshot()
        _ = pedal_apply(&old, &new, failed ? 1 : 0, &output)
        return output.abi_version == 1 ? decode(output) : nil
    }
    static func isStale(_ snapshot: QuotaSnapshot?, at now: Date, interval: Double, failed: Bool) -> Bool {
        var native = encode(snapshot)
        return pedal_is_stale(&native, now.timeIntervalSince1970, interval, failed ? 1 : 0) != 0
    }
    static func remaining(_ used: Double) -> Double { pedal_remaining(used) }
}
