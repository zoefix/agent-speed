#include <algorithm>
#include <cmath>
#include <ctime>

#include "agentspeed/report_body.hpp"

namespace agentspeed::report {

namespace {

constexpr double kMaxTokS = 1e9;
constexpr int kMaxCount = 100'000;
constexpr std::int64_t kMaxWindowMs = 900'000;

double finite(double value, double high) { return std::clamp(std::isfinite(value) ? value : 0.0, 0.0, high); }
Json tokS(double value) { return Json::decimal(finite(value, kMaxTokS), 1); }
Json share(double value) { return Json::decimal(finite(value, 1), 3); }
std::int64_t perSecond(double value) { return static_cast<std::int64_t>(Json::round(finite(value, 9e15), 0)); }
std::int64_t bytesPerSecond(double value) { return static_cast<std::int64_t>(Json::round(finite(value, 1e12), 0)); }
Json optional(const std::optional<std::int64_t>& value) { return value ? Json::of(*value) : Json::null(); }

Json delta(const Usage& u) {
    return Json::object({{"requests", Json::of(u.requests)},
                         {"output", Json::of(u.output)},
                         {"input", Json::of(u.input)},
                         {"cachedInput", Json::of(u.cachedInput)},
                         {"cacheWriteInput", Json::of(u.cacheWriteInput)},
                         {"reasoning", Json::of(u.reasoning)},
                         {"measuredOutput", Json::of(u.measuredOutput)},
                         {"measuredMs", Json::of(u.measuredMs)}});
}

Json counts(const AgentCounts& a) {
    return Json::object({{"generating", Json::of(std::min(a.generating, kMaxCount))},
                         {"tools", Json::of(std::min(a.tools, kMaxCount))},
                         {"active", Json::of(std::min(a.active, kMaxCount))}});
}

std::optional<traffic::Rate> larger(const std::optional<traffic::Rate>& a, const std::optional<traffic::Rate>& b) {
    if (!a) return b;
    if (!b) return a;
    return traffic::Rate{std::max(a->rx, b->rx), std::max(a->tx, b->tx)};
}

Json trafficJson(const TrafficSample& traffic, const std::optional<traffic::Rate>& peak, const Bytes& bytes) {
    if (traffic.status != TrafficStatus::Ok) return Json::object({{"status", Json::of(traffic.status == TrafficStatus::Off ? "off" : "unavailable")}});
    const auto& rate = traffic.rate;
    const auto top = larger(peak, rate);
    const auto value = [](const std::optional<traffic::Rate>& r, bool rx) {
        return r ? std::optional<std::int64_t>(bytesPerSecond(rx ? r->rx : r->tx)) : std::nullopt;
    };
    return Json::object({{"status", Json::of("ok")},
                         {"includesTools", Json::of(traffic.includesTools)},
                         {"rxBytesPerSecond", optional(value(rate, true))},
                         {"txBytesPerSecond", optional(value(rate, false))},
                         {"peakRxBytesPerSecond", optional(value(top, true))},
                         {"peakTxBytesPerSecond", optional(value(top, false))},
                         {"rxBytes", Json::of(std::max<std::int64_t>(0, bytes.rx))},
                         {"txBytes", Json::of(std::max<std::int64_t>(0, bytes.tx))}});
}

std::vector<SourceKind> sourceOrder() {
    std::vector<SourceKind> out;
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) out.push_back(static_cast<SourceKind>(k));
    return out;
}

// One entry per source with activity in the window, in source order (REPORTING 6.6.3, METRICS 11.7).
std::vector<Json> sourceEntries(const WindowContent& content, const Sample& sample, const std::set<Section>& sections) {
    std::map<SourceKind, Usage> usage;
    for (const auto& [key, value] : content.usage.byModel()) usage[key.source] += value;
    const bool withCost = sections.contains(Section::Cost);
    const bool trafficOk = sections.contains(Section::Traffic) && sample.traffic.status == TrafficStatus::Ok;
    std::vector<Json> entries;
    for (const auto kind : sourceOrder()) {
        const auto sourceIt = sample.sources.find(kind);
        const auto source = sourceIt == sample.sources.end() ? SourceSample{} : sourceIt->second;
        const auto agentsIt = sample.facts.agents.find(kind);
        const auto agents = agentsIt == sample.facts.agents.end() ? AgentCounts{} : agentsIt->second;
        const auto d = usage.contains(kind) ? usage[kind] : Usage{};
        const auto bytesIt = content.agentBytes.find(kind);
        const auto bytes = bytesIt == content.agentBytes.end() ? Bytes{} : bytesIt->second;
        std::optional<traffic::Rate> rate;
        if (trafficOk) {
            if (const auto own = sample.traffic.sourceRates.find(kind); own != sample.traffic.sourceRates.end()) rate = own->second;
            else if (sample.traffic.rate) rate = traffic::Rate{};
        }
        std::vector<double> values{Json::round(source.live.output, 1),
                                   Json::round(source.live.total, 1),
                                   static_cast<double>(agents.generating),
                                   static_cast<double>(agents.tools),
                                   static_cast<double>(d.requests),
                                   static_cast<double>(d.output),
                                   static_cast<double>(d.input),
                                   static_cast<double>(d.cachedInput),
                                   static_cast<double>(d.cacheWriteInput),
                                   static_cast<double>(d.reasoning),
                                   static_cast<double>(d.measuredOutput),
                                   static_cast<double>(d.measuredMs)};
        if (withCost) {
            values.insert(values.end(), {Json::round(source.liveCost, 0), static_cast<double>(d.costMicroUsd), static_cast<double>(d.unpricedRequests)});
        }
        if (trafficOk) {
            values.insert(values.end(), {Json::round(rate ? rate->rx : 0, 0), Json::round(rate ? rate->tx : 0, 0), static_cast<double>(bytes.rx),
                                         static_cast<double>(bytes.tx)});
        }
        if (std::none_of(values.begin(), values.end(), [](double v) { return v > 0; })) continue;

        auto entry = Json::object({{"source", Json::of(sourceId(kind))},
                                   {"live", Json::object({{"outputTokS", tokS(source.live.output)},
                                                          {"totalTokS", tokS(source.live.total)},
                                                          {"estimatedShare", share(source.estimatedShare)}})},
                                   {"agents", counts(agents)},
                                   {"delta", delta(d)}});
        if (withCost) {
            entry.add("cost", Json::object({{"liveMicroUsdPerSecond", Json::of(perSecond(source.liveCost))},
                                            {"knownShare", share(source.knownShare)},
                                            {"deltaMicroUsd", Json::of(d.costMicroUsd)},
                                            {"unpricedRequests", Json::of(d.unpricedRequests)}}));
        }
        if (trafficOk) {
            const auto value = [&](bool rx) { return rate ? std::optional<std::int64_t>(bytesPerSecond(rx ? rate->rx : rate->tx)) : std::nullopt; };
            entry.add("traffic", Json::object({{"rxBytesPerSecond", optional(value(true))},
                                               {"txBytesPerSecond", optional(value(false))},
                                               {"rxBytes", Json::of(bytes.rx)},
                                               {"txBytes", Json::of(bytes.tx)}}));
        }
        entries.push_back(std::move(entry));
    }
    return entries;
}

struct ModelEntry {
    std::string id;
    std::set<SourceKind> sources;
    Usage usage;
    int generating = 0;
    std::map<SourceKind, std::int64_t> harnessRequests;
    bool usual = false;  // an entry of METRICS 11.3, not one only the task score adds
    std::vector<const TaskAgent*> taskAgents;
    double thinking = 0;  // unrounded model sums of the entry's raw models
    std::map<SourceKind, TaskRow> taskRows;
    std::map<ModeKey, Usage> modes;
    void addMode(ModeKey key, const Usage& value) {
        key.modelId = id;
        modes[key] += value;
    }
};

bool safeCount(std::int64_t v) { return v >= 0 && v <= 9'007'199'254'740'991; }

// REPORTING-EXTENSIONS 3: the per-mode breakdown, or nullopt when it cannot be sent whole (sums, sizes, identifiers).
std::optional<Json> modesJson(const ModelEntry& entry) {
    const auto& u = entry.usage;
    if (entry.modes.size() > 64 || !safeCount(u.requests) || !safeCount(u.output) || !safeCount(u.measuredOutput) || !safeCount(u.measuredMs))
        return std::nullopt;
    if (entry.modes.empty() && (u.requests > 0 || u.output > 0 || u.measuredOutput > 0 || u.measuredMs > 0)) return std::nullopt;
    std::map<ModeKind, Usage> sums;
    for (const auto& [key, usage] : entry.modes) sums[key.kind] += usage;
    for (const auto& [_, sum] : sums) {
        if (sum.requests != u.requests || sum.output != u.output || sum.measuredOutput != u.measuredOutput || sum.measuredMs != u.measuredMs)
            return std::nullopt;
    }
    std::vector<Json> rows;
    for (const auto& [key, usage] : entry.modes) {
        const bool evidenceOk = key.evidence == "served" || key.evidence == "recorded" || key.evidence == "selected" || key.evidence == "unknown";
        if (!entry.sources.contains(key.source) || !ExecutionModes::isReportKind(key.kind) || !ExecutionModes::isIdentifier(key.mode, 64) || !evidenceOk ||
            ((key.mode == "unknown") != (key.evidence == "unknown")) || (key.provider && !ExecutionModes::isIdentifier(*key.provider, 32)) ||
            !safeCount(usage.requests) || !safeCount(usage.output) || !safeCount(usage.measuredOutput) || !safeCount(usage.measuredMs))
            return std::nullopt;
        auto row = Json::object({{"source", Json::of(sourceId(key.source))}});
        if (key.provider) row.add("provider", Json::of(*key.provider));
        row.add("kind", Json::of(modeKindName(key.kind)));
        row.add("mode", Json::of(key.mode));
        row.add("evidence", Json::of(key.evidence));
        row.add("requests", Json::of(usage.requests));
        row.add("output", Json::of(usage.output));
        row.add("measuredOutput", Json::of(usage.measuredOutput));
        row.add("measuredMs", Json::of(usage.measuredMs));
        rows.push_back(std::move(row));
    }
    return Json::array(std::move(rows));
}

const TaskFacts& noTaskFacts() {
    static const TaskFacts empty;
    return empty;
}

// One entry per model with any increment in the window (a late price included) or a generating agent, by output then id,
// `other` last (REPORTING 6.6.4, METRICS 11.3, 11.7).
std::vector<Json> modelEntries(const WindowContent& content, const Sample& sample, int maxModels, bool withCost, const Extensions& extensions,
                               bool taskScore) {
    std::map<std::string, ModelEntry> byId;
    const auto entryOf = [&](const std::string& id) -> ModelEntry& {
        auto& entry = byId[id];
        entry.id = id;
        return entry;
    };
    for (const auto& [key, value] : content.usage.byModel()) {
        auto& entry = entryOf(key.modelId);
        entry.usage += value;
        entry.sources.insert(key.source);
        entry.harnessRequests[key.source] += value.requests;
    }
    for (const auto& [key, value] : content.usage.byMode()) entryOf(key.modelId).addMode(key, value);
    for (const auto& [key, count] : sample.facts.generatingModels) {
        if (count <= 0) continue;
        auto& entry = entryOf(key.modelId);
        entry.generating += count;
        entry.sources.insert(key.source);
    }
    // METRICS 11.10 Membership: the models an agent is live with and the models with task rows get entries too.
    const auto& tasks = sample.facts.tasks ? *sample.facts.tasks : noTaskFacts();
    if (taskScore) {
        for (auto& [_, entry] : byId) entry.usual = true;
        for (const auto& agent : tasks.agents)
            if (agent.form == "live") entryOf(agent.modelId).sources.insert(agent.source);
        for (const auto& [key, row] : content.tasks) {
            auto& entry = entryOf(key.modelId);
            entry.sources.insert(key.source);
            entry.taskRows[key.source] += row;
        }
        for (const auto& agent : tasks.agents)
            if (const auto it = byId.find(agent.modelId); it != byId.end()) it->second.taskAgents.push_back(&agent);
        for (auto& [id, entry] : byId)
            if (const auto it = tasks.modelThinking.find(id); it != tasks.modelThinking.end()) entry.thinking = it->second;
    }
    std::vector<ModelEntry> entries;
    for (auto& [_, entry] : byId) entries.push_back(std::move(entry));
    std::stable_sort(entries.begin(), entries.end(), [](const ModelEntry& a, const ModelEntry& b) {
        return a.usage.output != b.usage.output ? a.usage.output > b.usage.output : a.id < b.id;
    });
    if (static_cast<int>(entries.size()) > maxModels) {
        ModelEntry other;
        other.id = "other";
        for (auto it = entries.begin() + (maxModels - 1); it != entries.end(); ++it) {
            other.usage += it->usage;
            other.generating += it->generating;
            other.sources.insert(it->sources.begin(), it->sources.end());
            for (const auto& [source, requests] : it->harnessRequests) other.harnessRequests[source] += requests;
            other.usual = other.usual || it->usual || !taskScore;
            other.taskAgents.insert(other.taskAgents.end(), it->taskAgents.begin(), it->taskAgents.end());
            other.thinking += it->thinking;
            for (const auto& [source, row] : it->taskRows) other.taskRows[source] += row;
            for (const auto& [mode, usage] : it->modes) other.addMode(mode, usage);
        }
        entries.resize(static_cast<std::size_t>(maxModels - 1));
        entries.push_back(std::move(other));
    }
    std::vector<Json> out;
    for (const auto& entry : entries) {
        std::vector<Json> sources;
        for (const auto kind : sourceOrder())
            if (entry.sources.contains(kind)) sources.push_back(Json::of(sourceId(kind)));
        auto json = Json::object({{"id", Json::of(entry.id)},
                                  {"sources", Json::array(std::move(sources))},
                                  {"requests", Json::of(entry.usage.requests)},
                                  {"output", Json::of(entry.usage.output)},
                                  {"measuredOutput", Json::of(entry.usage.measuredOutput)},
                                  {"measuredMs", Json::of(entry.usage.measuredMs)},
                                  {"generating", Json::of(std::min(entry.generating, kMaxCount))}});
        if (withCost) json.add("deltaMicroUsd", Json::of(entry.usage.costMicroUsd));
        std::int64_t harnessSum = 0;
        for (const auto& [_, requests] : entry.harnessRequests) harnessSum += requests;
        const bool usual = !taskScore || entry.usual;
        if (usual && extensions.modelHarnessRequests && entry.harnessRequests.size() <= 8 && harnessSum == entry.usage.requests) {
            std::vector<Json> rows;
            for (const auto& [source, requests] : entry.harnessRequests) {
                if (requests > 0) rows.push_back(Json::object({{"source", Json::of(sourceId(source))}, {"requests", Json::of(requests)}}));
            }
            json.add("harnessRequests", Json::array(std::move(rows)));
        }
        if (usual && extensions.executionModes) {
            if (auto modes = modesJson(entry)) json.add("modeUsage", std::move(*modes));
        }
        if (taskScore) {
            json.add("taskScore", taskValue(entry.taskAgents, entry.thinking));
            std::vector<Json> stats;
            for (const auto kind : sourceOrder())
                if (const auto it = entry.taskRows.find(kind); it != entry.taskRows.end()) stats.push_back(it->second.json(kind));
            json.add("taskStats", Json::array(std::move(stats)));
        }
        out.push_back(std::move(json));
    }
    return out;
}

bool fits(const std::string& data, const Options& options) {
    return static_cast<int>(data.size()) <= options.bodyLimit && (!options.stream || messageBytes(data) <= static_cast<std::size_t>(options.stream->maxMessageBytes));
}

}  // namespace

const char* sectionName(Section section) {
    switch (section) {
    case Section::Sources: return "sources";
    case Section::Models: return "models";
    case Section::Cost: return "cost";
    case Section::Traffic: return "traffic";
    }
    return "";
}

std::optional<Section> sectionFromName(std::string_view name) {
    for (const auto section : kAllSections)
        if (name == sectionName(section)) return section;
    return std::nullopt;
}

ClientInfo::ClientInfo(std::string v, std::string p, int major)
    : version(isVersion(v) ? std::move(v) : "0.0.0"), platform(std::move(p)), osMajor(std::clamp(major, 1, 99)) {}

bool ClientInfo::isVersion(std::string_view text) {
    if (text.size() > 32) return false;
    const auto dash = text.find('-');
    const auto core = text.substr(0, dash);
    int parts = 0;
    std::size_t start = 0;
    while (true) {
        const auto dot = core.find('.', start);
        const auto number = core.substr(start, dot == std::string_view::npos ? std::string_view::npos : dot - start);
        if (number.empty() || !std::all_of(number.begin(), number.end(), [](char c) { return c >= '0' && c <= '9'; })) return false;
        ++parts;
        if (dot == std::string_view::npos) break;
        start = dot + 1;
    }
    if (parts != 3) return false;
    if (dash == std::string_view::npos) return true;
    const auto pre = text.substr(dash + 1);
    return !pre.empty() && std::all_of(pre.begin(), pre.end(), [](char c) { return std::isalnum(static_cast<unsigned char>(c)) || c == '.' || c == '-'; });
}

Json ClientInfo::json() const {
    return Json::object({{"app", Json::of("AgentSpeed")}, {"version", Json::of(version)}, {"platform", Json::of(platform)}, {"osMajor", Json::of(osMajor)}});
}

std::string formatInstant(std::int64_t ms) {
    const auto seconds = static_cast<std::time_t>(ms >= 0 ? ms / 1000 : (ms - 999) / 1000);
    const auto millis = ms - static_cast<std::int64_t>(seconds) * 1000;
    std::tm tm{};
    gmtime_r(&seconds, &tm);
    char buffer[40];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%dT%H:%M:%S", &tm);
    char out[48];
    std::snprintf(out, sizeof out, "%s.%03dZ", buffer, static_cast<int>(millis));
    return out;
}

std::string reportMessage(const Headers& headers, std::string_view body) {
    static constexpr std::array<std::string_view, 4> kNames = {"X-AgentSpeed-Device", "X-AgentSpeed-Timestamp", "X-AgentSpeed-Nonce",
                                                               "X-AgentSpeed-Signature"};
    std::string text = R"({"type":"report","headers":{)";
    for (std::size_t i = 0; i < kNames.size(); ++i) {
        std::string value;
        for (const auto& [name, v] : headers) {
            if (name.size() == kNames[i].size() &&
                std::equal(name.begin(), name.end(), kNames[i].begin(), [](char a, char b) { return std::tolower(static_cast<unsigned char>(a)) == std::tolower(static_cast<unsigned char>(b)); })) {
                value = v;
                break;
            }
        }
        text += (i ? "," : "") + Json::quoted(kNames[i]) + ":" + Json::quoted(value);
    }
    return text + R"(},"body":)" + Json::quoted(body) + "}";
}

std::size_t messageBytes(std::string_view body) {
    static const std::size_t overhead = reportMessage({{"X-AgentSpeed-Device", std::string(36, '0')},
                                                       {"X-AgentSpeed-Timestamp", std::string(10, '0')},
                                                       {"X-AgentSpeed-Nonce", std::string(22, '0')},
                                                       {"X-AgentSpeed-Signature", std::string(86, '0')}},
                                                      "")
                                            .size();
    std::size_t size = overhead;
    for (const unsigned char b : body) {
        size += (b == '"' || b == '\\' || b == '\b' || b == '\f' || b == '\n' || b == '\r' || b == '\t') ? 2 : b < 0x20 ? 6 : 1;
    }
    return size;
}

Json realtime(bool mixed, std::int64_t sentAtMs, std::optional<std::int64_t> observationAgeMs) {
    return Json::object({{"sourceMode", Json::of(mixed ? "mixed" : "log")},
                         {"observedAt", observationAgeMs ? Json::of(formatInstant(sentAtMs - *observationAgeMs)) : Json::null()},
                         {"observationAgeMs", optional(observationAgeMs)},
                         {"sourceResolutionMs", mixed ? Json::null() : Json::of(1000)}});
}

Json body(const Envelope& envelope, const WindowContent& content, const Sample& sample, const Options& options) {
    const auto total = content.usage.total();
    AgentCounts agents;
    for (const auto& [_, c] : sample.facts.agents) {
        agents.generating += c.generating;
        agents.tools += c.tools;
        agents.active += c.active;
    }
    const auto value = sample.value(options.totalMetric);
    auto json = Json::object({
        {"schema", Json::of(1)},
        {"client", envelope.client.json()},
        {"deviceId", Json::of(envelope.deviceId)},
        {"username", Json::of(envelope.username)},
        {"run", Json::object({{"id", Json::of(envelope.runId)}, {"seq", Json::of(envelope.seq)}})},
        {"sentAt", Json::of(formatInstant(envelope.sentAtMs))},
        {"windowMs", Json::of(std::clamp<std::int64_t>(envelope.windowMs, 0, kMaxWindowMs))},
        {"activity", Json::of(activityName(envelope.activity))},
        {"live", Json::object({{"outputTokS", tokS(sample.live.output)},
                               {"totalTokS", tokS(sample.live.total)},
                               {"estimatedShare", share(sample.estimatedShare)},
                               {"agents", counts(agents)}})},
        {"peak", Json::object({{"outputTokS", tokS(content.peak.output)}, {"totalTokS", tokS(content.peak.total)}})},
        {"delta", delta(total)},
        {"title", Json::object({{"level", Json::of(std::clamp(envelope.titleLevel, 0, TitleTracker::kMaxLevel))},
                                {"metric", Json::of(options.totalMetric ? "total" : "output")},
                                {"redline", Json::decimal(options.redline, 1)},
                                {"heat", share(TitleTracker::heat(value, options.redline))}})},
    });
    const auto& sections = options.sections;
    if (sections.contains(Section::Cost)) {
        json.add("cost", Json::object({{"liveMicroUsdPerSecond", Json::of(perSecond(sample.liveCost))},
                                       {"peakMicroUsdPerSecond", Json::of(perSecond(std::max(content.peakCost, sample.liveCost)))},
                                       {"knownShare", share(sample.knownShare)},
                                       {"deltaMicroUsd", Json::of(total.costMicroUsd)},
                                       {"unpricedRequests", Json::of(total.unpricedRequests)},
                                       {"priceVersion", Json::of(std::max(1, options.priceVersion))}}));
    }
    if (sections.contains(Section::Traffic)) {
        Bytes bytes;
        for (const auto& [_, b] : content.agentBytes) {
            bytes.rx += b.rx;
            bytes.tx += b.tx;
        }
        json.add("traffic", trafficJson(sample.traffic, content.peakRate, bytes));
    }
    if (sections.contains(Section::Sources)) json.add("sources", Json::array(sourceEntries(content, sample, sections)));
    const bool taskScore = options.taskScoreActive();
    if (sections.contains(Section::Models) && options.maxModels > 0) {
        json.add("models", Json::array(modelEntries(content, sample, options.maxModels, sections.contains(Section::Cost), options.extensions, taskScore)));
    }
    if (taskScore) {
        const auto& tasks = sample.facts.tasks ? *sample.facts.tasks : noTaskFacts();
        std::vector<const TaskAgent*> all;
        for (const auto& agent : tasks.agents) all.push_back(&agent);
        json.add("taskScore", taskValue(all, tasks.thinking));
    }
    if (options.stream) {
        const bool mixed = sections.contains(Section::Traffic) && sample.traffic.status == TrafficStatus::Ok;
        json.add("realtime", realtime(mixed, envelope.sentAtMs, options.stream->observationAgeMs));
    }
    return json;
}

Fitted fitted(const Envelope& envelope, const WindowContent& content, const Sample& sample, Options options) {
    auto document = body(envelope, content, sample, options);
    auto data = document.compact();
    if (!fits(data, options) && (options.extensions.executionModes || options.extensions.modelHarnessRequests)) {
        options.extensions.executionModes = options.extensions.modelHarnessRequests = false;
        document = body(envelope, content, sample, options);
        data = document.compact();
    }
    for (const auto section : {Section::Models, Section::Sources}) {
        if (fits(data, options)) break;
        options.modelsDroppedForSize = options.modelsDroppedForSize || (section == Section::Models && options.sections.contains(section));
        options.sections.erase(section);
        document = body(envelope, content, sample, options);
        data = document.compact();
    }
    return {std::move(document), std::move(data)};
}

}  // namespace agentspeed::report
