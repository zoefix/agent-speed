#include <gtest/gtest.h>

#include <ctime>

#include "agentspeed/traffic.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
using namespace agentspeed::traffic;

namespace {

std::string utcDate(std::int64_t ms) {
    const std::time_t seconds = static_cast<std::time_t>(ms >= 0 ? ms / 1000 : (ms - 999) / 1000);
    std::tm tm{};
    gmtime_r(&seconds, &tm);
    char buffer[16];
    std::strftime(buffer, sizeof buffer, "%Y-%m-%d", &tm);
    return buffer;
}

const char* roleName(Classification::Role role) {
    switch (role) {
        case Classification::Role::Agent: return "agent";
        case Classification::Role::Self: return "self";
        case Classification::Role::Tool: return "tool";
        default: return "";
    }
}

std::uint64_t u64(const json::Value& v) { return static_cast<std::uint64_t>(*v.truncated()); }

struct RatesAt {
    std::optional<Rate> machine;
    std::map<SourceKind, RatePair> sources;
    std::map<std::string, RatePair> agents;
};

void expectRate(const std::optional<Rate>& got, const json::Value& want, const std::string& label) {
    SCOPED_TRACE(label);
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value());
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_NEAR(got->rx, *want.at(0).number(), 0.0015);
    EXPECT_NEAR(got->tx, *want.at(1).number(), 0.0015);
}

void expectPair(const RatePair& got, const json::Value& want, const std::string& label) {
    if (want["withTools"].valid()) expectRate(got.withTools, want["withTools"], label + " withTools");
    if (want["selfOnly"].valid()) expectRate(got.selfOnly, want["selfOnly"], label + " selfOnly");
}

}  // namespace

TEST(Traffic, FormatsBytes) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "traffic-cases.json");
    ASSERT_TRUE(file.ok);
    for (const auto& row : file.root()["format"].elements()) {
        const auto kind = *row["kind"].string();
        const double value = *row["value"].number();
        const auto got = kind == "total" ? formatTotal(value) : formatRate(value, kind == "bits");
        // The fixture writes a plain space where the display uses a no-break space.
        auto want = *row["text"].string();
        for (auto at = want.find(' '); at != std::string::npos; at = want.find(' ', at + 2)) want.replace(at, 1, "\u00a0");
        EXPECT_EQ(got, want) << kind << " " << value;
    }
}

