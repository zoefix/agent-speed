#include <gtest/gtest.h>

#include "agentspeed/report_scheduler.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::report;
using namespace fixtures;

namespace {

struct Simulation {
    std::vector<std::string> delivered;
    std::vector<int> lost;
    std::int64_t built = 0, discarded = 0, increments = 0;
};

// Runs one case like simulate_scheduler: every 500 ms tick, increments into a ledger, outcomes per seq.
Simulation simulate(const json::Value& item) {
    const auto config = item["config"];
    Limits limits;
    if (config["intervalSeconds"].valid()) limits.intervalSeconds = static_cast<int>(*config["intervalSeconds"].truncated());
    if (config["heartbeatSeconds"].valid()) limits.heartbeatSeconds = static_cast<int>(*config["heartbeatSeconds"].truncated());
    if (config["maxAgeSeconds"].valid()) limits.maxAgeSeconds = static_cast<int>(*config["maxAgeSeconds"].truncated());
    Scheduler scheduler(limits, false, false, [] { return 1.0; });

    std::map<std::int64_t, std::int64_t> increments;
    Simulation out;
    for (const auto& spec : item["increments"].elements())
        for (auto t = *spec["from"].truncated(); t <= *spec["to"].truncated(); t += *spec["step"].truncated())
            increments[t] += *spec["output"].truncated();
    std::map<std::int64_t, std::vector<Event>> events;
    for (const auto& pair : item["events"].elements()) {
        const auto name = *pair.at(1).string();
        events[*pair.at(0).truncated()].push_back(name == "pause" ? Event::Pause : name == "resume" ? Event::Resume
                                                  : name == "networkOff"                        ? Event::NetworkOff
                                                                                                : Event::NetworkOn);
    }
    std::map<int, std::vector<std::string>> outcomes;
    for (const auto& [seq, queue] : item["outcomes"].members())
        for (const auto& name : queue.elements()) outcomes[std::stoi(seq)].push_back(*name.string());
    std::vector<std::pair<std::int64_t, std::int64_t>> activity;
    for (const auto& range : item["activity"].elements()) activity.emplace_back(*range.at(0).truncated(), *range.at(1).truncated());

    std::map<int, int> attempts;
    std::map<int, std::int64_t> outputs;
    std::int64_t ledger = 0;
    const auto outcomeOf = [&](const Ticket& ticket) {
        const auto& queue = outcomes[ticket.seq];
        const auto index = attempts[ticket.seq]++;
        const auto name = index < static_cast<int>(queue.size()) ? queue[static_cast<std::size_t>(index)] : std::string("ok");
        if (name == "ok") return Outcome{};
        if (name.starts_with("rateLimited:")) return Outcome{Outcome::Kind::RateLimited, std::nullopt, std::stoi(name.substr(12))};
        return Outcome{Outcome::Kind::Transient, std::nullopt, 0};
    };
    std::function<void(const Actions&, std::int64_t)> apply = [&](const Actions& actions, std::int64_t t) {
        for (const auto& action : actions) {
            switch (action.kind) {
            case Action::Kind::Discard:
                if (!action.keepingTick) {
                    out.discarded += ledger;
                    ledger = 0;
                }
                break;
            case Action::Kind::Build:
                outputs[action.ticket.seq] = ledger;
                out.built += ledger;
                ledger = 0;
                break;
            case Action::Kind::Send: apply(scheduler.finish(action.ticket, outcomeOf(action.ticket), t), t); break;
            case Action::Kind::Delivered: {
                const auto& ticket = action.ticket;
                const auto output = outputs.contains(ticket.seq) ? outputs[ticket.seq] : -1;
                out.delivered.push_back(std::to_string(ticket.seq) + "," + std::to_string(ticket.builtAt) + "," + std::to_string(ticket.windowMs) +
                                        ",\"" + activityName(ticket.activity) + "\"," + std::to_string(output));
                break;
            }
            case Action::Kind::Lost: out.lost.push_back(action.ticket.seq); break;
            }
        }
    };
    for (const auto& range : item["ticks"].elements()) {
        for (auto t = *range.at(0).truncated(); t <= *range.at(1).truncated(); t += 500) {
            const bool active = std::any_of(activity.begin(), activity.end(), [&](const auto& a) { return a.first <= t && t <= a.second; });
            const auto it = events.find(t);
            auto actions = scheduler.tick(t, active, false, it == events.end() ? std::vector<Event>{} : it->second);
            Actions rest;
            for (const auto& action : actions) {
                if (action.kind == Action::Kind::Discard && action.keepingTick) {
                    out.discarded += ledger;
                    ledger = 0;
                } else {
                    rest.push_back(action);
                }
            }
            const auto increment = increments.contains(t) ? increments[t] : 0;
            ledger += increment;
            out.increments += increment;
            apply(rest, t);
        }
    }
    std::sort(out.lost.begin(), out.lost.end());
    out.discarded += ledger;
    return out;
}

}  // namespace

TEST(ReportScheduler, VectorTimelines) {
    const auto text = readText(fixtureDir() / "report-vectors.json");
    json::Document doc;
    ASSERT_TRUE(doc.parse(text));
    const auto cases = doc.root()["scheduler"].elements();
    EXPECT_EQ(cases.size(), 11U);
    for (const auto& item : cases) {
        const auto name = *item["name"].string();
        SCOPED_TRACE(name);
        std::vector<std::string> delivered;
        for (const auto& row : item["expect"]["delivered"].elements()) {
            std::string cells;
            for (const auto& cell : row.elements()) cells += (cells.empty() ? "" : ",") + compactJson(cell);
            delivered.push_back(cells);
        }
        std::vector<int> lost;
        for (const auto& seq : item["expect"]["lost"].elements()) lost.push_back(static_cast<int>(*seq.truncated()));
        const auto got = simulate(item);
        EXPECT_EQ(got.delivered, delivered);
        EXPECT_EQ(got.lost, lost);
        EXPECT_EQ(got.built + got.discarded, got.increments) << "exactly once";
    }
}

TEST(ReportScheduler, BlockedSendsNothing) {
    Scheduler scheduler(Limits{}, false, false, [] { return 1.0; });
    int sends = 0;
    for (std::int64_t t = 0; t <= 60'000; t += 500)
        for (const auto& action : scheduler.tick(t, true, true)) sends += action.kind == Action::Kind::Send;
    EXPECT_EQ(sends, 0);
    EXPECT_EQ(scheduler.seq(), 0);
}

TEST(ReportScheduler, OneRequestInFlight) {
    Scheduler scheduler(Limits{}, false, false, [] { return 1.0; });
    std::vector<int> sent, lost;
    for (std::int64_t t = 0; t <= 19'500; t += 500) {
        for (const auto& action : scheduler.tick(t, true, false)) {
            if (action.kind == Action::Kind::Send) sent.push_back(action.ticket.seq);
            if (action.kind == Action::Kind::Lost) lost.push_back(action.ticket.seq);
        }
    }
    EXPECT_EQ(sent, std::vector<int>{1});
    EXPECT_EQ(lost, (std::vector<int>{2}));
    EXPECT_TRUE(scheduler.busy());
}
