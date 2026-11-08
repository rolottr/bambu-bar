import XCTest

/// Links to bambubar.app carry UTM tags (the browser gets no Referer from the app), with the GitHub build's medium.
final class WebsiteTests: XCTestCase {
    private func query(_ url: URL) -> [String: String] {
        let items = URLComponents(url: url, resolvingAgainstBaseURL: false)?.queryItems ?? []
        return Dictionary(uniqueKeysWithValues: items.map { ($0.name, $0.value ?? "") })
    }

    func testAboutLinksAreTagged() {
        XCTAssertEqual(Website.home.absoluteString,
                       "https://bambubar.app/?utm_source=bambu_bar&utm_medium=app&utm_campaign=about&utm_content=website")
        XCTAssertEqual(Website.changelog.path, "/changelog")
        XCTAssertEqual(query(Website.changelog), ["utm_source": "bambu_bar", "utm_medium": "app",
                                                  "utm_campaign": "about", "utm_content": "release_notes"])
    }

    func testSupportLinkKeepsItsPlacement() {
        let url = Website.url("download", campaign: "support_project", content: "menu_panel")
        XCTAssertEqual(url.absoluteString,
                       "https://bambubar.app/download?utm_source=bambu_bar&utm_medium=app&utm_campaign=support_project&utm_content=menu_panel")
    }
}
