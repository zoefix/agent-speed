#include "agentspeed/hermes.hpp"

#include <dirent.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <fstream>
#include <regex>

#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::hermes {

namespace {

constexpr const char* kTurnLogger = "agent.turn_context";
constexpr const char* kLoopLogger = "agent.conversation_loop";
constexpr const char* kLevels[] = {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"};
constexpr std::int64_t kBacklogWindowMs = 15 * 60'000;
constexpr std::int64_t kLargeFileBytes = 256LL * 1024 * 1024;
constexpr std::int64_t kLargeFileTailBytes = 64LL * 1024 * 1024;
constexpr int kMissingLimit = 2;
constexpr std::int64_t kHourMs = 3'600'000;

// --- Python posixpath / ntpath helpers -------------------------------------------------------------------------

bool isSeparator(char c, bool windows) { return c == '/' || (windows && c == '\\'); }

std::string replaceAll(std::string s, char from, char to) {
    std::replace(s.begin(), s.end(), from, to);
    return s;
}

bool isAbsolute(const std::string& path, bool windows) {
    if (!windows) return !path.empty() && path[0] == '/';
    const std::string p = replaceAll(path, '/', '\\');
    auto letter = [](char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); };
    return p.rfind("\\\\", 0) == 0 || (p.size() >= 3 && letter(p[0]) && p[1] == ':' && p[2] == '\\');
}

std::string foldCase(const std::string& path) {
    std::string s = replaceAll(path, '/', '\\');
    for (char& c : s) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return s;
}

std::pair<std::string, std::string> splitRoot(const std::string& path, bool windows) {
    if (!windows) {
        std::size_t slashes = 0;
        while (slashes < path.size() && path[slashes] == '/') ++slashes;
        // POSIX keeps exactly two leading slashes; one or three and more become one.
        const std::string prefix = slashes == 0 ? "" : slashes == 2 ? "//" : "/";
        return {prefix, path.substr(slashes)};
    }
    const std::string p = replaceAll(path, '/', '\\');
    if (p.rfind("\\\\", 0) == 0) {
        const auto server = p.find('\\', 2);
        if (server == std::string::npos) return {p, ""};
        const auto share = p.find('\\', server + 1);
        if (share == std::string::npos) return {p, ""};
        return {p.substr(0, share), p.substr(share)};
    }
    const std::string drive = p.size() >= 2 && p[1] == ':' ? p.substr(0, 2) : "";
    std::string rest = p.substr(drive.size());
    if (!rest.empty() && rest[0] == '\\') {
        const auto first = rest.find_first_not_of('\\');
        return {drive + "\\", first == std::string::npos ? "" : rest.substr(first)};
    }
    return {drive, rest};
}

std::string normalizePath(const std::string& path, bool windows) {
    const char separator = windows ? '\\' : '/';
    const auto [prefix, rest] = splitRoot(path, windows);
    const bool rooted = !prefix.empty() && isSeparator(prefix.back(), windows);
    std::vector<std::string> parts;
    std::string current;
    auto flush = [&] {
        const std::string part = current;
        current.clear();
        if (part.empty() || part == ".") return;
        if (part == ".." && (!parts.empty() ? parts.back() != ".." : rooted)) {
            if (!parts.empty()) parts.pop_back();
            return;
        }
        parts.push_back(part);
    };
    for (char c : rest) {
        if (c == '/' || (windows && c == '\\')) {
            flush();
        } else {
            current += c;
        }
    }
    flush();
    std::string joined;
    for (std::size_t i = 0; i < parts.size(); ++i) {
        if (i) joined += separator;
        joined += parts[i];
    }
    const std::string result = prefix + joined;
    return result.empty() ? "." : result;
}

std::string directoryName(const std::string& path, bool windows) {
    const auto [prefix, rest] = splitRoot(path, windows);
    std::size_t cut = rest.size();
    while (cut > 0 && !isSeparator(rest[cut - 1], windows)) --cut;
    std::string head = rest.substr(0, cut);
    while (!head.empty() && isSeparator(head.back(), windows)) head.pop_back();
    return prefix + head;
}

std::string baseName(const std::string& path, bool windows) {
    const auto rest = splitRoot(path, windows).second;
    std::size_t cut = rest.size();
    while (cut > 0 && !isSeparator(rest[cut - 1], windows)) --cut;
    return rest.substr(cut);
}

std::string join(bool windows, const std::string& left, const std::string& right) {
    if (left.empty()) return right;
    return isSeparator(left.back(), windows) ? left + right : left + (windows ? '\\' : '/') + right;
}

std::string trim(const std::string& s) {
    const auto b = s.find_first_not_of(" \t\r\n\v\f");
    if (b == std::string::npos) return {};
    const auto e = s.find_last_not_of(" \t\r\n\v\f");
    return s.substr(b, e - b + 1);
}

bool folderExists(const std::string& path) {
    struct stat st {};
    return !path.empty() && stat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode);
}

