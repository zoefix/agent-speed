#include <gtest/gtest.h>

#include <algorithm>
#include <map>

#include "agentspeed/modes.hpp"
#include "agentspeed/report.hpp"
#include "agentspeed/report_mode_rows.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;

// spec/fixtures/execution-mode-cases.json (spec/EXECUTION-MODES.md): normalization, attribution, the report capability,
// the derived speed, the effort meter, bolts and the display order.
namespace {

std::optional<ModeKind> kindOf(std::string_view name) {
    for (const auto k : {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing, ModeKind::Workflow,
                         ModeKind::Effort, ModeKind::Speed}) {
        if (modeKindName(k) == name) return k;
    }
    return std::nullopt;
}

std::optional<ModeEvidence> evidenceOf(std::string_view name) {
    for (const auto e : {ModeEvidence::Configured, ModeEvidence::Selected, ModeEvidence::Recorded, ModeEvidence::Served}) {
        if (modeEvidenceName(e) == name) return e;
    }
    return std::nullopt;
}

std::optional<ModeScope> scopeOf(std::string_view name) {
    for (const auto s : {ModeScope::Configuration, ModeScope::Session, ModeScope::Turn, ModeScope::Request, ModeScope::Response}) {
        if (modeScopeName(s) == name) return s;
    }
    return std::nullopt;
}

ModeFact factOf(const json::Value& v) {
    ModeFact f;
    f.kind = *kindOf(*v["kind"].string());
    f.value = *v["value"].string();
    f.raw = v["rawValue"].string().value_or(f.value);
    f.evidence = *evidenceOf(*v["evidence"].string());
    f.scope = *scopeOf(*v["scope"].string());
    f.provider = fixtures::optString(v["provider"]);
    return f;
}

ExecutionModes modesOf(const json::Value& list) {
    std::vector<ModeFact> facts;
    for (const auto& f : list.elements()) facts.push_back(factOf(f));
    return ExecutionModes(std::move(facts));
}

// The adapters of the fixture, with the same rules as the native readers.
std::optional<ModeFact> normalize(std::string_view adapter, const std::optional<std::string>& raw, const std::optional<std::string>& provider,
                                  ModeEvidence evidence, ModeScope scope) {
    if (provider && !ExecutionModes::isIdentifier(*provider, 32)) return std::nullopt;
    const auto first = [](const ExecutionModes& modes) {
        return modes.empty() ? std::nullopt : std::optional<ModeFact>(modes.facts().front());
    };
    if (adapter == "serviceTier") {
        if (provider == "anthropic") return std::nullopt;
        if (provider == "google") return first(native::googleTier(raw, evidence, scope));
        return ModeFact::create(ModeKind::ServiceTier, raw, evidence, provider, scope);
    }
    if (adapter == "zcodeModel") return first(native::zcodeModel(raw, evidence, scope));
    if (adapter == "zcodeProvider") return first(native::zcodeProvider(raw, evidence, scope));
    if (adapter == "droidModel") return first(native::droidModel(raw, evidence, scope));
    if (adapter == "ompTier") return first(native::ompTier(raw, provider, evidence, scope));
    if (adapter == "hermesEffort") return first(native::hermesEffort(raw, evidence, scope));
    if (adapter == "hermesServiceTier") return first(native::hermesServiceTier(raw, evidence, scope));
    if (adapter == "claudeSpeed") return ModeFact::create(ModeKind::ServiceTier, raw, evidence, provider, scope);
    if (adapter == "kimiModel") {
        const auto modes = native::kimiModel(raw);
        return modes.empty() ? std::nullopt : std::optional<ModeFact>(modes.facts().front());
    }
    if (adapter == "routing") {
        static const std::map<std::string, std::string> kCanonical = {
            {"fast-model", "fast"}, {"balanced-model", "balanced"}, {"deep-model", "deep"}, {"auto", "auto"}};
        const auto it = kCanonical.find(raw.value_or(""));
        if (it == kCanonical.end()) return std::nullopt;
        auto fact = ModeFact::create(ModeKind::Routing, raw, evidence, provider, scope);
        if (fact) fact->value = it->second;
        return fact;
    }
    if (adapter == "workflow") {
        if (raw != "fast" && raw != "planning") return std::nullopt;
        return ModeFact::create(ModeKind::Workflow, raw, evidence, provider, scope);
    }
    if (adapter == "effort") return native::effort(raw, evidence, scope);
    return std::nullopt;
}

class ExecutionModeCases : public ::testing::Test {
protected:
    static void SetUpTestSuite() {
        file_ = new fixtures::JsonFile(fixtures::fixtureDir() / "execution-mode-cases.json");
        ASSERT_TRUE(file_->ok);
    }
    static void TearDownTestSuite() {
        delete file_;
        file_ = nullptr;
    }
    static json::Value section(std::string_view name) { return file_->root()[name]; }
    static fixtures::JsonFile* file_;
};
fixtures::JsonFile* ExecutionModeCases::file_ = nullptr;

}  // namespace

