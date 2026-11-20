#include "agentspeed/openbitfun.hpp"

#include <algorithm>
#include <fcntl.h>
#include <unistd.h>
#include <stdexcept>
#include <set>

#include "agentspeed/harness.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"

namespace agentspeed::openbitfun {

std::string projectsOf(const std::string& path) {
    const auto normal = fsx::normalize(path);
    return path_text::basename(normal) == "projects" ? normal : fsx::join(normal, "projects");
}

namespace {

std::vector<fsx::Entry> documents(const std::string& metadata, bool includeTraces) {
    const auto session = fsx::parentOf(metadata);
    std::vector<fsx::Entry> files;
    const auto add = [&](const std::string& folder, std::string_view prefix) {
        if (!fsx::isPlainDirectory(folder)) return;
        for (auto& file : fsx::list(folder)) {
            if (file.stat.regular && file.name.starts_with(prefix) && file.name.ends_with(".json")) {
                files.push_back(std::move(file));
            }
        }
    };
    add(fsx::join(session, "turns"), "turn-");
    if (includeTraces) {
        add(fsx::join(session, "request-traces"), "request-");
        const auto shared = fsx::join(fsx::parentOf(fsx::parentOf(session)), "request-traces");
        if (fsx::isPlainDirectory(shared)) add(fsx::join(shared, path_text::basename(session)), "request-");
    }
    std::sort(files.begin(), files.end(), [](const auto& a, const auto& b) { return a.path < b.path; });
    return files;
}

fsx::Stat combined(fsx::Stat metadata, const std::vector<fsx::Entry>& files, const UsageSnapshot* usage) {
    for (const auto& file : files) {
        metadata.length += file.stat.length;
        metadata.writeNs = std::max(metadata.writeNs, file.stat.writeNs);
    }
    if (usage) {
        metadata.contentSignature = usage->signature;
        metadata.writeNs = std::max(metadata.writeNs, usage->writeNs);
    }
    return metadata;
}

}  // namespace

std::optional<fsx::Stat> statOf(const std::string& metadata) {
    const auto stat = fsx::statOf(metadata);
    if (!stat || !stat->regular) return std::nullopt;
    const auto usage = UsageLedger::shared().forSession(path_text::basename(fsx::parentOf(metadata)));
    return combined(*stat, documents(metadata, !usage), usage.get());
}

void Parser::reset() {
    TranscriptParser::reset();
    session_.reset();
    parent_.reset();
    created_ = 0;
    responses_.clear();
    turns_.clear();
    traces_.clear();
    traceOrder_.clear();
    rounds_.clear();
    usage_.reset();
    baseline_.clear();
}

void Parser::feed(std::string_view text, ResponseSink&) {
    json::Document document;
    if (!document.parse(text) || !document.root().isObject()) { countMalformed(); return; }
    const auto root = document.root();
    const auto measurements = root["modelRounds"];
    if (const auto turn = root["turnId"].nonEmptyString(); turn && measurements.isArray()) {
        if (!session_ || root["sessionId"].nonEmptyString() != session_) return;
        const auto status = root["status"];
        const bool turnEnded = status.equals("completed") || status.equals("cancelled") || status.equals("error");
        auto first = harness::timestamp(root["startTime"]);
        auto last = harness::timestamp(root["endTime"]);
        const bool useRoundStart = !first, useRoundEnd = !last;
        auto previous = rounds_.lower_bound({*turn, {}});
        while (previous != rounds_.end() && previous->first.first == *turn) previous = rounds_.erase(previous);
        for (const auto& item : measurements.elements()) {
            const auto id = item["id"].nonEmptyString();
            if (!id) continue;
            auto model = item["effectiveModelName"].nonEmptyString();
            if (!model) model = item["modelConfigId"].nonEmptyString();
            const auto end = harness::timestamp(item["endTime"]);
            const auto phase = item["status"];
            const bool ended = turnEnded || end || phase.equals("completed") || phase.equals("failed") ||
                               phase.equals("cancelled") || phase.equals("interrupted");
            const auto start = harness::timestamp(item["startTime"]);
            if (useRoundStart && start) first = std::min(first.value_or(*start), *start);
            if (const auto time = end ? end : start; useRoundEnd && time) last = std::max(last.value_or(*time), *time);
            rounds_[{*turn, *id}] = {model, start, end,
                                     harness::milliseconds(item["durationMs"]), ended, item["attemptCount"].count()};
        }
        turns_[*turn] = {first, last, turnEnded};
        return;
    }
    const auto trace = root["trace_id"].nonEmptyString();
    if (!trace) {
        session_ = root["sessionId"].nonEmptyString();
        if (!session_) session_ = root["session_id"].nonEmptyString();
        if (!session_) return;
        const auto relationship = root["relationship"];
        parent_ = relationship["parentSessionId"].nonEmptyString();
        if (!parent_) parent_ = relationship["parent_session_id"].nonEmptyString();
        auto kind = root["sessionKind"].nonEmptyString();
        if (!kind) kind = root["session_kind"].nonEmptyString();
        setSubagent(kind == "subagent" || relationship["kind"].nonEmptyString() == "subagent");
        auto cwd = root["workspacePath"].nonEmptyString();
        if (!cwd) cwd = root["workspace_path"].nonEmptyString();
        setProjectFromCwd(cwd);
        created_ = harness::milliseconds(root["createdAt"]).value_or(harness::milliseconds(root["created_at"]).value_or(0));
        return;
    }
    if (usage_ || harness::milliseconds(root["version"]) != 1 || root["session_id"].nonEmptyString() != session_) return;
    const auto timing = root["timing"];
    const auto timestamp = [](const json::Value& value) -> std::optional<std::int64_t> {
        const auto text = value.string();
        return text ? parseTimestamp(*text) : std::nullopt;
    };
    auto start = timestamp(timing["started_at"]);
    const auto end = timestamp(timing["finished_at"]);
    const auto response = root["response"];
    const auto request = root["request"];
    auto [position, inserted] = traces_.try_emplace(*trace);
    if (inserted) traceOrder_.push_back(*trace);
    auto& attempt = position->second;
    if (attempt.completed && !response.isObject()) return;
    attempt = {root["turn_id"].nonEmptyString(), root["operation_kind"].nonEmptyString(),
               root["operation_id"].nonEmptyString(), request["model_id"].nonEmptyString(),
               start, end, response.isObject(), std::nullopt};
    const auto usage = response["usage"];
    if (!response.isObject() || !end || !usage.isObject()) return;
    if (const auto duration = harness::milliseconds(timing["duration_ms"])) {
        start = *duration <= 900'000 ? std::optional(*end - *duration) : std::nullopt;
    }
    ParsedResponse record;
    record.key = "openbitfun:" + *trace;
    record.source = SourceKind::OpenBitFun;
    record.agentId = agentId();
    record.model = request["model_id"].nonEmptyString();
    record.provider = request["provider"].nonEmptyString();
    record.startMs = start;
    record.endMs = *end;
    record.tokens = {usage["promptTokenCount"].count(), usage["cachedContentTokenCount"].count(),
                     usage["cacheCreationTokenCount"].count(), usage["candidatesTokenCount"].count(), 0};
    record.tokens.reasoning = std::min(record.tokens.output, usage["reasoningTokenCount"].count());
    attempt.record = std::move(record);
}

void Parser::upsert(const ParsedResponse& record, ResponseSink& sink) {
    const auto previous = responses_.find(record.key);
    if (previous != responses_.end() && previous->second == record) return;
    std::string owner;
    const auto completion = deduper().tryClaim(record.key, agentId(), &owner);
    if (!completion && owner != agentId()) return;
    const bool first = previous == responses_.end();
    responses_[record.key] = record;
    sink.onResponse(record, completion && first && record.requestCount > 0);
}

void Parser::withdraw(ParsedResponse record, ResponseSink& sink) {
    record.startMs.reset();
    record.tokens = {};
    record.requestCount = 0;
    upsert(record, sink);
}

bool Parser::setUsage(std::shared_ptr<const UsageSnapshot> usage) {
    if (usage == usage_) return false;
    if (!usage && usage_) {
        traces_.clear();
        traceOrder_.clear();
    }
    usage_ = std::move(usage);
    return true;
}

void Parser::applyRounds(ResponseSink& sink) {
    if (!session_) return;
    std::optional<std::int64_t> pending;
    std::set<std::string> active;
    const auto roundKey = [&](const RoundId& id) { return "openbitfun:round:" + *session_ + ":" + id.first + ":" + id.second; };
    for (const auto& trace : traceOrder_) {
        const auto& line = traces_.at(trace);
        if (!usage_ && line.start) {
            const auto time = line.end.value_or(*line.start);
            if (!lastLineMs() || time >= *lastLineMs()) setModel(line.model);
            touch(time);
            if (!line.completed) {
                bool closed = false;
                if (line.turn) {
                    const auto turn = turns_.find(*line.turn);
                    closed = turn != turns_.end() && turn->second.closed;
                } else {
                    int matches = 0;
                    for (const auto& [id, turn] : turns_) {
                        if (!turn.start || *turn.start > *line.start || (turn.closed && (!turn.end || *line.start > *turn.end))) continue;
                        ++matches;
                        closed = turn.closed;
                    }
                    closed = matches == 1 && closed;
                }
                if (!closed) pending = std::min(pending.value_or(*line.start), *line.start);
            }
        }
        if (!line.record) continue;
        auto record = *line.record;
        if (line.operation == "model_round" && line.turn && line.round) {
            record.traceRoundKey = roundKey({*line.turn, *line.round});
        } else if (!line.operation && line.start) {
            for (const auto& [id, round] : rounds_) {
                if ((line.turn && *line.turn != id.first) || !round.start || *round.start > *line.start
                    || !round.end || record.endMs > *round.end) continue;
                if (record.traceRoundKey) { record.traceRoundKey.reset(); break; }
                record.traceRoundKey = roundKey(id);
            }
        }
        if (usage_) {
            if (auto it = responses_.find(record.key); it != responses_.end()) {
                if (!record.traceRoundKey) record.traceRoundKey = it->second.traceRoundKey;
                withdraw(record, sink);
            }
        } else {
            active.insert(record.key);
            upsert(record, sink);
        }
    }
    std::vector<const decltype(rounds_)::value_type*> ordered;
    for (const auto& entry : rounds_) ordered.push_back(&entry);
    const auto timeOf = [](const Round& r) { return r.end.value_or(r.start.value_or(0)); };
    std::stable_sort(ordered.begin(), ordered.end(), [&](const auto* a, const auto* b) {
        return timeOf(a->second) < timeOf(b->second);
    });
    for (const auto* entry : ordered) {
        const auto& [id, round] = *entry;
        if (round.start) {
            const auto time = timeOf(round);
            if (!lastLineMs() || time >= *lastLineMs()) setModel(round.model);
            touch(time);
            if (!round.completed) pending = std::min(pending.value_or(*round.start), *round.start);
        }
        if (!round.end || !usage_ || baseline_.contains(id)) continue;
        const auto found = usage_->rounds.find(id);
        if (found == usage_->rounds.end()) continue;
        const auto& attempts = found->second;
        if (static_cast<std::int64_t>(attempts.size()) < round.attempts) continue;
        ParsedResponse record;
        record.key = roundKey(id);
        record.source = SourceKind::OpenBitFun;
        record.agentId = agentId();
        record.model = attempts.back().model ? attempts.back().model : round.model;
        record.provider = attempts.back().provider;
        record.endMs = *round.end;
        record.requestCount = static_cast<std::int64_t>(attempts.size());
        bool measured = true;
        auto& total = record.tokens;
        for (const auto& attempt : attempts) {
            if (!attempt.tokens) { measured = false; continue; }
            const auto& t = *attempt.tokens;
            total.input = TokenCounts::saturatingAdd(total.input, t.input);
            total.cachedInput = TokenCounts::saturatingAdd(total.cachedInput, t.cachedInput);
            total.cacheWriteInput = TokenCounts::saturatingAdd(total.cacheWriteInput, t.cacheWriteInput);
            total.output = TokenCounts::saturatingAdd(total.output, t.output);
            total.reasoning = TokenCounts::saturatingAdd(total.reasoning, t.reasoning);
        }
        const auto duration = round.duration.value_or(record.endMs - round.start.value_or(record.endMs));
        if (measured && duration > 0 && duration <= 900'000) record.startMs = record.endMs - duration;
        active.insert(record.key);
        upsert(record, sink);
    }
    for (const auto& [key, record] : responses_)
        if (record.requestCount > 0 && !active.contains(key)) withdraw(record, sink);
    if (lastLineMs()) setPhase(pending ? Phase::Generating : Phase::Idle, pending.value_or(*lastLineMs()), pending);
}

void SessionTailer::startAt(std::int64_t offset, Alignment) {
    offset_ = offset;
    if (offset <= 0) return;
    const auto usage = ledger_.forSession(path_text::basename(fsx::parentOf(path_)));
    if (usage) {
        for (const auto& [id, attempts] : usage->rounds) baseline_.insert(id);
    }
    for (const auto& e : documents(path_, !usage)) known_[e.path] = {e.stat.length, e.stat.writeNs, e.stat.device, e.stat.inode};
}

TailResult SessionTailer::read(TranscriptParser& parser, ResponseSink& sink) {
    const auto metadata = fsx::statOf(path_);
    if (!metadata || !metadata->regular) throw FileGone();
    const auto usage = ledger_.forSession(path_text::basename(fsx::parentOf(path_)));
    auto files = documents(path_, !usage);
    const auto stat = combined(*metadata, files, usage.get());
    auto& session = static_cast<Parser&>(parser);
    if (!baseline_.empty()) {
        session.setBaseline(std::move(baseline_));
        baseline_.clear();
    }
    TailResult result{false, stat.length, stat.writeNs, session.setUsage(usage) ? 1 : 0};
    files.insert(files.begin(), {"metadata.json", path_, *metadata});
    std::set<std::string> present;
    for (const auto& e : files) {
        present.insert(e.path);
        const Signature signature{e.stat.length, e.stat.writeNs, e.stat.device, e.stat.inode};
        const auto previous = known_.find(e.path);
        if (previous != known_.end() && previous->second == signature) continue;
        if (e.stat.length > (64LL << 20)) {
            if (e.path == path_) throw std::runtime_error("OpenBitFun metadata exceeds the document limit");
            known_[e.path] = signature;
            continue;
        }
        const int fd = ::open(e.path.c_str(), O_RDONLY | O_NOFOLLOW | O_CLOEXEC | O_NONBLOCK);
        if (fd < 0) throw std::runtime_error("OpenBitFun document unreadable");
        std::string bytes(static_cast<std::size_t>(e.stat.length), '\0');
        std::size_t filled = 0;
        while (filled < bytes.size()) {
            const auto count = ::read(fd, bytes.data() + filled, bytes.size() - filled);
            if (count < 0 && errno == EINTR) continue;
            if (count <= 0) break;
            filled += static_cast<std::size_t>(count);
        }
        ::close(fd);
        if (filled != bytes.size()) throw std::runtime_error("OpenBitFun document incomplete");
        parser.feed(bytes, sink);
        result.bytesRead += static_cast<std::int64_t>(filled);
        known_[e.path] = signature;
    }
    if (result.bytesRead > 0) session.applyRounds(sink);
    offset_ = result.length;
    std::erase_if(known_, [&](const auto& entry) { return !present.contains(entry.first); });
    return result;
}

}  // namespace agentspeed::openbitfun
