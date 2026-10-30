#include "agentspeed/configured_modes.hpp"

#include <fcntl.h>
#include <sqlite3.h>
#include <sys/stat.h>
#include <unistd.h>

#include <regex>
#include <sstream>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::configured {

namespace {

constexpr std::int64_t kMaxBytes = 1 << 20;

// A symbolic link is not followed.
std::optional<std::string> contents(const std::string& path) {
    const int descriptor = ::open(path.c_str(), O_RDONLY | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC);
    if (descriptor < 0) return std::nullopt;
    std::optional<std::string> text;
    struct stat st {};
    if (::fstat(descriptor, &st) == 0 && S_ISREG(st.st_mode) && st.st_size <= kMaxBytes) {
        std::string data(static_cast<std::size_t>(st.st_size), '\0');
        std::size_t filled = 0;
        while (filled < data.size()) {
            const auto read = ::pread(descriptor, data.data() + filled, data.size() - filled, static_cast<off_t>(filled));
            if (read <= 0) break;
            filled += static_cast<std::size_t>(read);
        }
        if (filled == data.size() && text::sanitizeUtf8(data) == data) {
            if (data.rfind("\xEF\xBB\xBF", 0) == 0) data.erase(0, 3);
            text = std::move(data);
        }
    }
    ::close(descriptor);
    return text;
}

// The JSON object of a config file.
bool parseObject(const std::string& path, std::string& text, json::Document& document) {
    auto data = contents(path);
    if (!data) return false;
    text = std::move(*data);
    return document.parse(text, 64) && document.root().isObject();
}

ExecutionModes tierOfCodexConfig(const std::string& text) {
    static const std::regex tierLine(R"re(^service_tier\s*=\s*(["'])([A-Za-z0-9][A-Za-z0-9._:-]{0,63})\1\s*(?:#.*)?$)re");
    std::istringstream in(text);
    ExecutionModes modes;
    bool found = false;
    std::string line;
    while (std::getline(in, line)) {
        const auto begin = line.find_first_not_of(" \t\r\n\v\f");
        const auto end = line.find_last_not_of(" \t\r\n\v\f");
        const std::string trimmed = begin == std::string::npos ? std::string() : line.substr(begin, end - begin + 1);
        if (!trimmed.empty() && trimmed[0] == '#') continue;
        if (trimmed.find("\"\"\"") != std::string::npos || trimmed.find("'''") != std::string::npos) return {};
        if (!trimmed.empty() && trimmed[0] == '[') break;
        std::smatch match;
        if (std::regex_match(trimmed, match, tierLine)) {
            if (found) return {};
            modes = native::openAiTier(match[2].str(), ModeEvidence::Configured, ModeScope::Configuration);
            found = true;
        } else if (trimmed.rfind("service_tier", 0) == 0 &&
                   (trimmed.size() == 12 || trimmed[12] == '=' || trimmed[12] == ' ' || trimmed[12] == '\t')) {
            return {};
        }
    }
    return modes;
}

ExecutionModes configuredEffort(const std::optional<std::string>& token) {
    return ExecutionModes::of(native::effort(token, ModeEvidence::Configured, ModeScope::Configuration));
}

}  // namespace

ExecutionModes GeminiEfforts::modes(const std::optional<std::string>& model) const {
    if (!model) return {};
    const auto it = byModel.find(*model);
    return it == byModel.end() ? ExecutionModes() : it->second;
}

std::string codexConfigPath(const std::string& sessionsRoot) {
    std::string base = sessionsRoot;
    while (base.size() > 1 && base.back() == '/') base.pop_back();
    const auto slash = base.find_last_of('/');
    if (slash != std::string::npos && base.substr(slash + 1) == "sessions") base.resize(slash);
    return base + "/config.toml";
}

ExecutionModes codex(const std::string& path) {
    const auto text = contents(path);
    return text ? tierOfCodexConfig(*text) : ExecutionModes();
}

ExecutionModes antigravity(const std::string& path) {
    std::string text;
    json::Document document;
    if (!parseObject(path, text, document)) return {};
    return native::googleTier(document.root()["userSettings"]["vertexServiceTier"].string(), ModeEvidence::Configured,
                              ModeScope::Configuration);
}

ExecutionModes workBuddy(const std::string& path) {
    std::string text;
    json::Document document;
    if (!parseObject(path, text, document)) return {};
    return configuredEffort(document.root()["reasoningEffort"].string());
}

// Of `modelConfigs.overrides` and then `customOverrides`, in order, an entry counts only when its `match` is exactly the
// one model. The last that names a level, or a budget of 0, decides; another budget names no level.
GeminiEfforts gemini(const std::string& path) {
    std::string text;
    json::Document document;
    if (!parseObject(path, text, document)) return {};
    const auto configs = document.root()["modelConfigs"];
    std::map<std::string, std::string> chosen;
    for (const char* name : {"overrides", "customOverrides"}) {
        for (const auto& entry : configs[name].elements()) {
            const auto match = entry["match"];
            const auto model = match["model"].string();
            const auto thinking = entry["modelConfig"]["generateContentConfig"]["thinkingConfig"];
            if (!match.isObject() || match.size() != 1 || !model || !thinking.valid()) continue;
            if (const auto level = thinking["thinkingLevel"].string()) {
                chosen[*model] = *level;
            } else if (thinking["thinkingBudget"].number() == 0.0) {
                chosen[*model] = "off";
            }
        }
    }
    GeminiEfforts efforts;
    for (const auto& [model, token] : chosen) efforts.byModel[model] = configuredEffort(token);
    return efforts;
}

}  // namespace agentspeed::configured

