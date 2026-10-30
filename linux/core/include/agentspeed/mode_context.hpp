#pragma once

#include <map>
#include <optional>
#include <set>
#include <string>

#include "agentspeed/configured_modes.hpp"
#include "agentspeed/locations.hpp"

namespace agentspeed {

// What file-based agents show beyond their own facts: configured values and WorkBuddy's session-current effort. Files
// are checked at startup and at every rediscovery and read again only when their size or modification time changed.
class ModeContext {
public:
    // A root of an enabled source; the files of a disabled source are never read.
    void addRoot(RootFamily family, const std::string& root);
    void setGeminiSettings(const std::string& path) { gemini_.path = path; }

    // `workBuddySessions`: the main sessions of each WorkBuddy root. True when any value changed.
    bool refresh(const std::map<std::string, std::set<std::string>>& workBuddySessions = {});

    // The configured facts of an agent found under `root`; Gemini's depend on its model.
    ExecutionModes configured(RootFamily family, const std::string& root, const std::optional<std::string>& model) const;
    // The session-current fact of a WorkBuddy transcript.
    ExecutionModes session(const std::string& root, const std::string& sessionId) const;

private:
    struct Stamp {
        std::int64_t length = 0, writeNs = 0;
        bool operator==(const Stamp&) const = default;
    };
    static std::optional<Stamp> stampOf(const std::string& path);

    struct Config {
        std::string path;
        ExecutionModes (*read)(const std::string&) = nullptr;
        ExecutionModes value;
        std::optional<Stamp> stamp;
        bool refresh();
    };
    struct Gemini {
        std::string path;
        configured::GeminiEfforts value;
        std::optional<Stamp> stamp;
        bool refresh();
    };
    // WorkBuddy's `workbuddy.db` beside one `projects` folder.
    struct Store {
        std::string path;
        std::optional<Stamp> stamp, walStamp;
        std::map<std::string, ExecutionModes> values;
        std::set<std::string> queried;
        bool refresh(const std::set<std::string>& sessions);
    };

    std::map<std::string, Config> configs_;
    std::map<std::string, Store> stores_;
    Gemini gemini_;
};

}  // namespace agentspeed
