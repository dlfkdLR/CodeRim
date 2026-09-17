// swift-tools-version: 6.2

import PackageDescription

let package = Package(
    name: "CodeRim",
    platforms: [
        .macOS(.v14)
    ],
    products: [
        .executable(name: "CodeRim", targets: ["CodeRim"]),
        .executable(name: "CodeRimCLI", targets: ["CodeRimCLI"]),
        .executable(name: "CodeRimClaudeBridge", targets: ["CodeRimClaudeBridge"])
    ],
    dependencies: [
        .package(url: "https://github.com/sparkle-project/Sparkle", exact: "2.9.6"),
        .package(url: "https://github.com/steipete/CodexBar", revision: "51ed16bdd3abe35ec53af99818e1b5f0d2a631d3")
    ],
    targets: [
        .systemLibrary(
            name: "CSQLite",
            path: "Sources/CSQLite"
        ),
        .executableTarget(
            name: "CodeRim",
            dependencies: [
                "ClaudeBridgeCore",
                "CodeRimShared",
                "CSQLite",
                .product(name: "CodexBarCore", package: "CodexBar"),
                .product(name: "Sparkle", package: "Sparkle")
            ],
            path: "Sources/CodeRim",
            resources: [
                .process("Resources")
            ],
            linkerSettings: [
                .linkedFramework("AppKit"),
                .linkedFramework("CoreServices"),
                .linkedFramework("Security"),
                .linkedFramework("ServiceManagement"),
                .linkedLibrary("sqlite3")
            ]
        ),
        .target(name: "CodeRimShared"),
        .target(name: "CodeRimWidgetUI", dependencies: ["CodeRimShared"], path: "Sources/CodeRimWidget", exclude: ["CodeRimWidget.swift"]),
        .executableTarget(name: "CodeRimCLI", dependencies: ["CodeRimShared"]),
        .testTarget(name: "CodeRimCompanionTests", dependencies: ["CodeRimShared", "CodeRimCLI", "CodeRimWidgetUI"]),
        .target(
            name: "ClaudeBridgeCore",
            path: "Sources/ClaudeBridgeCore"
        ),
        .executableTarget(
            name: "CodeRimClaudeBridge",
            dependencies: ["ClaudeBridgeCore"],
            path: "Sources/CodeRimClaudeBridge"
        ),
        .testTarget(
            name: "CodeRimTests",
            dependencies: ["CodeRim", "ClaudeBridgeCore", "CodeRimShared"],
            path: "Tests/CodeRimTests"
        )
    ]
)