TEST_F(ExecutionModeCases, Normalization) {
    for (const auto& c : section("normalization").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto got = normalize(*c["adapter"].string(), fixtures::optString(c["rawValue"]), fixtures::optString(c["provider"]),
                                   *evidenceOf(*c["evidence"].string()), *scopeOf(*c["scope"].string()));
        if (c["expected"].isNull()) {
            EXPECT_FALSE(got.has_value()) << got->value;
            continue;
        }
        ASSERT_TRUE(got.has_value());
        EXPECT_EQ(*got, factOf(c["expected"]));
    }
}

TEST_F(ExecutionModeCases, Attribution) {
    for (const auto& c : section("attribution").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto got = modesOf(c["facts"]).attribute(*kindOf(*c["kind"].string()));
        EXPECT_EQ(got.mode, *c["expected"]["mode"].string());
        EXPECT_EQ(got.evidence, *c["expected"]["evidence"].string());
        EXPECT_EQ(got.provider, fixtures::optString(c["expected"]["provider"]));
    }
    for (const auto& c : section("speed").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto got = modesOf(c["facts"]).attribute(ModeKind::Speed);
        EXPECT_EQ(got.kind, ModeKind::Speed);
        EXPECT_EQ(got.mode, *c["expected"]["mode"].string());
        EXPECT_EQ(got.evidence, *c["expected"]["evidence"].string());
        EXPECT_EQ(got.provider, fixtures::optString(c["expected"]["provider"]));
    }
}

TEST_F(ExecutionModeCases, Current) {
    for (const auto& c : section("current").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto got = effort_speed::current(*kindOf(*c["kind"].string()), modesOf(c["facts"]), modesOf(c["session"]), modesOf(c["configured"]));
        EXPECT_EQ(got.mode, *c["expected"]["mode"].string());
        EXPECT_EQ(got.evidence, *c["expected"]["evidence"].string());
        EXPECT_EQ(got.provider, fixtures::optString(c["expected"]["provider"]));
    }
}

TEST_F(ExecutionModeCases, Ledger) {
    const auto attribution = [](const json::Value& v) {
        return ModeAttribution{ModeKind::Effort, *v["mode"].string(), *v["evidence"].string(), std::nullopt};
    };
    for (const auto& c : section("ledger").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto got = effort_speed::ledgerEffort(attribution(c["response"]), attribution(c["current"]));
        EXPECT_EQ(got.mode, *c["expected"]["mode"].string());
        EXPECT_EQ(got.evidence, *c["expected"]["evidence"].string());
    }
}

TEST_F(ExecutionModeCases, AntigravityEffort) {
    for (const auto& c : section("antigravityEffort").elements()) {
        SCOPED_TRACE(*c["id"].string());
        if (c["model"].valid()) {
            const auto got = native::antigravityModelEffort(*c["model"].string());
            if (c["expected"].isNull()) {
                EXPECT_FALSE(got.has_value());
                continue;
            }
            ASSERT_TRUE(got.has_value());
            EXPECT_EQ(got->first, *c["expected"].elements()[0].string());
            EXPECT_EQ(got->second, *c["expected"].elements()[1].string());
        } else {
            EXPECT_EQ(native::antigravityLabelEffort(*c["label"].string()), fixtures::optString(c["expected"]));
        }
    }
}

