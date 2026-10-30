#include <gtest/gtest.h>

#include <map>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/price_book.hpp"
#include "agentspeed/report_values.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

// spec/fixtures/model-attribution-cases.json (METRICS 4.1 step 4, 11.3–11.4): every Claude response keeps its own
// model, the agent shows its current one, and the report ledger counts each model under its report id.
namespace {

// Every upsert reaches the report ledger as a store running live at the response's own end time feeds it: a first
// emission is counted, a later upsert of a counted key grows it (METRICS 11.4).
class LiveSink : public ResponseSink {
public:
    void onResponse(const ParsedResponse& r, bool completion) override {
        if (!byKey.contains(r.key)) order.push_back(r.key);
        byKey[r.key] = r;
        const auto cost = book.cost(r, pricing::UtcDay(r.endMs).label());
        const auto id = reportId(r.model, r.provider);
        if (completion) {
            ledger.count(r, cost, id);
        } else if (ledger.counted(r.key)) {
            ledger.grow(r, cost, id);
        }
    }
    std::string reportId(const std::optional<std::string>& model, const std::optional<std::string>& provider) const {
        return pricing::PriceBook::bundled()->reportModelId(model, provider);
    }
    std::vector<std::string> order;
    std::map<std::string, ParsedResponse> byKey;
    pricing::PriceBook book;
    report::Ledger ledger;
};

}  // namespace

TEST(ModelAttribution, MatchesTheReferenceCases) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "model-attribution-cases.json");
    ASSERT_TRUE(file.ok);
    int checked = 0;
    for (const auto& c : file.root()["cases"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        Deduper deduper;
        LiveSink sink;
        for (const auto& f : c["files"].elements()) {
            const auto path = *f["path"].string();
            SCOPED_TRACE(path);
            ClaudeParser parser("/fixtures/" + path, deduper);
            sink.order.clear();
            for (const auto& line : f["lines"].elements()) parser.feed(*line.string(), sink);
            const auto want = c["expect"][path];
            EXPECT_EQ(sourceId(parser.source()), *want["source"].string());
            EXPECT_EQ(parser.isSubagent(), want["isSubagent"].isTrue());
            EXPECT_EQ(parser.model(), fixtures::optString(want["model"]));
            EXPECT_EQ(sink.reportId(parser.model(), std::nullopt), *want["reportModelId"].string());
            const auto responses = want["responses"].elements();
            ASSERT_EQ(sink.order.size(), responses.size());
            for (std::size_t i = 0; i < responses.size(); ++i) {
                const auto& got = sink.byKey[sink.order[i]];
                EXPECT_EQ(got.key, *responses[i]["key"].string());
                EXPECT_EQ(got.model, fixtures::optString(responses[i]["model"]));
                EXPECT_EQ(got.tokens.output, *responses[i]["output"].truncated());
                EXPECT_EQ(sink.reportId(got.model, got.provider), *responses[i]["reportModelId"].string());
            }
        }
        std::map<report::ModelKey, report::Usage> ledger = sink.ledger.counters().byModel();
        const auto rows = c["ledger"].elements();
        EXPECT_EQ(ledger.size(), rows.size());
        for (const auto& row : rows) {
            const report::ModelKey key{*row["modelId"].string(), *sourceFromId(*row["source"].string())};
            SCOPED_TRACE(key.modelId);
            ASSERT_TRUE(ledger.contains(key));
            EXPECT_EQ(ledger[key].requests, *row["requests"].truncated());
            EXPECT_EQ(ledger[key].output, *row["output"].truncated());
        }
        ++checked;
    }
    EXPECT_GE(checked, 4);
    for (const auto& pair : file.root()["reportIds"].elements()) {
        EXPECT_EQ(pricing::PriceBook::bundled()->reportModelId(*pair.at(0).string(), std::nullopt), *pair.at(1).string()) << *pair.at(0).string();
    }
}
