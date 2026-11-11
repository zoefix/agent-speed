#include "protocol.hpp"

#include <algorithm>

#include "agentspeed/json.hpp"
#include "agentspeed/remote_messages.hpp"
#include "json_out.hpp"

namespace agentspeed::relay {

namespace {

constexpr std::size_t kMaxId = 4096;
constexpr std::size_t kMaxKey = 512;
constexpr std::size_t kMaxProject = 256;
constexpr std::size_t kMaxModel = 256;
constexpr std::size_t kMaxProvider = 64;
constexpr std::size_t kMaxTool = 128;
constexpr std::size_t kMaxActions = 64;
constexpr std::int64_t kMaxSafeInteger = 9'007'199'254'740'992;  // 2^53
constexpr std::int64_t kMaxRequestCount = 100'000;
constexpr std::size_t kMaxInputDepth = 64;

std::optional<std::int64_t> safeInteger(const json::Value& v) {
    if (!v.isNumber() || !v.isIntegerLiteral()) return std::nullopt;
    const auto value = v.truncated();
    if (!value || *value > kMaxSafeInteger || *value < -kMaxSafeInteger) return std::nullopt;
    return value;
}

std::optional<std::string> stringOf(const json::Value& v) { return v.isString() ? v.string() : std::nullopt; }

std::optional<std::int64_t> timeOrNull(const std::optional<std::int64_t>& ms) {
    return ms && validTime(*ms) ? ms : std::nullopt;
}

// Cut at a character boundary: a tool name is kept, shortened, rather than dropped with its action.
std::string truncated(const std::string& text, std::size_t max) {
    auto clean = sanitizeUtf8(text);
    if (clean.size() <= max) return clean;
    std::size_t end = max;
    while (end > 0 && (static_cast<unsigned char>(clean[end]) & 0xC0) == 0x80) --end;
    clean.resize(end);
    return clean;
}

void writeModes(JsonOut& j, const ExecutionModes& modes) {
    j.beginArray();
    for (const auto& fact : modes.facts()) {
        j.beginObject();
        j.key("kind").string(modeKindName(fact.kind));
        j.key("value").string(fact.value);
        j.key("rawValue").string(fact.raw);
        j.key("evidence").string(modeEvidenceName(fact.evidence));
        j.key("provider").optionalString(fact.provider);
        j.key("scope").string(modeScopeName(fact.scope));
        j.endObject();
    }
    j.endArray();
}

void writeAction(JsonOut& j, const work::Action& a) {
    j.beginObject();
    j.key("tool").string(truncated(a.tool, kMaxTool));
    j.key("digest").string(a.digest);
    j.key("callMs").integer(a.callMs);
    j.key("resultMs").optionalInteger(timeOrNull(a.resultMs));
    j.key("failed").optionalBoolean(a.failed);
    j.key("model").optionalString(boundedText(a.model, kMaxModel));
    j.key("seeded").boolean(a.seeded);
    j.endObject();
}

bool sendable(const work::Action& a) {
    if (!validTime(a.callMs) || a.digest.size() != 16) return false;
    return std::all_of(a.digest.begin(), a.digest.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
}

std::optional<std::set<SourceKind>> sourcesOf(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::set<SourceKind> out;
    for (const auto& item : v.elements()) {
        const auto id = stringOf(item);
        if (!id) return std::nullopt;
        // A key of a newer app is not a source this relay knows.
        if (const auto kind = sourceFromId(*id)) out.insert(*kind);
    }
    return out;
}

bool flag(const json::Value& v, bool& out) {
    if (!v.isBool()) return false;
    out = v.isTrue();
    return true;
}

// `{sources, network, grokLive, claudeBridge}`; keys of sources a host cannot read are dropped (REMOTE 11).
std::optional<QuotaConfig> quotaConfigOf(const json::Value& v) {
    if (!v.isObject()) return std::nullopt;
    QuotaConfig config;
    auto sources = sourcesOf(v["sources"]);
    if (!sources || !flag(v["network"], config.network) || !flag(v["grokLive"], config.grokLive) ||
        !flag(v["claudeBridge"], config.claudeBridge)) {
        return std::nullopt;
    }
    for (const auto source : *sources) {
        if (remote::quotas::hostReads(source)) config.sources.insert(source);
    }
    return config;
}

}  // namespace

std::optional<std::string> boundedText(const std::optional<std::string>& text, std::size_t max) {
    if (!text) return std::nullopt;
    auto clean = sanitizeUtf8(*text);
    if (clean.size() > max || hasControl(clean)) return std::nullopt;
    return clean;
}

bool validTime(std::int64_t ms) { return ms >= kMinTimeMs && ms < kMaxTimeMs; }

std::optional<Command> decodeCommand(std::string_view line) {
    if (line.size() > kMaxInputLine) return std::nullopt;
    json::Document doc;
    if (!doc.parse(line, kMaxInputDepth) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    const auto type = stringOf(root["t"]);
    if (!type) return std::nullopt;
    if (*type == "ping") {
        const auto id = safeInteger(root["id"]);
        if (!id) return std::nullopt;
        return PingCommand{*id};
    }
    if (*type == "start") {
        StartCommand start;
        const auto protocol = safeInteger(root["protocol"]);
        const auto today = safeInteger(root["todayStartMs"]);
        const auto sources = sourcesOf(root["sources"]);
        const auto zone = root["zone"];
        const auto prices = root["prices"];
        if (!protocol || !today || !sources || !zone.isObject() || !prices.isObject()) return std::nullopt;
        const auto zoneId = stringOf(zone["id"]);
        const auto tzif = stringOf(zone["tzif"]);
        if (!zoneId || !tzif) return std::nullopt;
        start.protocol = *protocol;
        start.todayStartMs = *today;
        start.sources = *sources;
        start.zoneId = *zoneId;
        start.zoneTzif = *tzif;
        start.prices = std::string(prices.raw());
        start.quota = quotaConfigOf(root["quota"]);
        return start;
    }
    if (*type == "today") {
        const auto today = safeInteger(root["todayStartMs"]);
        if (!today) return std::nullopt;
        return TodayCommand{*today};
    }
    if (*type == "prices") {
        const auto table = root["table"];
        if (!table.isObject()) return std::nullopt;
        return PricesCommand{std::string(table.raw())};
    }
    if (*type == "zone") {
        const auto id = stringOf(root["id"]);
        const auto tzif = stringOf(root["tzif"]);
        if (!id || !tzif) return std::nullopt;
        return ZoneCommand{*id, *tzif};
    }
    if (*type == "history") {
        const auto digests = root["digests"];
        if (!digests.isObject()) return std::nullopt;
        HistoryCommand history;
        for (const auto& [key, value] : digests.members()) {
            if (const auto digest = stringOf(value)) history.digests[key] = *digest;
        }
        return history;
    }
    if (*type == "watch") {
        const auto on = root["on"];
        if (!on.isBool()) return std::nullopt;
        return WatchCommand{on.isTrue()};
    }
    if (*type == "quota") {
        auto config = quotaConfigOf(root["config"]);
        if (!config) return std::nullopt;
        return QuotaCommand{std::move(*config)};
    }
    return std::nullopt;
}

std::string helloLine(std::string_view version, std::string_view arch, std::int64_t nowMs) {
    JsonOut j;
    j.beginObject().key("t").string("hello").key("protocol").integer(kProtocol);
    j.key("version").string(version).key("arch").string(arch).key("now").integer(nowMs);
    return j.endObject().take();
}

std::string progressLine(double value) {
    JsonOut j;
    return j.beginObject().key("t").string("progress").key("value").fraction(value).endObject().take();
}

std::string readyLine() { return R"({"t":"ready"})"; }

std::string clockLine(std::int64_t id, std::int64_t nowMs) {
    JsonOut j;
    return j.beginObject().key("t").string("clock").key("id").integer(id).key("now").integer(nowMs).endObject().take();
}

std::string errorLine(std::string_view code, std::string_view detail) {
    JsonOut j;
    j.beginObject().key("t").string("error").key("code").string(code);
    return j.key("detail").string(detail.substr(0, 200)).endObject().take();
}

std::string agentLine(const AgentView& agent) {
    const auto& info = *agent.info;
    const auto id = boundedText(info.id, kMaxId);
    if (!id || id->empty()) return {};
    JsonOut j;
    j.beginObject().key("t").string("agent").key("a").beginObject();
    j.key("id").string(*id);
    j.key("source").string(sourceId(info.source));
    j.key("project").optionalString(boundedText(info.project, kMaxProject));
    j.key("cwd").optionalString(boundedText(agent.cwd, kMaxId));
    j.key("model").optionalString(boundedText(info.model, kMaxModel));
    j.key("subagent").boolean(info.isSubagent);
    j.key("phase").string(phaseId(info.phase));
    j.key("inFlightStartMs").optionalInteger(timeOrNull(info.inFlightStartMs));
    j.key("lastLineMs").optionalInteger(timeOrNull(info.lastLineMs));
    j.key("modes");
    writeModes(j, info.modes);
    j.key("sessionModes");
    writeModes(j, info.sessionModes);
    j.key("configuredModes");
    writeModes(j, info.configuredModes);
    j.key("actions").beginArray();
    if (agent.actions) {
        std::vector<const work::Action*> kept;
        for (const auto& a : *agent.actions) {
            if (sendable(a)) kept.push_back(&a);
        }
        const auto first = kept.size() > kMaxActions ? kept.size() - kMaxActions : 0;
        for (std::size_t k = first; k < kept.size(); ++k) writeAction(j, *kept[k]);
    }
    j.endArray();
    return j.endObject().endObject().take();
}

std::string agentRemovedLine(std::string_view id) {
    JsonOut j;
    return j.beginObject().key("t").string("agentRemoved").key("id").string(id).endObject().take();
}

std::string parentsLine(const std::vector<std::pair<std::string, std::string>>& links) {
    JsonOut j;
    j.beginObject().key("t").string("parents").key("links").beginObject();
    for (const auto& [child, parent] : links) {
        const auto c = boundedText(child, kMaxId);
        const auto p = boundedText(parent, kMaxId);
        if (!c || !p || c->empty() || p->empty()) continue;
        j.key(*c).string(*p);
    }
    return j.endObject().endObject().take();
}

std::string responseLine(const ParsedResponse& r, bool completion, const std::optional<std::string>& project) {
    const auto key = boundedText(r.key, kMaxKey);
    const auto agent = boundedText(r.agentId, kMaxId);
    if (!key || key->empty() || !agent || agent->empty() || !validTime(r.endMs)) return {};
    const auto clamp = [](std::int64_t v) { return std::clamp<std::int64_t>(v, 0, TokenCounts::kMax); };
    JsonOut j;
    j.beginObject().key("t").string("response").key("r").beginObject();
    j.key("key").string(*key);
    j.key("source").string(sourceId(r.source));
    j.key("agent").string(*agent);
    j.key("model").optionalString(boundedText(r.model, kMaxModel));
    j.key("startMs").optionalInteger(timeOrNull(r.startMs));
    j.key("endMs").integer(r.endMs);
    j.key("firstMs").optionalInteger(timeOrNull(r.firstMs));
    j.key("input").integer(clamp(r.tokens.input));
    j.key("cachedInput").integer(clamp(r.tokens.cachedInput));
    j.key("cacheWriteInput").integer(clamp(r.tokens.cacheWriteInput));
    j.key("cacheWrite1hInput").integer(clamp(r.tokens.cacheWrite1hInput));
    j.key("output").integer(clamp(r.tokens.output));
    j.key("reasoning").integer(clamp(r.tokens.reasoning));
    j.key("requestCount").integer(std::clamp<std::int64_t>(r.requestCount, 1, kMaxRequestCount));
    j.key("modes");
    writeModes(j, r.modes);
    j.key("final").boolean(r.final);
    j.key("provider").optionalString(boundedText(r.provider, kMaxProvider));
    j.key("fast").boolean(r.fast);
    j.endObject();
    j.key("completion").boolean(completion);
    j.key("project").string(boundedText(project, kMaxProject).value_or(""));
    return j.endObject().take();
}

std::string toolLine(std::string_view agentId, SourceKind source, const work::Action& action) {
    const auto agent = boundedText(std::string(agentId), kMaxId);
    if (!agent || agent->empty() || !validTime(action.callMs) || !action.resultMs || !validTime(*action.resultMs)) return {};
    JsonOut j;
    j.beginObject().key("t").string("tool");
    j.key("agent").string(*agent);
    j.key("source").string(sourceId(source));
    j.key("callMs").integer(action.callMs);
    j.key("resultMs").integer(*action.resultMs);
    j.key("failed").optionalBoolean(action.failed);
    j.key("model").optionalString(boundedText(action.model, kMaxModel));
    j.key("seeded").boolean(action.seeded);
    return j.endObject().take();
}

std::string statusLine(const StatusView& status) {
    const auto& w = status.warnings;
    JsonOut j;
    j.beginObject().key("t").string("status").key("sources").beginArray();
    for (const auto& s : status.sources) {
        j.beginObject();
        j.key("source").string(sourceId(s.source));
        j.key("available").boolean(s.available);
        j.key("sessions").integer(s.sessions);
        j.key("unsupported").boolean(std::find(w.unsupported.begin(), w.unsupported.end(), s.source) != w.unsupported.end());
        j.key("configuredModes");
        writeModes(j, s.configuredModes);
        j.endObject();
    }
    j.endArray();
    j.key("malformed").integer(std::clamp<std::int64_t>(w.malformedLines, 0, kMaxSafeInteger));
    j.key("unreadable").integer(w.unreadableFiles);
    j.key("partialHistory").boolean(w.partialHistory);
    return j.endObject().take();
}

std::string historyDayLine(std::string_view day, SourceKind source, std::string_view aggregate, std::string_view digest) {
    JsonOut j;
    j.beginObject().key("t").string("historyDay").key("day").string(day).key("source").string(sourceId(source));
    return j.key("aggregate").raw(aggregate).key("digest").string(digest).endObject().take();
}

std::string historyPerfLine(std::string_view day, SourceKind source, std::string_view model, std::string_view record) {
    JsonOut j;
    j.beginObject().key("t").string("historyPerf").key("day").string(day).key("source").string(sourceId(source));
    return j.key("model").string(model).key("record").raw(record).endObject().take();
}

std::string historyDoneLine(std::int64_t days, std::int64_t analyzed) {
    JsonOut j;
    return j.beginObject().key("t").string("historyDone").key("days").integer(days).key("analyzed").integer(analyzed).endObject().take();
}

std::string historyProgressLine(double value) {
    JsonOut j;
    return j.beginObject().key("t").string("historyProgress").key("value").fraction(value).endObject().take();
}

}  // namespace agentspeed::relay
