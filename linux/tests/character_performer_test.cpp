// Packs, the performer, the stage, Reduce Motion and the readout lights, ported from macOS PerformerTests/CalmTests.
#include <gtest/gtest.h>

#include <chrono>
#include <cstdlib>
#include <set>

#include "agentspeed/character/error.hpp"
#include "agentspeed/character/performer.hpp"
#include "character_support.hpp"

using namespace agentspeed::character;
using Kind = CharacterError::Kind;

namespace {

std::optional<CharacterError> loadError(const std::filesystem::path& root) {
    try {
        (void)Pack::load(root);
    } catch (const CharacterError& e) {
        return e;
    }
    return std::nullopt;
}

std::vector<Param> directorParams() {
    fixtures::JsonFile f(fixtures::fixtureDir() / "characters" / "director.json");
    return Param::decodeList(f.root()["params"]);
}

int slotOf(const Performer& p, const char* name) { return *p.director().space().slot(name); }

}  // namespace

TEST(CharacterPack, LoadsPosesInBandOrderAndFallsBackToTheNearestLowerBand) {
    const auto rig = tiny_pack::rigJSON();
    auto pack = Pack::load(tiny_pack::make({{"idle", rig}, {"boost", rig}, {"redline", rig}}));
    ASSERT_EQ(pack->poses.size(), 3u);
    EXPECT_EQ(pack->poses[0].band, Band::Idle);
    EXPECT_EQ(pack->poses[1].band, Band::Boost);
    EXPECT_EQ(pack->poses[2].band, Band::Redline);
    Performer performer(pack);
    EXPECT_EQ(performer.poseIndex(Band::Idle), 0);
    EXPECT_EQ(performer.poseIndex(Band::Cruise), 0);
    EXPECT_EQ(performer.poseIndex(Band::Boost), 1);
    EXPECT_EQ(performer.poseIndex(Band::Overdrive), 1);
    EXPECT_EQ(performer.poseIndex(Band::Redline), 2);
}

TEST(CharacterPack, RefusesPacksThatAreUnsafeOrIncomplete) {
    const auto rig = tiny_pack::rigJSON();
    auto e = loadError(tiny_pack::make({{"boost", rig}}));
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::MissingIdlePose);
    tiny_pack::ManifestOptions o;
    o.idleFolder = "../outside";
    e = loadError(tiny_pack::make({{"idle", rig}}, o));
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::UnsafePath);
    EXPECT_EQ(e->detail(), "../outside");
    o = {};
    o.preview = "/etc";
    e = loadError(tiny_pack::make({{"idle", rig}}, o));
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::UnsafePath);
    EXPECT_EQ(e->detail(), "/etc");
    o = {};
    o.id = "Bad ID";
    e = loadError(tiny_pack::make({{"idle", rig}}, o));
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::BadID);
    o = {};
    o.version = 2;
    e = loadError(tiny_pack::make({{"idle", rig}}, o));
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::Unsupported);
}

TEST(CharacterPack, RefusesSymbolicLinksThatLeaveThePack) {
    const auto root = tiny_pack::make({{"idle", tiny_pack::rigJSON()}});
    const auto outside = tiny_pack::tempDir("outside");
    std::filesystem::remove(root / "preview.png");
    std::filesystem::create_directory_symlink(outside, root / "preview.png");
    auto e = loadError(root);
    ASSERT_TRUE(e);
    EXPECT_EQ(e->kind(), Kind::UnsafePath);
    EXPECT_EQ(e->detail(), "preview.png");
}

TEST(CharacterPack, ComparesTheMinimumAppVersion) {
    tiny_pack::ManifestOptions o;
    o.minApp = "1.7.0";
    auto pack = Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}}, o));
    EXPECT_TRUE(pack->manifest.playable("1.7.0"));
    EXPECT_TRUE(pack->manifest.playable("1.10"));
    EXPECT_FALSE(pack->manifest.playable("1.6.3"));
}

TEST(CharacterPerformer, PosesAlignTheirFeetAndFaceToFeetDistance) {
    tiny_pack::RigOptions tall;
    tall.feetY = 95;
    tall.faceTop = -45;
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}, {"boost", tiny_pack::rigJSON(tall)}})));
    EXPECT_EQ(performer.placements()[0].scale, 1);
    EXPECT_LT(std::fabs(performer.placements()[1].scale - 70.0 / 130.0), 1e-12);
    const auto feet = performer.placements()[1].screen(50, 95, 200, 300, 2);
    EXPECT_EQ(feet.first, 200);
    EXPECT_EQ(feet.second, 300);
}