namespace agentspeed::session_modes {

namespace {

// The first column of `sql` for each id, where it is text; a file, table, column or row that is missing gives nothing.
// `sql` takes the id as `?1`.
std::map<std::string, std::string> textColumn(const std::string& path, const char* sql, const std::vector<std::string>& ids) {
    std::map<std::string, std::string> values;
    const auto file = fsx::statOf(path);
    if (ids.empty() || !file || !file->regular) return values;
    try {
        bool immutable = false;
        auto connection = sqlite::openReadOnly(path, immutable);
        auto statement = connection.prepare(sql);
        for (const auto& id : ids) {
            statement.reset();
            statement.bind(1, id);
            while (statement.step()) {
                if (statement.columnType(0) == SQLITE_TEXT) values[id] = std::string(statement.getText(0));
            }
        }
    } catch (const sqlite::Error&) {
        return {};
    }
    return values;
}

ExecutionModes selected(const std::optional<std::string>& token) {
    return ExecutionModes::of(native::effort(token, ModeEvidence::Selected, ModeScope::Session));
}

}  // namespace

std::map<std::string, ExecutionModes> workBuddy(const std::string& databasePath, const std::vector<std::string>& sessions) {
    std::map<std::string, ExecutionModes> out;
    for (const auto& [id, token] : textColumn(databasePath, "SELECT thought_level FROM sessions WHERE id = ?1", sessions)) {
        out[id] = selected(token);
    }
    return out;
}

std::map<std::string, ExecutionModes> hermes(const std::string& databasePath, const std::vector<std::string>& sessions) {
    std::map<std::string, ExecutionModes> out;
    const auto rows = textColumn(databasePath, "SELECT CASE WHEN length(model_config) <= 65536 THEN model_config END FROM sessions WHERE id = ?1", sessions);
    for (const auto& [id, text] : rows) {
        json::Document document;
        ExecutionModes modes;
        if (document.parse(text, 64) && document.root().isObject()) {
            const auto reasoning = document.root()["reasoning_config"];
            if (reasoning.isObject()) {
                const auto enabled = reasoning["enabled"];
                modes = selected(enabled.isBool() && !enabled.isTrue() ? std::optional<std::string>("disabled") : reasoning["effort"].string());
            }
        }
        out[id] = std::move(modes);
    }
    return out;
}

}  // namespace agentspeed::session_modes
