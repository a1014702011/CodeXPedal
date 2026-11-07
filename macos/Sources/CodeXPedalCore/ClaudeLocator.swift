// [AI修改] 自动发现 Claude Code CLI：显式变量、官方原生安装、Homebrew、npm、Claude 桌面版托管目录和 PATH。
import Foundation

public enum ClaudeLocator {
    public static let pathVariable = "CODEXPEDAL_CLAUDE_PATH"

    // [AI修改] 与 Claude Code 一致：CLAUDE_CONFIG_DIR 优先，否则使用 ~/.claude。
    public static func configDirectory(environment: [String: String] = ProcessInfo.processInfo.environment,
                                       home: String = NSHomeDirectory()) -> URL {
        let configured = (environment["CLAUDE_CONFIG_DIR"] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        return configured.isEmpty
            ? URL(fileURLWithPath: home).appendingPathComponent(".claude", isDirectory: true)
            : URL(fileURLWithPath: (configured as NSString).expandingTildeInPath, isDirectory: true)
    }

    private static func isExecutableFile(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory)
            && !isDirectory.boolValue && FileManager.default.isExecutableFile(atPath: path)
    }

    public static func resolve(environment: [String: String] = ProcessInfo.processInfo.environment,
                               home: String = NSHomeDirectory()) -> URL? {
        var paths: [String] = []
        let explicit = (environment[pathVariable] ?? "").trimmingCharacters(in: .whitespacesAndNewlines)
        if !explicit.isEmpty { paths.append((explicit as NSString).expandingTildeInPath) }
        paths += [
            "\(home)/.local/bin/claude", "\(home)/.claude/local/claude",
            "/opt/homebrew/bin/claude", "/usr/local/bin/claude", "\(home)/.npm-global/bin/claude"
        ]
        // [AI修改] 桌面版按版本号子目录存放 CLI，取最近修改的版本；目录数量有上限。
        let managedRoot = URL(fileURLWithPath: "\(home)/Library/Application Support/Claude/claude-code", isDirectory: true)
        if let versions = try? FileManager.default.contentsOfDirectory(at: managedRoot, includingPropertiesForKeys: [.contentModificationDateKey]) {
            let candidates = versions.prefix(64).flatMap { [$0.appendingPathComponent("claude").path, $0.appendingPathComponent("bin/claude").path] }
                .filter(isExecutableFile)
            let dated = candidates.map { path -> (String, Date) in
                let date = (try? FileManager.default.attributesOfItem(atPath: path)[.modificationDate] as? Date) ?? .distantPast
                return (path, date)
            }
            paths += dated.sorted { $0.1 > $1.1 }.map(\.0)
        }
        paths += (environment["PATH"] ?? "").split(separator: ":").prefix(256).map { "\($0)/claude" }
        guard let path = paths.first(where: isExecutableFile) else { return nil }
        return URL(fileURLWithPath: path).resolvingSymlinksInPath()
    }

    // [AI修改] 有 CLI 或有 Claude 配置目录都视为已安装（只装桌面版的用户可能没有 CLI 在 PATH 中）。
    public static func isInstalled(environment: [String: String] = ProcessInfo.processInfo.environment,
                                   home: String = NSHomeDirectory()) -> Bool {
        if resolve(environment: environment, home: home) != nil { return true }
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: configDirectory(environment: environment, home: home).path, isDirectory: &isDirectory)
            && isDirectory.boolValue
    }
}

// [AI修改] 联合探测已移除，刷新只定位勾选的来源，安装状态不决定界面可见性。
