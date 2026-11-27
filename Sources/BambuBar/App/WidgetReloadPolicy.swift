import Foundation

/// When the app asks WidgetKit to reload the widgets, and what connection a widget shows for a printer. Pure, so
/// the tests can play a connection's phases through it (WidgetReloadPolicyTests).
///
/// macOS gives each widget a daily allowance of reloads (40 to 70) and drops requests beyond it without a word;
/// the widget then plays out an old timeline until it is removed and added again. A printer that can't be reached
/// (switched off, unplugged) retries every 2 to 30 seconds, and each attempt used to flip its widget link between
/// online and offline, which changed the reload key: about 29 requests an hour, the day's allowance in two hours.
enum WidgetReloadPolicy {
    /// Where a printer's connection stands, as far as the widget is concerned.
    enum Connection: Equatable {
        /// Stopped, or given up with no retry to follow (a certificate that doesn't check out): offline at once.
        case idle
        /// An attempt is under way.
        case connecting
        case online
        /// The last attempt failed, or the printer went silent; another follows.
        case offline
        case unauthorized
    }

    /// How long after the printer's last message a reconnecting printer still counts as online: a drop the next
    /// attempt fixes stays off the widget. Past it, the next phase change shows the printer offline, once. The
    /// connection's watchdog gives up on a silent printer after 95 seconds.
    static let recentlyHeard: TimeInterval = 120

    /// The link a widget shows. `failedSinceOnline` is whether an attempt failed since the printer was last online
    /// (or since the connection started). Only the first attempt after a start or a wake shows "connecting" (no
    /// report yet) or the last report (the printer is most likely still there); attempts that follow a failure
    /// show the printer offline until it answers again, so retries don't move the widget.
    static func link(_ connection: Connection, hasStatus: Bool, lastHeard: Date?, failedSinceOnline: Bool,
                     now: Date) -> SharedSnapshot.Link {
        switch connection {
        case .online: return .online
        case .unauthorized: return .unauthorized
        case .idle: return .offline
        case .connecting, .offline:
            if hasStatus, let lastHeard, now.timeIntervalSince(lastHeard) < recentlyHeard { return .online }
            if connection == .connecting, !failedSinceOnline { return hasStatus ? .online : .connecting }
            return .offline
        }
    }

    /// The least time between two reloads that aren't forced.
    static let minimumSpacing: TimeInterval = 120

    /// Between retries of a correction: ten minutes, doubling for each request in a row no widget answered (macOS
    /// dropped it), up to an hour.
    static func retrySpacing(unanswered: Int) -> TimeInterval {
        min(3600, 600 * pow(2, Double(min(max(unanswered, 0), 3))))
    }

    /// Whether to ask for a reload now.
    /// - `keyChanged`: something a glance at the widget would catch changed since the last request.
    /// - `force`: a change that must reach the widget (a new state, a printer coming online).
    /// - `opened`: the panel or window opened while the app is in front, where reloads cost nothing.
    /// - `unanswered`: requests in a row that no widget answered, so macOS is likely dropping them.
    /// - `correctionAllowed`: the daily cap on corrections isn't reached.
    /// - `correctionDue`: a reload would move what a widget shows (read lazily: it may read the receipts file).
    ///
    /// A change that isn't forced waits out the retry spacing while requests go unanswered, as corrections do:
    /// asking again only spends what's left of the allowance. A forced change always goes through.
    static func shouldReload(keyChanged: Bool, force: Bool, opened: Bool, sinceLastReload elapsed: TimeInterval,
                             unanswered: Int, correctionAllowed: Bool, correctionDue: () -> Bool) -> Bool {
        if opened {
            return elapsed > minimumSpacing && (keyChanged || (correctionAllowed && correctionDue()))
        }
        let retry = retrySpacing(unanswered: unanswered)
        let changeSpacing = unanswered > 0 ? retry : minimumSpacing
        if keyChanged && (force || elapsed > changeSpacing) { return true }
        return correctionAllowed && elapsed > retry && correctionDue()
    }
}
