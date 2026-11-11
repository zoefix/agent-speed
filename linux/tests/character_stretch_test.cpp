// `Squash` stretches (+) or squashes (-) her whole figure over the feet, face included: past kHeld either way it reads
// as the art drawn out or pressed flat, so only an instant of a take-off or a landing goes there, and the crouch before
// a leap no longer than kCrouch (spec/CHARACTERS.md, Motion). Ported from macOS StretchTests.
#include <gtest/gtest.h>

#include <algorithm>
#include <set>
#include <string>
#include <vector>

#include "agentspeed/character/director.hpp"
#include "agentspeed/character/moves.hpp"
#include "agentspeed/character/pose_switch.hpp"
#include "character_support.hpp"

using namespace agentspeed::character;

namespace {

constexpr double kHeld = 0.2;
constexpr double kBrief = 0.25;
constexpr double kCrouch = 0.5;

double longest(const std::vector<double>& series, double dt, double sign = 1) {
    double run = 0, best = 0;
    for (double v : series) {
        run = sign * v > kHeld + 1e-9 ? run + dt : 0;
        best = std::max(best, run);
    }
    return best;
}

void expectInShape(const std::vector<double>& series, double dt, const std::string& name) {
    EXPECT_LT(longest(series, dt), kBrief) << name << " stretched";
    EXPECT_LT(longest(series, dt, -1), kCrouch) << name << " squashed";
}

std::vector<double> squash(const MotionSpec& m, double dt) {
    std::vector<double> out;
    const auto* keys = m.curves.find("Squash");
    if (!keys) return out;
    const Curve curve(*keys);
    for (int n = 0; n * dt <= m.duration; ++n) out.push_back(curve(n * dt));
    return out;
}

}  // namespace

TEST(CharacterStretch, NoActionHoldsHerDrawnOut) {
    const Side out = Side::Right, book = Side::Left;
    std::vector<MotionSpec> actions;
    for (const auto& name : moves::names()) actions.push_back(*moves::make(name, out, book));
    for (const auto& [name, needs] : moves::profile()) actions.push_back(*moves::profileMove(name, out, book));
    for (const auto& name : gestures::head()) actions.push_back(gestures::headGesture(name));
    for (const auto& name : gestures::face()) actions.push_back(gestures::faceGesture(name));
    actions.push_back(moves::sidestep(out, book, 0.5));
    actions.push_back(moves::peek(out, book, 1));
    actions.push_back(moves::pounce(out, book, 1));
    const double dt = 1.0 / 240;
    for (const auto& m : actions) expectInShape(squash(m, dt), dt, m.name);

    PoseSwitch swap;
    swap.request(1);
    std::vector<double> hop;
    while (swap.isRunning()) {
        const auto step = swap.update(dt);
        hop.push_back(step.output ? step.output->squash : 0);
    }
    expectInShape(hop, dt, "pose swap");
}

TEST(CharacterStretch, SheIsNeverHeldDrawnOutInAnyBand) {
    fixtures::JsonFile f(fixtures::fixtureDir() / "characters" / "director.json");
    const std::string text = R"({"signature": ["stretch", "yawn", "tiptoe", "curtsy", "hop", "pounce"], "bounce": 1.4})";
    agentspeed::json::Document doc;
    ASSERT_TRUE(doc.parse(text));
    Director director(Param::decodeList(f.root()["params"]), 7, Side::Right, Side::Left, -1, false,
                      MotionProfile::decode(doc.root()));
    const int slot = *director.space().slot("Squash");
    const std::vector<int> levels = {0, 0, 2, 6, 10, 13, 3, 0};
    const double dt = 1.0 / 60;
    const int frames = 240 * 60;
    std::set<std::string> played;
    std::vector<double> series;
    series.reserve(frames);
    for (int n = 0; n < frames; ++n) {
        director.setLevel(levels[static_cast<std::size_t>(n) * levels.size() / frames]);
        director.update(dt);
        series.push_back(director.values()[slot]);
        if (auto g = director.activeGesture()) played.insert(*g);
    }
    for (const char* g : {"stretch", "yawn", "tiptoe", "curtsy"}) EXPECT_TRUE(played.count(g)) << g;
    expectInShape(series, dt, "director");
}
