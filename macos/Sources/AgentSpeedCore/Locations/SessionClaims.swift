/// Which database of a source reads a session (METRICS 2.1): the first that has it. A session id another database of
/// the same source already provides is skipped, so a copied database adds no agents and no sessions.
///
/// Not thread-safe: shared by the databases of one source on the session queue.
final class SessionClaims {
    private var owners: [String: Int] = [:]

    /// True when `owner` may read the session: no other database has it.
    func claim(_ session: String, by owner: Int) -> Bool {
        if let current = owners[session] { return current == owner }
        owners[session] = owner
        return true
    }

    func release(_ session: String, by owner: Int) {
        if owners[session] == owner { owners[session] = nil }
    }

    func releaseAll(by owner: Int) {
        owners = owners.filter { $0.value != owner }
    }
}
