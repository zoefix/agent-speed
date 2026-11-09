// The shared fixtures of spec/CHARACTERS.md, section 9: every file matches the reference player.
#include <gtest/gtest.h>

#include <cmath>
#include <map>

#include "agentspeed/character/curve.hpp"
#include "agentspeed/character/director.hpp"
#include "agentspeed/character/hud.hpp"
#include "agentspeed/character/physics.hpp"
#include "agentspeed/character/pose_switch.hpp"
#include "agentspeed/character/rig.hpp"
#include "agentspeed/character/rng.hpp"
#include "agentspeed/character/springs.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::character;

namespace {

fixtures::JsonFile load(const char* name) {
    fixtures::JsonFile f(fixtures::fixtureDir() / "characters" / name);
    EXPECT_TRUE(f.ok) << name;
    return f;
}

double num(const json::Value& v) { return v.number().value_or(NAN); }

std::vector<double> nums(const json::Value& v) {
    std::vector<double> out;
    for (const auto& e : v.elements()) out.push_back(num(e));
    return out;
}

bool close(double a, double b, double tolerance = 1e-6) { return std::fabs(a - b) <= tolerance * std::max(1.0, std::fabs(b)); }

std::vector<CurveKey> curveKeys(const json::Value& v) {
    std::vector<CurveKey> out;
    for (const auto& k : v.elements()) out.push_back({num(k.at(0)), num(k.at(1))});
    return out;
}

}  // namespace

