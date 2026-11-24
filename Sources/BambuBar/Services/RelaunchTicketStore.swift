import Foundation

/// The hand-over between an app restarting for a new language and its replacement:
/// the predecessor's PID, a one-time token passed on the replacement's command line,
/// and any URLs queued during the restart.
struct RelaunchTicket: Codable, Equatable, Sendable {
    let token: UUID
    let predecessor: Int32
    let createdAt: Date
    var urls: [String] = []
}

/// Stores at most one `RelaunchTicket` in `UserDefaults`. A ticket is valid only for
/// a short window and only while its stored copy matches, so a stale or foreign token
/// never bypasses single-instance checks.
struct RelaunchTicketStore: @unchecked Sendable {
    static let key = "languageRelaunch.v1"
    static let argument = "--language-relaunch"
    private static let maximumSize = 32_768
    private static let maximumURLs = 32
    /// Seconds a ticket stays valid, allowing minor backwards clock skew.
    static let validity: ClosedRange<TimeInterval> = -5...30

    let defaults: UserDefaults
    var urlScheme = "bambubar"
    var processIdentifier: Int32 = ProcessInfo.processInfo.processIdentifier
    var now: @Sendable () -> Date = { Date() }

    static var shared: RelaunchTicketStore { RelaunchTicketStore(defaults: .standard) }

    func begin() -> RelaunchTicket? {
        let ticket = RelaunchTicket(token: UUID(), predecessor: processIdentifier, createdAt: now())
        guard let data = try? JSONEncoder().encode(ticket) else { return nil }
        defaults.set(data, forKey: Self.key)
        guard defaults.synchronize() else {
            cancel(ticket)
            return nil
        }
        return ticket
    }

    func isPending(_ ticket: RelaunchTicket) -> Bool {
        let elapsed = now().timeIntervalSince(ticket.createdAt)
        guard ticket.predecessor > 0, elapsed.isFinite, Self.validity.contains(elapsed), let stored = stored() else {
            return false
        }
        return stored.token == ticket.token
            && stored.predecessor == ticket.predecessor
            && stored.createdAt == ticket.createdAt
    }

    /// The ticket a replacement launched with `--language-relaunch <token>` takes over, if still pending.
    func pending(arguments: [String] = CommandLine.arguments) -> RelaunchTicket? {
        guard let index = arguments.firstIndex(of: Self.argument),
              arguments.indices.contains(index + 1),
              let token = UUID(uuidString: arguments[index + 1]),
              let ticket = stored(),
              ticket.token == token,
              ticket.predecessor != processIdentifier,
              isPending(ticket)
        else { return nil }
        return ticket
    }

    func cancel(_ ticket: RelaunchTicket) {
        guard stored()?.token == ticket.token else { return }
        defaults.removeObject(forKey: Self.key)
        _ = defaults.synchronize()
    }

    /// Queues URLs arriving during a restart so the replacement opens them; returns false if they couldn't be saved.
    func queue(_ urls: [URL], for ticket: RelaunchTicket) -> Bool {
        guard isPending(ticket), var stored = stored() else { return false }
        for value in urls.filter(isForwardable).map(\.absoluteString) where !stored.urls.contains(value) {
            stored.urls.append(value)
        }
        guard stored.urls.count <= Self.maximumURLs,
              let updated = try? JSONEncoder().encode(stored),
              updated.count <= Self.maximumSize
        else { return false }
        defaults.set(updated, forKey: Self.key)
        return defaults.synchronize()
    }

    /// The ticket is expired, missing, or tampered with.
    struct InvalidTicket: Error, Equatable {}

    /// Returns any queued URLs and removes the ticket.
    func consume(_ ticket: RelaunchTicket) throws -> [URL] {
        guard isPending(ticket), let stored = stored(), stored.urls.count <= Self.maximumURLs else {
            throw InvalidTicket()
        }
        let urls = stored.urls.compactMap(URL.init(string:)).filter(isForwardable)
        guard urls.count == stored.urls.count else { throw InvalidTicket() }
        cancel(ticket)
        return urls
    }

    private func isForwardable(_ url: URL) -> Bool { url.isFileURL || url.scheme == urlScheme }

    private func stored() -> RelaunchTicket? {
        guard let data = defaults.data(forKey: Self.key), data.count <= Self.maximumSize else { return nil }
        return try? JSONDecoder().decode(RelaunchTicket.self, from: data)
    }
}