std::vector<std::string> profileFolders(const std::string& profiles) {
    std::vector<std::string> out;
    DIR* dir = opendir(profiles.c_str());
    if (!dir) return out;
    while (auto* entry = readdir(dir)) {
        const std::string name = entry->d_name;
        if (name.empty() || name[0] == '.') continue;
        const std::string path = profiles + "/" + name;
        struct stat st {};
        if (lstat(path.c_str(), &st) == 0 && S_ISDIR(st.st_mode)) out.push_back(path);
    }
    closedir(dir);
    std::sort(out.begin(), out.end());
    return out;
}

bool hasTimestampShape(std::string_view line) {
    if (line.size() < kTimestampLength) return false;
    for (std::size_t i = 0; i < kTimestampLength; ++i) {
        const char c = line[i];
        bool ok = false;
        switch (i) {
            case 4:
            case 7: ok = c == '-'; break;
            case 10: ok = c == ' '; break;
            case 13:
            case 16: ok = c == ':'; break;
            case 19: ok = c == ','; break;
            default: ok = c >= '0' && c <= '9'; break;
        }
        if (!ok) return false;
    }
    return true;
}

int digits(std::string_view text, std::size_t start, std::size_t n) {
    int value = 0;
    for (std::size_t i = start; i < start + n; ++i) value = value * 10 + (text[i] - '0');
    return value;
}

std::string_view levelOf(std::string_view line) {
    const std::size_t at = kTimestampLength + 1;
    for (std::string_view level : kLevels) {
        if (line.size() > at + level.size() && line.substr(at, level.size()) == level && line[at + level.size()] == ' ') {
            return level;
        }
    }
    return {};
}

bool split(const std::string& rest, const std::string& logger, std::string& sessionId, std::string& message) {
    const std::string head = " " + logger + ": ";
    if (rest.rfind(head, 0) == 0) {
        sessionId.clear();
        message = rest.substr(head.size());
        return true;
    }
    if (rest.rfind(" [", 0) == 0) {
        const auto end = rest.find("] " + logger + ": ", 2);
        if (end != std::string::npos && end > 2) {
            sessionId = rest.substr(2, end - 2);
            message = rest.substr(end + logger.size() + 4);
            return true;
        }
    }
    return false;
}

const std::regex& turnPattern() {
    static const std::regex r(
        R"(^conversation turn: session=[^ ]* model=([\s\S]*?) provider=[^ ]* platform=([^ ]*) history=-?[0-9]+ msg=)");
    return r;
}

const std::regex& callPattern() {
    static const std::regex r(
        R"(^API call #([0-9]+): model=([\s\S]*?) provider=([^ ]*) in=(-?[0-9]+|\?) out=(-?[0-9]+|\?) total=(-?[0-9]+|\?) )"
        R"(latency=(-?[0-9]+(?:\.[0-9]+)?)s(?: cache=(-?[0-9]+)/-?[0-9]+ \(-?[0-9]+%\))?(?: write=(-?[0-9]+))?(?: [\s\S]*)?$)");
    return r;
}

std::int64_t saturatingSubtract(std::int64_t a, std::int64_t b) {
    std::int64_t result = 0;
    if (__builtin_sub_overflow(a, b, &result)) return a < 0 ? INT64_MIN : INT64_MAX;
    return result;
}

bool startsWithKey(std::string_view s, std::string_view key, std::size_t& after) {
    for (const std::string_view form : {std::string(key), "\"" + std::string(key) + "\"", "'" + std::string(key) + "'"}) {
        if (s.substr(0, form.size()) == form) {
            after = form.size();
            return true;
        }
    }
    return false;
}

bool isBlankOrTab(char c) { return c == ' ' || c == '\t'; }

// ^(model|"model"|'model')[ \t]*:([ \t]+(#.*)?)?$
bool modelHeader(std::string_view line) {
    std::size_t i = 0;
    if (!startsWithKey(line, "model", i)) return false;
    while (i < line.size() && isBlankOrTab(line[i])) ++i;
    if (i >= line.size() || line[i] != ':') return false;
    ++i;
    if (i == line.size()) return true;
    if (!isBlankOrTab(line[i])) return false;
    while (i < line.size() && isBlankOrTab(line[i])) ++i;
    return i == line.size() || line[i] == '#';
}

