import AppKit

/// Opens an issue's page on Bambu Lab's wiki. Its redirect (see `HMSCatalog.wikiURL`) sends some codes to the HMS
/// index although the code has a page: filed for every AMS slot at once, or under another model (a P1S's lead
/// screw reminder lives with the H2's). So the app asks where each of `HMSCatalog.wikiCandidates` leads and opens
/// the first real page, settling for the index only when all miss. The questions are HEAD requests to the same
/// redirect the browser would follow, carrying nothing the link doesn't.
@MainActor
enum HMSWiki {
    static func open(_ url: URL) {
        Task {
            NSWorkspace.shared.open(await resolve(url) ?? url)
        }
    }

    /// The page the link should land on, or nil to open it as it is (not a redirect link, or Bambu didn't answer).
    static func resolve(_ url: URL) async -> URL? {
        guard url.host == "e.bambulab.com" else { return nil }
        var index: URL?
        for candidate in HMSCatalog.wikiCandidates(for: url) {
            guard let page = await destination(of: candidate) else { continue }
            if !isIndex(page) { return page }
            index = index ?? page
        }
        return index
    }

    private static func isIndex(_ url: URL) -> Bool {
        url.path.hasSuffix("/hms/home")
    }

    /// Where the redirect points, without following it.
    private static func destination(of url: URL) async -> URL? {
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 4)
        request.httpMethod = "HEAD"
        guard let (_, response) = try? await session.data(for: request),
              let http = response as? HTTPURLResponse, (300..<400).contains(http.statusCode),
              let location = http.value(forHTTPHeaderField: "Location") else { return nil }
        return URL(string: location, relativeTo: url)?.absoluteURL
    }

    private static let session = URLSession(configuration: .ephemeral, delegate: StopAtRedirect(), delegateQueue: nil)

    private final class StopAtRedirect: NSObject, URLSessionTaskDelegate {
        func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                        newRequest request: URLRequest) async -> URLRequest? {
            nil
        }
    }
}
