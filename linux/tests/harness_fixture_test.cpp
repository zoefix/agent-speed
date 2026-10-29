#include <gtest/gtest.h>

#include <map>

#include "agentspeed/file_reader.hpp"
#include "agentspeed/grok_parser.hpp"
#include "agentspeed/kimi_parser.hpp"
#include "agentspeed/zcode.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

struct Latest : ResponseSink {
    std::vector<std::string> order;
    std::map<std::string, ParsedResponse> byKey;
    int completions = 0;
    void onResponse(const ParsedResponse& r, bool completion) override {
        if (!byKey.contains(r.key)) order.push_back(r.key);
        byKey[r.key] = r;
        completions += completion ? 1 : 0;
    }
    std::vector<ParsedResponse> latest() const {
        std::vector<ParsedResponse> out;
        for (const auto& key : order) out.push_back(byKey.at(key));
        return out;
    }
};

void expectResponses(const json::Value& expected, const std::vector<ParsedResponse>& responses) {
    const auto rows = expected.elements();
    ASSERT_EQ(responses.size(), rows.size());
    for (std::size_t i = 0; i < rows.size(); ++i) {
        const auto& got = responses[i];
        const auto& r = rows[i];
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
        EXPECT_EQ(got.final, r["final"].isTrue());
        EXPECT_EQ(got.fast, r["fast"].isTrue());
        EXPECT_EQ(got.requestCount, *fixtures::optInt(r["requestCount"]));
    }
}

std::optional<std::string> fixturePath(const json::Value& relative) {
    if (!relative.isString()) return std::nullopt;
    return (fixtures::fixtureDir() / *relative.string()).string();
}

template <typename Parser, typename Before>
void replayFiles(const json::Value& section, bool checkCompletions, Before before) {
    Deduper deduper;
    for (const auto& file : section["files"].elements()) {
        const auto path = *fixturePath(file["path"]);
        SCOPED_TRACE(path);
        Parser parser(path, deduper);
        Latest sink;
        before(parser, sink);
        const auto read = readWholeFile(path, [&](std::string_view line) { parser.feed(line, sink); });
        ASSERT_TRUE(read.ok);
        expectResponses(file["responses"], sink.latest());
        EXPECT_EQ(parser.project(), fixtures::optString(file["project"]));
        EXPECT_EQ(parser.isSubagent(), file["isSubagent"].isTrue());
        EXPECT_EQ(parser.parentPath(), fixturePath(file["parentFile"]));
        if (checkCompletions) {
            EXPECT_EQ(static_cast<std::size_t>(sink.completions), sink.order.size());
        }
    }
}

}  // namespace

TEST(HarnessFixtures, ZCodeMatchesEverySharedRequestAndStaysReadOnly) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "harness-expected.json");
    ASSERT_TRUE(expected.ok);
    const auto section = expected.root()["zcode"];
    const auto path = *fixturePath(section["database"]);
    const auto before = fixtures::readText(path);
    const auto folder = fs::path(path).parent_path();
    const auto names = fixtures::listFiles(folder);
    {
        const auto database = zcode::Database::open(path);
        const auto rows = database.read();
        for (const auto& session : section["sessions"].elements()) {
            const auto id = *session["id"].string();
            SCOPED_TRACE(id);
            std::vector<ParsedResponse> actual;
            for (const auto& row : rows) {
                if (row.sessionId != id) continue;
                if (auto response = row.response(path)) actual.push_back(*response);
            }
            expectResponses(session["responses"], actual);
            for (const auto& response : actual) EXPECT_EQ(response.agentId, path + "#" + id);
        }
    }
    EXPECT_EQ(fixtures::readText(path), before);
    EXPECT_EQ(fixtures::listFiles(folder), names);
}

TEST(HarnessFixtures, ZCodeSourceSeedsAgentsWithParents) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "harness-expected.json");
    const auto path = *fixturePath(expected.root()["zcode"]["database"]);
    Deduper deduper;
    zcode::Source source(path, deduper);
    ASSERT_EQ(source.databasePath(), path);
    const auto update = source.seed(0);
    EXPECT_FALSE(source.unsupported());
    EXPECT_FALSE(source.unreadable());
    EXPECT_EQ(update.responses.size(), 4u);
    const auto agents = source.agents();
    EXPECT_TRUE(std::any_of(agents.begin(), agents.end(), [](const zcode::Agent& a) { return a.parentSessionId.has_value(); }));
    EXPECT_FALSE(source.refresh().changed());
}

TEST(HarnessFixtures, KimiWireVariantsAndForkMirrorsShareOneRequestIdentity) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "harness-expected.json");
    ASSERT_TRUE(expected.ok);
    replayFiles<KimiParser>(expected.root()["kimi"], true, [](KimiParser&, Latest&) {});
}

TEST(HarnessFixtures, GrokBatchesExcludeChildRollupsAndForkCopies) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "harness-expected.json");
    ASSERT_TRUE(expected.ok);
    replayFiles<GrokParser>(expected.root()["grok"], false, [](GrokParser& parser, Latest& sink) { parser.beginBatch(sink); });
}
