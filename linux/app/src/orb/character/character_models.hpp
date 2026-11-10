#pragma once

#include <QString>
#include <map>
#include <string>
#include <vector>

#include "agentspeed/character/hud_models.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "orb/character/character_cards.hpp"
#include "orb/scene.hpp"

namespace orb {

// The character orb's models card (DESIGN 2a.6, Models): per line the vendor's dot, the display name in the value token
// cut at its end to the name width, and the speed right-aligned in tabular digits (`–` in the secondary token without
// one), `tok/s` once after the first line's number; then the "+N" line. Lines glide and fade by ModelsMotion; HudWings
// picks its place and its name width from `shape`.
class ModelsCard {
public:
    struct Texts {
        QString more;  // character.models.more (`+%@ 個模型`)
        QString unit;  // unit.tokS
    };
    struct Ink {
        Rgba number, secondary;
    };

    void sync(const std::vector<agentspeed::character::ModelRow>& rows, int limit, double time, bool still);
    // The widths and height the layout places it by.
    agentspeed::character::ModelsShape shape(const cards::CardMetrics& m, const Texts& texts) const;
    // Its lines inside a plate of `size` centered on the pen.
    void draw(Scene& scene, const cards::Pen& pen, agentspeed::character::CardSize size, const agentspeed::character::ModelsForm& form,
              const cards::CardMetrics& m, const Texts& texts, const Ink& ink, double time);
    bool moving(double time) const { return motion_.moving(time); }
    bool empty() const { return motion_.count() == 0; }
    const agentspeed::character::ModelsMotion& motion() const { return motion_; }

private:
    struct Columns {
        double number = 0, unit = 0, names = 0;
    };
    Columns columns(const cards::CardMetrics& m, const Texts& texts) const;
    const QString& displayName(const std::string& model) const;
    QString moreText(const QString& pattern, int count, bool names) const;

    agentspeed::character::ModelsMotion motion_;
    mutable std::map<std::string, QString> names_;
};

}  // namespace orb
