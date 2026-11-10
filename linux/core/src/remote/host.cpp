#include <algorithm>
#include <random>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_messages.hpp"
#include "agentspeed/report_json.hpp"

// REMOTE 2.1: validation, automatic names and the `remoteHosts` settings document.
namespace agentspeed::remote {

namespace {

constexpr std::string_view kDestinationForbidden = "\"'`\\;&|$<>()*?!{}#";

// The code points of UTF-8 text with their byte ranges; invalid bytes count as one code point each.
struct CodePoint {
    std::uint32_t value;
    std::size_t begin, end;
};

std::vector<CodePoint> codePoints(std::string_view text) {
    std::vector<CodePoint> out;
    for (std::size_t i = 0; i < text.size();) {
        const auto c = static_cast<unsigned char>(text[i]);
        std::size_t length = c >= 0xF0 ? 4 : c >= 0xE0 ? 3 : c >= 0xC0 ? 2 : 1;
        if (i + length > text.size()) length = 1;
        std::uint32_t cp = length == 1 ? c : length == 2 ? (c & 0x1Fu) : length == 3 ? (c & 0x0Fu) : (c & 0x07u);
        for (std::size_t k = 1; k < length; ++k) cp = (cp << 6) | (static_cast<unsigned char>(text[i + k]) & 0x3Fu);
        out.push_back({cp, i, i + length});
        i += length;
    }
    return out;
}

bool isControl(std::uint32_t cp) { return cp < 0x20 || (cp >= 0x7F && cp <= 0x9F); }

bool isSpace(std::uint32_t cp) {
    return cp == ' ' || (cp >= 0x09 && cp <= 0x0D) || cp == 0x85 || cp == 0xA0 || cp == 0x1680 || (cp >= 0x2000 && cp <= 0x200A) ||
           cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F || cp == 0x3000;
}

std::string trimmed(std::string_view text) {
    const auto points = codePoints(text);
    std::size_t first = 0, last = points.size();
    while (first < last && isSpace(points[first].value)) ++first;
    while (last > first && isSpace(points[last - 1].value)) --last;
    if (first == last) return {};
    return std::string(text.substr(points[first].begin, points[last - 1].end - points[first].begin));
}

// The first `count` code points.
std::string prefix(std::string_view text, std::size_t count) {
    const auto points = codePoints(text);
    if (points.size() <= count) return std::string(text);
    return std::string(text.substr(0, points[count].begin));
}

std::string lowerAscii(std::string_view text) {
    std::string out(text);
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(c >= 'A' && c <= 'Z' ? c + 32 : c); });
    return out;
}

bool byBytes(const std::string& a, const std::string& b) {
    return std::lexicographical_compare(a.begin(), a.end(), b.begin(), b.end(),
                                        [](char x, char y) { return static_cast<unsigned char>(x) < static_cast<unsigned char>(y); });
}

using Members = std::vector<std::pair<std::string, std::string>>;

std::string object(Members members) {
    std::stable_sort(members.begin(), members.end(), [](const auto& a, const auto& b) { return byBytes(a.first, b.first); });
    std::string out = "{";
    for (std::size_t i = 0; i < members.size(); ++i) {
        if (i) out += ',';
        out += report::Json::quoted(members[i].first) + ":" + members[i].second;
    }
    return out + "}";
}

std::string optionalText(const std::optional<std::string>& text) { return text ? report::Json::quoted(*text) : std::string("null"); }

Members unknownMembers(const json::Value& value, const std::set<std::string_view>& known) {
    Members out;
    std::set<std::string> seen;
    const auto members = value.members();
    // The last of repeated members wins, as the parser reads them.
    for (auto it = members.rbegin(); it != members.rend(); ++it) {
        if (known.contains(it->first) || !seen.insert(it->first).second) continue;
        out.emplace_back(it->first, std::string(it->second.isString() ? "\"" + std::string(it->second.raw()) + "\"" : it->second.raw()));
    }
    std::reverse(out.begin(), out.end());
    return out;
}

std::optional<Host> decodeHost(const json::Value& value) {
    if (!value.isObject()) return std::nullopt;
    const auto id = value["id"].string();
    if (!id || !validation::isValidId(*id)) return std::nullopt;
    const auto enabled = value["enabled"];
    auto host = Host::make(*id, value["destination"].string().value_or(""), value["name"].string(), !enabled.isBool() || enabled.isTrue(),
                           value["relayFolder"].string());
    host.quotaClaudeCli = value["quotaClaudeCli"].isTrue();
    host.extra = unknownMembers(value, {"id", "destination", "name", "enabled", "relayFolder", "quotaClaudeCli"});
    return host;
}

}  // namespace

