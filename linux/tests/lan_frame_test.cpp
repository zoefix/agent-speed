#include <gtest/gtest.h>

#include "agentspeed/lan_feed.hpp"
#include "agentspeed/lan_frame.hpp"
#include "agentspeed/report_body.hpp"
#include "lan_vectors.hpp"

// LAN 3 and 5.4: frames byte-exact from live values, the history rule, the UDP size rule and signatures.
using namespace agentspeed;
using namespace agentspeed::lan;

TEST(LanFrame, VectorsAreByteExact) {
    const auto cases = lan_vectors::section("frames").elements();
    ASSERT_EQ(cases.size(), 7u);
    for (const auto& c : cases) {
        const auto frame = buildFrame(lan_vectors::inputOf(c["input"]));
        EXPECT_EQ(frame.json(), *c["frame"].string()) << *c["name"].string();
        EXPECT_EQ(frame.wire(), *c["frame"].string()) << *c["name"].string();
    }
}

TEST(LanFrame, ChangedIgnoresSeqAndAt) {
    const auto input = lan_vectors::inputOf(lan_vectors::section("frames").at(1)["input"]);
    const auto a = buildFrame(input);
    auto later = input;
    later.seq += 1;
    later.atMs += 500;
    EXPECT_TRUE(a.sameContent(buildFrame(later)));
    later.history.push_back(80);
    EXPECT_FALSE(a.sameContent(buildFrame(later)));
    auto same = input;
    same.live.outputTokS = 75.91;
    EXPECT_TRUE(a.sameContent(buildFrame(same))) << "rounds to the same 75.9";
    auto faster = input;
    faster.live.outputTokS = 75.96;
    EXPECT_FALSE(a.sameContent(buildFrame(faster)));
}

TEST(LanFrame, ComputerName) {
    EXPECT_EQ(computerName("  host  "), "host");
    EXPECT_EQ(computerName("\t \n"), "Agent Speed");
    EXPECT_EQ(computerName(std::string(64, 'a') + " b"), std::string(64, 'a'));
    EXPECT_EQ(computerName(std::string(63, 'a') + " b"), std::string(63, 'a'));
}

TEST(LanFrame, ValuesComeFromTheReportSample) {
    report::Sample sample;
    sample.live = {75.94, 900};
    sample.estimatedShare = 0.41951;
    sample.sources[SourceKind::Codex] = {{75.9, 800}, 0.4195, 0, 1};
    sample.sources[SourceKind::Gemini] = {};
    sample.facts.agents[SourceKind::Codex] = {2, 0, 3};
    sample.facts.agents[SourceKind::ClaudeCode] = {0, 1, 1};
    const auto overall = overallValues(sample);
    EXPECT_DOUBLE_EQ(overall.outputTokS, 75.94);
    EXPECT_DOUBLE_EQ(overall.estimatedShare, 0.41951);
    EXPECT_EQ(overall.generating, 2);
    EXPECT_EQ(overall.tools, 1);
    EXPECT_EQ(overall.active, 4);
    const auto sources = sourceValues(sample);
    ASSERT_EQ(sources.size(), 2u);
    EXPECT_EQ(sources.at(SourceKind::Codex).active, 3);
    EXPECT_EQ(sources.at(SourceKind::Gemini).active, 0);
    EXPECT_FALSE(sources.contains(SourceKind::ClaudeCode)) << "a source the sample does not list is not enabled";
}

TEST(LanHistory, Vectors) {
    const auto cases = lan_vectors::section("history").elements();
    ASSERT_EQ(cases.size(), 5u);
    for (const auto& c : cases) {
        History history;
        std::vector<double> after;
        for (const auto& tick : c["ticks"].elements()) after = history.tick(*tick.at(0).truncated(), *tick.at(1).number());
        std::vector<double> expected;
        for (const auto& v : c["after"].elements()) expected.push_back(*v.number());
        EXPECT_EQ(after, expected) << *c["name"].string();
    }
}

