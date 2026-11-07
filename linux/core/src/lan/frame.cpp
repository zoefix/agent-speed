#include <algorithm>
#include <cmath>

#include "agentspeed/lan_frame.hpp"
#include "agentspeed/report_body.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::lan {

namespace {

using report::Json;

constexpr double kMaxTokS = 1e9;
constexpr int kMaxCount = 100'000;
constexpr std::string_view kDefaultName = "Agent Speed";

double finite(double value, double high) { return std::clamp(std::isfinite(value) ? value : 0.0, 0.0, high); }

Values valuesOf(const LiveValues& live) {
    Values v;
    v.generating = clampCount(live.generating);
    v.tools = clampCount(live.tools);
    v.active = clampCount(live.active);
    v.phase = std::string(phaseName(v.generating, v.tools));
    v.outputTokS = roundTokS(live.outputTokS);
    v.estimatedShare = roundShare(live.estimatedShare);
    return v;
}

void addValues(std::vector<Json::Member>& members, const Values& v) {
    members.emplace_back("phase", Json::of(v.phase));
    members.emplace_back("outputTokS", Json::decimal(v.outputTokS, 1));
    members.emplace_back("estimatedShare", Json::decimal(v.estimatedShare, 3));
}

Json sessions(const Values& v) {
    return Json::object({{"active", Json::of(v.active)}, {"generating", Json::of(v.generating)}, {"tools", Json::of(v.tools)}});
}

std::string trimmedName(std::string_view raw) {
    auto name = text::strip(raw);
    std::size_t points = 0, cut = 0;
    for (std::size_t i = 0; i < name.size();) {
        text::nextCodePoint(name, i);
        if (++points == kNameLimit) cut = i;
    }
    if (points > kNameLimit) name = text::strip(std::string_view(name).substr(0, cut));
    return name;
}

}  // namespace

double roundTokS(double value) { return Json::round(finite(value, kMaxTokS), 1); }
double roundShare(double value) { return Json::round(finite(value, 1), 3); }
int clampCount(int value) { return std::clamp(value, 0, kMaxCount); }

std::string_view phaseName(int generating, int tools) { return generating > 0 ? "generating" : tools > 0 ? "tools" : "idle"; }

std::string computerName(std::string_view raw) {
    auto name = trimmedName(raw);
    return name.empty() ? std::string(kDefaultName) : name;
}

LiveValues overallValues(const report::Sample& sample) {
    LiveValues out;
    out.outputTokS = sample.live.output;
    out.estimatedShare = sample.estimatedShare;
    for (const auto& [_, counts] : sample.facts.agents) {
        out.generating += counts.generating;
        out.tools += counts.tools;
        out.active += counts.active;
    }
    return out;
}

std::map<SourceKind, LiveValues> sourceValues(const report::Sample& sample) {
    std::map<SourceKind, LiveValues> out;
    for (const auto& [kind, source] : sample.sources) {
        auto& values = out[kind];
        values.outputTokS = source.live.output;
        values.estimatedShare = source.estimatedShare;
        if (const auto it = sample.facts.agents.find(kind); it != sample.facts.agents.end()) {
            values.generating = it->second.generating;
            values.tools = it->second.tools;
            values.active = it->second.active;
        }
    }
    return out;
}

Frame buildFrame(const FrameInput& input) {
    Frame frame;
    frame.instance = input.instance;
    frame.name = computerName(input.name);
    frame.run = input.run;
    frame.seq = input.seq;
    frame.atMs = input.atMs;
    frame.values = valuesOf(input.live);
    for (const auto& [kind, live] : input.sources) {
        auto values = valuesOf(live);
        if (values.active > 0 || values.outputTokS > 0) frame.sources.push_back({kind, std::move(values)});
    }
    frame.history = input.history;
    return frame;
}

std::string Frame::json(bool withSources, bool withHistory) const {
    std::vector<Json::Member> members{{"schema", Json::of(1)},
                                      {"type", Json::of("live")},
                                      {"instance", Json::of(instance)},
                                      {"name", Json::of(name)},
                                      {"run", Json::of(run)},
                                      {"seq", Json::of(seq)},
                                      {"at", Json::of(report::formatInstant(atMs))}};
    addValues(members, values);
    members.emplace_back("estimated", Json::of(estimated()));
    members.emplace_back("sessions", sessions(values));
    if (withSources) {
        std::vector<Json> list;
        for (const auto& entry : sources) {
            std::vector<Json::Member> item{{"source", Json::of(sourceId(entry.source))}};
            addValues(item, entry.values);
            item.emplace_back("sessions", sessions(entry.values));
            list.push_back(Json::object(std::move(item)));
        }
        members.emplace_back("sources", Json::array(std::move(list)));
    }
    if (withHistory) {
        std::vector<Json> samples;
        for (const double v : history) samples.push_back(Json::decimal(v, 1));
        members.emplace_back("history", Json::object({{"stepMs", Json::of(History::kStepMs)}, {"outputTokS", Json::array(std::move(samples))}}));
    }
    return Json::object(std::move(members)).compact();
}

std::string Frame::wire() const {
    auto bytes = json();
    if (bytes.size() > kFrameLimit) bytes = json(true, false);
    if (bytes.size() > kFrameLimit) bytes = json(false, false);
    return bytes;
}

bool Frame::sameContent(const Frame& o) const {
    return instance == o.instance && name == o.name && run == o.run && values == o.values && sources == o.sources && history == o.history;
}

}  // namespace agentspeed::lan
