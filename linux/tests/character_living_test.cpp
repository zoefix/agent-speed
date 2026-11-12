// The living character (spec/CHARACTERS.md, Behavior): fixture behavior.json, rig variants, and a living pack on the stage
// (mood poses after the bands, the mood's pose through the same swap, the shown pose's face patches, the inputs ignored
// by other packs), ported from the Windows BehaviorFixtureTests and LivingPerformerTests.
#include <gtest/gtest.h>

#include <chrono>
#include <cmath>
#include <cstdlib>
#include <set>

#include "agentspeed/character/arm_gestures.hpp"
#include "agentspeed/character/director.hpp"
#include "agentspeed/character/error.hpp"
#include "agentspeed/character/performer.hpp"
#include "character_support.hpp"

using namespace agentspeed;
using namespace agentspeed::character;

namespace {

constexpr double kDt = 1.0 / 60;

double num(const json::Value& v) { return v.number().value_or(NAN); }

// The scripted session of behavior.json: the inputs at a time, as the fixture's `about` says.
class BehaviorSession {
public:
    explicit BehaviorSession(json::Value doc) : doc_(doc) {}

    double seconds() const { return num(doc_["seconds"]); }

    int level(double t) const {
        int level = 0;
        for (const auto& row : doc_["levels"].elements()) {
            if (t >= num(row.at(0))) level = static_cast<int>(num(row.at(1)));
        }
        return level;
    }

