#include <gtest/gtest.h>
#include <fcntl.h>
#include <sys/stat.h>

#include <cstdio>
#include <fstream>
#include <map>

#include "agentspeed/file_reader.hpp"
#include "agentspeed/hermes.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

struct Collected : ResponseSink {
    std::vector<ParsedResponse> all;
    void onResponse(const ParsedResponse& r, bool) override { all.push_back(r); }
};

void setMtime(const fs::path& path, std::int64_t ms) {
    struct timespec times[2];
    times[0].tv_sec = times[1].tv_sec = static_cast<time_t>(ms / 1000);
    times[0].tv_nsec = times[1].tv_nsec = static_cast<long>((ms % 1000) * 1'000'000);
    utimensat(AT_FDCWD, path.c_str(), times, 0);
}

fs::path makeTemp() {
    char pattern[] = "/tmp/agentspeed-hermes-XXXXXX";
    return fs::path(mkdtemp(pattern));
}

bool isRecordLine(const std::string& line) {
    if (line.size() <= hermes::kTimestampLength + 1 || line[hermes::kTimestampLength] != ' ') return false;
    for (const char* level : {"DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL"}) {
        const std::string l = level;
        if (line.compare(hermes::kTimestampLength + 1, l.size(), l) == 0 && line.size() > hermes::kTimestampLength + 1 + l.size() &&
            line[hermes::kTimestampLength + 1 + l.size()] == ' ') {
            return true;
        }
    }
    return false;
}

void expectAgent(const hermes::Agent* agent, const json::Value& want) {
    ASSERT_NE(agent, nullptr);
    if (want["phase"].valid()) {
        EXPECT_EQ(phaseId(agent->phase), *want["phase"].string());
    }
    if (want["inFlightStartMs"].valid()) {
        EXPECT_EQ(agent->inFlightStartMs, fixtures::optInt(want["inFlightStartMs"]));
    }
    if (want["lastLineMs"].valid()) {
        EXPECT_EQ(agent->lastLineMs, fixtures::optInt(want["lastLineMs"]));
    }
    if (want["model"].valid()) {
        EXPECT_EQ(agent->model, fixtures::optString(want["model"]));
    }
    if (want["isSubagent"].valid()) {
        EXPECT_EQ(agent->isSubagent, want["isSubagent"].isTrue());
    }
}

// The append / rename / delete / write / remove steps of the rotation and Codex-runtime replays.
void applyStep(const fs::path& base, const json::Value& step) {
    if (const auto file = step["append"]; file.isString()) {
        const fs::path path = base / *file.string();
        std::ofstream out(path, std::ios::binary | std::ios::app);
        if (const auto lines = step["lines"]; lines.isArray()) {
            for (const auto& l : lines.elements()) out << *l.string() << "\n";
        } else if (const auto text = step["text"]; text.isString()) {
            out << *text.string();
        }
        out.close();
        if (auto mtime = fixtures::optInt(step["mtime"])) setMtime(path, *mtime);
    } else if (const auto rename = step["rename"]; rename.isArray()) {
        fs::rename(base / *rename.at(0).string(), base / *rename.at(1).string());
    } else if (const auto del = step["delete"]; del.isString()) {
        fs::remove(base / *del.string());
    } else if (const auto remove = step["remove"]; remove.isString()) {
        fs::remove(base / *remove.string());
    } else if (const auto write = step["write"]; write.isString()) {
        const fs::path path = base / *write.string();
        std::string text = step["text"].isString() ? *step["text"].string() : std::string();
        if (auto size = fixtures::optInt(step["size"]); size && static_cast<std::int64_t>(text.size()) < *size) {
            text.append(static_cast<std::size_t>(*size) - text.size(), '#');
        }
        std::ofstream(path, std::ios::binary | std::ios::trunc) << text;
        if (auto mtime = fixtures::optInt(step["mtime"])) setMtime(path, *mtime);
    }
}

}  // namespace

