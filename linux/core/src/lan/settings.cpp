#include <openssl/rand.h>

#include <algorithm>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/lan_address.hpp"
#include "agentspeed/lan_settings.hpp"
#include "agentspeed/report.hpp"
#include "agentspeed/report_json.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

using report::Json;

Json optionalText(const std::optional<std::string>& value) { return value ? Json::of(*value) : Json::null(); }

std::optional<int> portOf(const json::Value& value) {
    if (!value.isNumber() || !value.isIntegerLiteral()) return std::nullopt;
    const auto number = value.truncated();
    if (!number || *number < kMinPort || *number > kMaxPort) return std::nullopt;
    return static_cast<int>(*number);
}

std::optional<Target> targetOf(const json::Value& entry) {
    if (!entry.isObject()) return std::nullopt;
    Target target;
    const auto id = entry["id"].string();
    const auto kind = entry["kind"].string();
    const auto address = entry["address"].string();
    if (!id || !validUuid(*id) || !kind || !address) return std::nullopt;
    const auto parsedKind = kindFromName(*kind);
    if (!parsedKind) return std::nullopt;
    target.id = *id;
    target.kind = *parsedKind;
    target.address = text::strip(*address);
    if (!validTarget(target.kind, target.address)) return std::nullopt;
    if (const auto name = entry["name"].string()) target.name = cleanText(*name, 0, kTargetNameLimit).value_or("");
    if (const auto key = entry["key"]; key.valid() && !key.isNull()) {
        if (const auto text = key.string()) target.key = cleanText(*text, 1, kTargetKeyLimit);
    }
    if (const auto enabled = entry["enabled"]; enabled.isBool()) target.enabled = enabled.isTrue();
    return target;
}

}  // namespace

std::string_view kindName(TargetKind kind) { return kind == TargetKind::Http ? "http" : "udp"; }

std::optional<TargetKind> kindFromName(std::string_view name) {
    if (name == "udp") return TargetKind::Udp;
    if (name == "http") return TargetKind::Http;
    return std::nullopt;
}

bool validKey(std::string_view key) {
    return key.size() == kKeyLength && std::all_of(key.begin(), key.end(), [](char c) { return kKeyAlphabet.find(c) != std::string_view::npos; });
}

bool validUuid(std::string_view id) {
    if (id.size() != 36) return false;
    for (std::size_t i = 0; i < id.size(); ++i) {
        const char c = id[i];
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (c != '-') return false;
        } else if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) {
            return false;
        }
    }
    return id[14] == '4' && (id[19] == '8' || id[19] == '9' || id[19] == 'a' || id[19] == 'b');
}

std::string newKey() {
    unsigned char bytes[kKeyLength];
    RAND_bytes(bytes, sizeof bytes);
    std::string key;
    for (const auto b : bytes) key += kKeyAlphabet[b & 31];
    return key;
}

std::string newUuid() { return report::newDeviceId(); }

std::string newRunId() {
    unsigned char bytes[8];
    RAND_bytes(bytes, sizeof bytes);
    static constexpr char kHex[] = "0123456789abcdef";
    std::string run;
    for (const auto b : bytes) {
        run += kHex[b >> 4];
        run += kHex[b & 15];
    }
    return run;
}

std::string displayKey(std::string_view key) {
    std::string out;
    for (std::size_t i = 0; i < key.size(); i += 4) {
        if (i) out += '-';
        out += key.substr(i, 4);
    }
    return out;
}

std::optional<std::string> cleanText(std::string_view raw, std::size_t minimum, std::size_t limit) {
    const auto value = text::strip(raw);
    std::size_t points = 0;
    for (std::size_t i = 0; i < value.size();) {
        const auto cp = text::nextCodePoint(value, i);
        if (cp < 0x20 || cp == 0x7F) return std::nullopt;
        ++points;
    }
    if (points < minimum || points > limit) return std::nullopt;
    return value;
}

Settings Settings::parse(std::string_view text) {
    Settings settings;
    json::Document doc;
    if (!doc.parse(text)) return settings;
    const auto root = doc.root();
    const auto version = root["version"];
    if (!root.isObject() || !version.isNumber() || version.number() != 1.0) return settings;
    if (const auto instance = root["instance"].string(); instance && validUuid(*instance)) settings.instance = *instance;
    if (const auto server = root["server"]; server.isObject()) {
        if (const auto enabled = server["enabled"]; enabled.isBool()) settings.server.enabled = enabled.isTrue();
        if (const auto port = portOf(server["port"])) settings.server.port = *port;
        if (const auto key = server["key"].string(); key && validKey(*key)) settings.server.key = *key;
    }
    if (const auto targets = root["targets"]; targets.isArray()) {
        std::set<std::string> seen;
        for (const auto& entry : targets.elements()) {
            if (settings.targets.size() == kMaxTargets) break;
            auto target = targetOf(entry);
            if (!target || seen.contains(target->id)) continue;
            seen.insert(target->id);
            settings.targets.push_back(std::move(*target));
        }
    }
    return settings;
}

std::string Settings::json() const {
    std::vector<Json> list;
    for (const auto& t : targets) {
        list.push_back(Json::object({{"id", Json::of(t.id)},
                                     {"name", Json::of(t.name)},
                                     {"kind", Json::of(kindName(t.kind))},
                                     {"address", Json::of(t.address)},
                                     {"key", optionalText(t.key)},
                                     {"enabled", Json::of(t.enabled)}}));
    }
    return Json::object({{"version", Json::of(1)},
                         {"instance", optionalText(instance)},
                         {"server", Json::object({{"enabled", Json::of(server.enabled)},
                                                  {"port", Json::of(server.port)},
                                                  {"key", optionalText(server.key)}})},
                         {"targets", Json::array(std::move(list))}})
        .compact();
}

}  // namespace agentspeed::lan