// ^(openai_runtime|...)[ \t]*:(|[ \t].*)$ → the group after the colon.
std::optional<std::string_view> runtimeKey(std::string_view body) {
    std::size_t i = 0;
    if (!startsWithKey(body, "openai_runtime", i)) return std::nullopt;
    while (i < body.size() && isBlankOrTab(body[i])) ++i;
    if (i >= body.size() || body[i] != ':') return std::nullopt;
    const auto rest = body.substr(i + 1);
    if (!rest.empty() && !isBlankOrTab(rest[0])) return std::nullopt;
    return rest;
}

bool codexValue(std::string_view v) {
    auto isValue = [](std::string_view s) {
        static constexpr std::string_view kValue = "codex_app_server";
        if (s.size() != kValue.size()) return false;
        for (std::size_t i = 0; i < s.size(); ++i) {
            char c = s[i];
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            if (c != kValue[i]) return false;
        }
        return true;
    };
    std::size_t i = 0;
    if (v.empty() || !isBlankOrTab(v[0])) return false;
    while (i < v.size() && isBlankOrTab(v[i])) ++i;
    std::size_t end = 0;
    if (i < v.size() && (v[i] == '"' || v[i] == '\'')) {
        const char quote = v[i];
        const auto close = v.find(quote, i + 1);
        if (close == std::string_view::npos) return false;
        std::string_view inner = v.substr(i + 1, close - i - 1);
        while (!inner.empty() && isBlankOrTab(inner.front())) inner.remove_prefix(1);
        while (!inner.empty() && isBlankOrTab(inner.back())) inner.remove_suffix(1);
        if (!isValue(inner)) return false;
        end = close + 1;
    } else {
        std::size_t j = i;
        while (j < v.size() && !isBlankOrTab(v[j])) ++j;
        if (!isValue(v.substr(i, j - i))) return false;
        end = j;
    }
    const auto rest = v.substr(end);
    if (rest.find_first_not_of(" \t") == std::string_view::npos) return true;
    std::size_t k = 0;
    if (rest.empty() || !isBlankOrTab(rest[0])) return false;
    while (k < rest.size() && isBlankOrTab(rest[k])) ++k;
    return k < rest.size() && rest[k] == '#';
}

}  // namespace

std::string home(const std::function<std::optional<std::string>(const std::string&)>& environment, bool windows,
                 const std::string& userHome) {
    std::string fallback;
    if (windows) {
        const std::string local = trim(environment("LOCALAPPDATA").value_or(""));
        fallback = join(windows, !local.empty() ? local : join(windows, join(windows, userHome, "AppData"), "Local"), "hermes");
    } else {
        fallback = join(windows, userHome, ".hermes");
    }
    std::string raw = trim(environment("HERMES_HOME").value_or(""));
    if (raw == "~" || raw.rfind("~/", 0) == 0 || raw.rfind("~\\", 0) == 0) raw = userHome + raw.substr(1);
    if (raw.empty() || !isAbsolute(raw, windows)) return fallback;
    const std::string value = normalizePath(raw, windows);
    const std::string folded = windows ? foldCase(value) : value;
    std::string basis = normalizePath(fallback, windows);
    basis = windows ? foldCase(basis) : basis;
    const char separator = windows ? '\\' : '/';
    std::string trimmedBasis = basis;
    while (!trimmedBasis.empty() && trimmedBasis.back() == separator) trimmedBasis.pop_back();
    if (folded == basis || folded.rfind(trimmedBasis + separator, 0) == 0) return fallback;
    const std::string parent = directoryName(value, windows);
    return baseName(parent, windows) == "profiles" ? directoryName(parent, windows) : value;
}

std::vector<std::string> logFolders(const std::string& home) {
    if (home.empty()) return {};
    std::vector<std::string> folders{home + "/logs"};
    for (const auto& profile : profileFolders(home + "/profiles")) folders.push_back(profile + "/logs");
    std::vector<std::string> out;
    std::set<std::string> seen;
    for (const auto& f : folders) {
        if (seen.insert(f).second && folderExists(f)) out.push_back(f);
    }
    return out;
}

std::vector<std::string> settingFolders(const std::string& setting) {
    if (setting.empty()) return {};
    if (folderExists(setting + "/logs")) return logFolders(setting);
    return folderExists(setting) ? std::vector<std::string>{setting} : std::vector<std::string>{};
}

