// swift-tools-version:6.0
import PackageDescription

let package = Package(
    name: "NavTune",
    platforms: [.macOS(.v15)],
    targets: [
        .executableTarget(
            name: "NavTune",
            path: "Sources/NavTune"
        ),
        .testTarget(
            name: "NavTuneTests",
            dependencies: ["NavTune"],
            path: "Tests/NavTuneTests"
        ),
    ],
    swiftLanguageModes: [.v5]
)
