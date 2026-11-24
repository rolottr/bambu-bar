// swift-tools-version: 6.0
import PackageDescription

// Focused licensing and update tests; the macOS app and widget are built with XcodeGen.
let package = Package(
    name: "BambuBarLicensing",
    platforms: [.macOS(.v14)],
    targets: [
        // PolarClient builds links with Website and LicenseStore persists via Keychain.
        .target(
            name: "BambuBarLicensing",
            path: "Sources/BambuBar",
            sources: [
                "Licensing/LicenseStore.swift",
                "Licensing/PolarClient.swift",
                "Services/Keychain.swift",
                "Services/Website.swift",
            ]
        ),
        .testTarget(
            name: "LicensingTests",
            dependencies: ["BambuBarLicensing"],
            path: "Tests/LicensingTests"
        ),
        .target(
            name: "BambuBarUpdates",
            dependencies: ["BambuBarLicensing"],
            path: "Sources/BambuBar/Updates"
        ),
        .testTarget(
            name: "UpdatesTests",
            dependencies: ["BambuBarUpdates", "BambuBarLicensing"],
            path: "Tests/UpdatesTests"
        ),
    ],
    swiftLanguageModes: [.v5]
)
