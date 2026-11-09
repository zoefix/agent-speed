#pragma once

#include <array>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/character/rng.hpp"

namespace orb {

// A character's particles (CHARACTERS 8, Particles; characters/reference/effects.py): one kind per character, 55 %
// in front of her, puffs of 14 at take-offs, wing flaps, pounces and pose swaps. A pool of 160, sized once.
class CharacterParticles {
public:
    enum class Look { Petal, Ring, Star, Wisp, Chip, Feather, Dot, Glyph };
    enum class Origin { Body, Top, Head, Feet };
    struct Kind {
        Look look;
        double base, gain, floor, power;
        bool inverse;
        Origin origin;
        double vx0, vx1, vy0, vy1, wind, wobbleAmplitude, wobbleFrequency, size0, size1, grow, life0, life1, spin0, spin1;
        std::array<const char*, 2> glyphs;
        bool glow;
        bool additive() const { return glow || look == Look::Star || look == Look::Wisp; }
    };
    struct Particle {
        double x, y, vx, vy, rotation, spin, size, age, life, phase;
        bool front;
        std::array<float, 3> color;
        int glyph = 0;
        bool burst = false;
    };
    // A soft dot of a particle: center, radii, rotation (radians), straight color and alpha, softness.
    struct Dot {
        double x, y, rx, ry, rotation;
        std::array<float, 3> color;
        double alpha, softness;
    };

    static constexpr std::size_t kLimit = 160;

    CharacterParticles();
    // The pack's kind (`motion.particles`, default petals; a pose without the petal sprite gets sparkles).
    void use(const std::string& packID, const std::optional<std::string>& particles, const std::string& accent, bool hasSprite);
    void update(double dt, double intensity, std::array<double, 4> area, double unit, double rate);
    void burst(double x, double y, double unit, int count = 14);
    void clear();

    bool isPetals() const { return !kind_; }
    // Bursts and wind-blown petals ask for 60 fps.
    bool isLively() const;
    const std::vector<Particle>& items() const { return items_; }
    const Kind* kind() const { return kind_ ? &*kind_ : nullptr; }
    double alpha(const Particle& p) const;
    double size(const Particle& p) const;
    // Appends the dots of the particles on one side (every look but glyphs and petals).
    void dots(bool front, double opacity, std::vector<Dot>& out) const;
    double glyphCeiling(double unit) const;

private:
    Particle spawn(const Kind& kind, double intensity, std::array<double, 4> area, double unit, const double* at = nullptr);
    void updatePetals(double dt, double intensity, std::array<double, 4> area, double unit, double rate);
    void burstPetals(double x, double y, double unit, int count);

    std::vector<Particle> items_;
    std::optional<Kind> kind_;
    std::vector<std::array<float, 3>> colors_;
    std::string packID_;
    agentspeed::character::Rng rng_;
    double carry_ = 0;
};

}  // namespace orb
