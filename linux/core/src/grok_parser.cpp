#include "agentspeed/grok_parser.hpp"

#include <sys/stat.h>

#include <algorithm>

#include "agentspeed/harness.hpp"
#include "agentspeed/path_text.hpp"

namespace agentspeed {

namespace {

constexpr std::int64_t kMaxIntervalMs = 15 * 60'000;
constexpr std::size_t kMaxPending = 256;

std::string parentOf(const std::string& path) {
    const auto slash = path.rfind('/');
    return slash == std::string::npos ? std::string() : path.substr(0, slash);
}

GrokUsage readUsage(const json::Value& usage, std::optional<std::string> model) {
    GrokUsage result;
    result.model = std::move(model);
    const auto input = usage["inputTokens"].count();
    const auto output = usage["outputTokens"].count();
    result.tokens.input = input;
    result.tokens.cachedInput = std::min(input, usage["cachedReadTokens"].count());
    result.tokens.cacheWriteInput = std::min(input, usage["cacheCreationTokens"].count());
    result.tokens.output = output;
    result.tokens.reasoning = std::min(output, usage["reasoningTokens"].count());
    result.durationMs = usage["apiDurationMs"].count();
    result.requests = std::clamp<std::int64_t>(usage["modelCalls"].count(), 1, 100'000);
    return result;
}

}  // namespace

std::optional<GrokMetadata> GrokMetadata::read(const std::string& folder) {
    const auto document = harness::readMetadata(folder + "/summary.json", 1024 * 1024);
    if (!document) return std::nullopt;
    const auto root = document->root();
    const auto info = root["info"];
    return GrokMetadata{info["id"].nonEmptyString(),
                        info["cwd"].nonEmptyString(),
                        root["current_model_id"].nonEmptyString(),
                        root["parent_session_id"].nonEmptyString(),
                        root["session_kind"].nonEmptyString(),
                        harness::timestamp(root["created_at"])};
}

GrokParser::Line GrokParser::Line::read(std::string_view text) {
    Line result;
    if (json::isBlank(text)) return result;
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) {
        result.status = Status::Malformed;
        return result;
    }
    result.status = Status::Valid;
    const auto root = document.root();
    const auto method = root["method"].nonEmptyString();
    if (method != "session/update" && method != "_x.ai/session/update") return result;
    const auto parameters = root["params"];
    const auto update = parameters["update"];
    const auto meta = parameters["_meta"];
    result.type = update["sessionUpdate"].nonEmptyString();
    result.sessionId = parameters["sessionId"].nonEmptyString();
    result.promptId = update["prompt_id"].nonEmptyString();
    if (!result.promptId) result.promptId = meta["promptId"].nonEmptyString();
    if (!result.promptId) result.promptId = meta["eventId"].nonEmptyString();
    result.model = update["model_id"].nonEmptyString();
    if (!result.model) result.model = update["_meta"]["modelId"].nonEmptyString();
    result.timeMs = harness::timestamp(meta["agentTimestampMs"]);
    if (!result.timeMs) {
        const auto timestamp = root["timestamp"];
        const auto seconds = harness::milliseconds(timestamp);
        if (timestamp.isNumber()) {
            if (seconds && *seconds <= INT64_MAX / 1000) result.timeMs = harness::validTimestamp(*seconds * 1000);
        } else {
            result.timeMs = harness::validTimestamp(seconds);
        }
    }
    result.toolStatus = update["status"].nonEmptyString();
    if (result.type != "turn_completed") return result;

    const auto usage = update["usage"];
    const auto buckets = usage["modelUsage"];
    std::vector<GrokUsage> parsed;
    if (buckets.isObject() && buckets.size() > 0) {
        std::size_t taken = 0;
        for (const auto& [name, value] : buckets.members()) {
            if (taken++ == 256) break;
            if (value.isObject()) parsed.push_back(readUsage(value, name));
        }
    } else {
        auto totals = usage["totals"];
        if (!totals.isObject()) totals = usage;
        if (totals.isObject()) parsed.push_back(readUsage(totals, result.model));
    }
    result.usage = std::move(parsed);
    return result;
}

GrokParser::GrokParser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::GrokBuild) {
    sessionFolder_ = parentOf(this->agentId());
    sessionId_ = path_text::basename(sessionFolder_);
    applyMetadata(GrokMetadata::read(sessionFolder_));
}

void GrokParser::reset() {
    TranscriptParser::reset();
    responses_.clear();
    metadataSeen_ = false;
    suppressUsage_ = false;
    parentSessionId_.reset();
    parentPath_.reset();
    createdMs_.reset();
    pending_.clear();
    pendingOverflow_ = false;
    applyMetadata(GrokMetadata::read(sessionFolder_));
}

