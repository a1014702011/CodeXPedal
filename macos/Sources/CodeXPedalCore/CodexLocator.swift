// [AI修改] 只定位可执行的本机官方 CLI；不读取 token 文件，不使用 shell 拼接命令。
import Foundation

public enum CodexLocator {
    // [AI修改] 目录的搜索权限也可能被判为可执行，定位时必须排除目录。
    private static func isExecutableFile(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory)
            && !isDirectory.boolValue && FileManager.default.isExecutableFile(atPath: path)
    }

    // [AI修改] 在官方应用资源目录内有界递归查找 codex，兼容嵌套的 CodexCLI.app 安装结构。
    static func findExecutable(in roots: [URL]) throws -> URL {
        let fileManager = FileManager.default
        for root in roots {
            guard let enumerator = fileManager.enumerator(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isExecutableKey], options: [.skipsHiddenFiles]) else { continue }
            for case let url as URL in enumerator {
                let relativeDepth = url.pathComponents.count - root.pathComponents.count
                guard relativeDepth <= 8 else { enumerator.skipDescendants(); continue }
                guard url.lastPathComponent == "codex", isExecutableFile(url.path) else { continue }
                return url.resolvingSymlinksInPath()
            }
        }
        throw QuotaReadError.cliMissing
    }

    public static func resolve(explicitPath: String, environment: [String: String] = ProcessInfo.processInfo.environment) throws -> URL {
        let explicit = explicitPath.trimmingCharacters(in: .whitespacesAndNewlines)
        let configured = explicit.isEmpty ? (environment["CODEXPEDAL_CODEX_PATH"] ?? "") : explicit
        if !configured.isEmpty {
            let expanded = (configured as NSString).expandingTildeInPath
            guard expanded.hasPrefix("/"), isExecutableFile(expanded) else { throw QuotaReadError.invalidCLI }
            return URL(fileURLWithPath: expanded).resolvingSymlinksInPath()
        }
        let home = FileManager.default.homeDirectoryForCurrentUser.path
        let paths = [
            "/Applications/Codex.app/Contents/Resources/codex",
            "\(home)/Applications/Codex.app/Contents/Resources/codex",
            "/Applications/ChatGPT.app/Contents/Resources/codex",
            "/opt/homebrew/bin/codex", "/usr/local/bin/codex", "\(home)/.local/bin/codex"
        ] + (environment["PATH"] ?? "").split(separator: ":").map { "\($0)/codex" }
        if let path = paths.first(where: isExecutableFile) {
            return URL(fileURLWithPath: path).resolvingSymlinksInPath()
        }
        // [AI修改] 精确路径未命中时扫描官方应用的 Resources，用户无需手动配置嵌套 CLI 路径。
        let resourceRoots = [
            "/Applications/ChatGPT.app/Contents/Resources",
            "\(home)/Applications/ChatGPT.app/Contents/Resources",
            "/Applications/Codex.app/Contents/Resources",
            "\(home)/Applications/Codex.app/Contents/Resources"
        ].map(URL.init(fileURLWithPath:))
        return try findExecutable(in: resourceRoots)
    }
}
