#include "orb/color.hpp"

#include "heat.hpp"

namespace orb {

Rgba heatColor(double h) { return Rgba::from(heat::color(std::isfinite(h) ? h : 0.0)); }

Tokens Tokens::forDark(bool dark) {
    Tokens t;
    t.dark = dark;
    if (dark) {
        t.bodyTop = Rgba::hex(0x1B2030);
        t.bodyBottom = Rgba::hex(0x090B10);
        t.bodyOpacity = 0.88;
        t.rimTop = Rgba::white(0.14f);
        t.outerStroke = Rgba::black(0.35f);
        t.shadow = Rgba::black(0.45f);
        t.shadowOffset = 0.03;
        t.shadowBlur = 0.10;
        t.unlitSegment = Rgba::white(0.07f);
        t.valueText = Rgba::white(0.96f);
        t.idleValueText = Rgba::white(0.60f);
        t.secondaryText = Rgba::white(0.55f);
        t.auraOpacity = 0.55;
        t.lens = Rgba::white(0.06f);
        t.innerShadow = Rgba::black(0.55f);
    } else {
        t.bodyTop = Rgba::hex(0xFFFFFF);
        t.bodyBottom = Rgba::hex(0xE7EBF3);
        t.bodyOpacity = 0.86;
        t.rimTop = Rgba::white(0.95f);
        t.outerStroke = Rgba::black(0.08f);
        t.shadow = Rgba::black(0.16f);
        t.shadowOffset = 0.04;
        t.shadowBlur = 0.12;
        t.unlitSegment = Rgba::black(0.08f);
        t.valueText = Rgba::hex(0x0B0D12, 0.92f);
        t.idleValueText = Rgba::hex(0x0B0D12, 0.55f);
        t.secondaryText = Rgba::black(0.50f);
        t.auraOpacity = 0.32;
        t.lens = Rgba::white(0.7f);
        t.innerShadow = Rgba::hex(0x1B2340, 0.16f);
    }
    return t;
}

}  // namespace orb