std::string_view fieldIssueName(FieldIssue issue) {
    switch (issue) {
        case FieldIssue::Empty: return "empty";
        case FieldIssue::TooLong: return "tooLong";
        case FieldIssue::LeadingDash: return "leadingDash";
        case FieldIssue::ForbiddenCharacter: return "forbiddenCharacter";
        case FieldIssue::NotAbsolute: return "notAbsolute";
        case FieldIssue::UnsafeComponent: return "unsafeComponent";
    }
    return "forbiddenCharacter";
}

namespace validation {

std::optional<FieldIssue> destinationIssue(std::string_view destination) {
    if (destination.empty()) return FieldIssue::Empty;
    if (destination.size() > kDestinationLimit) return FieldIssue::TooLong;
    const bool allowed = std::all_of(destination.begin(), destination.end(), [](char ch) {
        const auto c = static_cast<unsigned char>(ch);
        return c >= 0x21 && c <= 0x7E && kDestinationForbidden.find(ch) == std::string_view::npos;
    });
    if (!allowed) return FieldIssue::ForbiddenCharacter;
    if (destination.front() == '-') return FieldIssue::LeadingDash;
    return std::nullopt;
}

bool isNameByte(unsigned char c) {
    return (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || c == '.' || c == '_' || c == '-';
}

std::optional<FieldIssue> folderIssue(std::string_view folder) {
    if (folder.empty()) return FieldIssue::Empty;
    if (folder.size() > kFolderLimit) return FieldIssue::TooLong;
    const bool allowed = std::all_of(folder.begin(), folder.end(), [](char ch) {
        const auto c = static_cast<unsigned char>(ch);
        return isNameByte(c) || c == '/' || c == '~';
    });
    if (!allowed) return FieldIssue::ForbiddenCharacter;
    if (!folder.starts_with("/") && !folder.starts_with("~/")) return FieldIssue::NotAbsolute;
    if (folder.find("//") != std::string_view::npos) return FieldIssue::UnsafeComponent;
    for (std::string_view rest = folder; !rest.empty();) {
        const auto slash = rest.find('/');
        if (rest.substr(0, slash) == "..") return FieldIssue::UnsafeComponent;
        if (slash == std::string_view::npos) break;
        rest.remove_prefix(slash + 1);
    }
    return std::nullopt;
}

std::string normalizedFolder(std::string_view folder) {
    std::string out(folder);
    while (out.size() > 1 && out.back() == '/') out.pop_back();
    return out;
}

bool isValidId(std::string_view id) {
    if (id == kLocalHost || id.size() != 36) return false;
    for (std::size_t i = 0; i < id.size(); ++i) {
        const bool dash = i == 8 || i == 13 || i == 18 || i == 23;
        const char c = id[i];
        if (dash ? c != '-' : !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))) return false;
    }
    return true;
}

std::optional<std::string> cleanName(const std::optional<std::string>& name) {
    if (!name) return std::nullopt;
    std::string visible;
    for (const auto& cp : codePoints(*name)) {
        if (!isControl(cp.value)) visible += name->substr(cp.begin, cp.end - cp.begin);
    }
    auto text = trimmed(visible);
    if (text.empty()) return std::nullopt;
    if (codePoints(text).size() <= kNameLimit) return text;
    return trimmed(prefix(text, kNameLimit));
}

std::string automaticName(std::string_view destination) {
    std::string_view rest = destination;
    const bool url = lowerAscii(rest.substr(0, 6)) == "ssh://";
    if (url) {
        rest.remove_prefix(6);
        if (const auto slash = rest.find('/'); slash != std::string_view::npos) rest = rest.substr(0, slash);
    }
    if (const auto at = rest.rfind('@'); at != std::string_view::npos) rest.remove_prefix(at + 1);
    if (rest.starts_with("[") && rest.find(']') != std::string_view::npos) {
        rest = rest.substr(1, rest.find(']') - 1);
    } else if (url) {
        if (const auto colon = rest.rfind(':'); colon != std::string_view::npos) {
            const auto port = rest.substr(colon + 1);
            if (std::all_of(port.begin(), port.end(), [](char c) { return c >= '0' && c <= '9'; })) rest = rest.substr(0, colon);
        }
    }
    auto name = prefix(rest, kNameLimit);
    return name.empty() ? prefix(destination, kNameLimit) : name;
}

std::string newId() {
    std::random_device device;
    std::uniform_int_distribution<int> byte(0, 255);
    unsigned char bytes[16];
    for (auto& b : bytes) b = static_cast<unsigned char>(byte(device));
    bytes[6] = static_cast<unsigned char>((bytes[6] & 0x0F) | 0x40);
    bytes[8] = static_cast<unsigned char>((bytes[8] & 0x3F) | 0x80);
    static constexpr char kHex[] = "0123456789ABCDEF";
    std::string out;
    for (int i = 0; i < 16; ++i) {
        if (i == 4 || i == 6 || i == 8 || i == 10) out += '-';
        out += kHex[bytes[i] >> 4];
        out += kHex[bytes[i] & 15];
    }
    return out;
}

}  // namespace validation

