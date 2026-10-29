#include "orb/style.hpp"

#include "orb/led_style.hpp"
#include "orb/liquid_style.hpp"
#include "orb/minimal_style.hpp"
#include "orb/needle_style.hpp"
#include "orb/reactor_style.hpp"

namespace orb {

QString styleKey(StyleKind kind) {
    switch (kind) {
    case StyleKind::Led: return QStringLiteral("led");
    case StyleKind::Needle: return QStringLiteral("needle");
    case StyleKind::Liquid: return QStringLiteral("liquid");
    case StyleKind::Reactor: return QStringLiteral("reactor");
    case StyleKind::Minimal: return QStringLiteral("minimal");
    }
    return QStringLiteral("led");
}

StyleKind styleFromKey(const QString& key) {
    for (const auto kind : kStyles) {
        if (styleKey(kind) == key) return kind;
    }
    return StyleKind::Led;
}

std::unique_ptr<Style> Style::make(StyleKind kind) {
    switch (kind) {
    case StyleKind::Led: return std::make_unique<LedStyle>();
    case StyleKind::Needle: return std::make_unique<NeedleStyle>();
    case StyleKind::Liquid: return std::make_unique<LiquidStyle>();
    case StyleKind::Reactor: return std::make_unique<ReactorStyle>();
    case StyleKind::Minimal: return std::make_unique<MinimalStyle>();
    }
    return std::make_unique<LedStyle>();
}

}  // namespace orb
