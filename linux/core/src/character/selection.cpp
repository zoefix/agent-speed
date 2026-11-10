#include "agentspeed/character/selection.hpp"

#include <algorithm>
#include <cmath>
#include <set>

namespace agentspeed::character {

bool isValidCharacterID(std::string_view id) {
    if (id.empty() || id.size() > 64 || id.front() == '-' || id.back() == '-') return false;
    return std::all_of(id.begin(), id.end(), [](char c) { return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'; });
}

std::optional<std::string> cleanCharacterID(std::string_view id) {
    const auto isSpace = [](char c) { return c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'; };
    while (!id.empty() && isSpace(id.front())) id.remove_prefix(1);
    while (!id.empty() && isSpace(id.back())) id.remove_suffix(1);
    if (!isValidCharacterID(id)) return std::nullopt;
    return std::string(id);
}

const char* key(CharacterReadout r) {
    switch (r) {
        case CharacterReadout::Intelligence: return "intelligence";
        case CharacterReadout::Thinking: return "thinking";
        case CharacterReadout::Latency: return "latency";
        case CharacterReadout::Network: return "network";
        case CharacterReadout::Cost: return "cost";
        case CharacterReadout::Agents: return "agents";
        case CharacterReadout::Models: return "models";
        case CharacterReadout::Quota: return "quota";
        case CharacterReadout::Model: return "model";
    }
    return "model";
}

const char* titleKey(CharacterReadout r) {
    switch (r) {
        case CharacterReadout::Intelligence: return "character.readouts.intel";
        case CharacterReadout::Thinking: return "character.readouts.thinking";
        case CharacterReadout::Latency: return "character.readouts.latency";
        case CharacterReadout::Network: return "character.readouts.network";
        case CharacterReadout::Cost: return "character.readouts.cost";
        case CharacterReadout::Agents: return "character.readouts.agents";
        case CharacterReadout::Models: return "character.readouts.models";
        case CharacterReadout::Quota: return "character.readouts.quota";
        case CharacterReadout::Model: return "character.readouts.model";
    }
    return "character.readouts.model";
}

bool CharacterReadouts::get(CharacterReadout r) const {
    switch (r) {
        case CharacterReadout::Intelligence: return intelligence;
        case CharacterReadout::Thinking: return thinking;
        case CharacterReadout::Latency: return latency;
        case CharacterReadout::Network: return network;
        case CharacterReadout::Cost: return cost;
        case CharacterReadout::Agents: return agents;
        case CharacterReadout::Models: return models;
        case CharacterReadout::Quota: return quota;
        case CharacterReadout::Model: return model;
    }
    return false;
}

void CharacterReadouts::set(CharacterReadout r, bool on) {
    switch (r) {
        case CharacterReadout::Intelligence: intelligence = on; break;
        case CharacterReadout::Thinking: thinking = on; break;
        case CharacterReadout::Latency: latency = on; break;
        case CharacterReadout::Network: network = on; break;
        case CharacterReadout::Cost: cost = on; break;
        case CharacterReadout::Agents: agents = on; break;
        case CharacterReadout::Models: models = on; break;
        case CharacterReadout::Quota: quota = on; break;
        case CharacterReadout::Model: model = on; break;
    }
}

std::optional<std::string> CharacterSelection::update(const std::optional<std::string>& locked,
                                                      const std::map<std::string, double>& vendorRates,
                                                      const std::vector<Pack>& installed, double now) {
    if (installed.empty()) {
        shown_.reset();
        candidate_.reset();
        return std::nullopt;
    }
    std::set<std::string> ids;
    for (const auto& p : installed) ids.insert(p.id);
    if (locked && ids.count(*locked)) {
        shown_ = locked;
        candidate_.reset();
        return shown_;
    }
    if (shown_ && !ids.count(*shown_)) shown_.reset();
    const auto wanted = fastest(vendorRates, installed);
    if (!wanted) {
        candidate_.reset();
        if (!shown_) shown_ = installed[0].id;
        return shown_;
    }
    if (!shown_) {
        shown_ = wanted;
        candidate_.reset();
        return shown_;
    }
    if (*wanted == *shown_) {
        candidate_.reset();
    } else if (candidate_ != wanted) {
        candidate_ = wanted;
        candidateSince_ = now;
    } else if (now - candidateSince_ >= kHold) {
        shown_ = wanted;
        candidate_.reset();
    }
    return shown_;
}

std::optional<std::string> CharacterSelection::fastest(const std::map<std::string, double>& vendorRates,
                                                       const std::vector<Pack>& installed) {
    std::vector<std::pair<std::string, double>> live;
    for (const auto& [vendor, rate] : vendorRates) {
        if (rate > 0 && std::isfinite(rate)) live.emplace_back(vendor, rate);
    }
    std::sort(live.begin(), live.end(), [](const auto& a, const auto& b) {
        return a.second != b.second ? a.second > b.second : a.first < b.first;
    });
    for (const auto& [vendor, rate] : live) {
        for (const auto& pack : installed) {
            if (std::find(pack.vendors.begin(), pack.vendors.end(), vendor) != pack.vendors.end()) return pack.id;
        }
    }
    return std::nullopt;
}

namespace footprint {

bool characterContains(double dx, double dy, double d, double tolerance) {
    const double radius = kCharacterWidth * d / 2 + tolerance;
    const double half = std::max(0.0, kCharacterHeight * d / 2 + tolerance - radius);
    const double y = -dy + kCharacterDrop * d;
    const double nearest = std::min(std::max(y, -half), half);
    return std::hypot(dx, y - nearest) <= radius;
}

Box characterBounds(double d) {
    const double w = kCharacterWidth * d, h = kCharacterHeight * d;
    return {-w / 2, -h / 2 + kCharacterDrop * d, w, h};
}

}  // namespace footprint
}  // namespace agentspeed::character