TEST(CharacterFixtures, RandomNumbersMatchSplitMix64) {
    auto f = load("rng.json");
    int checked = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        const auto seed = std::stoull(*c["seed"].string());
        Rng g(seed);
        for (const auto& e : c["u64"].elements()) EXPECT_EQ(g.nextUInt64(), std::stoull(*e.string()));
        g = Rng(seed);
        for (double expected : nums(c["random"])) EXPECT_TRUE(close(g.random(), expected, 1e-15));
        g = Rng(seed);
        EXPECT_TRUE(close(g.uniform(2, 5), num(c["uniform_2_5"]), 1e-15));
        g = Rng(seed);
        std::vector<int> items = {0, 1, 2, 3, 4, 5, 6, 7, 8, 9};
        g.shuffle(items);
        std::vector<int> expected;
        for (double d : nums(c["shuffle10"])) expected.push_back(static_cast<int>(d));
        EXPECT_EQ(items, expected);
        g = Rng(seed);
        std::vector<std::string> sample = g.sample(std::vector<std::string>{"a", "b", "c", "d", "e"}, 3);
        std::vector<std::string> want;
        for (const auto& e : c["sample_abcde_3"].elements()) want.push_back(*e.string());
        EXPECT_EQ(sample, want);
        ++checked;
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, CurvesMatchTheReference) {
    auto f = load("curves.json");
    int checked = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        Curve curve(curveKeys(c["keys"]), c["periodic"].isTrue());
        const auto xs = nums(c["x"]), ys = nums(c["y"]);
        for (std::size_t i = 0; i < xs.size(); ++i, ++checked) EXPECT_TRUE(close(curve(xs[i]), ys[i])) << "x=" << xs[i];
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, SpringsMatchTheReference) {
    auto f = load("springs.json");
    int checked = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        SecondOrder s(num(c["f"]), num(c["z"]), num(c["r"]), num(c["x0"]));
        const auto in = nums(c["inputs"]), out = nums(c["outputs"]);
        for (std::size_t i = 0; i < in.size(); ++i, ++checked) EXPECT_TRUE(close(s.update(num(c["dt"]), in[i]), out[i]));
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, OrbitsAndKeplerMatchTheReference) {
    auto f = load("orbits.json");
    int checked = 0;
    for (const auto& c : f.root()["orbits"].elements()) {
        Orbit o{num(c["a"]), num(c["e"]), num(c["period"]), num(c["phase"]), num(c["omega"]), num(c["height"]),
                num(c["tilt"]), num(c["precess"]), num(c["seed"])};
        const auto center = nums(c["center"]);
        const auto ts = nums(c["t"]);
        const auto spins = nums(c["spin"]);
        for (std::size_t i = 0; i < ts.size(); ++i, ++checked) {
            const auto p = o.at(ts[i], center[0], center[1]);
            const auto e = nums(c["xyDepth"].at(static_cast<std::uint32_t>(i)));
            EXPECT_TRUE(close(p.x, e[0]) && close(p.y, e[1]) && close(p.depth, e[2])) << "t=" << ts[i];
            EXPECT_TRUE(close(o.spin(ts[i]), spins[i]));
        }
    }
    for (const auto& k : f.root()["kepler"].elements()) {
        EXPECT_TRUE(close(Orbit::eccentricAnomaly(num(k["mean"]), num(k["e"])), num(k["E"])));
        ++checked;
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, RigMatchesTheReference) {
    auto f = load("rig.json");
    const Rig rig(RigDocument::decode(f.root()["rig"]));
    std::vector<Affine2> world;
    std::vector<double> opacity, skinned;
    int checked = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        std::vector<double> values;
        for (const auto& p : rig.params) {
            auto v = c["values"][p.id];
            values.push_back(v.isNumber() ? num(v) : p.def);
        }
        rig.worldTransforms(values, world);
        const auto expectedWorld = c["world"].elements();
        ASSERT_EQ(expectedWorld.size(), world.size());
        for (std::size_t i = 0; i < world.size(); ++i) {
            const auto e = nums(expectedWorld[i]);
            const auto& m = world[i];
            EXPECT_TRUE(close(m.a, e[0]) && close(m.b, e[1]) && close(m.tx, e[2]));
            EXPECT_TRUE(close(m.c, e[3]) && close(m.d, e[4]) && close(m.ty, e[5]));
        }
        const auto vertices = c["vertices"].elements();
        for (std::size_t i = 0; i < vertices.size(); ++i) {
            rig.skin(static_cast<int>(i), world, skinned);
            const auto e = nums(vertices[i]);
            ASSERT_EQ(e.size(), skinned.size());
            for (std::size_t k = 0; k < e.size(); ++k) EXPECT_TRUE(close(skinned[k], e[k])) << "part " << i;
        }
        rig.opacities(values, opacity);
        const auto eo = nums(c["opacity"]);
        ASSERT_EQ(eo.size(), opacity.size());
        for (std::size_t k = 0; k < eo.size(); ++k) EXPECT_TRUE(close(opacity[k], eo[k]));
        ++checked;
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, PhysicsMatchesTheReference) {
    auto f = load("physics.json");
    const auto spec = RigDocument::Chain::decode(f.root()["spec"]);
    const std::vector<std::string> names = {"Sway", "Lift", "Wind", "H1", "H2"};
    auto slot = [&](const std::string& n) -> std::optional<int> {
        auto it = std::find(names.begin(), names.end(), n);
        if (it == names.end()) return std::nullopt;
        return static_cast<int>(it - names.begin());
    };
    int checked = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        Physics physics({spec}, num(c["windSign"]), slot);
        std::vector<double> values(names.size(), 0);
        physics.reset(values);
        const double dt = num(c["dt"]);
        for (const auto& frame : c["frames"].elements()) {
            for (const auto& [k, v] : frame["in"].members()) values[static_cast<std::size_t>(*slot(k))] = num(v);
            physics.step(dt, values);
            physics.write(values);
            EXPECT_TRUE(close(values[3], num(frame["out"]["H1"])) && close(values[4], num(frame["out"]["H2"])));
            ++checked;
        }
    }
    EXPECT_GT(checked, 0);
}

TEST(CharacterFixtures, OdometerMatchesTheReference) {
    auto f = load("odometer.json");
    const double dt = num(f.root()["dt"]);
    Odometer odo;
    const auto targets = nums(f.root()["targets"]);
    const auto frames = f.root()["frames"].elements();
    ASSERT_EQ(targets.size(), frames.size());
    for (std::size_t n = 0; n < frames.size(); ++n) {
        const std::string text = odo.update(dt, targets[n]);
        EXPECT_EQ(text, *frames[n]["text"].string());
        const auto digits = frames[n]["digits"].elements();
        ASSERT_EQ(digits.size(), odo.digits().size());
        for (std::size_t i = 0; i < digits.size(); ++i) {
            const auto& d = odo.digits()[i];
            auto str = [](char ch) { return ch ? std::optional<std::string>(std::string(1, ch)) : std::nullopt; };
            EXPECT_EQ(str(d.current), digits[i].at(0).string());
            EXPECT_EQ(str(d.previous), digits[i].at(1).string());
            EXPECT_TRUE(close(d.t, num(digits[i].at(2))));
        }
    }
}

