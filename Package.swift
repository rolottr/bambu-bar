// swift-tools-version: 6.0
import PackageDescription

// Focused license tests; the macOS app and widget are built with XcodeGen.
let package = Package(
    name: "BambuBarLicensing",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "BambuBarUpdates", path: "Sources/BambuBar/Updates"),
        .testTarget(name: "UpdatesTests", dependencies: ["BambuBarUpdates"], path: "Tests/UpdatesTests"),
        // PolarClient builds its checkout links with Website, so the target compiles that file too.
        .target(name: "BambuBarLicensing", path: "Sources/BambuBar",
                sources: ["Licensing/LicenseStore.swift", "Licensing/PolarClient.swift", "Services/Website.swift"]),
        .testTarget(name: "LicensingTests", dependencies: ["BambuBarLicensing"], path: "Tests/LicensingTests"),
    ],
    swiftLanguageModes: [.v5]
)
