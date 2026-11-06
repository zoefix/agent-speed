#include <gtest/gtest.h>
#include <unistd.h>

#include <csignal>
#include <thread>

#include "channel.hpp"

using namespace agentspeed::relay;

namespace {

struct Pipe {
    int fds[2] = {-1, -1};
    Pipe() { EXPECT_EQ(::pipe(fds), 0); }
    ~Pipe() {
        for (const int fd : fds) {
            if (fd >= 0) ::close(fd);
        }
    }
    void closeWrite() {
        ::close(fds[1]);
        fds[1] = -1;
    }
    void write(const std::string& bytes) const { ASSERT_EQ(::write(fds[1], bytes.data(), bytes.size()), static_cast<ssize_t>(bytes.size())); }
    std::string readAll() const {
        std::string out;
        char buffer[4096];
        ssize_t n;
        while ((n = ::read(fds[0], buffer, sizeof buffer)) > 0) out.append(buffer, static_cast<std::size_t>(n));
        return out;
    }
};

}  // namespace

TEST(RelayChannel, ReadsLinesAcrossReadsAndSkipsOverlongOnes) {
    Pipe in;
    std::vector<std::string> lines;
    std::thread writer([&] {
        in.write("one\ntw");
        in.write("o\n" + std::string(100, 'x'));
        in.write(std::string(100, 'x') + "\nthree\r\n\nlast");
        in.closeWrite();
    });
    const bool ended = readLines(in.fds[0], -1, 64, [&](std::string_view line) { lines.emplace_back(line); });
    writer.join();
    EXPECT_TRUE(ended);
    EXPECT_EQ(lines, (std::vector<std::string>{"one", "two", "three\r", "", "last"}));
}

TEST(RelayChannel, StopsReadingWhenAskedTo) {
    Pipe in, stop;
    std::thread stopper([&] { stop.write("x"); });
    EXPECT_FALSE(readLines(in.fds[0], stop.fds[0], 64, [](std::string_view) {}));
    stopper.join();
}

TEST(RelayChannel, WritesWholeLinesAndNoticesABrokenPipe) {
    Pipe out;
    Output output(out.fds[1]);
    EXPECT_TRUE(output.line("a"));
    EXPECT_TRUE(output.lines({"b", "", std::string(kMaxOutputLine + 1, 'z'), "c"}));
    {
        LineBatch batch(output);
        batch.add("d");
        batch.add("");
    }
    out.closeWrite();
    EXPECT_EQ(out.readAll(), "a\nb\nc\nd\n");

    Pipe closed;
    ::close(closed.fds[0]);
    closed.fds[0] = -1;
    std::signal(SIGPIPE, SIG_IGN);
    Output broken(closed.fds[1]);
    EXPECT_FALSE(broken.line("x"));
    EXPECT_TRUE(broken.broken());
}

TEST(RelayChannel, HandsCommandsToTheMainLoop) {
    Inbox inbox;
    std::vector<Command> got;
    EXPECT_FALSE(inbox.wait(std::chrono::steady_clock::now() + std::chrono::milliseconds(10), got));
    EXPECT_TRUE(got.empty());
    inbox.push(WatchCommand{true});
    inbox.push(TodayCommand{5});
    EXPECT_FALSE(inbox.wait(std::chrono::steady_clock::now() + std::chrono::seconds(1), got));
    ASSERT_EQ(got.size(), 2u);
    EXPECT_TRUE(std::get<WatchCommand>(got[0]).on);
    inbox.close();
    got.clear();
    EXPECT_TRUE(inbox.wait(std::chrono::steady_clock::now() + std::chrono::seconds(1), got));
}