// spec/fixtures/parser-expected.json `hermes`: log sets, traces, wall times, homes, the runtime switch, rotation.
TEST(Hermes, LogSetsMatchTheReference) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    ASSERT_TRUE(expected.ok);
    const auto h = expected.root()["hermes"];
    const auto zone = TimeZone::load(*h["timeZone"].string());
    ASSERT_TRUE(zone);
    Deduper deduper;
    std::map<std::string, std::unique_ptr<hermes::LogSet>> sets;
    std::map<std::string, std::vector<ParsedResponse>> byAgent;
    std::map<std::string, std::vector<json::Value>> traces;
    for (const auto& t : h["traces"].elements()) traces[*t["file"].string()].push_back(t);
    for (const auto& entry : h["order"].elements()) {
        const std::string relative = *entry.string();
        const auto absolute = fixtures::fixtureDir() / relative;
        const std::string folder = absolute.parent_path().string();
        auto& set = sets[folder];
        if (!set) set = std::make_unique<hermes::LogSet>(folder, deduper, zone);
        Collected sink;
        std::int64_t physical = 0;
        readWholeFile(absolute, [&](std::string_view line) {
            set->line(line, sink);
            ++physical;
            for (const auto& t : traces[relative]) {
                if (*fixtures::optInt(t["afterLine"]) != physical) continue;
                SCOPED_TRACE(relative + " after line " + std::to_string(physical));
                expectAgent(set->agent(*t["session"].string()), t);
            }
        });
        for (const auto& r : sink.all) byAgent[r.agentId].push_back(r);
    }
    for (const auto& [relative, want] : h["sets"].members()) {
        SCOPED_TRACE(relative);
        const std::string folder = (fixtures::fixtureDir() / relative).string();
        ASSERT_TRUE(sets.contains(folder));
        const auto& set = *sets[folder];
        EXPECT_EQ(set.malformedLines(), *fixtures::optInt(want["malformedLines"]));
        const auto agents = want["agents"].members();
        ASSERT_EQ(set.agents().size(), agents.size());
        for (std::size_t i = 0; i < agents.size(); ++i) {
            const auto& [session, w] = agents[i];
            const auto* agent = set.agents()[i];
            SCOPED_TRACE(session);
            EXPECT_EQ(agent->sessionId, session);
            EXPECT_EQ(agent->agentId, folder + "#" + session);
            expectAgent(agent, w);
            const auto responses = w["responses"].elements();
            const auto& got = byAgent[agent->agentId];
            ASSERT_EQ(got.size(), responses.size());
            for (std::size_t k = 0; k < responses.size(); ++k) {
                const auto& r = responses[k];
                SCOPED_TRACE(got[k].key);
                EXPECT_EQ(got[k].key, *r["key"].string());
                EXPECT_EQ(got[k].startMs, fixtures::optInt(r["startMs"]));
                EXPECT_EQ(got[k].endMs, *fixtures::optInt(r["endMs"]));
                EXPECT_EQ(got[k].tokens.input, *fixtures::optInt(r["input"]));
                EXPECT_EQ(got[k].tokens.cachedInput, *fixtures::optInt(r["cachedInput"]));
                EXPECT_EQ(got[k].tokens.cacheWriteInput, *fixtures::optInt(r["cacheWriteInput"]));
                EXPECT_EQ(got[k].tokens.output, *fixtures::optInt(r["output"]));
                EXPECT_EQ(got[k].model, fixtures::optString(r["model"]));
                EXPECT_EQ(got[k].provider, fixtures::optString(r["provider"]));
            }
        }
    }
}

