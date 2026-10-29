#include <gtest/gtest.h>

#include <algorithm>
#include <map>
#include <memory>
#include <vector>

#include "agentspeed/actions.hpp"
#include "agentspeed/antigravity.hpp"
#include "agentspeed/claude_parser.hpp"
#include "agentspeed/codex_parser.hpp"
#include "agentspeed/droid.hpp"
#include "agentspeed/dsh_parser.hpp"
#include "agentspeed/file_reader.hpp"
#include "agentspeed/gemini_parser.hpp"
#include "agentspeed/line_splitter.hpp"
#include "agentspeed/omp_parser.hpp"
#include "agentspeed/openclaw.hpp"
#include "agentspeed/pi_parser.hpp"
#include "agentspeed/workbuddy_parser.hpp"
#include "agentspeed/opencode.hpp"
#include "agentspeed/parents.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

namespace {

class Collector : public ResponseSink {
public:
    void onResponse(const ParsedResponse& response, bool) override {
        if (!byKey.contains(response.key)) order.push_back(response.key);
        byKey[response.key] = response;
    }
    std::vector<std::string> order;
    std::map<std::string, ParsedResponse> byKey;
};

// A missing key is not checked; null means unknown.
void expectMode(const std::optional<ModeAttribution>& got, const json::Value& want, const char* what) {
    SCOPED_TRACE(what);
    if (!want.valid()) return;
    if (want.isNull()) {
        EXPECT_FALSE(got.has_value()) << (got ? got->mode : "");
        return;
    }
    ASSERT_TRUE(got.has_value());
    EXPECT_EQ(got->mode, *want["mode"].string());
    EXPECT_EQ(got->evidence, *want["evidence"].string());
    EXPECT_EQ(got->provider, fixtures::optString(want["provider"]));
}

void expectModes(const ExecutionModes& modes, const json::Value& want) {
    expectMode(effort_speed::effort(modes), want["effort"], "effort");
    expectMode(effort_speed::speed(modes), want["speed"], "speed");
}

void expectResponse(const ParsedResponse& got, const json::Value& r) {
    SCOPED_TRACE(got.key);
    EXPECT_EQ(got.key, *r["key"].string());
    EXPECT_EQ(got.startMs, fixtures::optInt(r["startMs"]));
    EXPECT_EQ(got.endMs, *fixtures::optInt(r["endMs"]));
    EXPECT_EQ(got.firstMs, fixtures::optInt(r["firstMs"]));
    EXPECT_EQ(got.tokens.input, *fixtures::optInt(r["input"]));
    EXPECT_EQ(got.tokens.cachedInput, *fixtures::optInt(r["cachedInput"]));
    EXPECT_EQ(got.tokens.cacheWriteInput, *fixtures::optInt(r["cacheWriteInput"]));
    EXPECT_EQ(got.tokens.cacheWrite1hInput, *fixtures::optInt(r["cacheWrite1hInput"]));
    EXPECT_EQ(got.tokens.output, *fixtures::optInt(r["output"]));
    EXPECT_EQ(got.tokens.reasoning, *fixtures::optInt(r["reasoning"]));
    EXPECT_EQ(got.model, fixtures::optString(r["model"]));
    EXPECT_EQ(got.provider, fixtures::optString(r["provider"]));
    EXPECT_EQ(got.fast, r["fast"].isTrue());
    EXPECT_EQ(got.final, r["final"].isTrue());
}

std::unique_ptr<TranscriptParser> parserFor(const std::string& relative, const std::string& agentId, Deduper& deduper) {
    // OpenClaw's agent-scoped Codex homes are Codex roots (METRICS 2).
    if (relative.rfind("codex/", 0) == 0 || relative.find("/codex-home/sessions/") != std::string::npos) {
        return std::make_unique<CodexParser>(agentId, deduper);
    }
    if (relative.rfind("claude/", 0) == 0) return std::make_unique<ClaudeParser>(agentId, deduper);
    if (relative.rfind("dsh/", 0) == 0) return std::make_unique<DshParser>(agentId, deduper);
    if (relative.rfind("gemini/", 0) == 0) return std::make_unique<GeminiParser>(agentId, deduper);
    if (relative.rfind("workbuddy/", 0) == 0) {
        // The folder `workbuddy` plays the home folder (data folders .workbuddy and .workbuddy-ai).
        const bool ai = relative.rfind("workbuddy/.workbuddy-ai/", 0) == 0;
        const auto root = (fixtures::fixtureDir() / (ai ? "workbuddy/.workbuddy-ai/projects" : "workbuddy/.workbuddy/projects")).string();
        return std::make_unique<WorkBuddyParser>(agentId, deduper, ai ? SourceKind::WorkBuddyAi : SourceKind::WorkBuddy, root);
    }
    if (relative.rfind("pi/", 0) == 0) return PiParser::forPath(agentId, deduper);
    if (relative.rfind("droid/", 0) == 0) return std::make_unique<droid::Parser>(agentId, deduper);
    if (relative.rfind("omp/", 0) == 0) {
        // The folder `omp` plays ~/.omp: roots omp/agent/sessions and omp/profiles/*/agent/sessions.
        const auto cut = agentId.find("/agent/sessions/");
        return std::make_unique<OmpParser>(agentId, deduper, agentId.substr(0, cut + 15));
    }
    return nullptr;
}

}  // namespace

