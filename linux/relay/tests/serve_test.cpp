#include <gtest/gtest.h>
#include <poll.h>
#include <unistd.h>

#include <future>
#include <set>
#include <thread>

#include "agentspeed/json.hpp"
#include "serve.hpp"

using namespace agentspeed;
using namespace agentspeed::relay;

namespace {

constexpr std::int64_t kClock = 1792490400000;

// serve() on two pipes, as the executable runs it on stdin and stdout.
class Served {
public:
    explicit Served(std::chrono::milliseconds startTimeout, std::chrono::milliseconds inputSilence = std::chrono::seconds(150)) {
        EXPECT_EQ(::pipe(in_), 0);
        EXPECT_EQ(::pipe(out_), 0);
        ServeOptions options;
        options.folder = "/nonexistent-relay-folder";
        options.version = "9.9.9";
        options.arch = "x86_64";
        options.clock = [] { return kClock; };
        options.startTimeout = startTimeout;
        options.inputSilence = inputSilence;
        result_ = std::async(std::launch::async, [this, options] { return serve(in_[0], out_[1], options); });
    }
    ~Served() {
        closeInput();
        if (result_.valid()) result_.wait();
        for (const int fd : {in_[0], out_[0], out_[1]}) ::close(fd);
    }
    void send(const std::string& line) const { ASSERT_GT(::write(in_[1], (line + "\n").data(), line.size() + 1), 0); }
    void closeInput() {
        if (in_[1] >= 0) ::close(in_[1]);
        in_[1] = -1;
    }
    std::string readLine(int timeoutMs = 5000) {
        while (true) {
            if (const auto newline = buffer_.find('\n'); newline != std::string::npos) {
                auto line = buffer_.substr(0, newline);
                buffer_.erase(0, newline + 1);
                return line;
            }
            pollfd fd{out_[0], POLLIN, 0};
            if (::poll(&fd, 1, timeoutMs) <= 0) return {};
            char chunk[4096];
            const auto n = ::read(out_[0], chunk, sizeof chunk);
            if (n <= 0) return {};
            buffer_.append(chunk, static_cast<std::size_t>(n));
        }
    }
    int result() { return result_.get(); }
    bool finished() const { return result_.wait_for(std::chrono::seconds(0)) == std::future_status::ready; }
    // Reads until a line contains `text`; false when the output ends or stays quiet for `timeoutMs`.
    bool readUntil(std::string_view text, int timeoutMs) {
        for (auto line = readLine(timeoutMs); !line.empty(); line = readLine(timeoutMs)) {
            if (line.find(text) != std::string::npos) return true;
        }
        return false;
    }

private:
    int in_[2] = {-1, -1}, out_[2] = {-1, -1};
    std::string buffer_;
    std::future<int> result_;
};

}  // namespace

TEST(RelayServe, AnswersPingsBeforeStartAndStopsWithoutAStart) {
    Served served(std::chrono::milliseconds(600));
    served.send(R"({"t":"today","todayStartMs":1})");
    served.send("garbage");
    served.send(R"({"t":"ping","id":11})");
    EXPECT_EQ(served.readLine(), R"({"t":"clock","id":11,"now":1792490400000})");
    const auto error = served.readLine();
    json::Document doc;
    ASSERT_TRUE(doc.parse(error)) << error;
    EXPECT_TRUE(doc.root()["t"].equals("error"));
    EXPECT_TRUE(doc.root()["code"].equals("badStart"));
    EXPECT_EQ(served.result(), 1);
}

TEST(RelayServe, EndsQuietlyWhenTheInputClosesBeforeStart) {
    Served served(std::chrono::seconds(30));
    served.closeInput();
    EXPECT_EQ(served.result(), 0);
    EXPECT_EQ(served.readLine(500), "");
}

TEST(RelayServe, SaysHelloThenRefusesAnotherProtocol) {
    Served served(std::chrono::seconds(30));
    served.send(R"({"t":"start","protocol":2,"todayStartMs":1792454400000,"sources":[],"zone":{"id":"UTC","tzif":""},"prices":{}})");
    EXPECT_EQ(served.readLine(), R"({"t":"hello","protocol":1,"version":"9.9.9","arch":"x86_64","now":1792490400000})");
    EXPECT_NE(served.readLine().find(R"("code":"badStart")"), std::string::npos);
    EXPECT_EQ(served.result(), 1);
}

TEST(RelayServe, SaysHelloBeforeAnyClockThatFollowsTheStart) {
    Served served(std::chrono::seconds(30));
    served.send(std::string(R"({"t":"start","protocol":2,"todayStartMs":1792454400000,"sources":[],"zone":{"id":"UTC","tzif":""},"prices":{}})") +
                "\n" + R"({"t":"ping","id":4})");
    EXPECT_EQ(served.readLine().rfind(R"({"t":"hello",)", 0), 0u);
    std::set<std::string> rest{served.readLine().substr(0, 12), served.readLine().substr(0, 12)};
    EXPECT_EQ(rest, (std::set<std::string>{R"({"t":"clock")", R"({"t":"error")"}));
    EXPECT_EQ(served.result(), 1);
}

namespace {
const std::string kStart = R"({"t":"start","protocol":1,"todayStartMs":1792454400000,"sources":[],"zone":{"id":"UTC","tzif":""},"prices":{}})";
}

TEST(RelayServe, StopsWhenTheAppFallsSilentAfterStart) {
    Served served(std::chrono::seconds(30), std::chrono::milliseconds(600));
    served.send(kStart);
    EXPECT_TRUE(served.readUntil(R"("code":"silence")", 3000));
    EXPECT_EQ(served.result(), 1);
}

TEST(RelayServe, PingsKeepTheRelayRunning) {
    Served served(std::chrono::seconds(30), std::chrono::milliseconds(600));
    served.send(kStart);
    for (int i = 0; i < 8; ++i) {
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        served.send(R"({"t":"ping","id":)" + std::to_string(i) + "}");
    }
    EXPECT_FALSE(served.finished());
    served.closeInput();
    EXPECT_EQ(served.result(), 0);
}