    BehaviorInputs inputs(double t, double dt) const {
        std::string phase = "idle";
        double idleAt = 0, since = 0;
        for (const auto& row : doc_["phases"].elements()) {
            if (t >= num(row.at(0))) {
                phase = *row.at(1).string();
                idleAt = num(row.at(2));
                since = num(row.at(0));
            }
        }
        BehaviorInputs in;
        for (const auto& row : doc_["requests"].elements()) {
            if (num(row.at(0)) <= t && t < num(row.at(1))) {
                in.wait = t - num(row.at(0));
                break;
            }
        }
        if (phase == "idle") {
            double jumps = 0;
            for (const auto& row : doc_["idleJumps"].elements()) {
                if (t >= num(row.at(0))) jumps += num(row.at(1));
            }
            in.idle = idleAt + t - since + jumps;
        }
        const auto keys = doc_["cursor"].elements();
        for (std::size_t i = 0; i + 1 < keys.size(); ++i) {
            const double t0 = num(keys[i].at(0)), t1 = num(keys[i + 1].at(0));
            if (t0 <= t && t <= t1) {
                const double k = (t - t0) / (t1 - t0);
                in.cursor = std::pair{num(keys[i].at(1)) + (num(keys[i + 1].at(1)) - num(keys[i].at(1))) * k,
                                      num(keys[i].at(2)) + (num(keys[i + 1].at(2)) - num(keys[i].at(2))) * k};
                break;
            }
        }
        const auto box = doc_["over"];
        in.over = in.cursor && num(box.at(0)) <= in.cursor->first && in.cursor->first <= num(box.at(1)) &&
                  num(box.at(2)) <= in.cursor->second && in.cursor->second <= num(box.at(3));
        auto fires = [&](double at) { return t - dt < at && at <= t; };
        for (const auto& row : doc_["events"].elements()) {
            if (!fires(num(row.at(0)))) continue;
            const auto name = row.at(1).string();
            if (name == "newPeak") in.newPeak = true;
            if (name == "trouble") in.trouble = true;
        }
        for (const auto& at : doc_["clicks"].elements()) {
            if (in.over && fires(num(at))) in.click = true;
        }
        in.phase = phase == "generating" ? AgentActivity::Generating : phase == "tools" ? AgentActivity::Tools : AgentActivity::Idle;
        in.hour = static_cast<int>(num(doc_["hour"]));
        return in;
    }

private:
    json::Value doc_;
};

std::string text(const std::optional<std::string>& s) { return s.value_or("-"); }
std::string text(std::optional<std::string_view> s) { return s ? std::string(*s) : "-"; }

std::string poseText(const Director& d) {
    const auto pose = d.poseWanted();
    if (!pose) return "-";
    return pose->keep() ? "keep" : std::string(pose->state);
}

std::string expected(const json::Value& frame, const char* key) {
    const auto v = frame[key];
    return v.isNull() ? "-" : v.string().value_or("?");
}

void playBehaviorCase(int index) {
    fixtures::JsonFile f(fixtures::fixtureDir() / "characters" / "behavior.json");
    ASSERT_TRUE(f.ok);
    const BehaviorSession session(f.root()["session"]);
    const auto c = f.root()["cases"].at(static_cast<std::uint32_t>(index));
    std::vector<RigDocument::Variant> variants;
    for (const auto& v : c["variants"].elements()) variants.push_back(RigDocument::Variant::decode(v));
    auto params = Param::decodeList(f.root()["params"]);
    for (const auto& v : variants) {
        params.push_back({v.param, 0, 1, 0});
        if (v.kind == "arm") {
            params.push_back({*v.arm, -1, 1, 0});
            params.push_back({*v.forearm, -1, 1, 0});
        }
    }
    std::vector<std::string> states;
    for (const auto& s : c["states"].elements()) states.push_back(*s.string());
    Director d(params, static_cast<std::uint64_t>(num(c["seed"])), *sideNamed(*c["armOut"].string()), *sideNamed(*c["armBook"].string()),
               num(c["orb"]), false, MotionProfile(), &variants, states);
    ASSERT_TRUE(d.living());
    const auto frames = c["frames"].elements();
    std::size_t next = 0;
    std::vector<std::string> mismatches;
    const int steps = static_cast<int>(session.seconds() * 60);
    for (int n = 0; n < steps; ++n) {
        const double t = n / 60.0;
        d.setLevel(session.level(t));
        const auto in = session.inputs(t, kDt);
        d.update(kDt, &in);
        if (next >= frames.size() || static_cast<int>(num(frames[next]["n"])) != n) continue;
        const auto frame = frames[next++];
        const std::pair<const char*, std::string> mine[] = {
            {"mood", d.mood() ? std::string(moodName(*d.mood())) : "-"},
            {"pose", poseText(d)},
            {"face", text(d.expression())},
            {"gaze", text(d.gazePatch())},
            {"mouth", text(d.mouthShape())},
            {"gesture", d.activeGesture().value_or("-")},
        };
        for (const auto& [key, value] : mine) {
            if (value != expected(frame, key)) mismatches.push_back("n=" + std::to_string(n) + " " + key + " " + value + " != " + expected(frame, key));
        }
        for (const auto& [k, v] : frame["values"].members()) {
            const auto value = d.value(k);
            if (!value || std::fabs(*value - num(v)) > 1e-4) {
                mismatches.push_back("n=" + std::to_string(n) + " " + k + " " + std::to_string(value.value_or(NAN)) + " != " + std::to_string(num(v)));
            }
        }
        if (mismatches.size() > 20) break;
    }
    if (next != frames.size() && mismatches.size() <= 20) {
        mismatches.push_back("checked " + std::to_string(next) + " of " + std::to_string(frames.size()) + " frames");
    }
    std::string joined;
    for (std::size_t i = 0; i < std::min<std::size_t>(20, mismatches.size()); ++i) joined += mismatches[i] + "\n";
    EXPECT_TRUE(mismatches.empty()) << c["name"].string().value_or("?") << ":\n" << joined;
}

std::shared_ptr<const Pack> livingPack(const std::vector<std::string>& states = {}, const std::string& members = "",
                                       const std::string& id = "tiny") {
    return Pack::load(living_pack::make(states, "", living_pack::rigJSON(), members, id));
}

int slotOf(const Performer& p, const char* name) { return *p.director().space().slot(name); }

BehaviorInputs idleFor(double idle, int hour = 12) {
    BehaviorInputs in;
    in.phase = AgentActivity::Idle;
    in.idle = idle;
    in.hour = hour;
    return in;
}

}  // namespace

TEST(CharacterBehavior, LivingCaseMatchesTheReference) { playBehaviorCase(0); }

TEST(CharacterBehavior, SparseCaseMatchesTheReference) { playBehaviorCase(1); }

