#include <gtest/gtest.h>

#include <algorithm>
#include <set>

#include "agentspeed/quota/estimate/engine.hpp"
#include "agentspeed/quota/estimate/store_file.hpp"
#include "quota_estimate_support.hpp"

// QUOTAS 11: the pipeline cases of spec/fixtures/quota-estimate-cases.json (quota_estimate_pipeline.py), raw events to
// accounts, ledger sums, instances, estimates, report entries, the store and the send cadence.
namespace {

namespace est = agentspeed::quota::estimate;
using agentspeed::json::Value;
using agentspeed::report::Json;
using namespace estimate_fixture;

// The reference's generated_store().
est::Store generatedStore(const Value& g) {
    est::Store s;
    s.version = static_cast<int>(integer(g["version"]));
    const auto t0 = integer(g["t0"]);
    const auto account = *g["account"].string();
    for (std::int64_t i = 1; i <= integer(g["observations"]); ++i) {
        est::Observation o;
        o.account = account;
        o.source = "claudeCode";
        o.kind = "sevenDay";
        o.minutes = 10080;
        o.t = o.tLo = t0 + i * est::kMinute;
        o.p = est::pyRound(static_cast<double>(i) / 10, 1);
        o.R = t0 + 6 * est::kDay;
        o.plan = "claude_max";
        s.observations.push_back(o);
    }
    for (std::int64_t i = 1; i <= integer(g["instances"]); ++i) {
        est::StoredInstance x;
        x.cls = {true, "anthropic/claude_max", std::nullopt, "fiveHour::300"};
        x.account = account;
        x.R = t0 - i * 5 * est::kHour;
        x.lo = 1e8;
        x.hi = 2e8;
        x.pMax = 40;
        x.tLast = x.R - est::kHour;
        s.instances.push_back(x);
    }
    s.keys.push_back({"claudeCode", {t0, account, "claude_max", std::nullopt}, std::nullopt});
    return s;
}

Json classJson(const est::CapClass& cls) {
    if (cls.byPlan) return Json::array({Json::of("plan"), Json::of(cls.id), optionalText(cls.tier), Json::of(cls.window)});
    return Json::array({Json::of("account"), Json::of(cls.id), Json::of(cls.window)});
}

Json instancesJson(const est::Engine& engine) {
    std::vector<const est::InstanceRow*> rows;
    for (const auto& x : engine.instances()) rows.push_back(&x);
    std::stable_sort(rows.begin(), rows.end(), [](const est::InstanceRow* a, const est::InstanceRow* b) {
        const auto plan = [](const est::InstanceRow* x) { return x->plan.value_or("None"); };
        return std::make_tuple(a->account, a->window, a->inst.R, plan(a)) < std::make_tuple(b->account, b->window, b->inst.R, plan(b));
    });
    std::vector<Json> out;
    for (const auto* x : rows) {
        Json bounds = Json::null();
        if (x->bounds) bounds = Json::object({{"lo", fnum(x->bounds->lo)}, {"hi", fnum(x->bounds->hi)}, {"offset", Json::of(x->bounds->offset)}});
        out.push_back(Json::object({{"account", Json::of(x->account)},
                                    {"window", Json::of(x->window)},
                                    {"plan", optionalText(x->plan)},
                                    {"tier", optionalText(x->tier)},
                                    {"class", classJson(x->cls)},
                                    {"R", Json::of(x->inst.R)},
                                    {"S", Json::of(x->inst.S)},
                                    {"Shi", Json::of(x->inst.Shi)},
                                    {"observations", Json::of(static_cast<std::int64_t>(x->inst.obs.size()))},
                                    {"bounds", std::move(bounds)}}));
    }
    return Json::array(std::move(out));
}

Json estimatesJson(const est::Engine& engine) {
    std::vector<std::pair<std::string, const est::Estimate*>> list;
    for (const auto& [cls, e] : engine.estimates()) list.emplace_back(cls.repr() + "\x1f" + cls.joined(), &e);
    std::sort(list.begin(), list.end(), [](const auto& a, const auto& b) { return a.first < b.first; });
    std::vector<Json::Member> members;
    for (const auto& [key, e] : list) {
        members.emplace_back(key.substr(key.find('\x1f') + 1), Json::object({{"cap", fnum(e->cap)},
                                                                            {"low", fnum(e->low)},
                                                                            {"high", fnum(e->high)},
                                                                            {"confidence", Json::of(est::confidenceId(e->confidence))},
                                                                            {"windows", Json::of(e->windows)}}));
    }
    return Json::object(std::move(members));
}

// The reference's `plans` output: account key → {plan, tier, rule}.
Json plansJson(const est::Engine& engine) {
    std::vector<Json::Member> members;
    for (const auto& [key, p] : engine.inferredPlans()) {
        members.emplace_back(key, Json::object({{"plan", Json::of(p.plan)}, {"tier", optionalText(p.tier)}, {"rule", Json::of(std::string(est::inferRuleId(p.rule)))}}));
    }
    return Json::object(std::move(members));
}

Json entriesJson(const std::vector<est::Entry>& entries) {
    std::vector<Json> out;
    for (const auto& e : entries) out.push_back(e.json());
    return Json::array(std::move(out));
}

struct Run {
    est::Salt salt;
    std::string origin;
    std::unique_ptr<est::Engine> engine;
    std::int64_t now = 0;
};

Run run(const Value& input) {
    Run r;
    r.salt = saltFromHex(*input["salt"].string());
    r.origin = *input["origin"].string();
    r.engine = std::make_unique<est::Engine>(r.salt, 8, *input["install"].string());
    for (const auto& e : input["events"].elements()) addEvent(*r.engine, r.salt, e);
    if (input["store"].valid()) {
        const auto parsed = est::parseStore(fixtures::compactJson(input["store"]));
        EXPECT_TRUE(parsed.has_value());
        if (parsed) r.engine->load(*parsed);
    }
    if (input["generatedStore"].valid()) {
        // Through the file format, as a later launch reads it.
        const auto parsed = est::parseStore(est::storeJson(generatedStore(input["generatedStore"])));
        EXPECT_TRUE(parsed.has_value());
        if (parsed) r.engine->load(*parsed);
    }
    r.now = integer(input["now"]);
    r.engine->build(r.now);
    return r;
}

void check(const Value& c) {
    SCOPED_TRACE(*c["name"].string());
    const auto input = c["input"];
    const auto want = c["expected"];
    auto r = run(input);
    const auto& engine = *r.engine;
    if (want["responses"].valid()) {
        std::vector<Json> list;
        for (const auto& a : engine.responseAccounts()) list.push_back(optionalText(a));
        expectJson(Json::array(std::move(list)).compact(), want["responses"], "responses");
    }
    if (want["sums"].valid()) {
        std::vector<Json> list;
        for (const auto& q : input["sums"].elements()) {
            const auto account = engine.responseAccounts().at(static_cast<std::size_t>(integer(q["accountsOf"].at(0))));
            EXPECT_TRUE(account.has_value());
            const auto name = textOf(q["name"]);
            const auto a = integer(q["a"]), b = integer(q["b"]);
            const auto& led = engine.ledger();
            const auto outer = led.outer(*account, name, a, b);
            list.push_back(Json::object({{"inner", Json::of(led.inner(*account, name, a, b).costMicroUsd)},
                                         {"outer", Json::array({Json::of(outer.costMicroUsd), Json::of(outer.unpricedRequests)})},
                                         {"outerHigh", fnum(led.outerHigh(*account, name, a, b))}}));
        }
        expectJson(Json::array(std::move(list)).compact(), want["sums"], "sums");
    }
    if (want["instances"].valid()) expectJson(instancesJson(engine).compact(), want["instances"], "instances");
    if (want["estimates"].valid()) expectJson(estimatesJson(engine).compact(), want["estimates"], "estimates");
    if (want["plans"].valid()) expectJson(plansJson(engine).compact(), want["plans"], "plans");
    if (want["entries"].valid()) expectJson(entriesJson(engine.entries(r.now, r.origin)).compact(), want["entries"], "entries");
    if (want["store"].valid()) {
        const auto text = est::storeJson(engine.store());
        expectJson(text, want["store"], "store");
        const auto back = est::parseStore(text);
        ASSERT_TRUE(back.has_value());
        EXPECT_EQ(est::storeJson(*back), text);
    }
    if (want["storeCounts"].valid()) {
        const auto s = engine.store();
        const auto got = Json::object({{"observations", Json::of(static_cast<std::int64_t>(s.observations.size()))},
                                       {"instances", Json::of(static_cast<std::int64_t>(s.instances.size()))},
                                       {"keys", Json::of(static_cast<std::int64_t>(s.keys.size()))}});
        expectJson(got.compact(), want["storeCounts"], "storeCounts");
    }
}

void checkGroup(const char* group) {
    const auto& file = cases();
    ASSERT_TRUE(file.ok);
    const auto list = file.root()["pipeline"][group];
    ASSERT_TRUE(list.isArray()) << group;
    ASSERT_GT(list.size(), 0u);
    for (const auto& c : list.elements()) check(c);
}

TEST(QuotaEstimatePipeline, Ledger) { checkGroup("ledger"); }
TEST(QuotaEstimatePipeline, CodexChains) { checkGroup("chains"); }
TEST(QuotaEstimatePipeline, DesktopMerge) { checkGroup("claude"); }
TEST(QuotaEstimatePipeline, InferredPlan) { checkGroup("inference"); }
TEST(QuotaEstimatePipeline, KeyTimeline) { checkGroup("keys"); }
TEST(QuotaEstimatePipeline, CapClasses) { checkGroup("classes"); }
TEST(QuotaEstimatePipeline, LoweredCap) { checkGroup("loweredCap"); }
TEST(QuotaEstimatePipeline, PlanChange) { checkGroup("planChange"); }
TEST(QuotaEstimatePipeline, LivePercent) { checkGroup("live"); }
TEST(QuotaEstimatePipeline, Store) { checkGroup("store"); }
TEST(QuotaEstimatePipeline, ReportEntries) { checkGroup("report"); }

Value pipelineCase(const char* group, const std::string& name) {
    for (const auto& c : cases().root()["pipeline"][group].elements()) {
        if (*c["name"].string() == name) return c;
    }
    return {};
}

// REPORTING-EXTENSIONS 11.4: two Codex accounts of one window are two entries, told apart by their tags only (seen
// 2026-11-11: a service rejected the pair before the tag existed).
TEST(QuotaEstimatePipeline, TwoCodexAccountsAreTwoEntriesWithTheirOwnTags) {
    const auto c = pipelineCase("report", "two Codex accounts");
    ASSERT_TRUE(c.valid());
    const auto input = c["input"];
    auto r = run(input);
    const auto entries = r.engine->entries(r.now, r.origin);
    ASSERT_EQ(entries.size(), 2u);
    for (const auto& e : entries) {
        EXPECT_EQ(e.sources, std::vector<std::string>{"codex"});
        EXPECT_EQ(e.kind, "sevenDay");
        EXPECT_EQ(e.accountTag.size(), 16u);
    }
    EXPECT_NE(entries[0].accountTag, entries[1].accountTag);
    EXPECT_LT(entries[0].accountTag, entries[1].accountTag);
    const auto salt = saltFromHex(*input["salt"].string());
    std::set<std::string> tags;
    for (const auto* id : {"acct-a", "acct-b"}) tags.insert(est::reportAccount(r.origin, est::accountKey(salt, "codex", id)));
    EXPECT_EQ(tags, (std::set<std::string>{entries[0].accountTag, entries[1].accountTag}));
    // Neither the key nor the id is in the JSON; the cadence sees two entries of one window, not one changed entry.
    for (const auto& e : entries) {
        const auto text = e.json().compact();
        EXPECT_EQ(text.find(e.account), std::string::npos);
        EXPECT_EQ(text.find("acct-"), std::string::npos);
    }
    EXPECT_FALSE(est::shouldSend(entries, &entries, 0, 1'000));
    const std::vector<est::Entry> one{entries[0]};
    EXPECT_TRUE(est::shouldSend(entries, &one, 0, 1'000));
    expectJson(entriesJson(entries).compact(), c["expected"]["entries"], "entries");
}

// QUOTAS 10.2: an inferred plan fills only Desktop's own account's entries, marked as inferred, and never forms a cap
// class (no class of the inferred plan; Desktop's instances keep their account class).
void expectInferredOnlyOnDesktop(const char* group, const std::string& name, const std::optional<std::string>& plan,
                                 const std::optional<std::string>& tier) {
    SCOPED_TRACE(name);
    const auto c = pipelineCase(group, name);
    ASSERT_TRUE(c.valid());
    auto r = run(c["input"]);
    const auto entries = r.engine->entries(r.now, r.origin);
    ASSERT_FALSE(entries.empty());
    bool desktop = false;
    for (const auto& e : entries) {
        if (e.source != "claudeDesktop") {
            EXPECT_FALSE(e.planInferred) << e.source;
            continue;
        }
        desktop = true;
        EXPECT_EQ(e.plan, plan) << e.kind;
        EXPECT_EQ(e.tier, tier) << e.kind;
        EXPECT_EQ(e.planInferred, plan.has_value()) << e.kind;
    }
    EXPECT_TRUE(desktop);
    for (const auto& x : r.engine->instances()) {
        if (x.inst.obs.back().source == "claudeDesktop") {
            EXPECT_FALSE(x.cls.byPlan) << x.window;
        }
    }
    if (plan) {
        for (const auto& [cls, _] : r.engine->estimates()) EXPECT_FALSE(cls.byPlan && cls.id == "anthropic/" + *plan && cls.tier == tier) << cls.joined();
    }
    expectJson(entriesJson(entries).compact(), c["expected"]["entries"], "entries");
}

TEST(QuotaEstimatePipeline, DesktopsOwnAccountWithoutAFiveHourWindowIsUltra) {
    expectInferredOnlyOnDesktop("claude", "Desktop on another account of the organization", "claude_ultra", std::nullopt);
    expectInferredOnlyOnDesktop("claude", "Desktop without CLI values", "claude_ultra", std::nullopt);
    expectInferredOnlyOnDesktop("inference", "Desktop without a 5-hour window", "claude_ultra", std::nullopt);
}

TEST(QuotaEstimatePipeline, DesktopsPlanByRatioOrNone) {
    expectInferredOnlyOnDesktop("inference", "Desktop at a quarter of the CLI's 5-hour cap", "claude_max", "default_claude_max_5x");
    expectInferredOnlyOnDesktop("inference", "Desktop at half the CLI's 5-hour cap", std::nullopt, std::nullopt);
    expectInferredOnlyOnDesktop("inference", "Desktop of a team organization", std::nullopt, std::nullopt);
}

// Every pipeline group of the fixture has a test above.
TEST(QuotaEstimatePipeline, EveryGroupIsCovered) {
    const std::set<std::string> covered{"ledger", "chains", "claude", "inference", "keys", "classes", "loweredCap", "planChange", "live", "store", "report", "cadence"};
    for (const auto& [name, _] : cases().root()["pipeline"].members()) EXPECT_TRUE(covered.contains(name)) << name;
}

TEST(QuotaEstimatePipeline, SendCadence) {
    const auto c = cases().root()["pipeline"]["cadence"];
    const auto e = c["entry"];
    est::Entry base;
    base.accountTag = *e["account"].string();
    for (const auto& s : e["sources"].elements()) base.sources.push_back(*s.string());
    base.kind = *e["window"]["kind"].string();
    base.name = textOf(e["window"]["name"]);
    base.minutes = integer(e["window"]["minutes"]);
    base.usedPercent = optNumber(e["usedPercent"]);
    base.resetsInMs = fixtures::optInt(e["resetsInMs"]);
    base.usedMicroUsd = integer(e["usedMicroUsd"]);
    base.confidence = est::confidenceFromId(*e["estimate"]["confidence"].string()).value_or(est::Confidence::None);
    est::Reporter reporter;
    std::vector<est::Entry> current{base};
    std::vector<std::string> got;
    for (const auto& s : c["steps"].elements()) {
        if (s["newRun"].isTrue()) reporter.newRun();
        const auto change = *s["change"].string();
        if (change == "set") {
            auto added = current.front();
            added.accountTag = "fedcba9876543210";
            current.push_back(added);
        } else if (change != "none") {
            auto x = current.front();
            if (change == "spend") x.usedMicroUsd += 1;
            if (change == "percent") x.usedPercent = *x.usedPercent + 1;
            if (change == "confidence") x.confidence = x.confidence != est::Confidence::High ? est::Confidence::High : est::Confidence::Medium;
            if (change == "reset") {
                x.resetsInMs = x.resetsInMs ? std::nullopt : std::optional<std::int64_t>(1'000'000);
                x.usedPercent.reset();
            }
            current.front() = x;
        }
        const bool switchOn = !s["switch"].valid() || s["switch"].isTrue();
        const auto q = reporter.step(integer(s["at"]), current, switchOn, s["leaving"].isTrue());
        const bool fits = !s["fits"].valid() || s["fits"].isTrue();
        if (q && !q->empty() && !fits) {
            reporter.dropped();
            got.push_back("absent");
            continue;
        }
        got.push_back(!q ? "absent" : (q->empty() ? "[]" : "entries"));
    }
    std::vector<std::string> want;
    for (const auto& x : c["expected"].elements()) want.push_back(*x.string());
    EXPECT_EQ(got, want);
}

}  // namespace
