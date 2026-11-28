#include <gtest/gtest.h>

#include <string>
#include <vector>

#include "agentspeed/claude_parser.hpp"

using namespace agentspeed;

// METRICS 4.1 assistant step 6: an earlier line of a finished message appended again keeps the phase (issue #79).
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

std::string request(const char* time) {
    return std::string(R"({"type":"api-request","entrypoint":"cli","cwd":"/w/p","timestamp":")") + time + R"("})";
}

std::string assistant(const char* time, const char* id, int output, const char* stop) {
    return std::string(R"({"type":"assistant","entrypoint":"cli","cwd":"/w/p","message":{"id":")") + id +
           R"(","model":"claude-opus-5-5","role":"assistant","stop_reason":)" + stop +
           R"(,"content":[{"type":"text","text":"x"}],"usage":{"input_tokens":1,"output_tokens":)" + std::to_string(output) +
           R"(}},"timestamp":")" + time + R"("})";
}

}  // namespace

TEST(ClaudeReplays, AnOlderSnapshotAfterEndTurnDoesNotReopenTheRequest) {
    Deduper deduper;
    Sink sink;
    ClaudeParser parser("/a.jsonl", deduper);
    const auto partial = assistant("2026-10-20T10:00:01Z", "same", 10, "null");
    parser.feed(request("2026-10-20T10:00:00Z"), sink);
    parser.feed(partial, sink);
    parser.feed(assistant("2026-10-20T10:00:02Z", "same", 200, R"("end_turn")"), sink);
    ASSERT_EQ(parser.phase(), Phase::Idle);

    parser.feed(partial, sink);
    ASSERT_EQ(sink.responses.size(), 3u);
    EXPECT_TRUE(sink.responses.back().final);
    EXPECT_EQ(sink.responses.back().tokens.output, 200);
    EXPECT_EQ(sink.responses.back().endMs, kT0 + 2000);
    EXPECT_EQ(parser.phase(), Phase::Idle);
    EXPECT_FALSE(parser.inFlightStartMs().has_value());
    EXPECT_EQ(parser.phaseSinceMs(), kT0 + 2000);
}

TEST(ClaudeReplays, AReplayAfterTheNextRequestKeepsItsInFlightStart) {
    Deduper deduper;
    Sink sink;
    ClaudeParser parser("/a.jsonl", deduper);
    const auto partial = assistant("2026-10-20T10:00:01Z", "m1", 10, "null");
    const auto done = assistant("2026-10-20T10:00:02Z", "m1", 200, R"("end_turn")");
    for (const auto& line : {user("2026-10-20T10:00:00Z"), partial, done, user("2026-10-20T10:00:05Z"),
                             request("2026-10-20T10:00:05.100Z"), done, partial}) {
        parser.feed(line, sink);
    }
    EXPECT_EQ(parser.phase(), Phase::Generating);
    EXPECT_EQ(parser.inFlightStartMs(), kT0 + 5100);
}

TEST(ClaudeReplays, LinesBeforeTheFirstStopReasonAndLaterLinesAreNoReplays) {
    Deduper deduper;
    Sink sink;
    ClaudeParser parser("/a.jsonl", deduper);
    // A message's lines can be out of order; the first one with a stop reason still sets the phase.
    parser.feed(user("2026-10-20T10:00:00Z"), sink);
    parser.feed(assistant("2026-10-20T10:00:02Z", "m1", 10, "null"), sink);
    parser.feed(assistant("2026-10-20T10:00:01.500Z", "m1", 30, R"("tool_use")"), sink);
    EXPECT_EQ(parser.phase(), Phase::Tools);

    parser.feed(assistant("2026-10-20T10:00:03Z", "m1", 30, "null"), sink);
    EXPECT_EQ(parser.phase(), Phase::Generating);
    EXPECT_EQ(parser.inFlightStartMs(), kT0);
}
