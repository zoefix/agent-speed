#include <openssl/evp.h>

#include <map>
#include <mutex>
#include <regex>
#include <unordered_map>

#include "internal.hpp"

namespace agentspeed::tasks::detail {

std::string lowerAscii(std::string_view text) {
    std::string out(text);
    for (char& c : out) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    return out;
}

std::string upperAscii(std::string_view text) {
    std::string out(text);
    for (char& c : out) {
        if (c >= 'a' && c <= 'z') c = static_cast<char>(c - 'a' + 'A');
    }
    return out;
}

std::string replaceAll(std::string text, std::string_view from, std::string_view to) {
    if (from.empty()) return text;
    std::size_t at = 0;
    while ((at = text.find(from, at)) != std::string::npos) {
        text.replace(at, from.size(), to);
        at += to.size();
    }
    return text;
}

std::string rstrip(std::string_view text, std::string_view chars) {
    const auto end = text.find_last_not_of(chars);
    return end == std::string_view::npos ? std::string() : std::string(text.substr(0, end + 1));
}

std::string lstrip(std::string_view text, std::string_view chars) {
    const auto start = text.find_first_not_of(chars);
    return start == std::string_view::npos ? std::string() : std::string(text.substr(start));
}

std::string strip(std::string_view text, std::string_view chars) { return lstrip(rstrip(text, chars), chars); }

std::vector<std::string> split(std::string_view text, char sep) {
    std::vector<std::string> out;
    std::size_t start = 0;
    while (true) {
        const auto at = text.find(sep, start);
        out.emplace_back(text.substr(start, at == std::string_view::npos ? std::string_view::npos : at - start));
        if (at == std::string_view::npos) break;
        start = at + 1;
    }
    return out;
}

std::string join(const Words& words, std::string_view sep) {
    std::string out;
    for (std::size_t i = 0; i < words.size(); ++i) {
        if (i) out += sep;
        out += words[i];
    }
    return out;
}

Words slice(const Words& words, std::size_t from, std::size_t to) {
    if (from >= words.size()) return {};
    to = std::min(to, words.size());
    if (to <= from) return {};
    return Words(words.begin() + static_cast<std::ptrdiff_t>(from), words.begin() + static_cast<std::ptrdiff_t>(to));
}

std::string sha16(std::string_view text) {
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    EVP_Digest(text.data(), text.size(), digest, &length, EVP_sha256(), nullptr);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (int i = 0; i < 8; ++i) {
        out += kHex[digest[i] >> 4];
        out += kHex[digest[i] & 15];
    }
    return out;
}

std::optional<std::string> normCwd(const std::optional<std::string>& cwd) {
    if (!cwd) return std::nullopt;
    auto c = rstrip(replaceAll(*cwd, "\\", "/"), "/");
    if (c.empty()) return std::nullopt;
    return c;
}

std::pair<std::string, bool> cwdRelative(const std::string& path, const std::optional<std::string>& cwd) {
    const auto c = normCwd(cwd);
    if (!c) return {path, false};
    const bool drive = c->size() >= 2 && std::isalpha(static_cast<unsigned char>((*c)[0])) && (*c)[1] == ':' &&
                       (c->size() == 2 || (*c)[2] == '/');
    const auto a = drive ? lowerAscii(path) : path;
    const auto b = drive ? lowerAscii(*c) : *c;
    if (a == b) return {"", true};
    if (startsWith(a, b + "/")) return {path.substr(c->size() + 1), true};
    return {path, false};
}

std::int64_t num(std::string_view text) {
    const std::int64_t sign = startsWith(text, "-") ? -1 : 1;
    auto digits = lstrip(lstrip(text, "+-"), "0");
    if (digits.empty()) digits = "0";
    constexpr std::int64_t kMax = 999'999'999'999'999'999;
    if (digits.size() > 18) return sign * kMax;
    return sign * std::stoll(digits);
}

namespace {

const std::regex& compiled(const char* pattern) {
    static std::mutex mutex;
    static std::unordered_map<const char*, std::regex> cache;
    const std::lock_guard lock(mutex);
    auto it = cache.find(pattern);
    if (it == cache.end()) it = cache.emplace(pattern, std::regex(pattern, std::regex::ECMAScript)).first;
    return it->second;
}

}  // namespace

bool fullMatch(std::string_view text, const char* pattern) {
    return std::regex_match(text.begin(), text.end(), compiled(pattern));
}

bool prefixMatch(std::string_view text, const char* pattern) {
    return std::regex_search(text.begin(), text.end(), compiled(pattern), std::regex_constants::match_continuous);
}

namespace {

Groups groupsOf(const std::match_results<std::string_view::const_iterator>& m) {
    Groups out;
    for (std::size_t i = 0; i < m.size(); ++i) {
        if (m[i].matched) {
            out.emplace_back(m[i].str());
        } else {
            out.emplace_back(std::nullopt);
        }
    }
    return out;
}

}  // namespace

Groups fullGroups(std::string_view text, const char* pattern) {
    std::match_results<std::string_view::const_iterator> m;
    if (!std::regex_match(text.begin(), text.end(), m, compiled(pattern))) return {};
    return groupsOf(m);
}

Groups prefixGroups(std::string_view text, const char* pattern) {
    std::match_results<std::string_view::const_iterator> m;
    if (!std::regex_search(text.begin(), text.end(), m, compiled(pattern), std::regex_constants::match_continuous)) return {};
    return groupsOf(m);
}

Groups searchGroups(std::string_view text, const char* pattern) {
    std::match_results<std::string_view::const_iterator> m;
    if (!std::regex_search(text.begin(), text.end(), m, compiled(pattern))) return {};
    return groupsOf(m);
}

std::optional<std::int64_t> jsonInt(const json::Value& value) {
    if (!value.isNumber() || !value.isIntegerLiteral()) return std::nullopt;
    const auto raw = value.raw();
    const bool negative = startsWith(raw, "-");
    const auto digits = lstrip(raw.substr(negative ? 1 : 0), "0");
    if (digits.size() > 19) return std::nullopt;
    if (digits.size() == 19 && digits > (negative ? std::string("9223372036854775808") : std::string("9223372036854775807"))) return std::nullopt;
    return value.truncated();
}

std::unique_ptr<OwnedJson> leadingObject(std::string_view text) {
    const auto stripped = lstrip(text, " \t\r\n");
    if (!startsWith(stripped, "{")) return nullptr;
    // The end of the first value: braces outside strings.
    int depth = 0;
    bool inString = false;
    for (std::size_t i = 0; i < stripped.size(); ++i) {
        const char c = stripped[i];
        if (inString) {
            if (c == '\\') {
                ++i;
            } else if (c == '"') {
                inString = false;
            }
            continue;
        }
        if (c == '"') {
            inString = true;
        } else if (c == '{' || c == '[') {
            ++depth;
        } else if (c == '}' || c == ']') {
            if (--depth == 0) {
                auto owned = parseJson(stripped.substr(0, i + 1));
                if (owned && owned->root().isObject()) return owned;
                return nullptr;
            }
        }
    }
    return nullptr;
}

std::string blocksText(const json::Value& content) {
    if (content.isString()) return *content.string();
    if (!content.isArray()) return {};
    Words parts;
    for (const auto& b : content.elements()) {
        if (!b.isObject()) continue;
        const auto type = b["type"].string();
        if (type && in(*type, {"text", "input_text", "output_text"}) && b["text"].isString()) parts.push_back(*b["text"].string());
    }
    return join(parts, "\n");
}

std::string familyOf(SourceKind source) {
    switch (source) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop: return "claude";
        case SourceKind::Codex: return "codex";
        case SourceKind::Omp: return "omp";
        case SourceKind::Pi: return "pi";
        case SourceKind::OpenClaw: return "openclaw";
        case SourceKind::Droid: return "droid";
        default: return {};
    }
}

