#pragma once

#include <QString>
#include <QVariantList>
#include <memory>
#include <vector>

#include "agentspeed/character/hud_quota.hpp"

class Strings;

namespace orb {

// The 額度 card's content for one orb (DESIGN 2a.6, 額度): its sources as the card draws them, in source order with
// only the windows each account has, and the card as accessibility reads it.
struct CharacterQuota {
    using Sources = std::vector<agentspeed::character::QuotaSource>;

    std::shared_ptr<const Sources> sources = std::make_shared<const Sources>();
    // `a11y.orb.quota`: each source's name followed by its windows' `a11y.quota.window`, all joined by `list.separator`;
    // empty without a source.
    QString spoken;

    bool empty() const { return sources->empty(); }

    // From QuotaView::cardItems ({key, source, name, lines}). A remote host's source (DESIGN 13) keeps its host apart
    // from the name, so the card can set it after the name or on a line of its own.
    static CharacterQuota of(const QVariantList& items, const Strings& strings);
};

}  // namespace orb
