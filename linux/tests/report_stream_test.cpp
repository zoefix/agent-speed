#include <gtest/gtest.h>

#include "agentspeed/report_body.hpp"
#include "agentspeed/report_stream.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace agentspeed::report::stream;
using namespace fixtures;

namespace {

struct Vectors {
    std::string text = readText(fixtureDir() / "report-vectors.json");
    json::Document doc;
    Vectors() { EXPECT_TRUE(doc.parse(text)); }
    json::Value ws() const { return doc.root()["websocket"]; }
    json::Value keys() const { return doc.root()["keys"]; }
};

const Vectors& vectors() {
    static const Vectors v;
    return v;
}

StreamSettings settingsOf(const json::Value& config) {
    StreamSettings s;
    const std::array<std::pair<const char*, int*>, 6> fields = {std::pair{"minIntervalMs", &s.minIntervalMs}, std::pair{"maxInFlight", &s.maxInFlight},
                                                              std::pair{"ackTimeoutMs", &s.ackTimeoutMs}, std::pair{"maxMessageBytes", &s.maxMessageBytes},
                                                              std::pair{"pingIntervalSeconds", &s.pingIntervalSeconds},
                                                              std::pair{"pongTimeoutSeconds", &s.pongTimeoutSeconds}};
    for (const auto& [key, target] : fields)
        if (config[key].valid()) *target = static_cast<int>(*config[key].truncated());
    return s;
}

std::string limitsJson(const StreamSettings& s) {
    return "{\"minIntervalMs\":" + std::to_string(s.minIntervalMs) + ",\"maxInFlight\":" + std::to_string(s.maxInFlight) +
           ",\"ackTimeoutMs\":" + std::to_string(s.ackTimeoutMs) + ",\"maxMessageBytes\":" + std::to_string(s.maxMessageBytes) +
           ",\"pingIntervalSeconds\":" + std::to_string(s.pingIntervalSeconds) + ",\"pongTimeoutSeconds\":" + std::to_string(s.pongTimeoutSeconds) + "}";
}

struct Event {
    json::Value value;
    std::int64_t at = 0;
};

// simulate_stream of report_websocket.py: wake-ups the scheduler asks for become ticks without new data.
void simulate(const json::Value& item, std::vector<std::string>& log, std::unique_ptr<StreamScheduler>& out) {
    const auto config = item["config"];
    const auto heartbeat = config["heartbeatSeconds"].valid() ? *config["heartbeatSeconds"].truncated() : 60;
    out = std::make_unique<StreamScheduler>(settingsOf(config), heartbeat * 1000, !config["available"].valid() || config["available"].isTrue(),
                                            [] { return 1.0; });
    auto& scheduler = *out;
    std::vector<Event> events;
    for (const auto& event : item["events"].elements()) {
        const auto at = *event["at"].truncated();
        if (event["type"].equals("tick") && event["until"].valid()) {
            const auto step = event["step"].valid() ? *event["step"].truncated() : 500;
            for (auto t = at; t <= *event["until"].truncated(); t += step) events.push_back({event, t});
        } else {
            events.push_back({event, at});
        }
    }
    std::stable_sort(events.begin(), events.end(), [](const Event& a, const Event& b) { return a.at < b.at; });
    std::set<std::int64_t> tickTimes;
    for (const auto& e : events)
        if (e.value["type"].equals("tick")) tickTimes.insert(e.at);
    bool lastActive = false;
    const auto record = [&](const std::vector<stream::Action>& actions) {
        for (const auto& a : actions) log.push_back(a.log());
    };
    const auto wakeUntil = [&](std::int64_t limit) {
        while (scheduler.wakeAt() && *scheduler.wakeAt() < limit) {
            const auto at = *scheduler.wakeAt();
            scheduler.clearWake();
            if (!tickTimes.contains(at)) record(scheduler.tick(at, lastActive, false, std::nullopt));
        }
    };
    for (const auto& [event, t] : events) {
        wakeUntil(t);
        const auto type = *event["type"].string();
        if (scheduler.wakeAt() == t && type == "tick") scheduler.clearWake();
        if (type == "tick") {
            lastActive = event["active"].isTrue();
            std::optional<std::int64_t> observed;
            if (event["observedAgo"].valid()) observed = t - *event["observedAgo"].truncated();
            std::vector<std::string> names;
            for (const auto& name : event["events"].elements()) names.push_back(*name.string());
            record(scheduler.tick(t, lastActive, event["changed"].isTrue(), observed, names));
        } else if (type == "open") {
            record(scheduler.opened(t));
        } else if (type == "ready") {
            auto limits = scheduler.settings();
            const auto given = event["limits"];
            const auto changed = settingsOf(given);
            const StreamSettings defaults;
            if (given["minIntervalMs"].valid()) limits.minIntervalMs = changed.minIntervalMs;
            if (given["maxInFlight"].valid()) limits.maxInFlight = changed.maxInFlight;
            if (given["ackTimeoutMs"].valid()) limits.ackTimeoutMs = changed.ackTimeoutMs;
            if (given["maxMessageBytes"].valid()) limits.maxMessageBytes = changed.maxMessageBytes;
            if (given["pingIntervalSeconds"].valid()) limits.pingIntervalSeconds = changed.pingIntervalSeconds;
            if (given["pongTimeoutSeconds"].valid()) limits.pongTimeoutSeconds = changed.pongTimeoutSeconds;
            record(scheduler.ready(t, limits));
        } else if (type == "ack") {
            record(scheduler.ack(t, static_cast<int>(*event["seq"].truncated()), event["status"].string().value_or("accepted"),
                                 !event["sha"].valid() || event["sha"].equals("match")));
        } else if (type == "error") {
            std::optional<int> seq;
            if (event["seq"].isNumber()) seq = static_cast<int>(*event["seq"].truncated());
            std::optional<std::int64_t> retry;
            if (event["retryAfter"].isNumber()) retry = event["retryAfter"].truncated();
            record(scheduler.error(t, seq, *event["code"].string(), static_cast<int>(*event["httpStatus"].truncated()), retry, event["fatal"].isTrue()));
        } else if (type == "close") {
            record(scheduler.closed(t, static_cast<int>(*event["code"].truncated())));
        } else if (type == "reject") {
            std::optional<std::int64_t> retry;
            if (event["retryAfter"].isNumber()) retry = event["retryAfter"].truncated();
            record(scheduler.rejected(t, static_cast<int>(*event["status"].truncated()), event["code"].string(), retry));
        } else if (type == "settings") {
            const auto settings = event["settings"];
            record(scheduler.settingsChanged(t, settings.isObject() ? std::optional<StreamSettings>(settingsOf(settings)) : std::nullopt));
        } else if (type == "retry") {
            record(scheduler.retry(t));
        } else {
            ADD_FAILURE() << "unknown event " << type;
        }
    }
    wakeUntil(INT64_MAX);
}

std::string countersJson(const stream::Counters& c) {
    std::string out = "{";
    for (const auto& [name, value] : std::vector<std::pair<const char*, std::int64_t>>{{"built", c.built},
                                                                                      {"accepted", c.accepted},
                                                                                      {"duplicate", c.duplicate},
                                                                                      {"rejected", c.rejected},
                                                                                      {"stale", c.stale},
                                                                                      {"saturated", c.saturated},
                                                                                      {"unknown", c.unknown},
                                                                                      {"unmatched", c.unmatched}}) {
        if (value) out += (out.size() > 1 ? "," : "") + std::string("\"") + name + "\":" + std::to_string(value);
    }
    return out + "}";
}

}  // namespace

