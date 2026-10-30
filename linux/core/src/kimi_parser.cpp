#include "agentspeed/kimi_parser.hpp"

#include "agentspeed/harness.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed {

namespace {

std::string parentOf(const std::string& path) {
    const auto slash = path.rfind('/');
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

}  // namespace

struct KimiParser::Line {
    enum class Status { Blank, Malformed, Valid } status = Status::Blank;
    std::optional<std::string> type, agent, model, cwd, provider, role, responseId, thinkingEffort, thinkingLevel;
    // The line names a model, even an empty one (EXECUTION-MODES, Kimi Code CLI).
    bool namesModel = false;
    std::optional<std::int64_t> timeMs, durationMs, firstTokenMs, streamMs;
    std::optional<TokenCounts> usage;
    bool toolCalls = false, partial = false;

    static Line read(std::string_view text) {
        Line result;
        if (json::isBlank(text)) return result;
        json::Document document;
        if (!document.parse(text) || !document.root().isObject()) {
            result.status = Status::Malformed;
            return result;
        }
        const auto root = document.root();
        result.status = Status::Valid;
        result.type = root["type"].nonEmptyString();
        result.agent = root["agentId"].nonEmptyString();
        result.timeMs = harness::timestamp(root["time"]);
        if (!result.timeMs) result.timeMs = harness::timestamp(root["timestamp"]);
        result.cwd = root["environmentDisclosure"]["cwd"].nonEmptyString();
        result.model = root["modelAlias"].nonEmptyString();
        if (!result.model) result.model = root["model"].nonEmptyString();
        result.provider = root["provider"].nonEmptyString();
        result.namesModel = root["model"].valid();
        result.thinkingEffort = root["thinkingEffort"].nonEmptyString();
        result.thinkingLevel = root["thinkingLevel"].nonEmptyString();
        auto message = root["message"];
        const auto meta = message["meta"];
        if (message["message"].isObject()) message = message["message"];

        if (result.type == "context.append_loop_event") {
            const auto ev = root["event"];
            result.type = ev["type"].nonEmptyString();
            result.responseId = ev["messageId"].nonEmptyString();
            result.firstTokenMs = harness::milliseconds(ev["llmFirstTokenLatencyMs"]);
            result.streamMs = harness::milliseconds(ev["llmStreamDurationMs"]);
            const auto finish = ev["finishReason"].nonEmptyString();
            result.toolCalls = result.type == "tool.call" || finish == "tool_use" || finish == "tool_calls";
            if (const auto usage = ev["usage"]; usage.isObject()) result.usage = harness::kimiUsage(usage);
        } else if (result.type == "context.append_message" || result.type == "agent.message.appended" ||
                   result.type == "human.agent.message.appended") {
            result.role = message["role"].nonEmptyString();
            result.partial = message["partial"].isTrue();
            result.responseId = message["providerMessageId"].nonEmptyString();
            if (!result.responseId) result.responseId = message["id"].nonEmptyString();
            if (!result.responseId) result.responseId = meta["messageId"].nonEmptyString();
            auto usage = message["usage"];
            if (!usage.isObject()) usage = meta["usage"];
            if (usage.isObject()) result.usage = harness::kimiUsage(usage);
            auto timing = message["llmTiming"];
            if (!timing.isObject()) timing = meta["llmTiming"];
            result.firstTokenMs = harness::milliseconds(timing["llmFirstTokenLatencyMs"]);
            result.streamMs = harness::milliseconds(timing["llmStreamDurationMs"]);
            result.durationMs = harness::milliseconds(message["durationMs"]);
            if (!result.durationMs) result.durationMs = harness::milliseconds(meta["durationMs"]);
            auto model = meta["model"]["model"].nonEmptyString();
            if (!model) model = message["model"].nonEmptyString();
            if (model) result.model = model;
            result.namesModel = result.namesModel || meta["model"]["model"].valid();
            if (auto provider = meta["model"]["provider"].nonEmptyString()) result.provider = provider;
            const auto calls = message["toolCalls"];
            result.toolCalls = calls.isArray() && calls.size() > 0;
        }
        return result;
    }
};

KimiParser::KimiParser(std::string agentId, Deduper& deduper) : TranscriptParser(std::move(agentId), deduper, SourceKind::Kimi) {
    const auto folder = parentOf(this->agentId());
    localAgent_ = path_text::basename(folder);
    sessionFolder_ = parentOf(parentOf(folder));
    restoreMetadata();
}

void KimiParser::reset() {
    TranscriptParser::reset();
    responses_.clear();
    requestStart_.reset();
    requestId_.reset();
    provider_.reset();
    contextModes_ = ExecutionModes();
    restoreMetadata();
}

AgentLink KimiParser::link() const { return {agentId(), LinkKind::KimiSession, agentId(), parentPath_, createdMs_, 0}; }

void KimiParser::feed(std::string_view text, ResponseSink& sink) {
    const auto line = Line::read(text);
    if (line.status == Line::Status::Malformed) {
        countMalformed();
        return;
    }
    if (line.status != Line::Status::Valid || (line.agent && *line.agent != localAgent_)) return;
    setProjectFromCwd(line.cwd);
    setModel(line.model);
    if (line.provider) provider_ = line.provider;
    if (!line.timeMs) return;
    const auto time = *line.timeMs;
    touch(time);
    const auto& type = line.type;
    applyModes(line);
    if (type == "llm.request") {
        requestStart_ = time;
        requestId_ = line.responseId;
        setPhase(Phase::Generating, time, time);
    } else if (type == "step.begin" || type == "turn.step.started") {
        if (!requestStart_) requestStart_ = time;
        setPhase(Phase::Generating, time, requestStart_);
    } else if (type == "turn.prompt" || type == "turn.started") {
        setPhase(Phase::Generating, time, time);
    } else if (type == "step.end") {
        complete(line, time, sink);
    } else if (type == "context.append_message" || type == "agent.message.appended" || type == "human.agent.message.appended") {
        if (line.role == "assistant") {
            complete(line, time, sink);
        } else if (line.role == "user") {
            setPhase(Phase::Generating, time, time);
        } else if (line.role == "tool" || line.role == "tool_result") {
            setPhase(Phase::Generating, time, std::nullopt);
        }
    } else if (type == "tool.call") {
        setPhase(Phase::Tools, time, std::nullopt);
    } else if (type == "tool.result") {
        setPhase(Phase::Generating, time, std::nullopt);
    } else if (type == "turn.ended" || type == "turn.cancel") {
        requestStart_.reset();
        setPhase(Phase::Idle, time, std::nullopt);
    }
}

// A named model replaces the variant. A selection replaces the effort when it names one; every `llm.request` replaces
// it, or clears it without one.
void KimiParser::applyModes(const Line& line) {
    const auto replace = [&](ModeKind kind, const std::optional<ModeFact>& fact) {
        contextModes_ = contextModes_.without([kind](const ModeFact& f) { return f.kind == kind; }).with(fact);
    };
    if (line.namesModel) {
        const auto variant = native::kimiModel(line.model);
        replace(ModeKind::ModelVariant, variant.empty() ? std::nullopt : std::optional<ModeFact>(variant.facts().front()));
    }
    if (line.type == "profile.bind" || line.type == "config.update") {
        auto token = line.thinkingEffort;
        if (!token && line.type == "config.update") token = line.thinkingLevel;
        if (token) replace(ModeKind::Effort, native::effort(token, ModeEvidence::Selected, ModeScope::Session));
    } else if (line.type == "llm.request") {
        replace(ModeKind::Effort, native::effort(line.thinkingEffort, ModeEvidence::Recorded, ModeScope::Request));
    }
    setModes(contextModes_);
}

void KimiParser::complete(const Line& line, std::int64_t time, ResponseSink& sink) {
    if (line.partial) {
        setPhase(Phase::Generating, time, requestStart_);
        return;
    }
    if (line.usage) {
        auto id = line.responseId;
        if (!id && line.type == "step.end") id = requestId_;
        if (!id) id = path_text::basename(sessionFolder_) + ":" + localAgent_ + ":" + std::to_string(time);
        requestId_ = id;
        const std::string key = "kimi:" + *id;
        std::optional<std::int64_t> duration = line.durationMs;
        if (line.firstTokenMs && line.streamMs && *line.firstTokenMs <= INT64_MAX - *line.streamMs) {
            duration = *line.firstTokenMs + *line.streamMs;
        }
        const auto timed = responses_.find(key);
        const auto end = timed != responses_.end() ? timed->second.endMs : time;
        auto start = timed != responses_.end() ? timed->second.startMs : requestStart_;
        if (!start && duration && *duration >= 0 && *duration <= end) start = end - *duration;
        start = harness::validTimestamp(start);

        ParsedResponse response;
        response.key = key;
        response.source = SourceKind::Kimi;
        response.agentId = agentId();
        response.model = model();
        response.startMs = start;
        response.endMs = end;
        response.tokens = *line.usage;
        response.provider = provider_;
        response.modes = timed != responses_.end() ? timed->second.modes : contextModes_;
        std::string owner;
        const bool completion = deduper().tryClaim(key, agentId(), &owner);
        if (completion || owner == agentId()) {
            const auto previous = responses_.find(key);
            if (previous == responses_.end() || !(previous->second == response)) {
                responses_[key] = response;
                sink.onResponse(response, completion);
            }
        }
    }
    requestStart_.reset();
    setPhase(line.toolCalls ? Phase::Tools : Phase::Idle, time, std::nullopt);
}

void KimiParser::restoreMetadata() {
    setSource(SourceKind::Kimi);
    setSubagent(localAgent_ != "main");
    parentPath_ = isSubagent() ? std::optional<std::string>(sessionFolder_ + "/agents/main/wire.jsonl") : std::nullopt;
    const auto state = harness::readMetadata(sessionFolder_ + "/state.json", 512 * 1024);
    if (!state) return;
    const auto root = state->root();
    setProjectFromCwd(root["cwd"].nonEmptyString());
    createdMs_ = harness::timestamp(root["createdAt"]);
    const auto metadata = root["agents"][localAgent_];
    setSubagent(metadata["type"].equals("sub") || localAgent_ != "main");
    if (const auto parent = metadata["parentAgentId"].nonEmptyString(); parent && harness::isPlainComponent(*parent)) {
        parentPath_ = sessionFolder_ + "/agents/" + *parent + "/wire.jsonl";
    }
}

}  // namespace agentspeed