TEST_F(ExecutionModeCases, Capability) {
    for (const auto& c : section("capabilities").elements()) {
        SCOPED_TRACE(*c["id"].string());
        auto text = fixtures::compactJson(c["extensions"]);
        text = R"({"protocol":"agent-speed-report","versions":[1],"service":{"name":"E","terms":"https://e.com/t","privacy":"https://e.com/p"},"api":"https://api.e.com","extensions":)" +
               text + "}";
        json::Document doc;
        ASSERT_TRUE(doc.parse(text));
        const auto discovery = report::parseDiscovery(doc.root(), "https://e.com/.well-known/agent-speed-report");
        ASSERT_TRUE(discovery);
        EXPECT_EQ(discovery->has("executionModes") && c["modelsEnabled"].isTrue(), c["expected"].isTrue());
        const int version = discovery->has("executionModes") && c["modelsEnabled"].isTrue() ? discovery->extensions.at("executionModes") : 0;
        EXPECT_EQ(version, *c["version"].truncated());
    }
}

TEST_F(ExecutionModeCases, ReportRows) {
    const auto countsOf = [](const json::Value& v) {
        return report::mode_rows::Counts{*v["requests"].truncated(), *v["output"].truncated(), *v["measuredOutput"].truncated(),
                                         *v["measuredMs"].truncated()};
    };
    int checked = 0;
    for (const auto& c : section("reportRows").elements()) {
        SCOPED_TRACE(*c["id"].string());
        std::map<SourceKind, report::mode_rows::Counts> sources;
        for (const auto& [id, total] : c["sources"].members()) sources[*sourceFromId(std::string(id))] = countsOf(total);
        std::map<report::mode_rows::Group, report::mode_rows::Counts> groups;
        for (const auto& g : c["groups"].elements()) {
            const report::mode_rows::Group key{*sourceFromId(*g["source"].string()), *kindOf(*g["kind"].string()), *g["mode"].string(),
                                               *g["evidence"].string(), fixtures::optString(g["provider"])};
            groups[key] += countsOf(g);
        }
        const auto rows = report::mode_rows::build(sources, groups, static_cast<int>(*c["version"].truncated()));
        if (c["expected"].isNull()) {
            EXPECT_FALSE(rows.has_value());
        } else {
            ASSERT_TRUE(rows.has_value());
            const auto want = c["expected"].elements();
            ASSERT_EQ(rows->size(), want.size());
            for (std::size_t i = 0; i < want.size(); ++i) {
                const auto& got = (*rows)[i];
                EXPECT_EQ(sourceId(got.key.source), *want[i]["source"].string());
                EXPECT_EQ(modeKindName(got.key.kind), *want[i]["kind"].string());
                EXPECT_EQ(got.key.provider, fixtures::optString(want[i]["provider"]));
                EXPECT_EQ(got.key.mode, *want[i]["mode"].string());
                EXPECT_EQ(got.key.evidence, *want[i]["evidence"].string());
                EXPECT_EQ(got.counts, countsOf(want[i]));
            }
        }
        ++checked;
    }
    EXPECT_GE(checked, 9);
}

TEST_F(ExecutionModeCases, EffortMeterBoltsAndOrder) {
    for (const auto& c : section("effortDisplay").elements()) {
        const auto value = *c["value"].string();
        SCOPED_TRACE(value);
        const auto level = fixtures::optInt(c["expected"]["level"]);
        EXPECT_EQ(effort_speed::level(value), level ? std::optional<int>(static_cast<int>(*level)) : std::nullopt);
        EXPECT_EQ(effort_speed::isTopRung(value), c["expected"]["top"].isTrue());
    }
    for (const auto& c : section("speedBolts").elements()) {
        EXPECT_EQ(effort_speed::bolts(*c["value"].string()), *c["bolts"].truncated()) << *c["value"].string();
    }
    for (const auto& c : section("displayOrder").elements()) {
        SCOPED_TRACE(*c["id"].string());
        const auto kind = *kindOf(*c["kind"].string());
        std::vector<std::string> values;
        for (const auto& v : c["values"].elements()) values.push_back(*v.string());
        std::stable_sort(values.begin(), values.end(), [&](const std::string& a, const std::string& b) { return effort_speed::less(kind, a, b); });
        std::vector<std::string> want;
        for (const auto& v : c["expected"].elements()) want.push_back(*v.string());
        EXPECT_EQ(values, want);
    }
}