AgentLink GrokParser::link() const {
    return {agentId(), LinkKind::GrokBuildSession, sessionId_, parentSessionId_, createdMs_, 0};
}

void GrokParser::beginBatch(ResponseSink& sink) {
    applyMetadata(GrokMetadata::read(sessionFolder_));
    if (!metadataSeen_) return;
    while (!pending_.empty()) {
        const auto line = std::move(pending_.front());
        pending_.pop_front();
        if (line.timeMs) emitUsage(line, *line.timeMs, sink, false);
    }
}

void GrokParser::feed(std::string_view text, ResponseSink& sink) { apply(Line::read(text), sink); }

void GrokParser::apply(const Line& line, ResponseSink& sink) {
    if (line.status == Line::Status::Malformed) {
        countMalformed();
        return;
    }
    if (line.status != Line::Status::Valid || !line.timeMs) return;
    const auto time = *line.timeMs;
    setModel(line.model);
    touch(time);
    const auto& type = line.type;
    if (type == "user_message_chunk" || type == "agent_message_chunk" || type == "agent_thought_chunk") {
        setPhase(Phase::Generating, time, inFlightStartMs() ? inFlightStartMs() : std::optional<std::int64_t>(time));
    } else if (type == "tool_call" || type == "tool_call_update") {
        const bool done = line.toolStatus == "completed" || line.toolStatus == "failed";
        setPhase(done ? Phase::Generating : Phase::Tools, time, std::nullopt);
    } else if (type == "turn_completed") {
        if (!metadataSeen_ && line.usage && !line.usage->empty()) {
            for (const auto& bucket : *line.usage) {
                if (pending_.size() >= kMaxPending) {
                    pending_.pop_front();
                    pendingOverflow_ = true;
                }
                Line held = line;
                held.usage = std::vector<GrokUsage>{bucket};
                pending_.push_back(std::move(held));
            }
            markChanged();
            setPhase(Phase::Idle, time, std::nullopt);
            return;
        }
        emitUsage(line, time, sink);
        setPhase(Phase::Idle, time, std::nullopt);
    }
}

void GrokParser::emitUsage(const Line& line, std::int64_t time, ResponseSink& sink, bool updateModel) {
    if (suppressUsage_ || !line.usage) return;
    const auto prompt = line.promptId ? *line.promptId : line.sessionId.value_or(sessionId_) + ":" + std::to_string(time);
    for (const auto& bucket : *line.usage) {
        const auto model = bucket.model ? bucket.model : this->model();
        const std::string key = "grok:" + prompt + ":" + model.value_or("unknown");
        std::optional<std::int64_t> start;
        if (bucket.durationMs > 0 && bucket.durationMs <= kMaxIntervalMs && bucket.durationMs <= time) start = time - bucket.durationMs;
        ParsedResponse response;
        response.key = key;
        response.source = SourceKind::GrokBuild;
        response.agentId = agentId();
        response.model = model;
        response.startMs = start;
        response.endMs = time;
        response.tokens = bucket.tokens;
        response.requestCount = bucket.requests;
        std::string owner;
        const bool completion = deduper().tryClaim(key, agentId(), &owner);
        if (completion || owner == agentId()) {
            const auto previous = responses_.find(key);
            if (previous == responses_.end() || previous->second != response) {
                responses_[key] = response;
                sink.onResponse(response, completion);
            }
        }
        if (updateModel) setModel(model);
    }
}

void GrokParser::applyMetadata(const std::optional<GrokMetadata>& metadata) {
    setSource(SourceKind::GrokBuild);
    if (!metadata) return;
    if (!metadataSeen_) markChanged();
    if (metadata->id) sessionId_ = *metadata->id;
    setProjectFromCwd(metadata->cwd);
    setModel(metadata->model);
    parentSessionId_ = metadata->parent;
    createdMs_ = metadata->createdMs;
    const bool hasParent = parentSessionId_ && !parentSessionId_->empty();
    setSubagent(hasParent && (metadata->kind == "subagent" || metadata->kind == "subagent_fork"));
    suppressUsage_ = isSubagent() && hasParent;
    if (hasParent && harness::isPlainComponent(*parentSessionId_, 256)) {
        parentPath_ = parentOf(sessionFolder_) + "/" + *parentSessionId_ + "/updates.jsonl";
    } else {
        parentPath_.reset();
    }
    metadataSeen_ = true;
}

std::int64_t grokModifiedNs(const std::string& updatesPath, std::int64_t updatesWriteNs) {
    struct stat st {};
    const auto summary = parentOf(updatesPath) + "/summary.json";
    if (::stat(summary.c_str(), &st) != 0) return updatesWriteNs;
    const auto ns = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
    return std::max(ns, updatesWriteNs);
}

}  // namespace agentspeed