TEST(Traffic, MatchesTheReferenceCases) {
    fixtures::JsonFile file(fixtures::fixtureDir() / "traffic-cases.json");
    ASSERT_TRUE(file.ok);
    for (const auto& c : file.root()["cases"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        Env env;
        const auto platform = c["platform"].string().value_or("macos");
        env.platform = platform == "windows" ? Platform::Windows : platform == "linux" ? Platform::Linux : Platform::MacOS;
        env.home = c["home"].string().value_or("/Users/dev");
        for (const auto& [k, v] : c["env"].members()) env.vars[k] = *v.string();
        const auto tz = fixtures::optInt(c["tzOffsetMs"]).value_or(0);
        Engine engine(env, [tz](std::int64_t t) { return utcDate(t + tz); });
        std::vector<AgentFacts> agents;
        for (const auto& a : c["agents"].elements()) {
            AgentFacts f;
            f.id = *a["id"].string();
            f.source = *sourceFromId(*a["source"].string());
            f.parent = fixtures::optString(a["parent"]);
            f.transcript = fixtures::optString(a["transcript"]);
            f.cwdPath = fixtures::optString(a["cwdPath"]);
            for (const auto& iv : a["active"].elements()) f.active.emplace_back(*iv.at(0).truncated(), *iv.at(1).truncated());
            agents.push_back(f);
        }
        std::set<std::int64_t> rateTimes;
        for (const auto& t : c["rateTimes"].elements()) rateTimes.insert(*t.truncated());
        std::map<std::int64_t, RatesAt> rates;
        std::int64_t last = 0;
        for (const auto& s : c["samples"].elements()) {
            Sample sample;
            sample.t = last = *s["t"].truncated();
            if (s["processes"].isArray()) {
                std::vector<Process> processes;
                for (const auto& p : s["processes"].elements()) {
                    Process proc;
                    proc.pid = *p["pid"].truncated();
                    proc.ppid = fixtures::optInt(p["ppid"]).value_or(0);
                    proc.start = *p["start"].truncated();
                    proc.exe = p["exe"].string().value_or("");
                    for (const auto& a : p["args"].elements()) proc.args.push_back(*a.string());
                    proc.bundle = fixtures::optString(p["bundle"]);
                    proc.cwd = fixtures::optString(p["cwd"]);
                    for (const auto& f : p["openFiles"].elements()) proc.openFiles.push_back(*f.string());
                    processes.push_back(proc);
                }
                sample.processes = processes;
            }
            if (s["interfaces"].isObject()) {
                Interfaces ifaces;
                for (const auto& [name, v] : s["interfaces"].members()) ifaces[name] = {u64(v.at(0)), u64(v.at(1))};
                sample.interfaces = ifaces;
            }
            if (s["counters"].isObject()) {
                Counters counters;
                counters.stream = *s["counters"]["stream"].string();
                counters.continues = s["counters"]["continues"].isTrue();
                for (const auto& r : s["counters"]["rows"].elements()) {
                    counters.rows.push_back({*r["pid"].truncated(), *r["start"].truncated(), u64(r["rx"]), u64(r["tx"])});
                }
                sample.counters = counters;
            }
            engine.tick(sample, agents);
            if (rateTimes.contains(sample.t)) rates[sample.t] = {engine.machineRate(), engine.sourceRates(), engine.agentRates()};
        }
        const auto want = c["expected"];
        for (const auto& [pid, cls] : want["classes"].members()) {
            std::optional<std::string> got;
            for (const auto& [key, k] : engine.table().known()) {
                if (std::to_string(key.first) != pid) continue;
                got = k.source ? std::optional<std::string>(std::string(sourceId(*k.source)) + "/" + roleName(k.role)) : std::nullopt;
            }
            EXPECT_EQ(got, fixtures::optString(cls)) << "pid " << pid;
        }
        const auto date = utcDate(last + tz);
        const auto& days = engine.days();
        const auto today = days.contains(date) ? days.at(date) : Day{};
        if (const auto m = want["today"]["machine"]; m.isObject()) {
            if (m["rx"].valid()) {
                EXPECT_EQ(today.machine.rx, u64(m["rx"]));
            }
            if (m["tx"].valid()) {
                EXPECT_EQ(today.machine.tx, u64(m["tx"]));
            }
            if (m["sampledMs"].valid()) {
                EXPECT_EQ(today.machine.sampledMs, *m["sampledMs"].truncated());
            }
        }
        for (const auto& [source, w] : want["today"]["sources"].members()) {
            SCOPED_TRACE(source);
            const auto it = today.sources.find(*sourceFromId(source));
            const auto got = it == today.sources.end() ? SourceDay{} : it->second;
            const std::pair<const char*, std::uint64_t> fields[] = {{"rxSelf", got.rxSelf},           {"txSelf", got.txSelf},
                                                                    {"rxTools", got.rxTools},         {"txTools", got.txTools},
                                                                    {"unassignedRx", got.unassignedRx}, {"unassignedTx", got.unassignedTx}};
            for (const auto& [name, value] : fields) {
                if (w[name].valid()) {
                    EXPECT_EQ(value, u64(w[name])) << name;
                }
            }
        }
        const auto agentDays = engine.agentDays().contains(date) ? engine.agentDays().at(date) : std::map<std::string, AgentDay>{};
        for (const auto& [id, w] : want["today"]["agents"].members()) {
            SCOPED_TRACE(id);
            const auto it = agentDays.find(id);
            const auto got = it == agentDays.end() ? AgentDay{} : it->second;
            EXPECT_EQ(got.rxSelf, u64(w["rxSelf"]));
            EXPECT_EQ(got.txSelf, u64(w["txSelf"]));
            EXPECT_EQ(got.rxTools, u64(w["rxTools"]));
            EXPECT_EQ(got.txTools, u64(w["txTools"]));
        }
        for (const auto& [t, w] : want["rates"].members()) {
            SCOPED_TRACE("rates at " + t);
            const auto& got = rates.at(std::stoll(t));
            if (w["machine"].valid()) expectRate(got.machine, w["machine"], "machine");
            for (const auto& [source, pair] : w["sources"].members()) {
                const auto it = got.sources.find(*sourceFromId(source));
                expectPair(it == got.sources.end() ? RatePair{} : it->second, pair, source);
            }
            for (const auto& [id, pair] : w["agents"].members()) {
                const auto it = got.agents.find(id);
                expectPair(it == got.agents.end() ? RatePair{} : it->second, pair, id);
            }
        }
        if (want["history"].isArray()) {
            const auto rows = engine.history();
            const auto wantRows = want["history"].elements();
            ASSERT_EQ(rows.size(), wantRows.size());
            for (std::size_t i = 0; i < rows.size(); ++i) {
                EXPECT_EQ(rows[i].day, *wantRows[i]["day"].string());
                EXPECT_EQ(rows[i].key, *wantRows[i]["key"].string());
                EXPECT_EQ(rows[i].rx, u64(wantRows[i]["rx"]));
                EXPECT_EQ(rows[i].tx, u64(wantRows[i]["tx"]));
                EXPECT_EQ(rows[i].rxTools, u64(wantRows[i]["rxTools"]));
                EXPECT_EQ(rows[i].txTools, u64(wantRows[i]["txTools"]));
                EXPECT_EQ(rows[i].sampledMs, *wantRows[i]["sampledMs"].truncated());
            }
        }
    }
}