bool isLogName(std::string_view name) {
    constexpr std::string_view kLive = "agent.log";
    if (name.substr(0, kLive.size()) != kLive) return false;
    const auto rest = name.substr(kLive.size());
    if (rest.empty()) return true;
    if (rest.size() < 2 || rest[0] != '.') return false;
    return std::all_of(rest.begin() + 1, rest.end(), [](char c) { return c >= '0' && c <= '9'; });
}

std::vector<std::string> logFiles(const std::string& folder) {
    std::vector<std::string> out;
    DIR* dir = opendir(folder.c_str());
    if (!dir) return out;
    while (auto* entry = readdir(dir)) {
        const std::string name = entry->d_name;
        if (!isLogName(name)) continue;
        const std::string path = folder + "/" + name;
        struct stat st {};
        if (lstat(path.c_str(), &st) == 0 && S_ISREG(st.st_mode)) out.push_back(path);
    }
    closedir(dir);
    std::sort(out.begin(), out.end());
    return out;
}

std::optional<std::string> configPath(const std::string& folder, bool windows) {
    std::string trimmed = folder;
    while (trimmed.size() > 1 && (trimmed.back() == '/' || (windows && trimmed.back() == '\\'))) trimmed.pop_back();
    const auto cut = trimmed.find_last_of(windows ? "/\\" : "/");
    const std::string name = cut == std::string::npos ? trimmed : trimmed.substr(cut + 1);
    const bool isLogs = windows ? foldCase(name) == "logs" : name == "logs";
    if (!isLogs || cut == std::string::npos || cut == 0) return std::nullopt;
    return trimmed.substr(0, cut) + (windows ? "\\" : "/") + "config.yaml";
}

bool selectsCodexRuntime(std::string text) {
    if (text.rfind("\xEF\xBB\xBF", 0) == 0) text = text.substr(3);
    bool codex = false, inModel = false;
    std::optional<std::size_t> indent;
    std::size_t start = 0;
    while (start <= text.size()) {
        auto end = text.find('\n', start);
        if (end == std::string::npos) end = text.size();
        std::string_view line(text.data() + start, end - start);
        start = end + 1;
        if (!line.empty() && line.back() == '\r') line.remove_suffix(1);
        const auto firstVisible = line.find_first_not_of(" \t");
        if (firstVisible == std::string_view::npos || line[firstVisible] == '#') {
            if (end == text.size()) break;
            continue;
        }
        const auto spaces = line.find_first_not_of(' ');
        const std::size_t depth = spaces == std::string_view::npos ? line.size() : spaces;
        if (depth == 0) {
            inModel = modelHeader(line);
            indent.reset();
        } else if (inModel) {
            if (!indent) indent = depth;
            if (depth == *indent) {
                if (auto value = runtimeKey(line.substr(depth))) codex = codexValue(*value);
            }
        }
        if (end == text.size()) break;
    }
    return codex;
}

ConfigCheck checkConfig(const std::optional<std::string>& path, const std::optional<ConfigSignature>& known,
                        std::optional<ConfigSignature>& signature, bool& codex) {
    signature.reset();
    codex = false;
    if (!path) return ConfigCheck::Off;
    struct stat st {};
    if (stat(path->c_str(), &st) != 0) return errno == ENOENT || errno == ENOTDIR ? ConfigCheck::Off : ConfigCheck::Failed;
    if (!S_ISREG(st.st_mode)) return ConfigCheck::Off;
    signature = ConfigSignature{static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec,
                                static_cast<std::int64_t>(st.st_size)};
    if (signature == known) return ConfigCheck::Unchanged;
    if (signature->size > (1 << 20)) return ConfigCheck::Read;
    std::ifstream in(*path, std::ios::binary);
    if (!in) {
        signature.reset();
        return ConfigCheck::Failed;
    }
    std::string data(static_cast<std::size_t>((1 << 20) + 1), '\0');
    in.read(data.data(), static_cast<std::streamsize>(data.size()));
    data.resize(static_cast<std::size_t>(in.gcount()));
    codex = data.size() <= (1u << 20) && selectsCodexRuntime(text::sanitizeUtf8(data));
    return ConfigCheck::Read;
}

