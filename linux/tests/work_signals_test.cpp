#include <gtest/gtest.h>

#include "agentspeed/task_events.hpp"
#include "agentspeed/work_signals.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

TEST(WorkSignals, MatchTheReferenceCases) {
    fixtures::JsonFile cases(fixtures::fixtureDir() / "work-signal-cases.json");
    ASSERT_TRUE(cases.ok);
    for (const auto& c : cases.root()["digests"].elements()) {
        const auto compact = fixtures::compactJson(c["input"]);
        json::Document doc;
        ASSERT_TRUE(doc.parse(compact));
        EXPECT_EQ(tasks::inputDigest(doc.root()), *c["expect"].string()) << compact;
    }
    for (const auto& c : cases.root()["signals"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::deque<work::Action> actions;
        for (const auto& a : c["actions"].elements()) {
            const auto failed = a["failed"].isBool() ? std::optional<bool>(a["failed"].isTrue()) : std::nullopt;
            actions.push_back({*a["at"].truncated(), *a["tool"].string(), *a["digest"].string(), failed, 0});
        }
        const auto got = work::signals(actions, *c["nowMs"].truncated());
        const auto w = c["expect"];
        EXPECT_EQ(got.looping, fixtures::optInt(w["looping"]));
        EXPECT_EQ(got.repeating, fixtures::optInt(w["repeating"]));
        ASSERT_EQ(got.novelty.has_value(), !w["novelty"].isNull());
        if (got.novelty) {
            EXPECT_NEAR(*got.novelty, *w["novelty"].number(), 1e-12);
        }
        EXPECT_EQ(got.failed, *w["failed"].truncated());
        EXPECT_EQ(got.recovered, *w["recovered"].truncated());
    }
    const std::map<std::string, SourceKind> formats = {
        {"claude", SourceKind::ClaudeCode}, {"codex", SourceKind::Codex}, {"omp", SourceKind::Omp}, {"pi", SourceKind::Pi}, {"droid", SourceKind::Droid}};
    for (const auto& c : cases.root()["files"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        work::ActionLog log;
        for (const auto& line : c["lines"].elements()) {
            const auto compact = fixtures::compactJson(line.at(1));
            json::Document doc;
            ASSERT_TRUE(doc.parse(compact));
            log.feed(formats.at(*c["format"].string()), doc.root(), *line.at(0).truncated());
        }
        const auto want = c["expect"].elements();
        ASSERT_EQ(log.actions().size(), want.size());
        for (std::size_t i = 0; i < want.size(); ++i) {
            const auto& a = log.actions()[i];
            EXPECT_EQ(a.at, *want[i]["at"].truncated());
            EXPECT_EQ(a.tool, *want[i]["tool"].string());
            EXPECT_EQ(a.digest, *want[i]["digest"].string());
            EXPECT_EQ(a.failed, want[i]["failed"].isBool() ? std::optional<bool>(want[i]["failed"].isTrue()) : std::nullopt);
        }
    }
}