TEST(ReportStream, SchedulerTimelines) {
    const auto cases = vectors().ws()["scheduler"].elements();
    EXPECT_EQ(cases.size(), 22U);
    for (const auto& item : cases) {
        const auto name = *item["name"].string();
        SCOPED_TRACE(name);
        std::vector<std::string> log;
        std::unique_ptr<StreamScheduler> scheduler;
        simulate(item, log, scheduler);
        const auto expect = item["expect"];
        std::vector<std::string> want;
        for (const auto& line : expect["log"].elements()) want.push_back(*line.string());
        EXPECT_EQ(log, want);
        if (expect["counters"].valid()) {
            EXPECT_EQ(countersJson(scheduler->counters()), compactJson(expect["counters"]));
        }
        if (expect["latency"].valid()) {
            std::string latency = "[";
            for (const auto sample : scheduler->latency()) latency += (latency.size() > 1 ? "," : "") + std::to_string(sample);
            EXPECT_EQ(latency + "]", compactJson(expect["latency"]));
        }
        if (const auto state = expect["state"]; state.valid()) {
            EXPECT_EQ(connectionName(scheduler->connection()), *state["connection"].string());
            EXPECT_EQ(scheduler->seq(), *state["seq"].truncated());
            EXPECT_EQ(scheduler->failures(), *state["failures"].truncated());
            EXPECT_EQ(scheduler->stop(), state["stop"].string());
            EXPECT_EQ(scheduler->lastDelivered(), state["lastDelivered"].isNull() ? std::nullopt : state["lastDelivered"].truncated());
        }
    }
}

