#include <limits>

#include "fields.hpp"

// REMOTE 5.4: the agent, response, tool and status objects.
namespace agentspeed::remote::detail {

namespace {

template <typename Enum, std::size_t N>
std::optional<Enum> named(std::string_view text, const Enum (&values)[N], std::string_view (*nameOf)(Enum)) {
    for (const auto value : values) {
        if (nameOf(value) == text) return value;
    }
    return std::nullopt;
}

constexpr ModeKind kKinds[] = {ModeKind::ServiceTier, ModeKind::Accelerator, ModeKind::ModelVariant, ModeKind::Routing,
                               ModeKind::Workflow,    ModeKind::Effort,      ModeKind::Speed};
constexpr ModeEvidence kEvidence[] = {ModeEvidence::Configured, ModeEvidence::Selected, ModeEvidence::Recorded, ModeEvidence::Served};
constexpr ModeScope kScopes[] = {ModeScope::Configuration, ModeScope::Session, ModeScope::Turn, ModeScope::Request, ModeScope::Response};

// A fact valid by EXECUTION-MODES: created from its raw value, then given the relay's canonical value.
ModeFact fact(const Fields& f) {
    const auto kind = named(f.string("kind", 32), kKinds, modeKindName);
    const auto evidence = named(f.string("evidence", 32), kEvidence, modeEvidenceName);
    const auto scope = named(f.string("scope", 32), kScopes, modeScopeName);
    const auto value = f.string("value", 64);
    const auto raw = f.string("rawValue", 64);
    const auto provider = f.optionalString("provider", 32);
    if (!kind || !evidence || !scope || !ExecutionModes::isIdentifier(value, 64)) fail(LineError::Kind::OutOfRange, "fact");
    auto created = ModeFact::create(*kind, raw, *evidence, provider, *scope);
    if (!created) fail(LineError::Kind::OutOfRange, "fact");
    created->value = value;
    return *created;
}

}  // namespace

ExecutionModes Fields::modes(std::string_view key) const {
    std::vector<ModeFact> facts;
    const auto items = array(key, limits::kFactsPerModeList);
    for (std::size_t i = 0; i < items.size(); ++i) facts.push_back(fact(of(items[i], name(key) + "." + std::to_string(i))));
    return ExecutionModes(std::move(facts));
}

work::Action action(const Fields& f) {
    work::Action a;
    a.tool = f.string("tool", limits::kToolBytes);
    a.digest = f.hex("digest", 16);
    a.callMs = f.time("callMs");
    a.resultMs = f.optionalTime("resultMs");
    a.failed = f.optionalBoolean("failed");
    a.model = f.modelString("model", limits::kModelBytes);
    a.seeded = f.boolean("seeded");
    return a;
}

std::string relayAgentId(const Fields& f, std::string_view key) {
    auto id = f.string(key, limits::kAgentIdBytes);
    if (id.empty()) fail(LineError::Kind::OutOfRange, std::string(key));
    return id;
}

RemoteAgent agent(const Fields& f, const std::string& hostId, const std::string& prefix) {
    RemoteAgent a;
    a.hostId = hostId;
    a.relayId = relayAgentId(f, "id");
    a.id = prefix + a.relayId;
    const auto phase = f.string("phase", 16);
    if (phase == "idle") {
        a.phase = Phase::Idle;
    } else if (phase == "generating") {
        a.phase = Phase::Generating;
    } else if (phase == "tools") {
        a.phase = Phase::Tools;
    } else {
        fail(LineError::Kind::OutOfRange, "a.phase");
    }
    const auto items = f.array("actions", limits::kActionsPerAgent);
    for (std::size_t i = 0; i < items.size(); ++i) a.actions.push_back(action(Fields::of(items[i], "a.actions." + std::to_string(i))));
    a.source = f.source("source");
    a.project = f.optionalString("project", limits::kProjectBytes);
    a.cwd = f.optionalString("cwd", limits::kCwdBytes);
    a.model = f.modelString("model", limits::kModelBytes);
    a.subagent = f.boolean("subagent");
    a.inFlightStartMs = f.optionalTime("inFlightStartMs");
    a.lastLineMs = f.optionalTime("lastLineMs");
    a.modes = f.modes("modes");
    a.sessionModes = f.modes("sessionModes");
    a.configuredModes = f.modes("configuredModes");
    return a;
}

RemoteResponse response(const Fields& line, const std::string& hostId, const std::string& prefix) {
    const auto f = line.object("r");
    RemoteResponse out;
    out.hostId = hostId;
    auto& r = out.record;
    r.key = f.string("key", limits::kKeyBytes);
    if (r.key.empty()) fail(LineError::Kind::OutOfRange, "r.key");
    r.requestCount = f.integer("requestCount");
    if (r.requestCount < 1 || r.requestCount > 100'000) fail(LineError::Kind::OutOfRange, "r.requestCount");
    r.tokens.input = f.tokens("input");
    r.tokens.cachedInput = f.tokens("cachedInput");
    r.tokens.cacheWriteInput = f.tokens("cacheWriteInput");
    r.tokens.cacheWrite1hInput = std::min(r.tokens.cacheWriteInput, f.tokens("cacheWrite1hInput"));
    r.tokens.output = f.tokens("output");
    r.tokens.reasoning = f.tokens("reasoning");
    r.source = f.source("source");
    r.agentId = prefix + relayAgentId(f, "agent");
    r.model = f.modelString("model", limits::kModelBytes);
    r.startMs = f.optionalTime("startMs");
    r.endMs = f.time("endMs");
    r.firstMs = f.optionalTime("firstMs");
    r.final = f.boolean("final");
    r.provider = f.modelString("provider", limits::kProviderBytes);
    r.fast = f.boolean("fast");
    r.modes = f.modes("modes");
    out.completion = line.boolean("completion");
    out.project = line.string("project", limits::kProjectBytes);
    return out;
}

RemoteTool tool(const Fields& f, const std::string& hostId, const std::string& prefix) {
    RemoteTool t;
    t.hostId = hostId;
    t.agentId = prefix + relayAgentId(f, "agent");
    t.source = f.source("source");
    t.action.callMs = f.time("callMs");
    t.action.resultMs = f.time("resultMs");
    t.action.failed = f.optionalBoolean("failed");
    t.action.model = f.modelString("model", limits::kModelBytes);
    t.action.seeded = f.boolean("seeded");
    return t;
}

SourceReport status(const Fields& f) {
    SourceReport report;
    const auto items = f.array("sources", 64);
    for (std::size_t i = 0; i < items.size(); ++i) {
        const auto entry = Fields::of(items[i], "sources." + std::to_string(i));
        const auto key = entry.string("source", 64);
        RemoteSourceState state;
        state.available = entry.boolean("available");
        state.sessions = static_cast<int>(entry.count("sessions", std::numeric_limits<int>::max()));
        state.unsupported = entry.boolean("unsupported");
        state.configuredModes = entry.modes("configuredModes");
        const auto source = sourceFromId(key);
        if (!source) continue;
        state.source = *source;
        report.sources.push_back(std::move(state));
    }
    report.malformed = f.count("malformed");
    report.unreadable = f.count("unreadable");
    report.partialHistory = f.boolean("partialHistory");
    return report;
}

}  // namespace agentspeed::remote::detail