std::optional<std::int64_t> toUnixMs(int year, int month, int day, int hour, int minute, int second, int millisecond,
                                     const TimeZone& zone) {
    static constexpr int kDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    const bool leapYear = (year % 4 == 0 && year % 100 != 0) || year % 400 == 0;
    if (year < 1970 || year > 9999 || month < 1 || month > 12 || day < 1 ||
        day > kDays[month - 1] + (month == 2 && leapYear ? 1 : 0) || hour > 23 || minute > 59 || second > 59 ||
        millisecond < 0 || millisecond > 999) {
        return std::nullopt;
    }
    const std::int64_t wall =
        (daysFromCivil(year, static_cast<unsigned>(month), static_cast<unsigned>(day)) * 86400 + hour * 3600 + minute * 60 + second) *
        1000;
    const std::int64_t before = zone.offsetMs(wall - 14 * kHourMs);
    const std::int64_t after = zone.offsetMs(wall + 14 * kHourMs);
    std::optional<std::int64_t> earliest;
    for (std::int64_t offset : {before, after}) {
        const std::int64_t candidate = wall - offset;
        if (zone.offsetMs(candidate) == offset && (!earliest || candidate < *earliest)) earliest = candidate;
    }
    return earliest.value_or(wall - before) + millisecond;
}

std::optional<std::int64_t> timestampOf(std::string_view line, const TimeZone& zone) {
    if (!hasTimestampShape(line)) return std::nullopt;
    return toUnixMs(digits(line, 0, 4), digits(line, 5, 2), digits(line, 8, 2), digits(line, 11, 2), digits(line, 14, 2),
                    digits(line, 17, 2), digits(line, 20, 3), zone);
}

void Agent::touch(std::int64_t ts) {
    if (!lastLineMs || ts > *lastLineMs) {
        lastLineMs = ts;
        ++version;
    }
}

void Agent::startTurn(std::int64_t ts, const std::string& m, bool subagent, bool codexRuntime) {
    setPhase(codexRuntime ? Phase::Idle : Phase::Generating, codexRuntime ? std::nullopt : std::optional<std::int64_t>(ts));
    setModel(m);
    if (isSubagent != subagent) {
        isSubagent = subagent;
        ++version;
    }
    if (!subagent && (!lastMainTurnMs || ts > *lastMainTurnMs)) lastMainTurnMs = ts;
}

void Agent::call(std::int64_t ts, const std::string& m) {
    setModel(m);
    if (phase == Phase::Generating) setPhase(Phase::Generating, ts);
}

void Agent::setModel(const std::string& m) {
    if (!m.empty() && m != model) {
        model = m;
        ++version;
    }
}

void Agent::setPhase(Phase p, std::optional<std::int64_t> inFlight) {
    if (phase != p || inFlightStartMs != inFlight) {
        phase = p;
        inFlightStartMs = inFlight;
        ++version;
    }
}

LogSet::LogSet(std::string folder, Deduper& deduper, std::shared_ptr<const TimeZone> zone)
    : folder_(std::move(folder)), deduper_(deduper), zone_(std::move(zone)), configPath_(configPath(folder_)) {}

Agent* LogSet::agent(const std::string& sessionId) {
    const auto it = agents_.find(sessionId);
    return it == agents_.end() ? nullptr : it->second.get();
}

bool LogSet::checkConfig() {
    const bool before = codexRuntime_;
    std::optional<ConfigSignature> signature;
    bool codex = false;
    switch (hermes::checkConfig(configPath_, configSignature_, signature, codex)) {
        case ConfigCheck::Off:
            configSignature_.reset();
            setCodexRuntime(false);
            break;
        case ConfigCheck::Read:
            configSignature_ = signature;
            setCodexRuntime(codex);
            break;
        case ConfigCheck::Unchanged:
        case ConfigCheck::Failed:
            break;
    }
    return before != codexRuntime_;
}

void LogSet::setCodexRuntime(bool on) {
    if (on && !codexRuntime_) {
        for (auto* agent : ordered_) {
            if (agent->phase == Phase::Generating) agent->endTurn();
        }
    }
    codexRuntime_ = on;
}

Agent& LogSet::agentFor(const std::string& sessionId, std::int64_t ts) {
    auto it = agents_.find(sessionId);
    if (it == agents_.end()) {
        auto agent = std::make_unique<Agent>();
        agent->agentId = agentIdOf(folder_, sessionId);
        agent->sessionId = sessionId;
        ordered_.push_back(agent.get());
        it = agents_.emplace(sessionId, std::move(agent)).first;
    }
    it->second->touch(ts);
    return *it->second;
}