TEST(ReportStream, Messages) {
    for (const auto& item : vectors().ws()["messages"].elements()) {
        const auto name = *item["name"].string();
        SCOPED_TRACE(name);
        const auto max = item["maxMessageBytes"].valid() ? static_cast<std::size_t>(*item["maxMessageBytes"].truncated()) : 65'536U;
        const auto text = *item["text"].string();
        const auto got = parseMessage(text, max);
        const auto expect = item["expect"];
        if (expect.isString()) {
            EXPECT_FALSE(got) << "expected invalid";
            continue;
        }
        ASSERT_TRUE(got);
        const auto type = *expect["type"].string();
        if (type == "other") {
            EXPECT_EQ(got->type, Message::Type::Other);
        } else if (type == "ready") {
            EXPECT_EQ(got->type, Message::Type::Ready);
            EXPECT_EQ(limitsJson(got->limits), compactJson(expect["limits"]));
        } else if (type == "ack") {
            EXPECT_EQ(got->type, Message::Type::Ack);
            EXPECT_EQ(got->runId, *expect["runId"].string());
            EXPECT_EQ(got->seq, *expect["seq"].truncated());
            EXPECT_EQ(got->status, *expect["status"].string());
            EXPECT_EQ(got->bodySha256, *expect["bodySha256"].string());
            EXPECT_EQ(got->rank, expect["rank"].isNull() ? std::nullopt : expect["rank"].truncated());
        } else {
            EXPECT_EQ(got->type, Message::Type::Error);
            EXPECT_EQ(got->runId, expect["runId"].string().value_or(""));
            EXPECT_EQ(got->seq, expect["seq"].isNull() ? 0 : *expect["seq"].truncated());
            EXPECT_EQ(got->httpStatus, *expect["httpStatus"].truncated());
            EXPECT_EQ(got->code, *expect["code"].string());
            EXPECT_EQ(got->retryAfterSeconds, expect["retryAfterSeconds"].isNull() ? std::nullopt : expect["retryAfterSeconds"].truncated());
            EXPECT_EQ(got->serverTime, expect["serverTime"].isNull() ? std::nullopt : expect["serverTime"].truncated());
            EXPECT_EQ(got->fatal, expect["fatal"].isTrue());
        }
    }
}

TEST(ReportStream, RejectionsLatencyEncodingAndRealtime) {
    for (const auto& item : vectors().ws()["rejections"].elements()) {
        SCOPED_TRACE(compactJson(item));
        const auto body = item["body"].string();
        const auto retry = item["retryAfter"].string();
        const auto got = classifyRejection(static_cast<int>(*item["status"].truncated()), body ? std::optional<std::string_view>(*body) : std::nullopt,
                                           retry ? std::optional<std::string_view>(*retry) : std::nullopt);
        EXPECT_EQ(got.code, item["expect"]["code"].string().value_or(""));
        EXPECT_EQ(got.retryAfterSeconds, item["expect"]["retryAfterSeconds"].isNull() ? std::nullopt : item["expect"]["retryAfterSeconds"].truncated());
    }
    for (const auto& item : vectors().ws()["latency"].elements()) {
        std::vector<std::int64_t> samples;
        for (const auto& s : item["samples"].elements()) samples.push_back(*s.truncated());
        EXPECT_EQ(percentile(samples, 50), item["p50"].isNull() ? std::nullopt : item["p50"].truncated());
        EXPECT_EQ(percentile(samples, 95), item["p95"].isNull() ? std::nullopt : item["p95"].truncated());
    }
    for (const auto& item : vectors().ws()["encoding"].elements()) {
        Headers headers;
        for (const auto& [name, value] : item["headers"].members()) headers.emplace_back(name, *value.string());
        const auto body = *item["body"].string();
        EXPECT_EQ(reportMessage(headers, body), *item["message"].string());
        EXPECT_LE(reportMessage(headers, body).size(), messageBytes(body) + 0);
    }
    for (const auto& item : vectors().ws()["realtime"].elements()) {
        const auto age = item["observationAgeMs"].isNull() ? std::nullopt : item["observationAgeMs"].truncated();
        EXPECT_EQ(realtime(item["sourceMode"].equals("mixed"), *item["sentAtMs"].truncated(), age).compact(), compactJson(item["expect"]));
    }
    std::vector<std::int64_t> backoff;
    for (const auto& v : vectors().ws()["backoff"].elements()) backoff.push_back(*v.truncated());
    EXPECT_EQ(backoff, (std::vector<std::int64_t>{500, 1000, 2000, 4000, 8000, 16000, 30000, 30000, 30000}));
}

TEST(ReportStream, DiscoveryUrlAndHandshake) {
    for (const auto& item : vectors().ws()["discovery"].elements()) {
        SCOPED_TRACE(*item["name"].string());
        const auto parsed = parseDiscovery(item["document"], "https://example.com/.well-known/agent-speed-report");
        ASSERT_TRUE(parsed);
        if (item["expect"].isNull()) {
            EXPECT_FALSE(parsed->websocket);
        } else {
            ASSERT_TRUE(parsed->websocket);
            EXPECT_EQ(limitsJson(*parsed->websocket), compactJson(item["expect"]));
        }
    }
    for (const auto& item : vectors().ws()["url"].elements()) EXPECT_EQ(websocketUrl(*item["api"].string()), *item["expect"].string());
    const auto handshake = vectors().ws()["handshake"];
    const auto key = PrivateKey::fromHex(*vectors().keys()["device"]["d"].string())->publicKey();
    std::map<std::string, std::string> headers;
    for (const auto& [name, value] : handshake["headers"].members()) {
        auto lower = name;
        std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
        headers[lower] = *value.string();
    }
    EXPECT_EQ(authorityOf(*handshake["url"].string()), *handshake["authority"].string());
    EXPECT_EQ(pathOf(*handshake["url"].string()), *handshake["path"].string());
    EXPECT_EQ(verifyRequest(key, "GET", *handshake["authority"].string(), *handshake["path"].string(), headers, ""), Verdict::Valid);
    const auto report = vectors().ws()["report"];
    EXPECT_EQ(sha256Hex(*report["body"].string()), *report["bodySha256"].string());
}
