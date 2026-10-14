// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "UsageDisplay",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(name: "UsageDisplay", path: "Sources/UsageDisplay"),
        .testTarget(name: "UsageDisplayTests", dependencies: ["UsageDisplay"])
    ]
)
