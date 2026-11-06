#include "agentspeed/opencode_set.hpp"

#include <algorithm>

#include "agentspeed/fs_util.hpp"

namespace agentspeed::opencode {

void merge(Update& into, Update other) {
    for (auto& response : other.responses) into.responses.push_back(std::move(response));
    for (auto& [id, state] : other.agents) into.agents[id] = std::move(state);
    for (auto& id : other.removed) {
        if (!into.agents.contains(id)) into.removed.push_back(std::move(id));
    }
    into.reset = into.reset || other.reset;
}

SourceSet::SourceSet(const std::vector<std::string>& roots, Deduper& deduper) : roots_(roots) {
    for (std::size_t i = 0; i < roots_.size(); ++i) {
        auto source = std::make_unique<Source>(roots_[i], deduper);
        source->setClaims(&claims_, static_cast<int>(i));
        sources_.push_back(std::move(source));
    }
}

bool SourceSet::databaseExists() const {
    return std::any_of(sources_.begin(), sources_.end(), [](const auto& s) { return fsx::isRegularFile(s->databasePath()); });
}

std::string SourceSet::displayRoot() const {
    for (std::size_t i = 0; i < sources_.size(); ++i) {
        if (fsx::isRegularFile(sources_[i]->databasePath())) return roots_[i];
    }
    return roots_.empty() ? std::string() : roots_.front();
}

Update SourceSet::seed(std::int64_t todayStartMs) {
    Update update;
    for (auto& source : sources_) {
        source->refreshAvailability();
        merge(update, source->seed(todayStartMs));
    }
    return update;
}

std::optional<Update> SourceSet::refreshIfChanged() {
    std::optional<Update> update;
    for (auto& source : sources_) {
        auto next = source->refreshIfChanged();
        if (!next) continue;
        if (!update) update.emplace();
        merge(*update, std::move(*next));
    }
    return update;
}

SourceSet::Rediscovery SourceSet::rediscover(std::int64_t nowMs, std::int64_t todayStartMs) {
    Rediscovery out;
    for (auto& source : sources_) {
        auto next = source->rediscover(nowMs, todayStartMs);
        merge(next.reset ? out.seeded : out.live, std::move(next));
    }
    return out;
}

}  // namespace agentspeed::opencode