TEST(CharacterBehavior, VariantsSkipTheItemsTheyCannotRead) {
    json::Document doc;
    ASSERT_TRUE(doc.parse(R"([{"id": "wave", "kind": "arm", "param": "Var.wave", "side": "right", "front": true, "arm": "Arm.right@wave",
                              "forearm": "Forearm.right@wave", "lead": {"Lift.right": 0.2}, "approach": {"Arm.right@wave": -0.25}},
                             {"id": "odd", "kind": "arm", "param": "Var.odd", "front": "yes"},
                             {"kind": "patch", "param": "Var.expr-smile"},
                             7,
                             {"id": "expr-smile", "kind": "patch", "param": "Var.expr-smile", "region": "face"}])"));
    const auto variants = RigDocument::Variant::decodeList(doc.root());
    ASSERT_EQ(variants.size(), 2u);
    EXPECT_EQ(variants[0].id, "wave");
    EXPECT_EQ(variants[1].id, "expr-smile");
    EXPECT_EQ(variants[0].approach->at("Arm.right@wave"), -0.25);
    EXPECT_EQ(variants[0].front, true);
    EXPECT_EQ(variants[1].region, "face");
    json::Document object;
    ASSERT_TRUE(object.parse(R"({"id": "wave"})"));
    EXPECT_TRUE(RigDocument::Variant::decodeList(object.root()).empty());
}

TEST(CharacterBehavior, ArmGesturesNeedTheirVariantAndCombosBoth) {
    RigDocument::Variant wave{"wave", "arm", "Var.wave", "right", true, "Arm.right@wave", "Forearm.right@wave", std::map<std::string, double>{{"Lift.right", 0.3}},
                              std::map<std::string, double>{{"Arm.right@wave", -0.25}}, std::nullopt};
    const auto m = arm_gestures::armGesture(wave, 1.6, "wave", std::string("smile"), true);
    ASSERT_TRUE(m);
    EXPECT_NEAR(m->duration, 0.22 + 0.1 + 0.32 + 1.6 + 0.24 + 0.1 + 0.36, 1e-12);
    EXPECT_TRUE(m->curves.find("Var.wave"));
    EXPECT_TRUE(m->curves.find("FootPoint"));
    EXPECT_EQ(m->curves.find("Arm.right@wave")->front().v, -0.25);
    auto broken = wave;
    broken.forearm.reset();
    EXPECT_FALSE(arm_gestures::armGesture(broken, 1.6, "wave", std::nullopt));
    RigDocument::Variant raise = wave;
    raise.id = "raise";
    raise.param = "Var.raise";
    raise.side = "left";
    const auto cheer = arm_gestures::combo("hooray", raise, wave, 1.6, std::string("happy"));
    ASSERT_TRUE(cheer);
    EXPECT_NEAR(cheer->duration, m->duration + 0.1, 1e-12);
    EXPECT_EQ(cheer->expression, "happy");
    EXPECT_FALSE(arm_gestures::combo("hooray", broken, wave, 1.6, std::nullopt));
}

TEST(CharacterLiving, MoodPosesLoadAfterTheBandsAndNeverStandForABand) {
    const auto pack = livingPack({"sleepy", "think"});
    std::vector<std::string> names;
    for (const auto& p : pack->poses) names.push_back(p.name());
    EXPECT_EQ(names, (std::vector<std::string>{"idle", "cruise", "think", "sleepy"}));
    EXPECT_EQ(pack->states, (std::vector<std::string>{"think", "sleepy"}));
    EXPECT_EQ(pack->poses[2].band, Band::Idle);
    EXPECT_TRUE(pack->living);
    Performer performer(pack);
    EXPECT_TRUE(performer.living() && performer.director().living());
    EXPECT_EQ(performer.poseIndex(Band::Redline), 1);
}

TEST(CharacterLiving, AMoodPoseLeadingOutsideThePackIsRefusedAndAMissingOneSkipped) {
    try {
        (void)Pack::load(living_pack::make({}, R"("think":"../think")"));
        FAIL() << "a state outside the pack loads";
    } catch (const CharacterError& e) {
        EXPECT_EQ(e.kind(), CharacterError::Kind::UnsafePath);
        EXPECT_EQ(e.detail(), "../think");
    }
    EXPECT_THROW((void)Pack::load(living_pack::make({}, R"("dance":"/etc")")), CharacterError);
    const auto pack = Pack::load(living_pack::make({"think"}, R"("focus":"poses/focus")"));
    EXPECT_EQ(pack->states, (std::vector<std::string>{"think"}));
}