void LogSet::line(std::string_view raw, ResponseSink& sink) {
    if (raw.size() <= kTimestampLength + 1 || raw[kTimestampLength] != ' ' || !hasTimestampShape(raw)) return;
    const auto afterTime = raw.substr(kTimestampLength + 1);
    std::size_t levelLength = 0;
    for (std::string_view level : kLevels) {
        if (afterTime.size() > level.size() && afterTime.substr(0, level.size()) == level && afterTime[level.size()] == ' ') {
            levelLength = level.size();
            break;
        }
    }
    if (levelLength == 0) return;
    const auto restBytes = afterTime.substr(levelLength);
    if (restBytes.find(std::string(kTurnLogger) + ": ") == std::string_view::npos &&
        restBytes.find(std::string(kLoopLogger) + ": ") == std::string_view::npos) {
        return;
    }
    const std::string text = text::sanitizeUtf8(raw);
    const std::string level(levelOf(text));
    const std::string rest = text.substr(kTimestampLength + 1 + level.size());
    std::string sessionId, message, logger;
    for (const char* candidate : {kTurnLogger, kLoopLogger}) {
        if (split(rest, candidate, sessionId, message)) {
            logger = candidate;
            break;
        }
    }
    if (logger.empty()) return;
    enum class Kind { None, Turn, Call, End } kind = Kind::None;
    std::smatch turn;
    if (logger == kTurnLogger) {
        if (level == "INFO" && std::regex_search(message, turn, turnPattern())) kind = Kind::Turn;
    } else if (level == "INFO" && message.rfind("API call #", 0) == 0) {
        kind = Kind::Call;
    } else if ((level == "INFO" || level == "WARNING") &&
               (message.rfind("Turn ended: ", 0) == 0 || message.rfind("Turn ended with pending tool result", 0) == 0)) {
        kind = Kind::End;
    } else if (level == "ERROR") {
        std::string after = message;
        if (!after.empty() && after[0] == '[') {
            const auto close = after.find(']');
            if (close != std::string::npos) {
                std::size_t k = close + 1;
                while (k < after.size() && after[k] == ' ') ++k;
                after = after.substr(k);
            }
        }
        if (after.rfind("API call failed after ", 0) == 0 || after.rfind("Non-retryable client error: ", 0) == 0) kind = Kind::End;
    }
    if (kind == Kind::None) return;
    const auto ts = timestampOf(text, *zone_);
    if (!ts) return;
    switch (kind) {
        case Kind::Turn:
            agentFor(sessionId, *ts).startTurn(*ts, turn[1].str(), turn[2].str() == "subagent", codexRuntime_);
            break;
        case Kind::End:
            agentFor(sessionId, *ts).endTurn();
            break;
        default:
            call(sessionId, message, text.substr(0, kTimestampLength), *ts, sink);
            break;
    }
}

std::int64_t count(const std::string& text) {
    if (!text.empty() && text[0] == '-') return 0;
    std::int64_t value = 0;
    for (char c : text) {
        value = value * 10 + (c - '0');
        if (value > TokenCounts::kMax) return TokenCounts::kMax;
    }
    return value;
}

std::int64_t latencyMs(const std::string& text) {
    const auto dot = text.find('.');
    const std::string whole = dot == std::string::npos ? text : text.substr(0, dot);
    const std::string fraction = dot == std::string::npos ? std::string() : text.substr(dot + 1);
    constexpr std::int64_t kLimit = INT64_MAX / 10;
    std::int64_t value = 0;
    for (char c : whole) {
        if (value > kLimit) return INT64_MAX;
        value = value * 10 + (c - '0');
        if (value < 0) return INT64_MAX;
    }
    int millis = 0;
    for (std::size_t i = 0; i < 3; ++i) millis = millis * 10 + (i < fraction.size() ? fraction[i] - '0' : 0);
    if (value > (INT64_MAX - millis) / 1000) return INT64_MAX;
    return value * 1000 + millis;
}

void LogSet::call(const std::string& sessionId, const std::string& message, const std::string& stamp, std::int64_t ts,
                  ResponseSink& sink) {
    std::smatch m;
    if (!std::regex_match(message, m, callPattern())) {
        ++malformedLines_;
        return;
    }
    Agent& agent = agentFor(sessionId, ts);
    const std::string key = "hermes:" + sessionId + ":" + m[1].str() + ":" + stamp;
    if (!deduper_.tryClaim(key, folder_)) return;
    const std::string model = m[2].str();
    agent.call(ts, model);
    const std::string input = m[4].str(), output = m[5].str();
    if (input == "?" || output == "?") return;
    TokenCounts tokens;
    tokens.input = count(input);
    tokens.cachedInput = m[8].matched ? count(m[8].str()) : 0;
    tokens.cacheWriteInput = m[9].matched ? count(m[9].str()) : 0;
    tokens.output = count(output);
    if (tokens.input == 0 && tokens.output == 0) return;
    const std::string latency = m[7].str();
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Hermes;
    r.agentId = agent.agentId;
    if (!model.empty()) r.model = model;
    if (latency[0] != '-') r.startMs = saturatingSubtract(ts, latencyMs(latency));
    r.endMs = ts;
    r.tokens = tokens;
    if (const std::string provider = m[3].str(); !provider.empty() && provider != "unknown") r.provider = provider;
    sink.onResponse(r, true);
}