std::string toolName(const std::string& family, const std::string& tool) {
    return family == "omp" || family == "pi" || family == "openclaw" ? lowerAscii(tool) : tool;
}

std::string kindOf(const std::string& family, const std::string& tool) {
    using Table = std::map<std::string, std::vector<std::string>>;
    static const std::map<std::string, Table> kKinds = {
        {"claude",
         {{"read", {"Read", "NotebookRead", "LS"}},
          {"search", {"Grep", "Glob", "LSP"}},
          {"edit", {"Edit", "MultiEdit", "Write", "NotebookEdit"}},
          {"command", {"Bash", "PowerShell"}},
          {"delegate", {"Task", "Agent", "SendMessage"}},
          {"web", {"WebFetch", "WebSearch"}}}},
        {"codex",
         {{"read", {"read_file", "list_dir", "view_image"}},
          {"search", {"grep_files"}},
          {"edit", {"apply_patch"}},
          {"command", {"shell", "container.exec", "local_shell", "shell_command", "exec_command", "exec"}},
          {"delegate", {"spawn_agent", "send_input", "resume_agent"}},
          {"web", {"web_search", "web_fetch"}}}},
        {"omp",
         {{"read", {"read"}},
          {"search", {"grep", "search", "find", "glob", "ast_grep", "lsp"}},
          {"edit", {"edit", "write", "ast_edit"}},
          {"command", {"bash"}},
          {"delegate", {"task"}},
          {"web", {"web_search", "fetch", "browser"}}}},
        {"pi", {{"read", {"read", "ls"}}, {"search", {"grep", "find"}}, {"edit", {"edit", "write"}}, {"command", {"bash", "powershell"}}}},
        {"openclaw",
         {{"read", {"read", "ls"}},
          {"edit", {"edit", "write", "apply_patch"}},
          {"command", {"exec", "bash"}},
          {"delegate", {"sessions_spawn", "sessions_send"}},
          {"web", {"web_search", "web_fetch", "x_search", "browser"}}}},
        {"droid",
         {{"read", {"Read", "LS"}},
          {"search", {"Grep", "Glob"}},
          {"edit", {"Create", "Edit", "MultiEdit"}},
          {"command", {"Execute"}},
          {"delegate", {"Task"}},
          {"web", {"WebSearch", "FetchUrl"}}}},
    };
    const auto family_ = kKinds.find(family);
    if (family_ == kKinds.end()) return {};
    for (const auto& [kind, names] : family_->second) {
        for (const auto& n : names) {
            if (n == tool) return kind;
        }
    }
    return {};
}

std::unique_ptr<OwnedJson> parseJson(std::string_view text) {
    auto owned = std::make_unique<OwnedJson>();
    owned->text = std::string(text);
    if (!owned->document.parse(owned->text)) return nullptr;
    return owned;
}

json::Value decodeArgs(const json::Value& value, std::unique_ptr<OwnedJson>& holder) {
    if (value.isObject()) return value;
    if (value.isString()) {
        const auto text = *value.string();
        if (startsWith(lstrip(text, " \t\r\n"), "{")) {
            holder = parseJson(text);
            if (holder && holder->root().isObject()) return holder->root();
        }
    }
    return {};
}

}  // namespace agentspeed::tasks::detail