TEST(CharacterLiving, AnUnreadableMoodPoseIsSkippedAndBrokenStatesAreIgnored) {
    const auto root = living_pack::make({"think", "wait"});
    tiny_pack::write(root / "poses" / "think" / "rig.json", "{ not json");
    std::filesystem::remove(root / "poses" / "wait" / "atlas.png");
    const auto pack = Pack::load(root);
    EXPECT_TRUE(pack->states.empty());
    EXPECT_EQ(pack->poses.size(), 2u);
    EXPECT_TRUE(pack->living);

    tiny_pack::ManifestOptions o;
    o.extra = R"(,"states":{"think":3})";
    const auto odd = Pack::load(tiny_pack::make({{"idle", tiny_pack::rigJSON()}}, o));
    EXPECT_TRUE(odd->manifest.states.empty());
    EXPECT_FALSE(odd->living);
}

TEST(CharacterLiving, APackWithoutPatchesOrStatesIgnoresTheInputs) {
    const auto rig = tiny_pack::rigJSON();
    const auto pack = Pack::load(tiny_pack::make({{"idle", rig}, {"cruise", rig}}));
    EXPECT_FALSE(pack->living);
    Performer plain(pack, 3), fed(pack, 3);
    BehaviorInputs inputs;
    inputs.phase = AgentActivity::Generating;
    inputs.wait = 12;
    inputs.cursor = std::pair{0.3, 0.1};
    inputs.over = true;
    for (int n = 0; n < 600; ++n) {
        const int level = n < 300 ? 0 : 3;
        plain.step(kDt, level);
        fed.step(kDt, level, &inputs);
    }
    EXPECT_EQ(plain.values(), fed.values());
    EXPECT_FALSE(fed.director().poseWanted());
    EXPECT_EQ(fed.director().behavior(), nullptr);
}

TEST(CharacterLiving, RestingShowsTheSleepyPoseAtOnceAndWorkWakesHer) {
    const auto pack = livingPack({"sleepy", "greet", "think"});
    Performer performer(pack);
    const int spin = slotOf(performer, "Spin");
    int sleepy = -1;
    for (std::size_t i = 0; i < pack->poses.size(); ++i) {
        if (pack->poses[i].state == "sleepy") sleepy = static_cast<int>(i);
    }
    const auto resting = idleFor(900, 1);
    performer.step(kDt, 0, &resting);
    EXPECT_EQ(performer.director().mood(), Mood::Rest);
    EXPECT_EQ(performer.activePose(), sleepy);
    EXPECT_EQ(performer.director().poseName(), "sleepy");
    EXPECT_FALSE(performer.director().poseBand());
    // The first frame shows the mood pose without a turn.
    for (const auto& e : performer.events()) EXPECT_NE(e.kind, PerformerEvent::Kind::PoseSwapped);
    EXPECT_EQ(performer.values()[static_cast<std::size_t>(spin)], 0);

    std::vector<std::string> seen;
    BehaviorInputs request;
    request.phase = AgentActivity::Generating;
    request.hour = 1;
    for (int n = 0; n < 60 * 14; ++n) {
        request.wait = n / 60.0;
        performer.step(kDt, 0, &request);
        const auto name = pack->poses[static_cast<std::size_t>(performer.activePose())].name();
        if (seen.empty() || seen.back() != name) seen.push_back(name);
    }
    // Waking up greets in the greet pose, the long wait for the first token thinks in the think pose.
    EXPECT_EQ(seen, (std::vector<std::string>{"sleepy", "greet", "idle", "think"}));
}

