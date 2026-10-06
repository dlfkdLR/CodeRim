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
        .package(url: "https://github.com/steipete/SweetCookieKit", exact: "0.5.3"),
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
                .product(name: "SweetCookieKit", package: "SweetCookieKit"),
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
                .linkedLibrary("sqlite3"),
                // Still runs on macOS 14, but is stamped as built against the macOS 26 SDK. Without
                // that stamp macOS 26 gives the app its compatibility look: a flat full-height
                // sidebar and square-ish window instead of the floating glass sidebar.
                .unsafeFlags(["-Xlinker", "-platform_version", "-Xlinker", "macos",
                              "-Xlinker", "14.0", "-Xlinker", "26.0"])
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