TEST(Hermes, WallTimesHomesAndRuntimeConfigs) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    const auto h = expected.root()["hermes"];
    for (const auto& w : h["wallTimes"].elements()) {
        const std::string line = *w["line"].string();
        SCOPED_TRACE(line);
        const auto zone = TimeZone::load(*w["timeZone"].string());
        ASSERT_TRUE(zone);
        const auto got = isRecordLine(line) ? hermes::timestampOf(line, *zone) : std::nullopt;
        EXPECT_EQ(got, fixtures::optInt(w["ms"]));
    }
    for (const auto& c : h["homes"].elements()) {
        const auto env = c["env"];
        auto lookup = [&](const std::string& name) -> std::optional<std::string> {
            const auto v = env[name];
            return v.isString() ? v.string() : std::nullopt;
        };
        const bool windows = c["platform"].equals("win32");
        SCOPED_TRACE(*c["expected"].string());
        EXPECT_EQ(hermes::home(lookup, windows, *c["home"].string()), *c["expected"].string());
    }
    for (const auto& c : h["runtimeConfigs"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        EXPECT_EQ(hermes::selectsCodexRuntime(*c["text"].string()), c["codexRuntime"].isTrue());
    }
}

TEST(Hermes, RotationFollowsFileIdentity) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    const auto r = expected.root()["hermes"]["rotation"];
    const auto zone = TimeZone::load(*r["timeZone"].string());
    ASSERT_TRUE(zone);
    const fs::path dir = makeTemp();
    Deduper deduper;
    hermes::Source source(dir.string(), true, deduper, zone);
    std::vector<std::string> keys;
    bool seeded = false;
    int step = 0;
    for (const auto& s : r["steps"].elements()) {
        ++step;
        if (!s["read"].isTrue()) {
            applyStep(dir, s);
            continue;
        }
        SCOPED_TRACE("step " + std::to_string(step));
        auto update = seeded ? source.refresh(true) : source.seed(0);
        seeded = true;
        for (const auto& [resp, _] : update.responses) keys.push_back(resp.key);
        std::vector<std::string> want;
        for (const auto& k : s["responseKeys"].elements()) want.push_back(*k.string());
        EXPECT_EQ(keys, want);
        ASSERT_EQ(source.sets().size(), 1u);
        for (const auto& [session, w] : s["agents"].members()) {
            SCOPED_TRACE(session);
            expectAgent(source.sets()[0]->agent(session), w);
        }
    }
    fs::remove_all(dir);
}

TEST(Hermes, CodexRuntimeSwitchFollowsConfig) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    const auto c = expected.root()["hermes"]["codexRuntime"];
    const auto zone = TimeZone::load(*c["timeZone"].string());
    ASSERT_TRUE(zone);
    const fs::path home = makeTemp();
    for (const auto& folder : c["logFolders"].elements()) fs::create_directories(home / *folder.string());
    Deduper deduper;
    hermes::Source source(home.string(), false, deduper, zone);
    std::vector<std::string> keys;
    bool seeded = false;
    int step = 0;
    for (const auto& s : c["steps"].elements()) {
        ++step;
        if (!s["read"].isTrue()) {
            applyStep(home, s);
            continue;
        }
        SCOPED_TRACE("step " + std::to_string(step));
        auto update = seeded ? source.refresh(true) : source.seed(0);
        seeded = true;
        for (const auto& [resp, _] : update.responses) keys.push_back(resp.key);
        std::vector<std::string> want;
        for (const auto& k : s["responseKeys"].elements()) want.push_back(*k.string());
        EXPECT_EQ(keys, want);
        for (const auto& [relative, w] : s["sets"].members()) {
            SCOPED_TRACE(relative);
            const std::string folder = (home / relative).string();
            hermes::LogSet* set = nullptr;
            for (auto* candidate : source.sets()) {
                if (candidate->folder() == folder) set = candidate;
            }
            ASSERT_NE(set, nullptr);
            EXPECT_EQ(set->codexRuntime(), w["codexRuntime"].isTrue());
            for (const auto& [session, a] : w["agents"].members()) {
                SCOPED_TRACE(session);
                expectAgent(set->agent(session), a);
            }
        }
    }
    fs::remove_all(home);
}
