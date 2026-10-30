// swift-tools-version: 5.9
// [AI修改] 新 macOS 客户端独立编译，不再链接旧仪表盘源码。
import PackageDescription
import Foundation

// [AI修改] 显式链接.a，避免误选不随应用分发的dylib。
let coreDirectory = ProcessInfo.processInfo.environment["CODEXPEDAL_CORE_LIB_DIR"] ?? "../build/rust/release"

let package = Package(
    name: "CodeXPedal",
    platforms: [.macOS(.v13)],
    products: [.executable(name: "CodeXPedal", targets: ["CodeXPedal"])],
    targets: [
        .target(name: "CSharedCore", linkerSettings: [.unsafeFlags([coreDirectory + "/libcodexpedal_core.a"]), .linkedLibrary("iconv")]),
        .target(name: "CodeXPedalCore", dependencies: ["CSharedCore"]),
        .executableTarget(name: "CodeXPedal", dependencies: ["CodeXPedalCore"]),
        .testTarget(name: "CodeXPedalCoreTests", dependencies: ["CodeXPedalCore"])
    ]
)