TEST(LanDatagram, Vectors) {
    const auto cases = lan_vectors::section("datagrams").elements();
    ASSERT_EQ(cases.size(), 4u);
    for (const auto& c : cases) {
        const auto name = *c["name"].string();
        json::Document doc;
        const auto frameText = *c["frame"].string();
        ASSERT_TRUE(doc.parse(frameText));
        // The frame of the case, rebuilt from its own members (the vectors give the frame, not its input).
        const auto f = doc.root();
        FrameInput input;
        input.instance = *f["instance"].string();
        input.name = *f["name"].string();
        input.run = *f["run"].string();
        input.seq = *f["seq"].truncated();
        input.atMs = 1'794'042'000'250;
        const auto values = [](const json::Value& v) {
            LiveValues live;
            live.outputTokS = *v["outputTokS"].number();
            live.estimatedShare = *v["estimatedShare"].number();
            live.generating = static_cast<int>(*v["sessions"]["generating"].truncated());
            live.tools = static_cast<int>(*v["sessions"]["tools"].truncated());
            live.active = static_cast<int>(*v["sessions"]["active"].truncated());
            return live;
        };
        input.live = values(f);
        for (const auto& s : f["sources"].elements()) input.sources[*sourceFromId(*s["source"].string())] = values(s);
        for (const auto& v : f["history"]["outputTokS"].elements()) input.history.push_back(*v.number());
        const auto frame = buildFrame(input);
        const auto key = c["key"].isNull() ? std::nullopt : c["key"].string();
        const auto d = datagram(frame, key);
        EXPECT_EQ(d.bytes, *c["datagram"].string()) << name;
        EXPECT_EQ(static_cast<std::int64_t>(d.bytes.size()), *c["bytes"].truncated()) << name;
        EXPECT_LE(d.bytes.size(), kDatagramLimit) << name;
        std::vector<std::string> omitted;
        if (d.historyOmitted) omitted.emplace_back("history");
        if (d.sourcesOmitted) omitted.emplace_back("sources");
        std::vector<std::string> expected;
        for (const auto& o : c["omitted"].elements()) expected.push_back(*o.string());
        EXPECT_EQ(omitted, expected) << name;
        // With history kept, the frame itself is byte-exact too (the reference writes unrounded history inputs such
        // as 1235.0 in the cases that leave it out).
        if (!d.historyOmitted) {
            EXPECT_EQ(frame.json(), frameText) << name;
        }
    }
}

TEST(LanSignature, Vectors) {
    const auto cases = lan_vectors::section("signatures").elements();
    ASSERT_EQ(cases.size(), 3u);
    for (const auto& c : cases) EXPECT_EQ(signature(*c["key"].string(), *c["body"].string()), *c["signature"].string());
}

TEST(LanFeed, SeqHistoryAndChanges) {
    Feed feed("9b1c4e7a2f6d8e03");
    Tick tick{0, 1'794'042'000'000, {75.94, 0.42, 1, 0, 1}, {}};
    auto first = feed.tick(tick, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "box");
    EXPECT_EQ(first.frame->seq, 1);
    EXPECT_TRUE(first.changed);
    EXPECT_EQ(first.frame->history, std::vector<double>{75.9});
    tick.monotonicMs = 500;
    tick.wallMs += 500;
    auto second = feed.tick(tick, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "box");
    EXPECT_EQ(second.frame->seq, 2);
    EXPECT_FALSE(second.changed) << "only seq and at moved";
    tick.monotonicMs = 1000;
    auto third = feed.tick(tick, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "box");
    EXPECT_TRUE(third.changed) << "a new history sample";
    EXPECT_EQ(feed.latest(), third.frame);
    feed.stop();
    EXPECT_FALSE(feed.running());
    const auto single = feed.single(tick, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "box");
    EXPECT_EQ(single->seq, 4);
    EXPECT_FALSE(feed.running());
    tick.monotonicMs = 9000;
    const auto again = feed.tick(tick, "3f2504e0-4f89-41d3-9a0c-0305e82c3301", "box");
    EXPECT_EQ(again.frame->seq, 5) << "seq continues";
    EXPECT_EQ(again.frame->history.size(), 1u) << "the history starts over";
    EXPECT_TRUE(again.changed);
}