TEST(CharacterPerformer, AJumpWithTuckedLegsKeepsItsSizeByTheMiddleRatio) {
    tiny_pack::RigOptions idle, jump;
    idle.torsoLength = 50;
    jump.feetY = 60;
    jump.torsoLength = 48;
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON(idle)}, {"redline", tiny_pack::rigJSON(jump)}})));
    EXPECT_LT(std::fabs(performer.placements()[1].scale - 50.0 / 48.0), 1e-12);
    EXPECT_EQ(Performer::poseScale({70.0, 28.0, std::nullopt}, {40.0, 28.0, std::nullopt}), 70.0 / 40.0);
    EXPECT_EQ(Performer::poseScale({std::nullopt, 28.0, 50.0}, {std::nullopt, 28.0, 50.0}), 1);
}

TEST(CharacterPerformer, SwapsThePoseEdgeOnWhenTheBandChanges) {
    tiny_pack::RigOptions other;
    other.objectSide = "right";
    other.name = "other";
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}, {"boost", tiny_pack::rigJSON(other)}})));
    const int spin = slotOf(performer, "Spin");
    for (int n = 0; n < 60; ++n) performer.step(1.0 / 60, 0);
    EXPECT_EQ(performer.activePose(), 0);
    std::optional<double> swappedAt;
    for (int n = 0; n < 60; ++n) {
        performer.step(1.0 / 60, 6);
        for (const auto& e : performer.events()) {
            if (e == PerformerEvent{PerformerEvent::Kind::PoseSwapped, Band::Boost}) swappedAt = performer.values()[spin];
        }
    }
    EXPECT_EQ(performer.activePose(), 1);
    ASSERT_TRUE(swappedAt);
    EXPECT_EQ(*swappedAt, 0.75);
    EXPECT_EQ(performer.director().book(), Side::Right);
    EXPECT_EQ(performer.values()[spin], 0);
}

TEST(CharacterStage, TurnsFromOneCharacterToAnotherEdgeOn) {
    const auto rig = tiny_pack::rigJSON();
    tiny_pack::ManifestOptions a, b;
    a.id = "a";
    b.id = "b";
    Performer pa(Pack::load(tiny_pack::make({{"idle", rig}}, a)));
    Performer pb(Pack::load(tiny_pack::make({{"idle", rig}, {"boost", rig}}, b)));
    Stage stage(std::move(pa));
    for (int n = 0; n < 30; ++n) stage.step(1.0 / 60, 6);
    stage.switchTo(std::move(pb));
    std::optional<int> swapFrame;
    for (int n = 0; n < 60; ++n) {
        stage.step(1.0 / 60, 6);
        if (stage.swapped()) {
            swapFrame = n;
            EXPECT_EQ(stage.current().pack().id(), "b");
            EXPECT_EQ(stage.current().activePose(), 1);
            EXPECT_EQ(stage.current().values()[slotOf(stage.current(), "Spin")], 0.75);
        }
    }
    ASSERT_TRUE(swapFrame);
    EXPECT_TRUE(*swapFrame >= 11 && *swapFrame <= 13) << *swapFrame;
    EXPECT_FALSE(stage.incoming().has_value());
}

TEST(CharacterCalm, PoseSwapsCrossfadeWithoutTurning) {
    tiny_pack::RigOptions other;
    other.objectSide = "right";
    other.name = "other";
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}, {"boost", tiny_pack::rigJSON(other)}})));
    performer.setCalm(true);
    const int spin = slotOf(performer, "Spin");
    for (int n = 0; n < 30; ++n) performer.step(1.0 / 60, 0);
    double maxSpin = 0;
    bool faded = false;
    for (int n = 0; n < 60; ++n) {
        performer.step(1.0 / 60, 6);
        maxSpin = std::max(maxSpin, performer.values()[spin]);
        if (performer.fadingPose() == 0) faded = true;
    }
    EXPECT_EQ(performer.activePose(), 1);
    EXPECT_TRUE(faded);
    EXPECT_EQ(maxSpin, 0);
    EXPECT_FALSE(performer.fadingPose().has_value());
    EXPECT_EQ(performer.fadeProgress(), 1);
}

TEST(CharacterCalm, CharacterChangesCrossfade) {
    const auto rig = tiny_pack::rigJSON();
    tiny_pack::ManifestOptions a, b;
    a.id = "a";
    b.id = "b";
    Stage stage(Performer(Pack::load(tiny_pack::make({{"idle", rig}}, a))));
    stage.calm = true;
    stage.step(1.0 / 60, 2);
    stage.switchTo(Performer(Pack::load(tiny_pack::make({{"idle", rig}}, b))));
    stage.step(1.0 / 60, 2);
    EXPECT_TRUE(stage.swapped());
    EXPECT_EQ(stage.current().pack().id(), "b");
    ASSERT_TRUE(stage.outgoing().has_value());
    EXPECT_EQ(stage.outgoing()->pack().id(), "a");
    for (int n = 0; n < 30; ++n) stage.step(1.0 / 60, 2);
    EXPECT_FALSE(stage.outgoing().has_value());
}

