#include "agentspeed/host_selection.hpp"

#include <algorithm>
#include <map>
#include <set>

#include "agentspeed/report_json.hpp"

namespace agentspeed::hosts {

namespace {

constexpr std::string_view kLocalId = "local";

bool contains(const std::vector<std::string>& ids, std::string_view id) { return std::find(ids.begin(), ids.end(), id) != ids.end(); }

}  // namespace

HostSelection HostSelection::normalized(const std::vector<std::string>& ids) {
    std::set<std::string> seen;
    std::vector<std::string> unique;
    for (const auto& id : ids) {
        if (!id.empty() && seen.insert(id).second) unique.push_back(id);
    }
    HostSelection selection;
    if (contains(unique, kLocalId)) selection.ids_.emplace_back(kLocalId);
    for (const auto& id : unique) {
        if (id != kLocalId) selection.ids_.push_back(id);
    }
    return selection;
}

HostSelection HostSelection::fromJson(const json::Value& value) {
    if (const auto text = value.string()) return *text == "all" ? all() : normalized({*text});
    std::vector<std::string> ids;
    if (value.isArray()) {
        for (const auto& item : value.elements()) {
            if (const auto id = item.string()) ids.push_back(*id);
        }
    }
    return normalized(ids);
}

std::string HostSelection::json() const {
    if (isAll()) return "\"all\"";
    std::string out = "[";
    for (std::size_t k = 0; k < ids_.size(); ++k) {
        if (k) out += ',';
        out += report::Json::quoted(ids_[k]);
    }
    return out + "]";
}

std::optional<std::vector<std::string>> HostSelection::hostSet() const {
    if (isAll()) return std::nullopt;
    return ids_;
}

bool HostSelection::tracks(std::string_view hostId) const { return isAll() || contains(ids_, hostId); }

std::optional<HostSelection> HostSelection::toggling(const std::string& hostId) const {
    if (isAll()) return normalized({hostId});
    if (contains(ids_, hostId)) {
        std::vector<std::string> remaining;
        for (const auto& id : ids_) {
            if (id != hostId) remaining.push_back(id);
        }
        if (remaining.empty()) return std::nullopt;
        return normalized(remaining);
    }
    auto added = ids_;
    added.push_back(hostId);
    return normalized(added);
}

std::optional<HostSelection> HostSelection::toggling(const std::string& hostId, const std::vector<std::string>& known) const {
    if (isAll()) return normalized({hostId}).ordered(known);
    if (contains(ids_, hostId)) {
        std::vector<std::string> remaining;
        for (const auto& id : ids_) {
            if (id != hostId) remaining.push_back(id);
        }
        if (std::none_of(remaining.begin(), remaining.end(), [&](const std::string& id) { return contains(known, id); })) return std::nullopt;
        return normalized(remaining).ordered(known);
    }
    auto added = ids_;
    added.push_back(hostId);
    return normalized(added).ordered(known);
}

HostSelection HostSelection::removing(const std::string& hostId) const {
    if (isAll()) return *this;
    std::vector<std::string> remaining;
    for (const auto& id : ids_) {
        if (id != hostId) remaining.push_back(id);
    }
    return normalized(remaining);
}

HostSelection HostSelection::ordered(const std::vector<std::string>& order) const {
    if (isAll()) return *this;
    std::map<std::string, std::size_t> rank;
    for (std::size_t k = 0; k < order.size(); ++k) rank.try_emplace(order[k], k);
    std::vector<std::string> known, unknown;
    for (const auto& id : ids_) (rank.contains(id) ? known : unknown).push_back(id);
    std::stable_sort(known.begin(), known.end(), [&](const std::string& a, const std::string& b) { return rank[a] < rank[b]; });
    known.insert(known.end(), unknown.begin(), unknown.end());
    return normalized(known);
}

std::vector<HostEntry> HostSelection::entries(const std::vector<HostChoice>& choices) const {
    std::size_t checked = 0;
    for (const auto& choice : choices) checked += !isAll() && tracks(choice.id) ? 1 : 0;
    std::vector<HostEntry> out;
    out.reserve(choices.size());
    for (const auto& choice : choices) {
        const bool isChecked = !isAll() && tracks(choice.id);
        out.push_back({choice.id, choice.name, isChecked, !(isChecked && checked == 1)});
    }
    return out;
}

bool pickerShown(const std::vector<HostChoice>& choices) {
    return std::any_of(choices.begin(), choices.end(), [](const HostChoice& c) { return c.id != kLocalId; });
}

std::optional<NameSuffix> nameSuffix(const HostSelection& selection, const std::vector<HostChoice>& choices) {
    if (selection.isAll() || !pickerShown(choices)) return std::nullopt;
    std::optional<NameSuffix> out;
    for (const auto& choice : choices) {
        if (!selection.tracks(choice.id)) continue;
        if (!out) out = NameSuffix{choice.name, 0};
        else ++out->others;
    }
    return out;
}

}  // namespace agentspeed::hosts
