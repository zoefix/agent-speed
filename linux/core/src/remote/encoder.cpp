#include <openssl/evp.h>

#include <algorithm>

#include "agentspeed/json.hpp"
#include "agentspeed/remote_protocol.hpp"
#include "agentspeed/report_json.hpp"

// REMOTE 5.3: the app → relay lines, the compact price table and the history digest.
namespace agentspeed::remote {

namespace {

std::string quoted(std::string_view text) { return report::Json::quoted(text); }

std::optional<std::string> line(std::string text) {
    text += '\n';
    if (text.size() > limits::kAppLineBytes) return std::nullopt;
    return text;
}

std::string zoneMembers(const Zone& zone) { return "\"id\":" + quoted(zone.id) + ",\"tzif\":" + quoted(encode::base64(zone.tzif)); }

std::string quotaConfig(const HostQuotaConfig& config) {
    std::string keys;
    for (const auto source : config.sources) {
        if (!keys.empty()) keys += ',';
        keys += quoted(sourceId(source));
    }
    const auto flag = [](bool on) { return on ? "true" : "false"; };
    return std::string("{\"sources\":[") + keys + "],\"network\":" + flag(config.network) + ",\"grokLive\":" + flag(config.grokLive) +
           ",\"claudeBridge\":" + flag(config.claudeBridge) + "}";
}

bool byBytes(const std::string& a, const std::string& b) {
    return std::lexicographical_compare(a.begin(), a.end(), b.begin(), b.end(),
                                        [](char x, char y) { return static_cast<unsigned char>(x) < static_cast<unsigned char>(y); });
}

}  // namespace

std::optional<std::string> compactPriceTable(std::string_view json) {
    if (json.size() > limits::kAppLineBytes) return std::nullopt;
    json::Document document;
    if (!document.parse(json) || !document.root().isObject()) return std::nullopt;
    std::string out;
    out.reserve(json.size());
    bool inString = false, escaped = false;
    for (const char c : json) {
        if (inString) {
            out += c;
            if (escaped) {
                escaped = false;
            } else if (c == '\\') {
                escaped = true;
            } else if (c == '"') {
                inString = false;
            }
        } else if (c == '"') {
            inString = true;
            out += c;
        } else if (c != ' ' && c != '\t' && c != '\n' && c != '\r') {
            out += c;
        }
    }
    return out;
}

namespace encode {

std::optional<std::string> start(std::int64_t todayStartMs, const std::set<SourceKind>& sources, const Zone& zone,
                                 std::string_view compactPrices, const std::optional<HostQuotaConfig>& quota) {
    std::string keys;
    for (const auto source : sources) {
        if (!keys.empty()) keys += ',';
        keys += quoted(sourceId(source));
    }
    return line("{\"t\":\"start\",\"protocol\":" + std::to_string(kProtocol) + ",\"todayStartMs\":" + std::to_string(todayStartMs) +
                ",\"sources\":[" + keys + "],\"zone\":{" + zoneMembers(zone) + "},\"prices\":" + std::string(compactPrices) +
                (quota ? ",\"quota\":" + quotaConfig(*quota) : std::string()) + "}");
}

std::optional<std::string> quota(const HostQuotaConfig& config) { return line("{\"t\":\"quota\",\"config\":" + quotaConfig(config) + "}"); }

std::optional<std::string> ping(std::int64_t id) { return line("{\"t\":\"ping\",\"id\":" + std::to_string(id) + "}"); }

std::optional<std::string> today(std::int64_t todayStartMs) {
    return line("{\"t\":\"today\",\"todayStartMs\":" + std::to_string(todayStartMs) + "}");
}

std::optional<std::string> prices(std::string_view compactPrices) {
    return line("{\"t\":\"prices\",\"table\":" + std::string(compactPrices) + "}");
}

std::optional<std::string> zone(const Zone& zone) { return line("{\"t\":\"zone\"," + zoneMembers(zone) + "}"); }

std::optional<std::string> history(const std::map<std::string, std::string>& digests) {
    std::vector<std::string> keys;
    for (const auto& [key, _] : digests) keys.push_back(key);
    std::sort(keys.begin(), keys.end(), byBytes);
    std::string members;
    for (const auto& key : keys) {
        if (!members.empty()) members += ',';
        members += quoted(key) + ":" + quoted(digests.at(key));
    }
    return line("{\"t\":\"history\",\"digests\":{" + members + "}}");
}

std::optional<std::string> watch(bool on) { return line(std::string("{\"t\":\"watch\",\"on\":") + (on ? "true" : "false") + "}"); }

std::string base64(std::string_view bytes) {
    static constexpr char kAlphabet[] = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
    std::string out;
    out.reserve((bytes.size() + 2) / 3 * 4);
    std::size_t i = 0;
    for (; i + 2 < bytes.size(); i += 3) {
        const auto n = (static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[i])) << 16) |
                       (static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[i + 1])) << 8) |
                       static_cast<unsigned char>(bytes[i + 2]);
        out += kAlphabet[(n >> 18) & 63];
        out += kAlphabet[(n >> 12) & 63];
        out += kAlphabet[(n >> 6) & 63];
        out += kAlphabet[n & 63];
    }
    if (const auto rest = bytes.size() - i; rest > 0) {
        std::uint32_t n = static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[i])) << 16;
        if (rest == 2) n |= static_cast<std::uint32_t>(static_cast<unsigned char>(bytes[i + 1])) << 8;
        out += kAlphabet[(n >> 18) & 63];
        out += kAlphabet[(n >> 12) & 63];
        out += rest == 2 ? kAlphabet[(n >> 6) & 63] : '=';
        out += '=';
    }
    return out;
}

}  // namespace encode

std::string historyDigest(std::string_view aggregateJson, std::vector<std::pair<std::string, std::string>> perf) {
    std::stable_sort(perf.begin(), perf.end(), [](const auto& a, const auto& b) { return byBytes(a.first, b.first); });
    std::string text(aggregateJson);
    for (const auto& [model, json] : perf) text += json;
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

}  // namespace agentspeed::remote
