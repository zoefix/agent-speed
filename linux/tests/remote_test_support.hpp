#pragma once

#include <algorithm>
#include <cstdint>
#include <functional>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "agentspeed/remote_connection.hpp"
#include "agentspeed/remote_transport.hpp"

// A scripted relay instead of `ssh`, and a clock the test advances by hand (REMOTE 10: app tests).
namespace remote_test {

using namespace agentspeed::remote;

inline constexpr std::int64_t kNow = 1'780'000'000'000;  // 2026-05-28
inline const std::string kHost = "8C0D6B0A-1111-4222-8333-444455556666";

class FakeScheduler : public Scheduler {
public:
    std::int64_t now = kNow;
    std::int64_t nowMs() const override { return now; }
    TimerId schedule(std::int64_t atMs, std::function<void()> work) override {
        const auto id = next_++;
        timers_[id] = {atMs, std::move(work)};
        return id;
    }
    void cancel(TimerId id) override { timers_.erase(id); }
    // Moves the clock forward, firing every timer that falls due on the way, in deadline order.
    void advance(std::int64_t ms) {
        const auto target = now + ms;
        while (true) {
            auto due = timers_.end();
            for (auto it = timers_.begin(); it != timers_.end(); ++it) {
                if (it->second.first <= target && (due == timers_.end() || it->second.first < due->second.first)) due = it;
            }
            if (due == timers_.end()) break;
            now = std::max(now, due->second.first);
            auto work = std::move(due->second.second);
            timers_.erase(due);
            work();
        }
        now = target;
    }
    std::size_t pending() const { return timers_.size(); }

private:
    TimerId next_ = 1;
    std::map<TimerId, std::pair<std::int64_t, std::function<void()>>> timers_;
};

class FakeProcess : public Process {
public:
    ProcessRequest request;
    ProcessHandlers handlers;
    std::vector<std::string> written;
    bool inputClosed = false, terminated = false, killed = false, exited = false;

    void write(std::string data, std::function<void(bool)> done) override {
        const bool ok = !inputClosed && !exited;
        if (ok) written.push_back(std::move(data));
        if (done) done(ok);
    }
    void closeInput() override { inputClosed = true; }
    void terminate() override { terminated = true; }
    void kill() override { killed = true; }

    // The remote command: the last argument.
    const std::string& command() const { return request.arguments.back(); }
    bool isProbe() const { return command().find("echo end") != std::string::npos; }
    bool isUpload() const { return command().find(".upload-") != std::string::npos; }
    bool isRun() const { return command().find(" serve --folder ") != std::string::npos; }

    void out(const std::string& text) {
        if (handlers.stdoutData) handlers.stdoutData(text);
    }
    void line(const std::string& text) { out(text + "\n"); }
    void err(const std::string& text) {
        if (handlers.stderrData) handlers.stderrData(text);
    }
    void exit(int status) {
        if (exited) return;
        exited = true;
        if (handlers.exit) handlers.exit(status);
    }
    // Lines the app wrote to the relay, joined.
    std::string input() const {
        std::string all;
        for (const auto& w : written) all += w;
        return all;
    }
};

class FakeTransport : public Transport {
public:
    bool sshAvailable = true;
    std::vector<std::shared_ptr<FakeProcess>> launched;
    // Runs right after a launch, e.g. to answer the probe.
    std::function<void(FakeProcess&)> onLaunch;

    bool available(const std::string&) const override { return sshAvailable; }
    std::shared_ptr<Process> launch(const ProcessRequest& request, ProcessHandlers handlers) override {
        auto process = std::make_shared<FakeProcess>();
        process->request = request;
        process->handlers = std::move(handlers);
        launched.push_back(process);
        if (onLaunch) onLaunch(*process);
        return process;
    }
    FakeProcess& last() { return *launched.back(); }
};

inline std::string helloLine(std::int64_t nowMs = kNow, std::int64_t protocol = 1) {
    return R"({"t":"hello","protocol":)" + std::to_string(protocol) + R"(,"version":"1.6.1","arch":"x86_64","now":)" + std::to_string(nowMs) + "}";
}

inline std::string agentLine(const std::string& id, const std::string& phase = "idle", std::int64_t lastLineMs = kNow - 1000,
                             const std::string& extra = "") {
    return R"({"t":"agent","a":{"id":")" + id + R"(","source":"codex","project":"proj","cwd":"/home/dev/proj","model":"gpt-5","subagent":false,"phase":")" +
           phase + R"(","inFlightStartMs":null,"lastLineMs":)" + std::to_string(lastLineMs) +
           R"(,"modes":[],"sessionModes":[],"configuredModes":[],"actions":[)" + extra + "]}}";
}

inline std::string responseLine(const std::string& key, const std::string& agent, std::int64_t endMs, std::int64_t output = 100,
                                bool completion = true, std::int64_t startMs = 0) {
    const auto start = startMs ? startMs : endMs - 2000;
    return R"({"t":"response","r":{"key":")" + key + R"(","source":"codex","agent":")" + agent + R"(","model":"gpt-5","startMs":)" +
           std::to_string(start) + R"(,"endMs":)" + std::to_string(endMs) +
           R"(,"firstMs":null,"input":1000,"cachedInput":200,"cacheWriteInput":0,"cacheWrite1hInput":0,"output":)" + std::to_string(output) +
           R"(,"reasoning":10,"requestCount":1,"modes":[],"final":true,"provider":null,"fast":false},"completion":)" +
           (completion ? "true" : "false") + R"(,"project":"proj"})";
}

inline std::string clockLine(std::int64_t id, std::int64_t nowMs) {
    return R"({"t":"clock","id":)" + std::to_string(id) + R"(,"now":)" + std::to_string(nowMs) + "}";
}

inline const std::string kProbeOutput = "system Linux x86_64\ntool sha256sum\nend\n";

}  // namespace remote_test