Source::Source(std::string root, bool chosen, Deduper& deduper, std::shared_ptr<const TimeZone> zone)
    : root_(std::move(root)), chosen_(chosen), deduper_(deduper), zone_(std::move(zone)) {
    folders_ = folders();
}

std::vector<std::string> Source::folders() const {
    auto list = chosen_ ? settingFolders(root_) : logFolders(root_);
    for (auto& f : list) {
        while (f.size() > 1 && f.back() == '/') f.pop_back();
    }
    return list;
}

std::vector<LogSet*> Source::sets() const {
    std::vector<LogSet*> out;
    for (const auto& t : sets_) out.push_back(t->set.get());
    return out;
}

std::int64_t Source::malformedLines() const {
    std::int64_t total = removedMalformedLines_;
    for (const auto& t : sets_) total += t->set->malformedLines();
    return total;
}

void Source::setZone(std::shared_ptr<const TimeZone> zone) {
    zone_ = zone;
    for (auto& t : sets_) t->set->setZone(zone);
}

Source::Tracked& Source::add(const std::string& folder) {
    auto tracked = std::make_unique<Tracked>();
    tracked->set = std::make_unique<LogSet>(folder, deduper_, zone_);
    sets_.push_back(std::move(tracked));
    return *sets_.back();
}

Update Source::seed(std::int64_t todayStartMs) {
    Update update;
    folders_ = folders();
    for (const auto& folder : folders_) {
        Tracked& tracked = add(folder);
        tracked.set->checkConfig();
        readSet(tracked, update, todayStartMs - kBacklogWindowMs, false);
    }
    return update;
}

Update Source::refresh(bool discover) {
    Update update;
    if (discover) {
        folders_ = folders();
        for (auto it = sets_.begin(); it != sets_.end();) {
            if (std::find(folders_.begin(), folders_.end(), (*it)->set->folder()) != folders_.end()) {
                ++it;
                continue;
            }
            removedMalformedLines_ += (*it)->set->malformedLines();
            for (const auto* a : (*it)->set->agents()) update.removed.push_back(a->agentId);
            update.changed = true;
            it = sets_.erase(it);
        }
        for (const auto& folder : folders_) {
            if (std::none_of(sets_.begin(), sets_.end(), [&](const auto& t) { return t->set->folder() == folder; })) {
                add(folder);
                update.changed = true;
            }
        }
        std::stable_sort(sets_.begin(), sets_.end(), [&](const auto& a, const auto& b) {
            return std::find(folders_.begin(), folders_.end(), a->set->folder()) <
                   std::find(folders_.begin(), folders_.end(), b->set->folder());
        });
    }
    for (auto& tracked : sets_) {
        if (discover) update.changed |= tracked->set->checkConfig();
        readSet(*tracked, update, std::nullopt, discover);
    }
    return update;
}