TEST(CharacterLiving, ABandChangeSwapsNothingWhileAMoodPoseShows) {
    const auto pack = livingPack({"sleepy"});
    Performer performer(pack);
    const auto resting = idleFor(900, 1);
    int swaps = 0;
    for (int n = 0; n < 240; ++n) {
        performer.step(kDt, n < 60 ? 0 : 3, &resting);
        for (const auto& e : performer.events()) swaps += e.kind == PerformerEvent::Kind::PoseSwapped;
    }
    EXPECT_EQ(pack->poses[static_cast<std::size_t>(performer.activePose())].name(), "sleepy");
    EXPECT_EQ(swaps, 0);
    EXPECT_EQ(performer.activePose(), performer.wantedPose());
}

TEST(CharacterLiving, TheFaceCenterIsTheShownPosesFaceBoxOnScreen) {
    Performer performer(livingPack());
    const auto [x, y] = performer.faceCenter(200, 400, 2);
    // Face box [40, 10, 60, 30], feet (50, 90): the face center is right above the feet, 70 px up.
    EXPECT_NEAR(x, 200, 1e-9);
    EXPECT_NEAR(y, 400 - 70 * 2, 1e-9);
}

TEST(CharacterLiving, TheCursorTurnsHerHeadAndHerEyesTakeTheGazePatch) {
    Performer performer(livingPack());
    const auto angleX = static_cast<std::size_t>(slotOf(performer, "AngleX"));
    auto looking = idleFor(30);
    looking.cursor = std::pair{0.9, 0.05};
    double turned = 0;
    for (int n = 0; n < 60 * 3; ++n) {
        performer.step(kDt, 0, &looking);
        turned = std::max(turned, performer.values()[angleX]);
    }
    EXPECT_GT(turned, 0.45) << "head toward the cursor on her right";
    EXPECT_EQ(performer.director().behavior()->gaze(), "right");
    EXPECT_TRUE(performer.director().gazePatch() == "right" || performer.director().expression());
    looking.cursor.reset();
    for (int n = 0; n < 60 * 3; ++n) performer.step(kDt, 0, &looking);
    EXPECT_EQ(performer.director().behavior()->lookWeight(), 0);
}

TEST(CharacterLiving, SheActsWhileHerHeadFollowsAMovingCursor) {
    // Every idle action excluded, so only the look and the face can make her act.
    Performer performer(livingPack({}, R"(,"motion":{"exclude":["open","chin","hairtuck","hug","yawn","stretch","lookaround","tilt","foottap","ponder","step","bow"]})"));
    auto inputs = idleFor(30);
    inputs.cursor = std::pair{0.5, 0.1};
    for (int n = 0; n < 60 * 3; ++n) performer.step(kDt, 0, &inputs);
    EXPECT_FALSE(performer.director().activeGesture());
    EXPECT_EQ(performer.director().behavior()->lookWeight(), 1);
    EXPECT_FALSE(performer.isActing()) << "a still cursor, a settled look and face";
    for (int n = 0; n < 30; ++n) {
        inputs.cursor = std::pair{0.5 + 0.005 * (n + 1), 0.1};
        performer.step(kDt, 0, &inputs);
        EXPECT_TRUE(performer.isActing()) << "following the moving cursor, frame " << n;
    }
    int settled = -1;
    for (int n = 0; n < 60; ++n) {
        performer.step(kDt, 0, &inputs);
        if (!performer.isActing() && settled < 0) settled = n;
    }
    EXPECT_GE(settled, 30);
    EXPECT_LE(settled, 40);
    inputs.cursor.reset();
    performer.step(kDt, 0, &inputs);
    EXPECT_TRUE(performer.isActing()) << "the look lets go";
}

TEST(CharacterLiving, SheTalksInMouthShapesWhileOutputFlows) {
    Performer performer(livingPack());
    const auto a = static_cast<std::size_t>(slotOf(performer, "Var.mouth-a"));
    const auto o = static_cast<std::size_t>(slotOf(performer, "Var.mouth-o"));
    std::set<std::string> shapes;
    bool mixed = false, talked = false;
    BehaviorInputs generating;
    generating.phase = AgentActivity::Generating;
    for (int n = 0; n < 60 * 20; ++n) {
        performer.step(kDt, 3, &generating);
        if (const auto shape = performer.director().mouthShape()) shapes.insert(std::string(*shape));
        talked = talked || performer.director().talking();
        const double va = performer.values()[a], vo = performer.values()[o];
        mixed = mixed || (va > 0 && (va < 1 || vo > 0));
    }
    EXPECT_TRUE(talked);
    EXPECT_EQ(shapes, (std::set<std::string>{"a", "o"}));
    EXPECT_FALSE(mixed) << "mouth shapes switch on a frame and never blend";
}

