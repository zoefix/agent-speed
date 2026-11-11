#include "serve.hpp"

#include <sys/utsname.h>
#include <unistd.h>

#include <atomic>
#include <memory>
#include <thread>
#include <type_traits>
#include <variant>

#include "agentspeed/price_book.hpp"
#include "channel.hpp"
#include "history_worker.hpp"
#include "live_session.hpp"
#include "protocol.hpp"

namespace agentspeed::relay {

namespace {

using SteadyClock = std::chrono::steady_clock;

// The reading thread (REMOTE 5.3): `ping` is answered here at once, every other command goes to the main loop. The
// first valid `start` is answered here with `hello` too, so no later `clock` can come before it (5.2).
class Reader {
public:
    Reader(int fd, Output& out, Inbox& inbox, const ServeOptions& options) {
        if (::pipe(stop_) != 0) stop_[0] = stop_[1] = -1;
        thread_ = std::thread([this, fd, &out, &inbox, &options] {
            bool started = false;
            const bool ended = readLines(fd, stop_[0], kMaxInputLine, [&](std::string_view line) {
                lastLine_.store(SteadyClock::now().time_since_epoch().count(), std::memory_order_relaxed);
                auto command = decodeCommand(line);
                if (!command) return;
                if (const auto* ping = std::get_if<PingCommand>(&*command)) {
                    out.line(clockLine(ping->id, options.clock()));
                    return;
                }
                if (std::holds_alternative<StartCommand>(*command)) {
                    if (started) return;
                    started = true;
                    out.line(helloLine(options.version, options.arch, options.clock()));
                }
                inbox.push(std::move(*command));
            });
            if (ended && options.onInputClosed) options.onInputClosed();
            inbox.close();
        });
    }
    ~Reader() {
        if (stop_[1] >= 0) {
            const char byte = 0;
            [[maybe_unused]] const auto written = ::write(stop_[1], &byte, 1);
        }
        if (thread_.joinable()) thread_.join();
        for (const int fd : stop_) {
            if (fd >= 0) ::close(fd);
        }
    }
    Reader(const Reader&) = delete;
    Reader& operator=(const Reader&) = delete;

    SteadyClock::time_point lastLine() const {
        return SteadyClock::time_point(SteadyClock::duration(lastLine_.load(std::memory_order_relaxed)));
    }

private:
    std::atomic<SteadyClock::rep> lastLine_{SteadyClock::now().time_since_epoch().count()};
    int stop_[2] = {-1, -1};
    std::thread thread_;
};

std::shared_ptr<const pricing::PriceTable> tableOf(const std::string& json) { return pricing::PriceTable::load(json); }

// The quota session outlives the live one, whose monitor feeds its Codex collector.
struct Session {
    std::unique_ptr<HistoryWorker> history;
    std::unique_ptr<QuotaSession> quota;
    std::unique_ptr<LiveSession> live;
};

// The commands after `start` (REMOTE 5.3).
void apply(Command& command, Session& session) {
    std::visit(
        [&](auto& c) {
            using T = std::decay_t<decltype(c)>;
            if constexpr (std::is_same_v<T, TodayCommand>) {
                session.live->setToday(c.todayStartMs);
            } else if constexpr (std::is_same_v<T, PricesCommand>) {
                session.history->setPrices(tableOf(c.table));
            } else if constexpr (std::is_same_v<T, ZoneCommand>) {
                session.history->setZone(std::move(c.id), std::move(c.tzif));
            } else if constexpr (std::is_same_v<T, HistoryCommand>) {
                session.history->request(std::move(c.digests));
            } else if constexpr (std::is_same_v<T, WatchCommand>) {
                session.live->setWatch(c.on);
            } else if constexpr (std::is_same_v<T, QuotaCommand>) {
                session.quota->setConfig(c.config);
                session.quota->tick();
            }
        },
        command);
}

}  // namespace

std::int64_t systemClockMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

std::string buildArch() {
#if defined(__x86_64__)
    return "x86_64";
#elif defined(__aarch64__)
    return "aarch64";
#else
    utsname name{};
    return uname(&name) == 0 ? std::string(name.machine) : std::string("unknown");
#endif
}

int serve(int inFd, int outFd, const ServeOptions& options) {
    Output out(outFd);
    Inbox inbox;
    Reader reader(inFd, out, inbox, options);
    Session session;
    const auto finish = [&](int code) {
        if (session.history) session.history->stop();
        return code;
    };

    // REMOTE 5.3: lines before a valid `start` are ignored (a `ping` is answered by the reader).
    const auto startDeadline = SteadyClock::now() + options.startTimeout;
    std::optional<StartCommand> start;
    while (!start) {
        std::vector<Command> commands;
        const bool closed = inbox.wait(startDeadline, commands);
        for (auto& command : commands) {
            if (auto* s = std::get_if<StartCommand>(&command)) {
                start = std::move(*s);
                break;
            }
        }
        if (start) break;
        if (closed) return 0;
        if (SteadyClock::now() >= startDeadline) {
            out.line(errorLine("badStart", "no valid start line within 30 seconds"));
            return 1;
        }
    }
    if (start->protocol != kProtocol) {
        out.line(errorLine("badStart", "unsupported protocol"));
        return 1;
    }

    try {
        session.history = std::make_unique<HistoryWorker>(out, options.folder, start->sources, tableOf(start->prices));
        session.history->setZone(start->zoneId, start->zoneTzif);
        auto quotaOptions = options.quota;
        quotaOptions.folder = options.folder;
        quotaOptions.version = options.version;
        session.quota = std::make_unique<QuotaSession>(out, start->sources, std::move(quotaOptions), options.clock);
        // The config is applied before seeding, so the seeded rollouts' rate limits count (QUOTAS 3); the first reports
        // follow the seeding.
        if (start->quota) session.quota->setConfig(*start->quota);
        session.live = std::make_unique<LiveSession>(out, start->sources, options.clock);
        session.live->setRateLimitObserver(session.quota->codexObserver());
        session.live->start(start->todayStartMs);
        session.quota->tick();
        auto next = SteadyClock::now() + options.tick;
        while (!out.broken()) {
            std::vector<Command> commands;
            const bool closed = inbox.wait(next, commands);
            for (auto& command : commands) apply(command, session);
            if (closed) return finish(0);
            if (SteadyClock::now() - reader.lastLine() >= options.inputSilence) {
                out.line(errorLine("silence", "no line from the app in time"));
                return finish(1);
            }
            if (SteadyClock::now() >= next) {
                session.live->tick();
                session.quota->tick();
                next = SteadyClock::now() + options.tick;
            }
        }
    } catch (const std::exception&) {
        out.line(errorLine("internal", "the relay stopped after an unexpected error"));
    }
    return finish(1);
}

}  // namespace agentspeed::relay