void Source::readSet(Tracked& tracked, Update& update, std::optional<std::int64_t> thresholdMs, bool forget) {
    struct Listed {
        std::string path;
        std::int64_t writeNs, length;
    };
    std::vector<Listed> listed;
    for (const auto& path : logFiles(tracked.set->folder())) {
        struct stat st {};
        if (stat(path.c_str(), &st) == 0) {
            listed.push_back({path, static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec,
                              static_cast<std::int64_t>(st.st_size)});
        }
    }
    std::stable_sort(listed.begin(), listed.end(), [](const Listed& a, const Listed& b) {
        return a.writeNs != b.writeNs ? a.writeNs < b.writeNs : a.path < b.path;
    });
    std::set<FileKey> seenKeys;
    std::map<std::string, std::pair<std::int64_t, std::int64_t>> stamps;
    for (const auto& l : listed) {
        const std::pair<std::int64_t, std::int64_t> stamp{l.writeNs, l.length};
        if (!thresholdMs) {
            if (auto known = tracked.stamps.find(l.path);
                known != tracked.stamps.end() && known->second == stamp && !unreadable_.contains(l.path)) {
                stamps[l.path] = known->second;
                if (auto key = tracked.keyOf.find(l.path); key != tracked.keyOf.end()) seenKeys.insert(key->second);
                continue;
            }
        }
        if (auto read = readFile(tracked, l.path, l.writeNs, thresholdMs, update)) {
            stamps[l.path] = read->second;
            tracked.keyOf[l.path] = read->first;
            seenKeys.insert(read->first);
        }
    }
    tracked.stamps = stamps;
    for (auto it = tracked.keyOf.begin(); it != tracked.keyOf.end();) {
        it = stamps.contains(it->first) ? std::next(it) : tracked.keyOf.erase(it);
    }
    for (auto it = unreadable_.begin(); it != unreadable_.end();) {
        const bool listedNow = std::any_of(listed.begin(), listed.end(), [&](const Listed& l) { return l.path == *it; });
        const auto cut = it->find_last_of('/');
        const bool inFolder = cut != std::string::npos && it->substr(0, cut) == tracked.set->folder();
        it = !listedNow && inFolder ? unreadable_.erase(it) : std::next(it);
    }
    for (auto it = tracked.logs.begin(); it != tracked.logs.end();) {
        if (seenKeys.contains(it->first)) {
            it->second.missing = 0;
            ++it;
        } else if (forget && ++it->second.missing >= kMissingLimit) {
            it = tracked.logs.erase(it);
        } else {
            ++it;
        }
    }
}

std::optional<std::pair<Source::FileKey, std::pair<std::int64_t, std::int64_t>>> Source::readFile(
    Tracked& tracked, const std::string& path, std::int64_t listedWriteNs, std::optional<std::int64_t> thresholdMs,
    Update& update) {
    const int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        if (errno == ENOENT || errno == ENOTDIR) {
            unreadable_.erase(path);
        } else {
            unreadable_.insert(path);
        }
        return std::nullopt;
    }
    struct stat st {};
    if (fstat(fd, &st) != 0) {
        close(fd);
        unreadable_.insert(path);
        return std::nullopt;
    }
    const FileKey key{static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino)};
    const std::int64_t writeNs = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
    const std::int64_t length = st.st_size;
    auto it = tracked.logs.find(key);
    if (it == tracked.logs.end()) {
        it = tracked.logs.emplace(key, Log{}).first;
        if (thresholdMs) {
            Log& log = it->second;
            const std::int64_t modifiedMs = listedWriteNs / 1'000'000;
            if (modifiedMs < *thresholdMs) {
                log.offset = length;
                log.alignment = length > 0 ? Log::Align::SkipPartialLine : Log::Align::None;
            } else if (length > kLargeFileBytes) {
                log.offset = length - kLargeFileTailBytes;
                log.alignment = Log::Align::SkipToNextLine;
                partialHistory_ = true;
            }
        }
    }
    Log& log = it->second;
    if (length < log.offset) {
        // Shrunk: read again from the start; the dedupe set discards what was counted (METRICS 5).
        log.offset = 0;
        log.alignment = Log::Align::None;
        log.lines.reset();
    }
    if (log.alignment != Log::Align::None) {
        if (log.alignment == Log::Align::SkipToNextLine) {
            log.lines.alignToNextLine();
        } else if (log.offset <= length) {
            char previous = 0;
            if (pread(fd, &previous, 1, log.offset - 1) != 1 || previous != '\n') log.lines.alignToNextLine();
        }
        log.alignment = Log::Align::None;
    }
    if (length > log.offset) {
        int lines = 0;
        std::vector<char> buffer(static_cast<std::size_t>(std::min<std::int64_t>(1 << 20, length - log.offset)));
        LogSet& set = *tracked.set;
        while (log.offset < length) {
            const auto want = static_cast<std::size_t>(std::min<std::int64_t>(static_cast<std::int64_t>(buffer.size()), length - log.offset));
            const auto got = pread(fd, buffer.data(), want, log.offset);
            if (got <= 0) break;
            log.offset += got;
            update.bytesRead += got;
            log.lines.feed(std::string_view(buffer.data(), static_cast<std::size_t>(got)), [&](std::string_view line) {
                ++lines;
                set.line(line, update);
            });
        }
        ++update.filesRead;
        update.changed |= lines > 0;
    }
    close(fd);
    unreadable_.erase(path);
    return std::make_pair(key, std::make_pair(writeNs, length));
}

}  // namespace agentspeed::hermes