Host Host::make(std::string id, std::string destination, std::optional<std::string> name, bool enabled, std::optional<std::string> relayFolder) {
    Host host;
    host.id = std::move(id);
    host.destination = std::move(destination);
    host.name = validation::cleanName(name);
    host.enabled = enabled;
    if (relayFolder && !relayFolder->empty()) host.relayFolder = std::move(relayFolder);
    return host;
}

std::string Host::effectiveRelayFolder() const { return validation::normalizedFolder(relayFolder.value_or(std::string(kDefaultRelayFolder))); }

bool Host::connectable() const {
    return !validation::destinationIssue(destination) && (!relayFolder || !validation::folderIssue(*relayFolder));
}

HostsDocument::HostsDocument(std::vector<Host> hosts) : hosts_(normalized(std::move(hosts))) {}

std::vector<Host> HostsDocument::normalized(std::vector<Host> hosts) {
    std::vector<Host> out;
    std::set<std::string> seen;
    for (auto& host : hosts) {
        if (out.size() >= kMaxHosts) break;
        if (validation::isValidId(host.id) && seen.insert(host.id).second) out.push_back(std::move(host));
    }
    return out;
}

std::optional<HostsDocument> HostsDocument::parse(std::string_view text) {
    json::Document document;
    if (!document.parse(text, 64) || !document.root().isObject()) return std::nullopt;
    const auto root = document.root();
    std::vector<Host> hosts;
    if (root["hosts"].isArray()) {
        for (const auto& item : root["hosts"].elements()) {
            if (auto host = decodeHost(item)) hosts.push_back(std::move(*host));
        }
    }
    HostsDocument out(std::move(hosts));
    if (const auto version = root["version"]; version.isNumber() && version.isIntegerLiteral()) {
        out.version_ = static_cast<int>(std::clamp<std::int64_t>(version.truncated().value_or(kVersion), kVersion, 1'000'000));
    }
    out.extra_ = unknownMembers(root, {"version", "hosts"});
    return out;
}

std::string HostsDocument::json() const {
    std::string hosts = "[";
    for (std::size_t i = 0; i < hosts_.size(); ++i) {
        const auto& h = hosts_[i];
        Members members = h.extra;
        members.emplace_back("id", report::Json::quoted(h.id));
        members.emplace_back("destination", report::Json::quoted(h.destination));
        members.emplace_back("name", optionalText(h.name));
        members.emplace_back("enabled", h.enabled ? "true" : "false");
        members.emplace_back("relayFolder", optionalText(h.relayFolder));
        if (h.quotaClaudeCli) members.emplace_back("quotaClaudeCli", "true");
        if (i) hosts += ',';
        hosts += object(std::move(members));
    }
    hosts += "]";
    Members members = extra_;
    members.emplace_back("version", std::to_string(version_));
    members.emplace_back("hosts", hosts);
    return object(std::move(members));
}

const Host* HostsDocument::find(std::string_view id) const {
    const auto it = std::find_if(hosts_.begin(), hosts_.end(), [&](const Host& h) { return h.id == id; });
    return it == hosts_.end() ? nullptr : &*it;
}

bool HostsDocument::add(Host host) {
    if (!canAdd() || !validation::isValidId(host.id) || find(host.id)) return false;
    hosts_.push_back(std::move(host));
    return true;
}

bool HostsDocument::update(Host host) {
    const auto it = std::find_if(hosts_.begin(), hosts_.end(), [&](const Host& h) { return h.id == host.id; });
    if (it == hosts_.end()) return false;
    if (host.extra.empty()) host.extra = it->extra;
    *it = std::move(host);
    return true;
}

void HostsDocument::remove(std::string_view id) {
    std::erase_if(hosts_, [&](const Host& h) { return h.id == id; });
}

std::map<std::string, std::string> HostsDocument::displayNames() const {
    std::set<std::string> taken;
    for (const auto& h : hosts_) {
        if (h.name) taken.insert(lowerAscii(*h.name));
    }
    std::map<std::string, std::string> names;
    for (const auto& h : hosts_) {
        if (h.name) {
            names[h.id] = *h.name;
            continue;
        }
        const auto base = h.automaticName();
        auto candidate = base;
        for (int counter = 2; taken.contains(lowerAscii(candidate)); ++counter) candidate = base + " (" + std::to_string(counter) + ")";
        taken.insert(lowerAscii(candidate));
        names[h.id] = candidate;
    }
    return names;
}

std::optional<std::string> HostsDocument::displayName(std::string_view id) const {
    const auto names = displayNames();
    const auto it = names.find(std::string(id));
    return it == names.end() ? std::nullopt : std::optional<std::string>(it->second);
}

}  // namespace agentspeed::remote
