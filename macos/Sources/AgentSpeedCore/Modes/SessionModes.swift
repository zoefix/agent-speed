import Foundation

/// Session-current facts (EXECUTION-MODES, Current values): a session's latest value, overwritten in place. It says what
/// the session has selected now, so it is held apart from every response.
enum SessionModes {
    /// `workbuddy.db` `sessions.thought_level` of each session id. Only these two columns are ever selected.
    static func workBuddy(databasePath: String, sessions: [String]) -> [String: ExecutionModes] {
        ForeignDatabase.text(path: databasePath, sql: "SELECT thought_level FROM sessions WHERE id = ?1", ids: sessions)
            .mapValues { selected($0) }
    }

    /// `state.db` `sessions.model_config.reasoning_config` of each session id: `enabled: false` is `off`, else its
    /// `effort`. The rest of the row and every other table are never selected.
    static func hermes(databasePath: String, sessions: [String]) -> [String: ExecutionModes] {
        ForeignDatabase.text(path: databasePath,
                             sql: "SELECT CASE WHEN length(model_config) <= 65536 THEN model_config END FROM sessions WHERE id = ?1",
                             ids: sessions)
            .mapValues { text in
                guard let json = try? JSONParser.parse(text), json.object != nil,
                      let reasoning = json["reasoning_config"], reasoning.object != nil else { return .empty }
                return selected(reasoning["enabled"]?.bool == false ? "disabled" : reasoning["effort"]?.string)
            }
    }

    private static func selected(_ token: String?) -> ExecutionModes {
        ExecutionModes([ExecutionModeAdapters.effort(token, evidence: .selected, scope: .session)].compactMap { $0 })
    }
}
