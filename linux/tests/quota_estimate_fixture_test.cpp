#include <gtest/gtest.h>

#include <map>

#include "agentspeed/quota/estimate/accounts.hpp"
#include "agentspeed/quota/estimate/entry.hpp"
#include "agentspeed/quota/estimate/segments.hpp"
#include "agentspeed/quota/estimate/vote.hpp"
#include "quota_estimate_support.hpp"

// QUOTAS 10 and REPORTING-EXTENSIONS 11: every unit case of spec/fixtures/quota-estimate-cases.json.
namespace {

namespace est = agentspeed::quota::estimate;
using agentspeed::json::Value;
using agentspeed::report::Json;
using namespace estimate_fixture;

constexpr std::int64_t kT0 = 1'794'000'000'000 / est::kDay * est::kDay;

Value root() {
    const auto& file = cases();
    EXPECT_TRUE(file.ok);
    return file.root();
}

TEST(QuotaEstimateFixture, Constants) {
    const auto c = root()["constants"];
    EXPECT_EQ(integer(c["lagMs"]), est::kLagMs);
    EXPECT_EQ(number(c["hiPenalty"]), est::kHiPenalty);
    EXPECT_EQ(number(c["fullStep"]), est::kFullStep);
    EXPECT_EQ(number(c["minWeight"]), est::kMinWeight);
    EXPECT_EQ(number(c["incrementStep"]), est::kIncrementStep);
    EXPECT_EQ(integer(c["segmentsPerInstance"]), static_cast<std::int64_t>(est::kSegmentsPerInstance));
    EXPECT_EQ(integer(c["instancesPerClass"]), static_cast<std::int64_t>(est::kInstancesPerClass));
    EXPECT_EQ(number(c["highWidth"]), est::kHighWidth);
    EXPECT_EQ(number(c["precisionFloor"]), est::kPrecisionFloor);
    EXPECT_EQ(integer(c["sendEveryMs"]), est::kSendEveryMs);
}

TEST(QuotaEstimateFixture, Resolution) {
    for (const auto& c : root()["resolution"].elements()) EXPECT_EQ(est::resolution(number(c["p"])), number(c["expected"])) << c["p"].raw();
}

TEST(QuotaEstimateFixture, AccountKeys) {
    const auto a = root()["accounts"];
    const auto salt = saltFromHex(*a["salt"].string());
    for (const auto& k : a["keys"].elements()) {
        EXPECT_EQ(est::accountKey(salt, *k["vendor"].string(), *k["id"].string()), *k["expected"].string());
    }
    EXPECT_EQ(est::saltHex(salt), *a["salt"].string());
}

TEST(QuotaEstimateFixture, Fingerprints) {
    const auto points = [](const Value& list) {
        std::vector<est::WindowPoint> out;
        for (const auto& p : list.elements()) {
            const auto kind = *p.at(1).string();
            const auto minutes = integer(p.at(3));
            out.push_back({integer(p.at(0)), est::windowId(kind, std::nullopt, minutes), integer(p.at(2)), minutes});
        }
        return out;
    };
    for (const auto& c : root()["accounts"]["fingerprints"].elements()) {
        SCOPED_TRACE(*c["why"].string());
        const auto got = est::fingerprint(points(c["a"]), points(c["b"]), integer(c["at"]));
        const auto want = c["expected"];
        if (want.isNull()) {
            EXPECT_FALSE(got.has_value());
        } else {
            ASSERT_TRUE(got.has_value());
            EXPECT_EQ(*got, want.isTrue());
        }
    }
}

std::optional<bool> optBool(const Value& v) {
    if (v.isNull()) return std::nullopt;
    return v.isTrue();
}

TEST(QuotaEstimateFixture, DesktopMergeAndSharedKeys) {
    const auto a = root()["accounts"];
    for (const auto& c : a["desktopMerge"].elements()) {
        EXPECT_EQ(est::desktopSharesCli(textOf(c["desktopOrg"]), textOf(c["cliOrg"]), textOf(c["cliOrgType"]), optBool(c["fingerprint"])),
                  c["expected"].isTrue())
            << fixtures::compactJson(c);
    }
    for (const auto& c : a["keysShare"].elements()) EXPECT_EQ(est::keysShare(optBool(c["fingerprint"])), c["expected"].isTrue());
}

TEST(QuotaEstimateFixture, Plans) {
    for (const auto& c : root()["accounts"]["plans"].elements()) {
        EXPECT_EQ(est::cleanPlan(c["value"]), textOf(c["expected"])) << c["value"].raw();
    }
}

TEST(QuotaEstimateFixture, CodexChains) {
    const auto c = root()["accounts"]["chains"];
    est::Chains chains;
    std::vector<std::string> roots;
    for (const auto& line : c["lines"].elements()) {
        std::vector<std::string> tags;
        for (const auto& t : line.elements()) tags.push_back(*t.string());
        roots.push_back(chains.link(tags));
    }
    std::map<std::string, std::string> names;
    std::vector<std::string> got;
    for (const auto& r : roots) {
        const auto root = chains.find(r);
        const auto it = names.try_emplace(root, "chain" + std::to_string(names.size())).first;
        got.push_back(it->second);
    }
    std::vector<std::string> want;
    for (const auto& e : c["expected"].elements()) want.push_back(*e.string());
    EXPECT_EQ(got, want);
}

Json segmentJson(const char* type, double lo, double hi, double dp) {
    return Json::object({{"type", Json::of(type)}, {"lo", fnum(lo)}, {"hi", fnum(hi)}, {"dp", Json::decimal(dp, 6)}});
}

TEST(QuotaEstimateFixture, Segments) {
    for (const auto& c : root()["segments"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        const auto led = ledgerFrom(c["rows"]);
        std::vector<est::Observation> obs;
        for (const auto& o : c["observations"].elements()) obs.push_back(observation(o));
        const auto insts = est::instances(obs);
        ASSERT_FALSE(insts.empty());
        const auto& inst = insts.front();
        const auto segs = est::segments(inst, led, "a", obs.front().name);
        const auto b = est::instanceBounds(segs, inst.R);
        std::vector<Json> list;
        for (const auto& s : segs) list.push_back(segmentJson(s.increment ? "increment" : "cumulative", s.lo, s.hi, s.dp));
        const auto got = Json::object({{"S", Json::of(inst.S)},
                                       {"Shi", Json::of(inst.Shi)},
                                       {"segments", Json::array(std::move(list))},
                                       {"instance", b ? segmentJson("instance", b->lo, b->hi, b->dp) : Json::null()}});
        expectJson(got.compact(), c["expected"], "segments");
    }
}

TEST(QuotaEstimateFixture, Vote) {
    for (const auto& c : root()["vote"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<est::Weighted> items;
        for (const auto& i : c["items"].elements()) {
            items.push_back({number(i.at(0)), optNumber(i.at(1)).value_or(est::kInfinity), number(i.at(2)), integer(i.at(3))});
        }
        const auto r = est::vote(items);
        ASSERT_TRUE(r.has_value());
        double above = 0;
        for (const auto& i : items) {
            if (i.lo > r->low && i.w >= est::kMinWeight) above += i.w;
        }
        const auto got = Json::object({{"low", Json::decimal(r->low, 9)},
                                       {"high", optionalNumber(r->high, 9)},
                                       {"inside", Json::decimal(est::pyRound(r->inside, 6), 6)},
                                       {"insideWindows", Json::of(r->insideWindows)},
                                       {"loss", Json::decimal(est::pyRound(r->loss, 6), 6)},
                                       {"confidence", Json::of(est::confidenceId(est::confidenceOf(r->low, r->high, r->inside, r->insideWindows, above)))}});
        expectJson(got.compact(), c["expected"], "vote");
    }
}

TEST(QuotaEstimateFixture, Weights) {
    for (const auto& c : root()["weights"].elements()) {
        const auto ageDays = number(c["ageDays"]);
        const double w = est::weight(number(c["dp"]), kT0, kT0 + static_cast<std::int64_t>(ageDays * static_cast<double>(est::kDay)),
                                     est::halfLifeMs(integer(c["minutes"])));
        EXPECT_NEAR(est::pyRound(w, 6), number(c["expected"]), 1e-12) << fixtures::compactJson(c);
        EXPECT_EQ(w >= est::kMinWeight, c["used"].isTrue());
    }
}

TEST(QuotaEstimateFixture, Estimate) {
    for (const auto& c : root()["estimate"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::vector<est::Interval> items;
        for (const auto& i : c["items"].elements()) {
            est::Interval x;
            x.lo = number(i["lo"]);
            x.hi = optNumber(i["hi"]).value_or(est::kInfinity);
            x.dp = number(i["dp"]);
            x.t = kT0 - static_cast<std::int64_t>(number(i["ageDays"]) * static_cast<double>(est::kDay));
            x.R = integer(i["window"]);
            items.push_back(x);
        }
        const auto e = est::estimate(items, kT0, 7 * est::kDay);
        const auto six = [](const std::optional<double>& v) { return v ? Json::decimal(est::pyRound(*v, 6), 6) : Json::null(); };
        const auto got = Json::object({{"confidence", Json::of(est::confidenceId(e.confidence))},
                                       {"cap", six(e.cap)},
                                       {"low", six(e.low)},
                                       {"high", six(e.high)},
                                       {"windows", Json::of(e.windows)}});
        expectJson(got.compact(), c["expected"], "estimate");
    }
}

TEST(QuotaEstimateFixture, PlanChangeMovesTheClass) {
    const auto c = root()["plan"];
    const auto led = ledgerFrom(c["rows"]);
    std::map<std::string, std::vector<est::Interval>> classes;
    for (const auto* group : {"old", "new"}) {
        std::vector<est::Observation> obs;
        for (const auto& o : c[group].elements()) obs.push_back(observation(o));
        for (const auto& inst : est::instances(obs)) {
            const auto b = est::instanceBounds(est::segments(inst, led, "a", std::nullopt), inst.R);
            ASSERT_TRUE(b.has_value());
            classes[*obs.front().plan].push_back(*b);
        }
    }
    std::vector<Json::Member> members;
    for (const auto& [plan, items] : classes) {
        const auto e = est::estimate(items, kT0 + 100 * est::kHour, 7 * est::kDay);
        const auto m = [](const std::optional<double>& v) { return v ? Json::of(est::micro(*v)) : Json::null(); };
        members.emplace_back(plan, Json::object({{"cap", m(e.cap)}, {"low", m(e.low)}, {"high", m(e.high)}, {"confidence", Json::of(est::confidenceId(e.confidence))}}));
    }
    expectJson(Json::object(std::move(members)).compact(), c["expected"], "plan");
}

TEST(QuotaEstimateFixture, LivePercent) {
    const auto c = root()["live"];
    const auto led = ledgerFrom(c["rows"]);
    for (const auto& k : c["cases"].elements()) {
        SCOPED_TRACE(*k["name"].string());
        const auto R = integer(k["R"]);
        const auto minutes = integer(k["minutes"]);
        const auto live = est::livePercent(number(k["p"]), integer(k["t"]), R, R - minutes * est::kMinute, minutes, *k["kind"].string(), optNumber(k["cap"]),
                                           led, "a", std::nullopt, integer(k["now"]), fixtures::optInt(k["firstAfterReset"]));
        const auto got = Json::object({{"live", Json::decimal(est::pyRound(live.percent, 6), 6)},
                                       {"estimated", Json::of(live.estimated)},
                                       {"resets", live.resetsMs ? Json::of(*live.resetsMs) : Json::null()}});
        expectJson(got.compact(), k["expected"], "live");
    }
}

est::EntryInput reportInput(const Value& v) {
    est::EntryInput in;
    in.source = *v["source"].string();
    for (const auto& s : v["sources"].elements()) in.sources.push_back(*s.string());
    in.plan = textOf(v["plan"]);
    in.tier = textOf(v["tier"]);
    in.kind = *v["kind"].string();
    in.name = textOf(v["name"]);
    in.minutes = integer(v["minutes"]);
    in.p = number(v["p"]);
    in.t = integer(v["t"]);
    in.R = integer(v["R"]);
    in.S = integer(v["S"]);
    in.account = *v["accounts"].at(0).string();
    const auto e = v["estimate"];
    in.estimate.confidence = est::confidenceFromId(*e["confidence"].string()).value_or(est::Confidence::None);
    in.estimate.cap = optNumber(e["cap"]);
    in.estimate.low = optNumber(e["low"]);
    in.estimate.high = optNumber(e["high"]);
    in.estimate.windows = static_cast<int>(integer(e["windows"]));
    return in;
}

TEST(QuotaEstimateFixture, ReportEntries) {
    const auto c = root()["report"];
    const auto led = ledgerFrom(c["rows"]);
    const auto base = reportInput(c["input"]);
    std::vector<est::Entry> made;
    for (const auto& e : c["entries"].elements()) {
        SCOPED_TRACE(*e["name"].string());
        auto in = base;
        if (*e["name"].string() == "no estimate") in.estimate = est::Estimate{};
        const auto entry = est::reportEntry(in, integer(e["now"]), led, 8);
        expectJson(entry.json().compact(), e["expected"], "entry");
        made.push_back(entry);
    }
    // The reference's cadence: a = the live entry, b = a with 5 more micro-USD, c = a at 24 %.
    const auto a = made.front();
    auto b = a;
    b.usedMicroUsd += 5;
    auto d = a;
    d.usedPercent = 24.0;
    auto added = b;
    added.sources = {"claudeCode"};
    const std::vector<est::Entry> sent{a};
    const std::vector<bool> results{
        est::shouldSend({a}, nullptr, 0, 0),
        est::shouldSend({b}, &sent, 0, 10'000),
        est::shouldSend({b}, &sent, 0, 60'000),
        est::shouldSend({d}, &sent, 0, 5'000),
        est::shouldSend({a, added}, &sent, 0, 5'000),
        est::shouldSend({a}, &sent, 0, 1'000, true),
    };
    std::vector<bool> want;
    for (const auto& k : c["cadence"].elements()) want.push_back(k["expected"].isTrue());
    EXPECT_EQ(results, want);
}

}  // namespace
