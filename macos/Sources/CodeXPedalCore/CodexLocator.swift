// [AI修改] 只定位可执行的本机官方 CLI；不读取 token 文件，不使用 shell 拼接命令。
import Foundation

public enum CodexLocator {
    // [AI修改] 目录的搜索权限也可能被判为可执行，定位时必须排除目录。
    private static func isExecutableFile(_ path: String) -> Bool {
        var isDirectory: ObjCBool = false
        return FileManager.default.fileExists(atPath: path, isDirectory: &isDirectory)
            && !isDirectory.boolValue && FileManager.default.isExecutableFile(atPath: path)
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
        guard let path = paths.first(where: isExecutableFile) else { throw QuotaReadError.cliMissing }
        return URL(fileURLWithPath: path).resolvingSymlinksInPath()
    }
}
