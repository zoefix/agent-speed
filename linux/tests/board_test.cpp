#include <gtest/gtest.h>

#include "agentspeed/board.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace fixtures;

TEST(Board, RankingCases) {
    const auto text = readText(fixtureDir() / "board-ranking-cases.json");
    json::Document doc;
    ASSERT_TRUE(doc.parse(text));
    int checked = 0;
    for (const auto& item : doc.root()["cases"].elements()) {
        SCOPED_TRACE(*item["name"].string());
        board::Ranking ranking;
        for (const auto& tick : item["ticks"].elements()) {
            std::vector<board::Group> groups;
            for (const auto& g : tick["groups"].elements()) groups.push_back({*g["id"].string(), *g["live"].number(), *g["lastActiveMs"].truncated()});
            const auto [order, events] = ranking.tick(groups);
            std::vector<std::string> want;
            for (const auto& id : tick["order"].elements()) want.push_back(*id.string());
            EXPECT_EQ(order, want);
            std::string got = "[";
            for (const auto& e : events) {
                got += (got.size() > 1 ? "," : "") + std::string(R"({"type":")") + (e.type == board::RankEvent::Type::NewLeader ? "newLeader" : "overtake") +
                       R"(","id":")" + e.id + R"(","from":)" + std::to_string(e.from) + R"(,"to":)" + std::to_string(e.to) + "}";
            }
            EXPECT_EQ(got + "]", compactJson(tick["events"]));
            ++checked;
        }
    }
    EXPECT_GT(checked, 20);
}