TEST(CharacterCalm, GroundedDirectorNeverLeavesTheGround) {
    Director director(directorParams(), 7, Side::Right, Side::Left, 1, false);
    director.grounded = true;
    std::set<std::string> played;
    for (int n = 0; n < 60 * 60; ++n) {
        director.setLevel(std::min(13, (n / 300) % 14));
        director.update(1.0 / 60);
        if (auto g = director.activeGesture()) played.insert(*g);
    }
    for (const char* g : {"hop", "jump", "hops", "spin", "step", "run", "jumpjoy"}) EXPECT_FALSE(played.count(g)) << g;
    EXPECT_FALSE(played.empty());
}

TEST(CharacterCalm, ZeroAmplitudeKeepsTheRestPoseButBreathes) {
    Performer performer(Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}})));
    performer.amplitude = 0;
    const int breath = slotOf(performer, "Breath");
    double breathSeen = 0;
    for (int n = 0; n < 240; ++n) {
        performer.step(1.0 / 60, 12);
        breathSeen = std::max(breathSeen, performer.values()[breath]);
        for (const char* name : {"PosX", "PosY", "Squash", "Lean"}) {
            EXPECT_LT(std::fabs(performer.values()[slotOf(performer, name)]), 1e-9) << name;
        }
    }
    EXPECT_GT(breathSeen, 0.3);
}

TEST(CharacterHud, RingLightsFillWithHeatAndSplitFrontAndBack) {
    CharacterHud hud;
    const HudLayout layout{{300, 600, 200, 34}, {300, 400, 220, 36}, 700, 1};
    hud.update(1.0 / 60, 1, 120, 0.5, 0.5, "x");
    hud.layout(1.0 / 60, layout, {}, 20, 360, 300);
    int front = 0, back = 0;
    for (const auto& d : hud.front()) front += d.y >= 600;
    for (const auto& d : hud.back()) back += d.y < 600 && d.additive;
    EXPECT_GT(front, 0);
    EXPECT_GT(back, 0);
    EXPECT_GE(hud.odometer().digits().size(), 1u);
}

TEST(CharacterHud, PeakPillPopsAndFades) {
    CharacterHud hud;
    const HudLayout layout{{300, 600, 200, 34}, {300, 400, 220, 36}, 700, 1};
    hud.celebratePeak();
    bool seen = false;
    for (int n = 0; n < 180; ++n) {
        hud.update(1.0 / 60, n / 60.0, 300, 0.8, 0.8, "x");
        hud.layout(1.0 / 60, layout, {}, 20, 360, 300);
        if (hud.peak()) seen = true;
    }
    EXPECT_TRUE(seen);
    EXPECT_FALSE(hud.peak().has_value());
}

// AGENT_SPEED_CHARACTER_PACK (an unpacked pack): plays 40 s through every band and prints the cost per frame.
TEST(CharacterPerformer, PlaysARealPackQuickly) {
    const char* path = std::getenv("AGENT_SPEED_CHARACTER_PACK");
    if (!path) GTEST_SKIP() << "AGENT_SPEED_CHARACTER_PACK not set";
    auto pack = Pack::load(path);
    Performer performer(pack);
    CharacterHud hud;
    const std::vector<int> levels = {0, 2, 6, 10, 13, 7, 1, 0};
    const int frames = 60 * 40;
    const auto start = std::chrono::steady_clock::now();
    for (int n = 0; n < frames; ++n) {
        const int level = levels[std::min<std::size_t>(levels.size() - 1, static_cast<std::size_t>(n / 300))];
        performer.step(1.0 / 60, level);
        const auto layout = performer.hudLayout(300, 600, 0.4, 0.5);
        hud.update(1.0 / 60, n / 60.0, level * 30.0, level / 13.0, performer.intensity(), "t");
        hud.layout(1.0 / 60, layout, {{217, 119, 87}}, 20, 360, 300);
    }
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / frames;
    std::printf("character frame: %.3f ms over %d frames, %zu poses, %d bones\n", ms, frames, pack->poses.size(),
                pack->poses[0].rig.boneCount());
    EXPECT_LT(ms, 2);
    // Her outline and the skinned vertices of every part, as the renderer needs them each frame.
    HudSilhouette silhouette(0, 444, 2);
    std::vector<double> scratch, skinned;
    const auto traceStart = std::chrono::steady_clock::now();
    for (int n = 0; n < 600; ++n) (void)performer.trace(silhouette, 222, 355, 0.3, scratch);
    const double traceMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - traceStart).count() / 600;
    const auto skinStart = std::chrono::steady_clock::now();
    for (int n = 0; n < 600; ++n) {
        for (int part = 0; part < static_cast<int>(performer.activeRig().parts.size()); ++part) performer.activeRig().skin(part, performer.world(), skinned);
    }
    const double skinMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - skinStart).count() / 600;
    std::printf("trace %.3f ms, skin %.3f ms per frame\n", traceMs, skinMs);
}
