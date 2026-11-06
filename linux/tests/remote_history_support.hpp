#pragma once

#include <gtest/gtest.h>
#include <unistd.h>

#include <filesystem>
#include <memory>
#include <string>

#include "agentspeed/history_store.hpp"
#include "agentspeed/remote_history.hpp"
#include "agentspeed/remote_protocol.hpp"

// Relay history lines (REMOTE 5.2) as the connection delivers them, and a store with the side tables.
namespace remote_history_lines {

using namespace agentspeed;
using namespace agentspeed::remote;

inline const std::string kHost = "8C0D6B0A-1111-4222-8333-944455556666";
inline const std::string kOtherHost = "1F2E3D4C-5555-4666-8777-988899990000";

inline std::string aggregate(int requests = 3, int output = 340, const std::string& model = "gpt-5.5", std::optional<int> cost = 1234) {
    const auto r = std::to_string(requests), o = std::to_string(output);
    const auto micro = cost ? std::to_string(*cost) : std::string("null");
    const std::string unpriced = cost ? "0" : "null";
    return "{\"requests\":" + r + ",\"input\":1200,\"cachedInput\":200,\"cacheWriteInput\":10,\"output\":" + o +
           ",\"reasoning\":20,\"measuredOutput\":" + o + ",\"measuredMs\":2000,\"unfinalized\":0,\"agents\":1,\"costMicroUsd\":" + micro +
           ",\"unpricedRequests\":" + unpriced + ",\"models\":{\"" + model + "\":[" + r + "," + o + "," + o + ",2000," + micro + "," +
           unpriced + "]},\"unpriced\":[],\"modes\":[[\"" + model + "\",\"serviceTier\",\"fast\",\"recorded\",\"openai\",1,100,90,500]]}";
}

inline const std::string kEmptyAggregate =
    "{\"requests\":0,\"input\":0,\"cachedInput\":0,\"cacheWriteInput\":0,\"output\":0,\"reasoning\":0,\"measuredOutput\":0,"
    "\"measuredMs\":0,\"unfinalized\":0,\"agents\":0,\"costMicroUsd\":0,\"unpricedRequests\":0,\"models\":{},\"unpriced\":[],\"modes\":[]}";

inline std::string record(int n = 4, int toolN = 0) {
    std::string bins = "[";
    for (int k = 0; k < 64; ++k) bins += (k ? "," : "") + std::string(k == 10 ? std::to_string(n) : "0");
    bins += "]";
    char ln[32];
    std::snprintf(ln, sizeof ln, "%.1f", toolN * 7.5);
    return "{\"n\":" + std::to_string(n) + ",\"outputSum\":" + std::to_string(n * 100) + ",\"durationMsSum\":" + std::to_string(n * 1000) +
           ",\"latencyBins\":" + bins + ",\"reasonN\":0,\"reasonOutput\":0,\"reasonDurationMsSum\":0,\"cacheN\":" + std::to_string(n) +
           ",\"cacheInput\":1000,\"cacheCached\":800,\"toolN\":" + std::to_string(toolN) + ",\"toolLnSum\":" + ln +
           ",\"toolFailed\":0,\"activeMs\":" + std::to_string(n * 1500) + "}";
}

inline RelayMessage day(const std::string& date, SourceKind source = SourceKind::Codex, const std::string& text = aggregate(),
                        std::optional<std::string> digest = std::nullopt) {
    HistoryDay line;
    line.day = date;
    line.source = source;
    const auto decoded = decodeAggregateText(text);
    EXPECT_TRUE(decoded) << text;
    if (decoded) line.aggregate = *decoded;
    line.aggregateJson = text;
    line.digest = digest ? *digest : std::string(15, 'a') + date.back();
    return line;
}

inline RelayMessage perfLine(const std::string& date, SourceKind source = SourceKind::Codex, const std::string& model = "gpt-5.5",
                         const std::string& text = record()) {
    HistoryPerf line;
    line.day = date;
    line.source = source;
    line.model = model;
    const auto decoded = decodePerfRecordText(text);
    EXPECT_TRUE(decoded) << text;
    if (decoded) line.record = *decoded;
    line.recordJson = text;
    return line;
}

inline RelayMessage done() { return HistoryDone{}; }

inline RelayMessage historyFailed() {
    RelayError error;
    error.code = RelayError::Code::HistoryFailed;
    error.codeText = "historyFailed";
    error.detail = "x";
    return error;
}

inline HostEvent event(RelayMessage message, const std::string& host = kHost, int connection = 1) {
    return HostEvent{host, connection, HostEvent::Message{std::move(message), false}};
}

inline HostEvent status(StatusCode code, const std::string& host = kHost, int connection = 1) {
    HostStatus value;
    value.status = Status::of(code);
    return HostEvent{host, connection, value};
}

inline HostEvent due(const std::string& host = kHost, int connection = 1) { return HostEvent{host, connection, HostEvent::HistoryDue{}}; }

inline HistoryEffect receive(HistoryLedger& ledger, const std::vector<HostEvent>& events, history::Store& store) {
    HistoryEffect last;
    for (const auto& e : events) last = ledger.receive(e, store);
    return last;
}

inline HistoryEffect request(const std::string& host, std::map<std::string, std::string> digests) {
    return HistoryEffect::request(host, std::move(digests));
}

// A store in its own temporary folder, with the side tables.
struct TempStore {
    std::filesystem::path folder;
    std::unique_ptr<history::Store> store;

    TempStore() {
        std::string pattern = (std::filesystem::temp_directory_path() / "agentspeed-remote-history-XXXXXX").string();
        folder = ::mkdtemp(pattern.data());
        store = history::Store::open(path());
        store->ensureRemoteTables();
    }
    ~TempStore() {
        store.reset();
        std::error_code ec;
        std::filesystem::remove_all(folder, ec);
    }
    std::string path() const { return (folder / "history.sqlite").string(); }
    history::Store& operator*() { return *store; }
    std::int64_t count(const std::string& table) const { return store->connection().scalarInt64("SELECT count(*) FROM " + table); }
};

inline std::set<SourceKind> codex() { return {SourceKind::Codex}; }

}  // namespace remote_history_lines