TEST(CharacterLiving, AClickPokesHerAndKeepsTheShownPose) {
    Performer performer(livingPack());
    const auto idle = idleFor(30);
    for (int n = 0; n < 60; ++n) performer.step(kDt, 0, &idle);
    auto click = idle;
    click.cursor = std::pair{0.0, 0.3};
    click.over = true;
    click.click = true;
    performer.step(kDt, 0, &click);
    EXPECT_EQ(performer.director().mood(), Mood::Poke);
    EXPECT_EQ(performer.director().poseWanted(), MoodPose{});
    EXPECT_EQ(performer.director().behavior()->face(nullptr, std::nullopt, Band::Idle), "surprised");
}

TEST(CharacterLiving, TheCharacterLeavingTheStageIsNeverPokedOrHovered) {
    Stage stage{Performer(livingPack({}, "", "a"))};
    stage.calm = true;
    const auto idle = idleFor(30);
    for (int n = 0; n < 30; ++n) stage.step(kDt, 0, &idle);
    stage.switchTo(Performer(livingPack({}, "", "b")));
    stage.step(kDt, 0, &idle);
    ASSERT_TRUE(stage.outgoing());
    EXPECT_EQ(stage.current().pack().id(), "b");
    EXPECT_EQ(stage.outgoing()->pack().id(), "a");
    auto click = idle;
    click.cursor = std::pair{0.0, 0.3};
    click.over = true;
    click.click = true;
    stage.step(kDt, 0, &click);
    EXPECT_EQ(stage.current().director().mood(), Mood::Poke);
    ASSERT_TRUE(stage.outgoing());
    EXPECT_EQ(stage.outgoing()->director().mood(), Mood::Wait);
    EXPECT_FALSE(stage.outgoing()->director().behavior()->inputs().over);
    EXPECT_EQ(stage.outgoing()->director().behavior()->inputs().cursor, (std::pair{0.0, 0.3}));
}

// AGENT_SPEED_LIVING_PACK=<folder>: a real living pack through the fixture's session, quickly.
TEST(CharacterLiving, PlaysARealLivingPackThroughTheSessionQuickly) {
    const char* path = std::getenv("AGENT_SPEED_LIVING_PACK");
    if (!path || !*path) GTEST_SKIP() << "AGENT_SPEED_LIVING_PACK is not set";
    const auto pack = Pack::load(path);
    ASSERT_TRUE(pack->living);
    Performer performer(pack);
    fixtures::JsonFile f(fixtures::fixtureDir() / "characters" / "behavior.json");
    const BehaviorSession session(f.root()["session"]);
    const int frames = static_cast<int>(session.seconds() * 60);
    std::vector<BehaviorInputs> inputs;
    for (int n = 0; n < frames; ++n) inputs.push_back(session.inputs(n / 60.0, kDt));
    std::set<std::string> poses, gestures, faces;
    const auto start = std::chrono::steady_clock::now();
    for (int n = 0; n < frames; ++n) {
        performer.step(kDt, session.level(n / 60.0), &inputs[static_cast<std::size_t>(n)]);
        poses.insert(pack->poses[static_cast<std::size_t>(performer.activePose())].name());
        if (const auto g = performer.director().activeGesture()) gestures.insert(*g);
        if (const auto& e = performer.director().expression()) faces.insert(*e);
    }
    const double perFrame = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count() / frames;
    EXPECT_LT(perFrame, 2.0);
    EXPECT_TRUE(poses.count("sleepy"));
    EXPECT_GE(poses.size(), 4u);
    EXPECT_GE(faces.size(), 3u);
    bool arms = false;
    for (const char* g : {"wave", "chin", "peace", "thumbsup", "hairtuck", "idea", "fist", "hip", "cheek"}) arms = arms || gestures.count(g);
    EXPECT_TRUE(arms);
}
