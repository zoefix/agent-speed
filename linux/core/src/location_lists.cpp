#include "agentspeed/location_lists.hpp"

#include <algorithm>
#include <array>
#include <utility>

#include "agentspeed/fs_util.hpp"
#include "agentspeed/json.hpp"
#include "agentspeed/report_json.hpp"

namespace agentspeed {

namespace {

constexpr std::array<std::pair<RootFamily, std::string_view>, 19> kNames = {{
    {RootFamily::Codex, "codex"},
    {RootFamily::Claude, "claude"},
    {RootFamily::DeepSeek, "dsh"},
    {RootFamily::OpenCode, "opencode"},
    {RootFamily::Gemini, "gemini"},
    {RootFamily::Omp, "omp"},
    {RootFamily::Antigravity, "antigravity"},
    {RootFamily::WorkBuddy, "workbuddy"},
    {RootFamily::WorkBuddyAi, "workbuddyAi"},
    {RootFamily::ZCode, "zcode"},
    {RootFamily::Kimi, "kimi"},
    {RootFamily::GrokBuild, "grokBuild"},
    {RootFamily::Pi, "pi"},
    {RootFamily::OpenClaw, "openclaw"},
    {RootFamily::Hermes, "hermes"},
    {RootFamily::Droid, "droid"},
    {RootFamily::Devin, "devin"},
    {RootFamily::Qoder, "qoder"},
    {RootFamily::QoderCli, "qoderCli"},
}};

const LocationList& automaticOnly() {
    static const LocationList list{DataLocation::automaticEntry()};
    return list;
}

std::optional<DataLocation> entryOf(const json::Value& value) {
    if (!value.isObject()) return std::nullopt;
    if (value["automatic"].isTrue()) return DataLocation::automaticEntry();
    if (const auto path = value["path"].string()) {
        if (auto standardized = LocationLists::standardized(*path)) return DataLocation::at(std::move(*standardized));
    }
    return std::nullopt;
}

std::string entryJson(const DataLocation& entry) {
    return entry.automatic ? std::string(R"({"automatic":true})") : R"({"path":)" + report::Json::quoted(entry.path) + "}";
}

}  // namespace

const std::vector<RootFamily>& locationListOrder() {
    static const std::vector<RootFamily> order = [] {
        std::vector<RootFamily> out;
        for (const auto& [family, name] : kNames) out.push_back(family);
        return out;
    }();
    return order;
}

std::string_view listName(RootFamily list) {
    for (const auto& [family, name] : kNames) {
        if (family == list) return name;
    }
    return {};
}

std::optional<RootFamily> listNamed(std::string_view name) {
    for (const auto& [family, known] : kNames) {
        if (known == name) return family;
    }
    return std::nullopt;
}

LocationLists::LocationLists(const RootSettings& s) {
    const std::pair<RootFamily, const std::optional<std::string>*> chosen[] = {
        {RootFamily::Codex, &s.codex},
        {RootFamily::Claude, &s.claude},
        {RootFamily::DeepSeek, &s.dsh},
        {RootFamily::OpenCode, &s.openCode},
        {RootFamily::Gemini, &s.gemini},
        {RootFamily::Omp, &s.omp},
        {RootFamily::Antigravity, &s.antigravity},
        {RootFamily::WorkBuddy, &s.workBuddy},
        {RootFamily::WorkBuddyAi, &s.workBuddyAi},
        {RootFamily::ZCode, &s.zcode},
        {RootFamily::Kimi, &s.kimi},
        {RootFamily::GrokBuild, &s.grokBuild},
        {RootFamily::Pi, &s.pi},
        {RootFamily::OpenClaw, &s.openClaw},
        {RootFamily::Hermes, &s.hermes},
        {RootFamily::Droid, &s.droid},
        {RootFamily::Devin, &s.devin},
        {RootFamily::Qoder, &s.qoder},
        {RootFamily::QoderCli, &s.qoderCli},
    };
    for (const auto& [family, path] : chosen) {
        if (*path) set(family, {DataLocation::at(**path)});
    }
}

LocationLists LocationLists::fromLegacy(const std::map<RootFamily, std::string>& paths) {
    LocationLists lists;
    for (const auto& [family, path] : paths) lists.set(family, {DataLocation::at(path)});
    return lists;
}

const LocationList& LocationLists::operator[](RootFamily list) const {
    const auto it = lists_.find(list);
    return it == lists_.end() ? automaticOnly() : it->second;
}

void LocationLists::set(RootFamily list, const LocationList& entries) {
    auto normal = normalized(entries);
    if (normal == automaticOnly()) {
        lists_.erase(list);
    } else {
        lists_[list] = std::move(normal);
    }
}

bool LocationLists::hasAutomatic(RootFamily list) const {
    const auto& entries = (*this)[list];
    return !entries.empty() && entries.front().automatic;
}

std::optional<std::string> LocationLists::standardized(std::string_view path) {
    if (path.empty() || path.front() != '/') return std::nullopt;
    return fsx::normalize(path);
}

LocationList LocationLists::normalized(const LocationList& list) {
    LocationList out;
    for (const auto& entry : list) {
        DataLocation next;
        if (entry.automatic) {
            next = DataLocation::automaticEntry();
        } else if (auto path = standardized(entry.path)) {
            next = DataLocation::at(std::move(*path));
        } else {
            continue;
        }
        if (std::find(out.begin(), out.end(), next) != out.end()) continue;
        if (next.automatic) {
            out.insert(out.begin(), std::move(next));
        } else {
            out.push_back(std::move(next));
        }
    }
    if (out.size() > kMaxEntries) out.resize(kMaxEntries);
    return out.empty() ? automaticOnly() : out;
}

std::string LocationLists::document() const {
    std::map<std::string, std::string> stored = unknown_;
    for (const auto& [family, entries] : lists_) {
        std::string text = "[";
        for (std::size_t i = 0; i < entries.size(); ++i) {
            if (i > 0) text += ',';
            text += entryJson(entries[i]);
        }
        text += ']';
        stored[std::string(listName(family))] = std::move(text);
    }
    std::string out = R"({"lists":{)";
    bool first = true;
    for (const auto& [name, text] : stored) {
        if (!first) out += ',';
        first = false;
        out += report::Json::quoted(name);
        out += ':';
        out += text;
    }
    out += R"(},"version":)";
    out += std::to_string(kDocumentVersion);
    out += '}';
    return out;
}

std::optional<LocationLists> LocationLists::fromDocument(std::string_view text) {
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) return std::nullopt;
    LocationLists out;
    const auto lists = document.root()["lists"];
    if (!lists.isObject()) return out;
    for (const auto& [name, value] : lists.members()) {
        const auto family = listNamed(name);
        if (!family) {
            out.unknown_[name] = std::string(value.raw());
            continue;
        }
        if (!value.isArray()) continue;
        LocationList entries;
        for (const auto& item : value.elements()) {
            if (auto entry = entryOf(item)) entries.push_back(std::move(*entry));
        }
        out.set(*family, entries);
    }
    return out;
}

}  // namespace agentspeed
