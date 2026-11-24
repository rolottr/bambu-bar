import XCTest

/// Links to bambubar.app carry UTM tags (the browser gets no Referer from the app) and reject clevermechanisms.dev.
final class WebsiteTests: XCTestCase {
    private func query(_ url: URL) -> [String: String] {
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    func testAboutLinksAreTagged() {
        XCTAssertEqual(Website.home.absoluteString,
                       "https://bambubar.app/?utm_source=bambu_bar&utm_medium=\(Website.medium)&utm_campaign=about&utm_content=website")
        XCTAssertEqual(Website.changelog.path, "/changelog")
        XCTAssertEqual(query(Website.changelog), ["utm_source": "bambu_bar", "utm_medium": Website.medium,
                                                  "utm_campaign": "about", "utm_content": "release_notes"])
        XCTAssertEqual(Website.support.path, "/support")
        XCTAssertEqual(query(Website.support), ["utm_source": "bambu_bar", "utm_medium": Website.medium,
                                                "utm_campaign": "about", "utm_content": "support"])
    }

    func testSupportLinkKeepsItsPlacement() {
        let url = Website.url("download", campaign: "support_project", content: "menu_panel")
        XCTAssertEqual(url.absoluteString,
                       "https://bambubar.app/download?utm_source=bambu_bar&utm_medium=\(Website.medium)&utm_campaign=support_project&utm_content=menu_panel")
    }

    func testRejectsCleverMechanismsDomainAndInvalidHosts() {
        XCTAssertNil(Website.validatedHost("clevermechanisms.dev"))
        XCTAssertNil(Website.validatedHost("CLEVERMECHANISMS.DEV"))
        XCTAssertNil(Website.validatedHost("bambubar.clevermechanisms.dev"))
        XCTAssertNil(Website.validatedHost("a.b.clevermechanisms.dev"))
        XCTAssertNil(Website.validatedHost(""))
        XCTAssertNil(Website.validatedHost("   "))
        XCTAssertNil(Website.validatedHost("https://bambubar.app"))
        XCTAssertNil(Website.validatedHost("localhost"))
        XCTAssertNil(Website.url("support", host: "clevermechanisms.dev", campaign: "about", content: "support"))
        XCTAssertNil(Website.url("support", host: "docs.clevermechanisms.dev", campaign: "about", content: "support"))

        XCTAssertEqual(Website.validatedHost("  BambuBar.App  "), "bambubar.app")
        XCTAssertEqual(
            Website.url("/support/", host: "bambubar.app", campaign: "about", content: "support")?.absoluteString,
            "https://bambubar.app/support?utm_source=bambu_bar&utm_medium=\(Website.medium)&utm_campaign=about&utm_content=support"
        )
    }
}
