#include "agentspeed/zcode_set.hpp"

#include <algorithm>

namespace agentspeed::zcode {

void merge(Update& into, Update other) {
    for (auto& response : other.responses) into.responses.push_back(std::move(response));
    for (auto& agent : other.agents) into.agents.push_back(std::move(agent));
    for (auto& id : other.removed) {
        const bool back = std::any_of(into.agents.begin(), into.agents.end(), [&](const Agent& a) { return a.id == id; });
        if (!back) into.removed.push_back(std::move(id));
    }
}

SourceSet::SourceSet(const std::vector<std::string>& roots, Deduper& deduper) : roots_(roots) {
    for (std::size_t i = 0; i < roots_.size(); ++i) {
        auto source = std::make_unique<Source>(roots_[i], deduper);
        source->setClaims(&claims_, static_cast<int>(i));
        sources_.push_back(std::move(source));
    }
}

bool SourceSet::available() const {
    return std::any_of(sources_.begin(), sources_.end(), [](const auto& s) { return s->available(); });
}

bool SourceSet::unsupported() const {
    return std::any_of(sources_.begin(), sources_.end(), [](const auto& s) { return s->unsupported(); });
}

std::string SourceSet::displayRoot() const {
    for (std::size_t i = 0; i < sources_.size(); ++i) {
        if (sources_[i]->available()) return roots_[i];
    }
    return roots_.empty() ? std::string() : roots_.front();
}

Update SourceSet::seed(std::int64_t todayStartMs) {
    Update update;
    for (auto& source : sources_) merge(update, source->seed(todayStartMs));
    return update;
}

Update SourceSet::refresh() {
    Update update;
    for (auto& source : sources_) merge(update, source->refresh());
    return update;
}

}  // namespace agentspeed::zcode