// spec/fixtures/parser-expected.json, for the sources ported so far (files in `order`, one shared dedupe set).
TEST(ParserFixtures, ClaudeAndCodexMatchTheReference) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    ASSERT_TRUE(expected.ok);
    Deduper deduper;
    int checked = 0;
    // `traces` of every source: the phase after feeding a file up to and including a physical line.
    std::map<std::string, std::vector<json::Value>> traces;
    for (const auto& [section, value] : expected.root().members()) {
        if (!value.isObject()) continue;
        if (const auto list = value["traces"]; list.isArray()) {
            for (const auto& t : list.elements()) {
                if (t["afterLine"].valid()) traces[*t["file"].string()].push_back(t);
            }
        }
    }
    std::vector<AgentLink> facts;
    std::map<std::string, std::string> wantParents;
    for (const auto& entry : expected.root()["order"].elements()) {
        const std::string relative = *entry.string();
        const auto absolute = (fixtures::fixtureDir() / relative).string();
        auto parser = parserFor(relative, absolute, deduper);
        if (!parser) continue;
        // METRICS 6.10: every action of the file, read with its parser.
        work::ActionReader actions(absolute, 0);
        parser->setLineObserver(&actions);
        Collector sink;
        std::int64_t physical = 0;
        auto check = [&] {
            for (const auto& t : traces[relative]) {
                if (*fixtures::optInt(t["afterLine"]) != physical) continue;
                SCOPED_TRACE(relative + " after line " + std::to_string(physical));
                EXPECT_EQ(phaseId(parser->phase()), *t["phase"].string());
                EXPECT_EQ(parser->inFlightStartMs(), fixtures::optInt(t["inFlightStartMs"]));
            }
        };
        const auto* gemini = dynamic_cast<GeminiParser*>(parser.get());
        if (gemini && gemini->isDocument()) {
            parser->feed(fixtures::readText(absolute), sink);
        } else if (auto* droidParser = dynamic_cast<droid::Parser*>(parser.get())) {
            // The history rule: the whole file from the start and one sidecar read (the `:session` rest is history's).
            droidParser->beginRead(0);
            LineSplitter lines;
            lines.feedPositioned(fixtures::readText(absolute), 0, [&](std::string_view line, std::int64_t offset, std::int64_t length) {
                droidParser->feedPositioned(line, offset, length);
                ++physical;
                check();
            });
            droidParser->applyCycle(droid::readSidecar(droid::sidecarOf(absolute)), sink);
        } else {
            const auto read = readWholeFile(absolute, [&](std::string_view line) {
                parser->feed(line, sink);
                ++physical;
                check();
            });
            ASSERT_TRUE(read.ok) << relative;
            parser->addMalformed(read.decodeErrors);
        }

        const auto want = expected.root()["files"][relative];
        SCOPED_TRACE(relative);
        EXPECT_EQ(sourceId(parser->source()), *want["source"].string());
        EXPECT_EQ(parser->project(), fixtures::optString(want["project"]));
        EXPECT_EQ(parser->model(), fixtures::optString(want["model"]));
        EXPECT_EQ(parser->isSubagent(), want["isSubagent"].isTrue());
        EXPECT_EQ(phaseId(parser->phase()), *want["phase"].string());
        EXPECT_EQ(parser->inFlightStartMs(), fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(parser->lastLineMs(), fixtures::optInt(want["lastLineMs"]));
        EXPECT_EQ(parser->malformedLines(), *fixtures::optInt(want["malformedLines"]));
        expectModes(parser->modes(), want);
        if (const auto modes = want["responseModes"]; modes.isObject()) {
            for (const auto& [key, value] : modes.members()) {
                SCOPED_TRACE(std::string(key));
                ASSERT_TRUE(sink.byKey.contains(std::string(key)));
                expectModes(sink.byKey[std::string(key)].modes, value);
            }
        }

        const auto responses = want["responses"].elements();
        ASSERT_EQ(sink.order.size(), responses.size());
        for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(sink.byKey[sink.order[i]], responses[i]);
        // `actions` is listed for the files that have them; a file without the key is not checked.
        const auto wantActions = want["actions"].elements();
        if (want["actions"].isArray()) {
            ASSERT_EQ(actions.actions().size(), wantActions.size());
        }
        for (std::size_t i = 0; i < wantActions.size(); ++i) {
            const auto& a = actions.actions()[i];
            const auto w = wantActions[i];
            SCOPED_TRACE("action " + std::to_string(i));
            EXPECT_EQ(a.tool, *w["tool"].string());
            EXPECT_EQ(a.digest, *w["digest"].string());
            EXPECT_EQ(a.callMs, *w["callMs"].truncated());
            EXPECT_EQ(a.resultMs, fixtures::optInt(w["resultMs"]));
            EXPECT_EQ(a.failed, w["failed"].isBool() ? std::optional<bool>(w["failed"].isTrue()) : std::nullopt);
            EXPECT_EQ(a.model, fixtures::optString(w["model"]));
        }
        facts.push_back(parser->link());
        if (auto parent = fixtures::optString(want["parentFile"])) {
            wantParents[absolute] = (fixtures::fixtureDir() / *parent).string();
        }
        ++checked;
    }
    EXPECT_EQ(checked, 56);
    std::map<std::string, std::string> gotParents;
    for (const auto& [id, parent] : parent_links::resolve(facts)) gotParents[id] = parent;
    EXPECT_EQ(gotParents, wantParents);

    // Then the OpenCode database, with the same dedupe set; reading it must not create any file.
    const auto opencodeDir = fixtures::fixtureDir() / "opencode";
    const auto before = fixtures::listFiles(opencodeDir);
    opencode::Source source(opencodeDir.string(), deduper);
    source.refreshAvailability();
    ASSERT_EQ(source.status(), opencode::Status::Ready);
    const auto update = source.seed(0);
    const auto wantAgents = expected.root()["opencode"]["agents"];
    ASSERT_EQ(update.agents.size(), wantAgents.members().size());
    for (const auto& [id, want] : wantAgents.members()) {
        SCOPED_TRACE(id);
        ASSERT_TRUE(update.agents.contains(id));
        const auto& got = update.agents.at(id);
        EXPECT_EQ(sourceId(SourceKind::OpenCode), *want["source"].string());
        EXPECT_EQ(got.project, fixtures::optString(want["project"]));
        EXPECT_EQ(got.model, fixtures::optString(want["model"]));
        EXPECT_EQ(got.isSubagent, want["isSubagent"].isTrue());
        const std::optional<std::string> parent =
            got.parentSessionId && update.agents.contains(opencode::agentId(*got.parentSessionId))
                ? std::optional<std::string>(opencode::agentId(*got.parentSessionId))
                : std::nullopt;
        EXPECT_EQ(parent, fixtures::optString(want["parentId"]));
        EXPECT_EQ(phaseId(got.phase), *want["phase"].string());
        EXPECT_EQ(got.inFlightStartMs, fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(got.lastLineMs, fixtures::optInt(want["lastLineMs"]));
        expectModes(got.modes, want);
        std::vector<ParsedResponse> mine;
        for (const auto& [r, _] : update.responses) {
            if (r.agentId == id) mine.push_back(r);
        }
        const auto responses = want["responses"].elements();
        ASSERT_EQ(mine.size(), responses.size());
        for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(mine[i], responses[i]);
        if (const auto modes = want["responseModes"]; modes.isObject()) {
            for (const auto& [key, value] : modes.members()) {
                const auto r = std::find_if(mine.begin(), mine.end(), [&](const ParsedResponse& p) { return p.key == key; });
                ASSERT_NE(r, mine.end()) << key;
                expectModes(r->modes, value);
            }
        }
    }
    EXPECT_EQ(fixtures::listFiles(opencodeDir), before);

    // Then the Antigravity databases, same dedupe set; reading must not create any file.
    const auto antigravityDir = fixtures::fixtureDir() / "antigravity";
    const auto antigravityBefore = fixtures::listFiles(antigravityDir);
    const auto wantDatabases = expected.root()["antigravity"]["databases"];
    std::vector<std::unique_ptr<antigravity::Parser>> databases;
    std::vector<Collector> databaseSinks;
    databaseSinks.reserve(32);
    std::vector<AgentLink> databaseLinks;
    for (const auto& entry : expected.root()["antigravity"]["order"].elements()) {
        const std::string relative = *entry.string();
        const auto absolute = (fixtures::fixtureDir() / relative).string();
        SCOPED_TRACE(relative);
        auto parser = std::make_unique<antigravity::Parser>(absolute, deduper);
        auto database = antigravity::Database::open(absolute);
        antigravity::Batch batch;
        antigravity::Cursors cursors;
        databaseSinks.emplace_back();
        Collector& sink = databaseSinks.back();
        if (database->supported()) {
            database->read(cursors, batch);
        } else {
            batch.unsupported = true;
        }
        parser->apply(batch, sink);
        const auto want = wantDatabases[relative];
        ASSERT_TRUE(want.valid());
        if (want["unsupported"].isTrue()) {
            EXPECT_TRUE(parser->unsupported());
            EXPECT_TRUE(sink.order.empty());
            continue;
        }
        EXPECT_FALSE(parser->unsupported());
        EXPECT_EQ(sourceId(parser->source()), *want["source"].string());
        EXPECT_EQ(parser->project(), fixtures::optString(want["project"]));
        EXPECT_EQ(parser->model(), fixtures::optString(want["model"]));
        EXPECT_EQ(parser->isSubagent(), want["isSubagent"].isTrue());
        EXPECT_EQ(phaseId(parser->phase()), *want["phase"].string());
        EXPECT_EQ(parser->inFlightStartMs(), fixtures::optInt(want["inFlightStartMs"]));
        EXPECT_EQ(parser->lastLineMs(), fixtures::optInt(want["lastLineMs"]));
        EXPECT_EQ(parser->malformedLines(), *fixtures::optInt(want["malformedLines"]));
        const auto responses = want["responses"].elements();
        ASSERT_EQ(sink.order.size(), responses.size());
        for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(sink.byKey[sink.order[i]], responses[i]);
        databaseLinks.push_back(parser->link());
        databases.push_back(std::move(parser));
    }
    std::map<std::string, std::string> wantDatabaseParents, gotDatabaseParents;
    for (const auto& [relative, want] : wantDatabases.members()) {
        if (auto parent = fixtures::optString(want["parentFile"])) {
            wantDatabaseParents[(fixtures::fixtureDir() / relative).string()] = (fixtures::fixtureDir() / *parent).string();
        }
    }
    for (const auto& [id, parent] : parent_links::resolve(databaseLinks)) gotDatabaseParents[id] = parent;
    EXPECT_EQ(gotDatabaseParents, wantDatabaseParents);
    EXPECT_EQ(fixtures::listFiles(antigravityDir), antigravityBefore);

    // Then the OpenClaw agent databases, one agent per transcript window; reading must not create any file.
    const auto openclawDir = fixtures::fixtureDir() / "openclaw";
    const auto openclawBefore = fixtures::listFiles(openclawDir);
    std::map<std::string, std::vector<json::Value>> windowTraces;
    for (const auto& t : expected.root()["openclaw"]["traces"].elements()) {
        windowTraces[*t["database"].string() + "#" + *t["window"].string()].push_back(t);
    }
    const auto prefix = fixtures::fixtureDir().string() + "/";
    for (const auto& entry : expected.root()["openclaw"]["dbOrder"].elements()) {
        const std::string relative = *entry.string();
        SCOPED_TRACE(relative);
        const auto want = expected.root()["openclaw"]["databases"][relative];
        std::map<std::string, Collector> sinks;
        auto parsers = openclaw::readAll(
            prefix + relative, deduper, [&](openclaw::Parser& p) -> ResponseSink& { return sinks[p.agentId()]; },
            [&](openclaw::Parser& p, std::int64_t seq) {
                for (const auto& t : windowTraces[p.agentId().substr(prefix.size())]) {
                    if (*fixtures::optInt(t["afterSeq"]) != seq) continue;
                    SCOPED_TRACE(p.agentId() + " after seq " + std::to_string(seq));
                    EXPECT_EQ(phaseId(p.phase()), *t["phase"].string());
                    EXPECT_EQ(p.inFlightStartMs(), fixtures::optInt(t["inFlightStartMs"]));
                }
            });
        if (want["unsupported"].isTrue()) {
            EXPECT_FALSE(parsers.has_value());
            continue;
        }
        ASSERT_TRUE(parsers.has_value());
        ASSERT_EQ(parsers->size(), want.members().size());
        for (const auto& parser : *parsers) {
            const std::string id = parser->agentId().substr(prefix.size());
            SCOPED_TRACE(id);
            const auto agent = want[id];
            ASSERT_TRUE(agent.valid());
            EXPECT_EQ(sourceId(parser->source()), *agent["source"].string());
            EXPECT_EQ(parser->project(), fixtures::optString(agent["project"]));
            EXPECT_EQ(parser->model(), fixtures::optString(agent["model"]));
            EXPECT_EQ(parser->isSubagent(), agent["isSubagent"].isTrue());
            const auto parent = parser->parentId() ? std::optional<std::string>(parser->parentId()->substr(prefix.size())) : std::nullopt;
            EXPECT_EQ(parent, fixtures::optString(agent["parentId"]));
            EXPECT_EQ(phaseId(parser->phase()), *agent["phase"].string());
            EXPECT_EQ(parser->inFlightStartMs(), fixtures::optInt(agent["inFlightStartMs"]));
            EXPECT_EQ(parser->lastLineMs(), fixtures::optInt(agent["lastLineMs"]));
            EXPECT_EQ(parser->malformedLines(), *fixtures::optInt(agent["malformedLines"]));
            auto& sink = sinks[parser->agentId()];
            const auto responses = agent["responses"].elements();
            ASSERT_EQ(sink.order.size(), responses.size());
            for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(sink.byKey[sink.order[i]], responses[i]);
        }
    }
    EXPECT_EQ(fixtures::listFiles(openclawDir), openclawBefore);
}
