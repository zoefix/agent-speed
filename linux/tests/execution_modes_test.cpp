#include <gtest/gtest.h>

#include <algorithm>
#include <map>

#include "agentspeed/modes.hpp"
#include "agentspeed/report.hpp"
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
    if (adapter == "serviceTier") {
        if (provider && *provider != "openai" && *provider != "openai-codex" && *provider != "azure") return std::nullopt;
        return ModeFact::create(ModeKind::ServiceTier, raw, evidence, provider, scope);
    }
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
    }
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
