// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "UsageMonitor",
    platforms: [.macOS(.v13)],
    dependencies: [
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.9.6")
    ],
    targets: [
        .executableTarget(
            name: "UsageMonitor",
            dependencies: [.product(name: "Sparkle", package: "Sparkle")],
            path: "Sources/UsageMonitor",
            // The app bundle embeds Sparkle.framework in Contents/Frameworks.
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]),
        .testTarget(name: "UsageMonitorTests", dependencies: ["UsageMonitor"])
    ]
)
