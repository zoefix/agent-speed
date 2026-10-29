#include <gtest/gtest.h>

#include <map>
#include <memory>
#include <vector>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/codex_parser.hpp"
#include "agentspeed/dsh_parser.hpp"
#include "agentspeed/file_reader.hpp"
#include "agentspeed/parents.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

class Collector : public ResponseSink {
public:
    void onResponse(const ParsedResponse& response, bool) override {
        if (!byKey.contains(response.key)) order.push_back(response.key);
        byKey[response.key] = response;
    }
    std::vector<std::string> order;
    std::map<std::string, ParsedResponse> byKey;
};

// A missing key is not checked; null means unknown.
void expectMode(const std::optional<ModeAttribution>& got, const json::Value& want, const char* what) {
    SCOPED_TRACE(what);
    if (!want.valid()) return;
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value()) << (got ? got->mode : "");
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_EQ(got->mode, *want["mode"].string());
    EXPECT_EQ(got->evidence, *want["evidence"].string());
    EXPECT_EQ(got->provider, fixtures::optString(want["provider"]));
}

void expectModes(const ExecutionModes& modes, const json::Value& want) {
    expectMode(effort_speed::effort(modes), want["effort"], "effort");
    expectMode(effort_speed::speed(modes), want["speed"], "speed");
}

std::unique_ptr<TranscriptParser> parserFor(const std::string& relative, const std::string& agentId, Deduper& deduper) {
    if (relative.rfind("codex/", 0) == 0) return std::make_unique<CodexParser>(agentId, deduper);
    if (relative.rfind("claude/", 0) == 0) return std::make_unique<ClaudeParser>(agentId, deduper);
    if (relative.rfind("dsh/", 0) == 0) return std::make_unique<DshParser>(agentId, deduper);
    return nullptr;
}

}  // namespace

// spec/fixtures/parser-expected.json, for the sources ported so far (files in `order`, one shared dedupe set).
TEST(ParserFixtures, ClaudeAndCodexMatchTheReference) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    ASSERT_TRUE(expected.ok);
    Deduper deduper;
    int checked = 0;
    std::vector<AgentLink> facts;
    std::map<std::string, std::string> wantParents;
    for (const auto& entry : expected.root()["order"].elements()) {
        const std::string relative = *entry.string();
        const auto absolute = (fixtures::fixtureDir() / relative).string();
        auto parser = parserFor(relative, absolute, deduper);
        if (!parser) continue;
        Collector sink;
        const auto read = readWholeFile(absolute, [&](std::string_view line) { parser->feed(line, sink); });
        ASSERT_TRUE(read.ok) << relative;
        parser->addMalformed(read.decodeErrors);

        const auto want = expected.root()["files"][relative];
        SCOPED_TRACE(relative);
        EXPECT_EQ(sourceId(parser->source()), *want["source"].string());
        EXPECT_EQ(parser->project(), fixtures::optString(want["project"]));
        EXPECT_EQ(parser->model(), fixtures::optString(want["model"]));
        EXPECT_EQ(parser->isSubagent(), want["isSubagent"].isTrue());
        EXPECT_EQ(phaseId(parser->phase()), *want["phase"].string());
        EXPECT_EQ(parser->inFlightStartMs(), fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(parser->lastLineMs(), fixtures::optInt(want["lastLineMs"]));
        EXPECT_EQ(parser->malformedLines(), *fixtures::optInt(want["malformedLines"]));
        expectModes(parser->modes(), want);
        if (const auto modes = want["responseModes"]; modes.isObject()) {
            for (const auto& [key, value] : modes.members()) {
                SCOPED_TRACE(std::string(key));
                ASSERT_TRUE(sink.byKey.contains(std::string(key)));
                expectModes(sink.byKey[std::string(key)].modes, value);
            }
        }

        const auto responses = want["responses"].elements();
        ASSERT_EQ(sink.order.size(), responses.size());
        for (std::size_t i = 0; i < responses.size(); ++i) {
            const auto& r = responses[i];
            const auto& got = sink.byKey[sink.order[i]];
            SCOPED_TRACE(got.key);
            EXPECT_EQ(got.key, *r["key"].string());
            EXPECT_EQ(got.startMs, fixtures::optInt(r["startMs"]));
            EXPECT_EQ(got.endMs, *fixtures::optInt(r["endMs"]));
            EXPECT_EQ(got.tokens.input, *fixtures::optInt(r["input"]));
            EXPECT_EQ(got.tokens.cachedInput, *fixtures::optInt(r["cachedInput"]));
            EXPECT_EQ(got.tokens.cacheWriteInput, *fixtures::optInt(r["cacheWriteInput"]));
            EXPECT_EQ(got.tokens.cacheWrite1hInput, *fixtures::optInt(r["cacheWrite1hInput"]));
            EXPECT_EQ(got.tokens.output, *fixtures::optInt(r["output"]));
            EXPECT_EQ(got.tokens.reasoning, *fixtures::optInt(r["reasoning"]));
            EXPECT_EQ(got.model, fixtures::optString(r["model"]));
            EXPECT_EQ(got.provider, fixtures::optString(r["provider"]));
            EXPECT_EQ(got.fast, r["fast"].isTrue());
            EXPECT_EQ(got.final, r["final"].isTrue());
        }
        facts.push_back(parser->link());
        if (auto parent = fixtures::optString(want["parentFile"])) {
            wantParents[absolute] = (fixtures::fixtureDir() / *parent).string();
        }
        ++checked;
    }
    EXPECT_EQ(checked, 11);
    std::map<std::string, std::string> gotParents;
    for (const auto& [id, parent] : parent_links::resolve(facts)) gotParents[id] = parent;
    EXPECT_EQ(gotParents, wantParents);
}