TEST(CharacterFixtures, PoseSwitchMatchesTheReference) {
    auto f = load("switch.json");
    const double dt = num(f.root()["dt"]);
    PoseSwitch s;
    s.request(1);
    int checked = 0;
    for (const auto& frame : f.root()["frames"].elements()) {
        const auto step = s.update(dt);
        const auto expected = frame["values"];
        EXPECT_EQ(step.swap.has_value(), frame["swap"].isTrue());
        if (expected.size() == 0) {
            EXPECT_FALSE(step.output.has_value());
            continue;
        }
        ASSERT_TRUE(step.output.has_value());
        EXPECT_TRUE(close(step.output->spin, num(expected["Spin"])));
        EXPECT_TRUE(close(step.output->squash, num(expected["Squash"])));
        EXPECT_TRUE(close(step.output->posY, num(expected["PosY"])));
        EXPECT_TRUE(close(step.output->skirtFlare, num(expected["SkirtFlare"])));
        ++checked;
    }
    EXPECT_GT(checked, 0);
}

namespace {

// Plays a director fixture case for 42 s at 60 fps and returns the mismatches (at most 20).
std::vector<std::string> playDirector(const json::Value& root, const json::Value& c, bool profiles, int& compared) {
    const auto params = Param::decodeList(root["params"]);
    std::vector<std::pair<double, int>> schedule;
    for (const auto& e : root["schedule"].elements()) schedule.emplace_back(num(e.at(0)), static_cast<int>(num(e.at(1))));
    std::vector<std::pair<double, std::string>> sides;
    if (profiles) {
        for (const auto& e : root["poseSides"].elements()) sides.emplace_back(num(e.at(0)), *e.at(1).string());
    }
    const Side out = *sideNamed(*c["armOut"].string()), book = *sideNamed(*c["armBook"].string());
    const MotionProfile profile = profiles ? MotionProfile::decode(c["profile"]) : MotionProfile();
    Director d(params, static_cast<std::uint64_t>(num(c["seed"])), out, book, num(c["orb"]), false, profile);
    const auto frames = c["frames"].elements();
    std::size_t next = 0;
    std::vector<std::string> mismatches;
    for (int n = 0; n < 60 * 42; ++n) {
        const double t = n / 60.0;
        int level = 0;
        for (const auto& [at, l] : schedule) {
            if (t >= at) level = l;
        }
        d.setLevel(level);
        if (!sides.empty() && t >= sides.front().first) {
            d.setSides(out, book, *bandNamed(sides.front().second));
            sides.erase(sides.begin());
        }
        d.update(1.0 / 60);
        if (next >= frames.size() || static_cast<int>(num(frames[next]["n"])) != n) continue;
        const auto frame = frames[next++];
        const auto gesture = frame["gesture"].string();
        if (d.activeGesture() != gesture) {
            mismatches.push_back("n=" + std::to_string(n) + " gesture " + d.activeGesture().value_or("-") +
                                 " != " + gesture.value_or("-"));
        }
        for (const auto& [k, v] : frame["values"].members()) {
            const auto mine = d.value(k);
            ++compared;
            if (!mine || std::fabs(*mine - num(v)) > 1e-4) {
                mismatches.push_back("n=" + std::to_string(n) + " " + k + " " + std::to_string(mine.value_or(NAN)) +
                                     " != " + std::to_string(num(v)));
            }
        }
        if (mismatches.size() > 20) break;
    }
    if (next != frames.size()) mismatches.push_back("frames compared " + std::to_string(next));
    return mismatches;
}

std::string joined(const std::vector<std::string>& v) {
    std::string out;
    for (const auto& s : v) out += s + "\n";
    return out;
}

}  // namespace

TEST(CharacterFixtures, DirectorMatchesTheReference) {
    auto f = load("director.json");
    int compared = 0, cases = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        const auto mismatches = playDirector(f.root(), c, false, compared);
        EXPECT_TRUE(mismatches.empty()) << "case " << cases << "\n" << joined(mismatches);
        ++cases;
    }
    EXPECT_EQ(cases, 2);
    EXPECT_GT(compared, 1000);
}

TEST(CharacterFixtures, DirectorProfilesMatchTheReference) {
    auto f = load("director-profiles.json");
    int compared = 0, cases = 0;
    for (const auto& c : f.root()["cases"].elements()) {
        const auto mismatches = playDirector(f.root(), c, true, compared);
        EXPECT_TRUE(mismatches.empty()) << c["name"].string().value_or("?") << "\n" << joined(mismatches);
        ++cases;
    }
    EXPECT_EQ(cases, 3);
    EXPECT_GT(compared, 1000);
}
