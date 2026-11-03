// [AI修改] 一键安装并登录 Claude Code CLI：用户确认后在“终端”中执行官方安装脚本和 claude auth login，程序不代填账号、不读取密码。
import Foundation
#if canImport(Security)
import Security
#endif

public enum ClaudeCliStatus: Sendable, Equatable {
    case notInstalled, notLoggedIn, ready
}

public enum ClaudeCliSetup {
    // [AI修改] 官方文档给出的 macOS 安装命令，只在用户点击“安装并登录”后执行。
    public static let installCommand = "curl -fsSL https://claude.ai/install.sh | bash"

    // [AI修改] 只查询钥匙串条目是否存在（不取数据，不会弹出授权框），或登录文件是否含令牌字段。
    public static func keychainHasCredentials() -> Bool {
        #if canImport(Security)
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword, kSecAttrService as String: "Claude Code-credentials",
                                    kSecReturnAttributes as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var item: CFTypeRef?
        return SecItemCopyMatching(query as CFDictionary, &item) == errSecSuccess
        #else
        return false
        #endif
    }

    public static func status(executable: URL? = ClaudeLocator.resolve(),
                              configDirectory: URL = ClaudeLocator.configDirectory(),
                              environment: [String: String] = ProcessInfo.processInfo.environment,
                              keychainHasItem: () -> Bool = keychainHasCredentials) -> ClaudeCliStatus {
        guard executable != nil else { return .notInstalled }
        if let token = environment["CLAUDE_CODE_OAUTH_TOKEN"], !token.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { return .ready }
        if keychainHasItem() { return .ready }
        let file = configDirectory.appendingPathComponent(".credentials.json")
        if let size = (try? FileManager.default.attributesOfItem(atPath: file.path))?[.size] as? NSNumber, size.intValue > 0, size.intValue <= 65_536,
           let text = try? String(contentsOf: file, encoding: .utf8), text.contains("\"accessToken\"") {
            return .ready
        }
        return .notLoggedIn
    }

    // [AI修改] 单引号包裹并转义路径，避免空格或特殊字符被 shell 解释。
    static func shellQuote(_ text: String) -> String { "'" + text.replacingOccurrences(of: "'", with: "'\\''") + "'" }

    public static func buildScript(executable: String?) -> String {
        """
        #!/bin/bash
        CLAUDE=\(shellQuote(executable ?? ""))
        if [ -z "$CLAUDE" ] || [ ! -x "$CLAUDE" ]; then
          echo "CodeXPedal：正在用官方脚本安装 Claude Code，请稍候（下载时没有进度显示）…"
          \(installCommand) || { echo "安装失败，请检查网络后重试。"; exit 1; }
          CLAUDE="$HOME/.local/bin/claude"
        fi
        echo ""
        echo "CodeXPedal：正在打开浏览器登录 Claude，请在浏览器中点击“授权”。"
        echo "如果浏览器没有自动打开，请按窗口中的提示复制链接或粘贴验证码。"
        "$CLAUDE" auth login
        echo ""
        echo "CodeXPedal：完成后会自动显示 Claude 额度，可以关闭此窗口。"

        """
    }

    // [AI修改] 写入应用缓存目录的 .command 文件并用“终端”打开，用户能看到进度并在需要时粘贴验证码。
    public static func launch() throws {
        let caches = FileManager.default.urls(for: .cachesDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Caches")
        let directory = caches.appendingPathComponent("CodeXPedal", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let script = directory.appendingPathComponent("claude-setup.command")
        try Data(buildScript(executable: ClaudeLocator.resolve()?.path).utf8).write(to: script, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: script.path)
        let opener = Process()
        opener.executableURL = URL(fileURLWithPath: "/usr/bin/open")
        opener.arguments = ["-a", "Terminal", script.path]
        try opener.run()
    }
}
