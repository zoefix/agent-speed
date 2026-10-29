#include <gtest/gtest.h>

#include <set>

#include "agentspeed/task_parsers.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

std::optional<std::string> optCwd(const json::Value& c) { return fixtures::optString(c["cwd"]); }
SourceKind sourceOf(const json::Value& c) { return *sourceFromId(*c["source"].string()); }

// The groups ported so far; a case of any other group is skipped.
const std::set<std::string> kPorted = {"actionKind", "command", "maskedStatus", "outcome", "resultText", "session", "notification", "codeScript", "codeItems"};

}  // namespace

TEST(TaskParsers, MatchTheReferenceCases) {
    fixtures::JsonFile cases(fixtures::fixtureDir() / "task-parser-cases.json");
    ASSERT_TRUE(cases.ok);
    int checked = 0;
    for (const auto& c : cases.root()["cases"].elements()) {
        const auto group = *c["group"].string();
        if (!kPorted.contains(group)) continue;
        SCOPED_TRACE(*c["id"].string());
        const auto expect = c["expect"];
        if (group == "actionKind") {
            EXPECT_EQ(tasks::actionKind(sourceOf(c), fixtures::optString(c["tool"]), c["input"], optCwd(c)), *expect.string());
        } else if (group == "command") {
            const auto info = tasks::commandInfo(*c["text"].string(), optCwd(c));
            EXPECT_EQ(info.cls, *expect["class"].string());
            EXPECT_EQ(info.suite, fixtures::optString(expect["suite"]));
            EXPECT_EQ(info.targeted, expect["targeted"].isTrue());
            EXPECT_EQ(info.key, *expect["key"].string());
            EXPECT_EQ(info.masked, expect["masked"].isTrue());
            EXPECT_EQ(info.background, expect["background"].isTrue());
            EXPECT_EQ(info.errorFilter, expect["errorFilter"].isTrue());
        } else if (group == "maskedStatus") {
            const auto status = tasks::maskedStatus(*c["text"].string(), *c["output"].string(), optCwd(c));
            if (expect.isNull()) {
                EXPECT_FALSE(status.has_value());
            } else {
                ASSERT_TRUE(status.has_value());
                EXPECT_EQ(status->exitCode, fixtures::optInt(expect["exitCode"]));
                EXPECT_EQ(status->failed, expect["failed"].isNull() ? std::nullopt : std::optional<bool>(expect["failed"].isTrue()));
            }
        }
        if (group == "outcome") {
            const auto o = tasks::outcome(sourceOf(c), c["result"], fixtures::optInt(c["t"]), fixtures::optString(c["tool"]), c["input"], optCwd(c));
            EXPECT_EQ(o.exitCode, fixtures::optInt(expect["exitCode"]));
            EXPECT_EQ(o.failed, expect["failed"].isNull() ? std::nullopt : std::optional<bool>(expect["failed"].isTrue()));
            EXPECT_EQ(o.t, fixtures::optInt(expect["t"]));
        } else if (group == "resultText") {
            EXPECT_EQ(tasks::resultText(sourceOf(c), c["result"]), *expect.string());
        } else if (group == "session") {
            EXPECT_EQ(tasks::continuation(sourceOf(c), fixtures::optString(c["tool"]), c["input"]), fixtures::optString(expect["continuation"]));
            EXPECT_EQ(tasks::runningSession(sourceOf(c), c["result"]), fixtures::optString(expect["running"]));
        } else if (group == "notification") {
            const auto n = tasks::notification(sourceOf(c), c["line"]);
            if (expect.isNull()) {
                EXPECT_FALSE(n.has_value());
            } else {
                ASSERT_TRUE(n.has_value());
                EXPECT_EQ(n->session, *expect["session"].string());
                EXPECT_EQ(n->exitCode, fixtures::optInt(expect["exitCode"]));
            }
        }
        if (group == "codeScript") {
            const auto script = tasks::codeScript(sourceOf(c), fixtures::optString(c["tool"]), c["input"]);
            if (expect.isNull()) {
                EXPECT_FALSE(script.has_value());
            } else {
                ASSERT_TRUE(script.has_value());
                const auto calls = expect["calls"].elements();
                ASSERT_EQ(script->calls.size(), calls.size());
                for (std::size_t i = 0; i < calls.size(); ++i) {
                    EXPECT_EQ(script->calls[i].tool, *calls[i]["tool"].string());
                    EXPECT_EQ(script->calls[i].input, fixtures::compactJson(calls[i]["input"]));
                    EXPECT_EQ(script->calls[i].digest, *calls[i]["digest"].string());
                }
                EXPECT_EQ(script->plain, expect["plain"].isTrue());
                const auto prints = expect["prints"].elements();
                ASSERT_EQ(script->prints.size(), prints.size());
                for (std::size_t i = 0; i < prints.size(); ++i) {
                    EXPECT_EQ(script->prints[i].first, *fixtures::optInt(prints[i].at(0)));
                    EXPECT_EQ(script->prints[i].second, *prints[i].at(1).string());
                }
            }
        } else if (group == "codeItems") {
            const auto items = tasks::codeItems(sourceOf(c), c["result"]);
            if (expect.isNull()) {
                EXPECT_FALSE(items.has_value());
            } else {
                ASSERT_TRUE(items.has_value());
                const auto want = expect.elements();
                ASSERT_EQ(items->size(), want.size());
                for (std::size_t i = 0; i < want.size(); ++i) EXPECT_EQ((*items)[i], fixtures::optString(want[i]));
            }
        }
        ++checked;
    }
    EXPECT_GT(checked, 300);
}
