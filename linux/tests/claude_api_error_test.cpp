#include <gtest/gtest.h>

#include <string>
#include <vector>

#include "agentspeed/claude_parser.hpp"

using namespace agentspeed;

// METRICS 4.1: Claude Code's API error message ends the turn; a retry wait stops the in-flight estimate.
namespace {

constexpr std::int64_t kT0 = 1'792'490'400'000;

class Sink : public ResponseSink {
public:
    void onResponse(const ParsedResponse& r, bool) override { responses.push_back(r); }
    std::vector<ParsedResponse> responses;
};

std::string user(const char* time) {
    return std::string(R"({"type":"user","entrypoint":"cli","cwd":"/w/p","message":{"role":"user","content":"go"},"timestamp":")") +
           time + R"("})";
}

std::string synthetic(const char* time, bool apiError) {
    return std::string(R"({"type":"assistant","entrypoint":"cli","cwd":"/w/p","uuid":"s1",)") +
           (apiError ? R"("isApiErrorMessage":true,"error":"rate_limit",)" : "") +
           R"("message":{"id":"x1","model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence",)"
           R"("content":[{"type":"text","text":"limit"}],"usage":{"input_tokens":0,"output_tokens":0}},"timestamp":")" +
           time + R"("})";
}

const std::string kRetry =
    R"({"type":"system","subtype":"api_error","level":"error","entrypoint":"cli","cwd":"/w/p","error":{"status":529},)"
    R"("retryInMs":8000,"retryAttempt":1,"maxRetries":10,"timestamp":"2026-10-20T10:00:02Z"})";

}  // namespace

TEST(ClaudeApiErrors, AnApiErrorMessageEndsTheTurnAndOtherSyntheticLinesDoNot) {
    Deduper deduper;
    Sink sink;
    ClaudeParser failed("/a.jsonl", deduper);
    failed.feed(user("2026-10-20T10:00:00Z"), sink);
    failed.feed(synthetic("2026-10-20T10:00:05Z", true), sink);
    EXPECT_TRUE(sink.responses.empty());
    EXPECT_EQ(failed.phase(), Phase::Idle);
    EXPECT_FALSE(failed.inFlightStartMs().has_value());

    ClaudeParser other("/b.jsonl", deduper);
    other.feed(user("2026-10-20T10:00:00Z"), sink);
    other.feed(synthetic("2026-10-20T10:00:05Z", false), sink);
    EXPECT_TRUE(sink.responses.empty());
    EXPECT_EQ(other.phase(), Phase::Generating);
    EXPECT_EQ(other.inFlightStartMs(), kT0);
}

TEST(ClaudeApiErrors, ARetryWaitStopsTheInFlightEstimateUntilTheNextAttempt) {
    Deduper deduper;
    Sink sink;
    ClaudeParser parser("/a.jsonl", deduper);
    parser.feed(user("2026-10-20T10:00:00Z"), sink);
    parser.feed(kRetry, sink);
    EXPECT_EQ(parser.phase(), Phase::Generating);
    EXPECT_FALSE(parser.inFlightStartMs().has_value());
    EXPECT_EQ(parser.phaseSinceMs(), kT0);

    parser.feed(R"({"type":"api-request","entrypoint":"cli","cwd":"/w/p","timestamp":"2026-10-20T10:00:10Z"})", sink);
    parser.feed(R"({"type":"assistant","entrypoint":"cli","cwd":"/w/p","message":{"id":"r1","model":"claude-opus-5-5",)"
                R"("role":"assistant","stop_reason":"end_turn","content":[{"type":"text","text":"x"}],)"
                R"("usage":{"input_tokens":1,"output_tokens":40}},"timestamp":"2026-10-20T10:00:14Z"})",
                sink);
    ASSERT_EQ(sink.responses.size(), 1u);
    EXPECT_EQ(sink.responses[0].startMs, kT0 + 10'000);
    EXPECT_EQ(parser.phase(), Phase::Idle);

    parser.feed(kRetry, sink);
    EXPECT_EQ(parser.phase(), Phase::Idle);
}
