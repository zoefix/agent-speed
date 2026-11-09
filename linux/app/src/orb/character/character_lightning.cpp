#include "orb/character/character_lightning.hpp"

#include <algorithm>
#include <cmath>

#include "agentspeed/character/rng.hpp"

namespace orb {

using agentspeed::character::Rng;

CharacterLightning::CharacterLightning() {
    strikes_.reserve(8);
    path_.reserve(16);
    lastText_.reserve(8);
}

void CharacterLightning::update(double dt, const std::vector<agentspeed::character::Odometer::Digit>& digits, double heat,
                                Strength strength) {
    time_ += dt;
    strikes_.erase(std::remove_if(strikes_.begin(), strikes_.end(), [this](const Strike& s) { return time_ - s.start >= s.life; }),
                   strikes_.end());
    int low = INT32_MAX, high = -1;
    const int n = static_cast<int>(digits.size());
    for (int i = 0; i < n; ++i) {
        const int fromRight = n - 1 - i;
        const char now = digits[static_cast<std::size_t>(i)].current;
        const char before = fromRight < static_cast<int>(lastText_.size()) ? lastText_[lastText_.size() - 1 - static_cast<std::size_t>(fromRight)] : 0;
        if (now && before != now && (before || !lastText_.empty())) {
            low = std::min(low, fromRight);
            high = std::max(high, fromRight);
        }
    }
    lastText_.clear();
    for (const auto& d : digits) {
        if (d.current) lastText_.push_back(d.current);
    }
    if (high < 0 || strength == Strength::Off) return;
    count_ += 1;
    const double power = 0.35 + 0.65 * std::clamp(heat, 0.0, 1.0);
    const bool bridge = high > low && (strength == Strength::Standard || strength == Strength::Max);
    const double life = (high > 0 ? 0.3 : 0.2) * (strength == Strength::Soft ? 0.7 : 1);
    if (strikes_.size() >= 8) strikes_.erase(strikes_.begin());
    strikes_.push_back({low, high, time_, life, power, 0x5EED0000ULL + count_ * 0x9E3779B9ULL, bridge});
}

double CharacterLightning::flash(int fromRight) const {
    double best = 0;
    for (const auto& s : strikes_) {
        if (fromRight >= s.low && fromRight <= s.high) best = std::max(best, envelope(s) * s.power);
    }
    return best;
}

double CharacterLightning::envelope(const Strike& strike) const {
    const double age = time_ - strike.start;
    if (age < 0 || age >= strike.life) return 0;
    Rng rng(strike.seed ^ (static_cast<std::uint64_t>(age * 30) * 0xBF58476D1CE4E5B9ULL));
    const double flicker = 0.55 + 0.45 * rng.random();
    return flicker * std::pow(1 - age / strike.life, 1.4);
}

int CharacterLightning::boltCount(const Strike& strike, Strength strength) const {
    if (strength == Strength::Off || strength == Strength::FlashOnly) return 0;
    const int crackles = std::min(strike.high - strike.low + 1, 3) + (strength == Strength::Max && strike.power > 0.7 ? 1 : 0);
    return crackles + (strike.bridge ? 1 : 0);
}

bool CharacterLightning::bolt(const Strike& strike, int index, Strength strength, int digitCount, double advance, double cap) {
    path_.clear();
    if (index >= boltCount(strike, strength) || digitCount <= 0) return false;
    const double age = time_ - strike.start;
    Rng rng((strike.seed + static_cast<std::uint64_t>(index + 1) * 0x94D049BB133111EBULL) ^
            (static_cast<std::uint64_t>(std::max(0.0, age) * 30) * 0xD6E8FEB86659FD93ULL));
    auto center = [&](int fromRight) {
        const int i = std::clamp(digitCount - 1 - fromRight, 0, digitCount - 1);
        return -digitCount * advance / 2 + advance * (i + 0.5);
    };
    const int crackles = boltCount(strike, strength) - (strike.bridge ? 1 : 0);
    if (index < crackles) {
        const int span = strike.high - strike.low + 1;
        const double cx = center(strike.low + (index % span));
        const double rx = advance * 0.6, ry = cap * 0.8;
        const double start = rng.uniform(0, 2 * 3.14159265358979);
        const double sweep = rng.uniform(1.2, 2.2) * (rng.random() < 0.5 ? -1 : 1);
        constexpr int points = 9;
        for (int k = 0; k < points; ++k) {
            const double f = static_cast<double>(k) / (points - 1);
            const double a = start + sweep * f;
            const double jag = k == 0 || k == points - 1 ? 0 : rng.uniform(-0.2, 0.2);
            path_.emplace_back(cx + std::cos(a) * rx * (1 + jag), std::sin(a) * ry * (1 + jag));
        }
        return true;
    }
    const double left = center(strike.high) - advance * 0.5;
    const double right = center(strike.low) + advance * 0.5;
    const double lift = cap * rng.uniform(0.95, 1.2);
    constexpr int points = 12;
    for (int k = 0; k < points; ++k) {
        const double f = static_cast<double>(k) / (points - 1);
        const double x = left + (right - left) * f;
        const double y = -cap * 0.2 - 4 * f * (1 - f) * lift;
        const double jag = k == 0 || k == points - 1 ? 0 : rng.uniform(-0.22, 0.22) * cap;
        path_.emplace_back(x + rng.uniform(-0.08, 0.08) * advance, y + jag);
    }
    return true;
}

}  // namespace orb
