#include "agentspeed/task_events.hpp"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <set>

#include "agentspeed/engine.hpp"
#include "agentspeed/rfc3339.hpp"
#include "tasks/internal.hpp"

namespace agentspeed::tasks {

struct Composer::Held {
    std::string text;
    json::Document doc;
    json::Value root() const { return doc.root(); }
};

namespace {

constexpr std::int64_t kCopyMs = 2000;

std::optional<std::int64_t> timestampOf(const json::Value& v) {
    const auto text = v.string();
    return text ? parseTimestamp(*text) : std::nullopt;
}

bool blank(const json::Value& v) {
    const auto text = v.string();
    return !text || detail::strip(*text, " \t\n\r\v\f").empty();
}

// Python truthiness of a JSON value.
bool truthy(const json::Value& v) {
    if (!v.valid()) return false;
    switch (v.type()) {
        case json::Type::Null:
        case json::Type::False:
            return false;
        case json::Type::True:
            return true;
        case json::Type::Number:
            return v.number().value_or(0) != 0;
        case json::Type::String:
            return !v.raw().empty();
        case json::Type::Array:
        case json::Type::Object:
            return v.size() > 0;
    }
    return false;
}

std::string quoted(std::string_view text) {
    std::string out = "\"";
    for (const unsigned char c : text) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buffer[8];
                    std::snprintf(buffer, sizeof buffer, "\\u%04x", c);
                    out += buffer;
                } else {
                    out += static_cast<char>(c);
                }
        }
    }
    return out + "\"";
}

std::shared_ptr<Composer::Held> holdText(std::string text) {
    auto held = std::make_shared<Composer::Held>();
    held->text = std::move(text);
    if (!held->doc.parse(held->text)) {
        held->text = "null";
        held->doc.parse(held->text);
    }
    return held;
}

std::shared_ptr<Composer::Held> hold(const json::Value& v) {
    if (!v.valid()) return holdText("null");
    if (v.isString()) return holdText("\"" + std::string(v.raw()) + "\"");
    return holdText(std::string(v.raw()));
}

bool ompGuard(const json::Value& message) {
    return message["model"].isString() && message["provider"].isString() && message["api"].isString() && message["usage"].isObject();
}

bool openclawMirror(const TranscriptParser& p, const json::Value& message) {
    const auto meta = message["__openclaw"];
    const auto origin = meta.isObject() ? meta["mirrorOrigin"] : json::Value();
    const auto provider = message["provider"].string().value_or("");
    return (origin.isString() && !origin.raw().empty()) || (message["api"].equals("cli") && provider == "claude-cli") ||
           provider.rfind("acp-", 0) == 0 || p.copyFacts().mirrorWindow;
}

bool ownedByOther(const TranscriptParser& p, const std::string& key, const std::string& path) {
    const auto owner = p.dedupeSet().owner(key);
    return owner && *owner != path;
}

}  // namespace

std::string inputDigest(const json::Value& value) {
    if (!value.valid() || value.isNull()) return detail::sha16("");
    if (value.isString()) return detail::sha16(*value.string());
    return detail::sha16(value.raw());
}

Composer::Composer(std::string path, SourceKind source) : path_(std::move(path)), source_(source) {}

Composer::~Composer() = default;

void Composer::reset() {
    cwd_.reset();
    events_.clear();
    models_.clear();
    calls_.clear();
    sessions_.clear();
    pending_.reset();
    headerSeen_ = false;
    stats_ = {};
}

void Composer::onLine(const TranscriptParser& parser, const json::Value& line, std::string_view) {
    std::optional<std::int64_t> t;
    source_ = parser.source();
    switch (source_) {
        case SourceKind::ClaudeCode:
        case SourceKind::ClaudeDesktop:
            t = claude(parser, line);
            break;
        case SourceKind::Codex:
            t = codex(parser, line);
            break;
        case SourceKind::Omp:
        case SourceKind::Pi:
        case SourceKind::OpenClaw:
            t = piFamily(parser, line);
            break;
        case SourceKind::Droid:
            t = droid(parser, line);
            break;
        default:
            return;
    }
    if (!t) return;
    ++stats_.lines;
    track(parser, *t);
}

std::optional<std::int64_t> Composer::claude(const TranscriptParser& p, const json::Value& obj) {
    const auto t = timestampOf(obj["timestamp"]);
    if (!t) return std::nullopt;
    if (!blank(obj["cwd"])) cwd_ = obj["cwd"].string();
    const auto kind = obj["type"];
    if (kind.equals("user") || kind.equals("attachment")) {
        if (const auto found = promptLine(source_, obj)) lineKind(*found, found->t.value_or(*t));
        if (kind.equals("attachment")) {
            if (const auto notice = notification(source_, obj)) {
                const auto queued = timestampOf(obj["attachment"]["timestamp"]);
                const auto code = notice->exitCode;
                finish(notice->session, {code, !code || *code != 0, std::nullopt}, std::nullopt, queued.value_or(*t));
            }
        }
        const auto message = kind.equals("user") && obj["message"].isObject() ? obj["message"] : json::Value();
        const auto content = message["content"];
        if (content.isArray()) {
            for (const auto& block : content.elements()) {
                if (block.isObject() && block["type"].equals("tool_result")) result(block["tool_use_id"], block, *t);
            }
        }
    } else if (kind.equals("assistant")) {
        const auto message = obj["message"].isObject() ? obj["message"] : json::Value();
        if (message["model"].equals("<synthetic>")) return t;
        json::Value key;
        for (const auto& candidate : {message["id"], obj["requestId"], obj["uuid"]}) {
            if (truthy(candidate)) {
                key = candidate;
                break;
            }
        }
        const bool duplicate = key.isString() && !key.raw().empty() && ownedByOther(p, *key.string(), path_);
        assistant(duplicate);
        if (!duplicate) callsIn(message["content"], "tool_use", "input", *t);
    }
    return t;
}

std::optional<std::int64_t> Composer::codex(const TranscriptParser& p, const json::Value& obj) {
    const auto t = timestampOf(obj["timestamp"]);
    if (!t) return std::nullopt;
    const auto kind = obj["type"];
    const auto payload = obj["payload"].isObject() ? obj["payload"] : json::Value();
    if (kind.equals("session_meta")) {
        if (headerSeen_) return std::nullopt;
        headerSeen_ = true;
        if (!blank(payload["cwd"])) cwd_ = payload["cwd"].string();
        return t;
    }
    const auto facts = p.copyFacts();
    if (facts.createdMs && *t < *facts.createdMs - kCopyMs) return std::nullopt;
    const auto lineOrdinal = obj["ordinal"].ordinal();
    if (facts.startOrdinal && lineOrdinal && *lineOrdinal < *facts.startOrdinal) return std::nullopt;
    const auto ptype = payload["type"];
    if (kind.equals("turn_context")) {
        if (!blank(payload["cwd"])) cwd_ = payload["cwd"].string();
    } else if (kind.equals("response_item")) {
        if (const auto found = promptLine(source_, obj)) {
            prompt(found->t.value_or(*t), found->continuation);
        } else if (ptype.equals("function_call") || ptype.equals("custom_tool_call")) {
            const bool custom = ptype.equals("custom_tool_call");
            const auto value = payload[custom ? "input" : "arguments"];
            const auto parsed = custom ? codeScript(source_, payload["name"].string(), value) : std::nullopt;
            if (parsed) {
                script(payload["call_id"], *t, *parsed);
            } else {
                call(payload["call_id"], *t, payload["name"], value);
            }
        } else if (ptype.equals("function_call_output") || ptype.equals("custom_tool_call_output")) {
            result(payload["call_id"], payload, *t);
        }
    }
    return t;
}

std::optional<std::int64_t> Composer::piFamily(const TranscriptParser& p, const json::Value& obj) {
    const auto kind = obj["type"];
    if (kind.equals("session")) {
        if (headerSeen_) return std::nullopt;
        headerSeen_ = true;
        if (!blank(obj["cwd"])) cwd_ = obj["cwd"].string();
        return timestampOf(obj["timestamp"]);
    }
    if (!headerSeen_ || !obj["id"].isString()) return std::nullopt;
    const auto t = timestampOf(obj["timestamp"]);
    const auto created = p.copyFacts().createdMs;
    if (!t || (created && *t < *created - kCopyMs)) return std::nullopt;
    const auto message = obj["message"];
    if (!kind.equals("message") || !message.isObject()) return t;
    const auto role = message["role"];
    if (role.equals("user")) {
        if (const auto found = promptLine(source_, obj)) lineKind(*found, found->t.value_or(*t));
    } else if (role.equals("assistant")) {
        if (!ompGuard(message)) return t;
        std::int64_t stamp = *t;
        if (const auto ts = message["timestamp"].finiteNumber(); ts && *ts > 0) stamp = *message["timestamp"].truncated();
        const auto key = std::string(sourceId(source_)) + ":" + *obj["id"].string() + ":" + std::to_string(stamp);
        const bool duplicate = ownedByOther(p, key, path_);
        assistant(duplicate);
        if (!duplicate && !(source_ == SourceKind::OpenClaw && openclawMirror(p, message))) {
            callsIn(message["content"], "toolCall", "arguments", *t);
        }
    } else if (role.equals("toolResult")) {
        result(message["toolCallId"], message, *t);
    }
    return t;
}

std::optional<std::int64_t> Composer::droid(const TranscriptParser& p, const json::Value& obj) {
    const auto kind = obj["type"];
    if (kind.equals("session_start")) {
        if (headerSeen_) return std::nullopt;
        headerSeen_ = true;
        if (!blank(obj["cwd"])) cwd_ = obj["cwd"].string();
        return timestampOf(obj["timestamp"]);
    }
    const auto t = timestampOf(obj["timestamp"]);
    const auto message = obj["message"];
    if (!t || !kind.equals("message") || !message.isObject()) return t;
    const auto role = message["role"];
    if (role.equals("user")) {
        if (const auto found = promptLine(source_, obj)) lineKind(*found, found->t.value_or(*t));
        const auto content = message["content"];
        if (content.isArray()) {
            for (const auto& block : content.elements()) {
                if (block.isObject() && block["type"].equals("tool_result")) result(block["tool_use_id"], block, *t);
            }
        }
    } else if (role.equals("assistant") && obj["id"].isString() && !obj["id"].raw().empty()) {
        const bool duplicate = ownedByOther(p, "droid:" + *obj["id"].string(), path_);
        assistant(duplicate);
        if (!duplicate) callsIn(message["content"], "tool_use", "input", *t);
    }
    return t;
}

void Composer::lineKind(const PromptLine& found, std::int64_t t) {
    if (found.kind == PromptLine::Kind::Prompt || found.kind == PromptLine::Kind::Command) {
        prompt(t, found.kind == PromptLine::Kind::Prompt && found.continuation);
        pending_ = {found.kind == PromptLine::Kind::Prompt ? "prompt" : "command", events_.back().serial};
    } else {
        if (pending_ && pending_->first == "command") withdraw();
        pending_.reset();
    }
}

void Composer::prompt(std::int64_t t, bool continuation) {
    Event e;
    e.type = Event::Type::Prompt;
    e.serial = nextSerial_++;
    e.t = t;
    e.continuation = continuation;
    events_.push_back(std::move(e));
    ++stats_.prompts;
}

void Composer::assistant(bool duplicate) {
    if (pending_ && duplicate) withdraw();
    pending_.reset();
}

void Composer::withdraw() {
    const auto serial = pending_->second;
    const auto it = std::find_if(events_.rbegin(), events_.rend(), [&](const Event& e) { return e.serial == serial; });
    if (it != events_.rend()) events_.erase(std::next(it).base());
    ++stats_.withdrawn;
    --stats_.prompts;
}

Event* Composer::find(std::uint64_t serial) {
    const auto it = std::lower_bound(events_.begin(), events_.end(), serial, [](const Event& e, std::uint64_t s) { return e.serial < s; });
    return it != events_.end() && it->serial == serial ? &*it : nullptr;
}

void Composer::callsIn(const json::Value& content, std::string_view blockType, std::string_view field, std::int64_t t) {
    if (!content.isArray()) return;
    for (const auto& block : content.elements()) {
        if (!block.isObject() || !block["type"].equals(blockType)) continue;
        call(block["id"], t, block["name"], block[field]);
    }
}

void Composer::call(const json::Value& callId, std::int64_t t, const json::Value& tool, const json::Value& value) {
    if (!callId.isString() || !tool.isString()) return;
    const auto id = *callId.string();
    if (calls_.contains(id)) return;
    calls_.emplace(id, action(t, *tool.string(), hold(value), inputDigest(value)));
}

void Composer::script(const json::Value& callId, std::int64_t t, const CodeScript& parsed) {
    if (!callId.isString()) return;
    const auto id = *callId.string();
    if (calls_.contains(id)) return;
    Script s;
    for (const auto& c : parsed.calls) s.subs.push_back(action(t, c.tool, holdText(c.input), c.digest));
    s.prints = parsed.prints;
    calls_.emplace(id, std::move(s));
    ++stats_.scripts;
}

Composer::Call Composer::action(std::int64_t t, const std::string& tool, std::shared_ptr<Held> value, const std::string& digest) {
    const auto input = value->root();
    Event e;
    e.type = Event::Type::Action;
    e.serial = nextSerial_++;
    e.t = t;
    e.kind = actionKind(source_, tool, input, cwd_);
    e.tool = tool;
    e.digest = digest;
    std::optional<CommandInfo> info;
    if (e.kind == "command") {
        if (const auto text = commandText(source_, tool, input)) {
            info = commandInfo(*text, cwd_);
            e.command = info;
        }
    } else if (e.kind == "edit") {
        e.hunks = editHunks(source_, tool, input, cwd_);
    }
    Call c;
    c.serial = e.serial;
    c.tool = tool;
    c.cwd = cwd_;
    c.info = info;
    c.continues = continuation(source_, tool, input);
    c.value = std::move(value);
    events_.push_back(std::move(e));
    ++stats_.calls;
    return c;
}

void Composer::result(const json::Value& callId, const json::Value& result, std::int64_t t) {
    const auto it = callId.isString() ? calls_.find(*callId.string()) : calls_.end();
    if (it != calls_.end() && std::holds_alternative<Script>(it->second)) {
        auto s = std::get<Script>(std::move(it->second));
        calls_.erase(it);
        const auto items = codeItems(source_, result);
        for (auto& sub : s.subs) sub.answered = true;
        if (items && !s.prints.empty() && items->size() == s.prints.size()) {
            ++stats_.scriptsMapped;
            std::set<int> done;
            for (std::size_t k = 0; k < s.prints.size(); ++k) {
                const int index = s.prints[k].first;
                if (!done.insert(index).second || index < 0 || static_cast<std::size_t>(index) >= s.subs.size()) continue;
                const auto item = holdText(quoted((*items)[k].value_or("")));
                answer(s.subs[static_cast<std::size_t>(index)], item->root(), t);
            }
        }
        return;
    }
    if (it == calls_.end() || std::get<Call>(it->second).answered) {
        ++stats_.orphanResults;
        return;
    }
    answer(std::get<Call>(it->second), result, t);
}

void Composer::answer(Call& c, const json::Value& result, std::int64_t t) {
    c.answered = true;
    ++stats_.results;
    const auto input = c.value ? c.value->root() : json::Value();
    const auto out = outcome(source_, result, t, c.tool, input, c.cwd);
    if (auto* e = find(c.serial)) e->outcome = out;
    const auto text = resultText(source_, result);
    const auto session = c.continues ? std::nullopt : runningSession(source_, result);
    if (session) {
        Running running;
        running.serial = c.serial;
        running.info = c.info;
        running.cwd = c.cwd;
        running.masked = shellCallState(source_, c.tool, input, c.cwd).second;
        running.texts.push_back(text);
        sessions_[*session] = std::move(running);
    } else if (c.info) {
        if (auto* e = find(c.serial)) e->failures = failures(c.info, text, c.cwd, out.failed);
    }
    if (c.continues && sessions_.contains(*c.continues)) {
        ++stats_.continuationResults;
        finish(*c.continues, out, text, t);
    }
    c.value.reset();
}

void Composer::finish(const std::string& key, const Outcome& out, const std::optional<std::string>& text, std::int64_t t) {
    const auto it = sessions_.find(key);
    if (it == sessions_.end()) return;
    auto& running = it->second;
    if (text) running.texts.push_back(*text);
    if (!out.exitCode || !out.failed) return;
    const Outcome final{out.exitCode, running.masked ? std::nullopt : out.failed, t};
    if (auto* e = find(running.serial)) {
        e->outcome = final;
        if (running.info) {
            std::string joined;
            const std::string sep = source_ == SourceKind::Codex ? "" : "\n";
            for (std::size_t i = 0; i < running.texts.size(); ++i) joined += (i ? sep : "") + running.texts[i];
            e->failures = failures(running.info, joined, running.cwd, final.failed);
        }
    }
    sessions_.erase(it);
    ++stats_.stitched;
}

void Composer::track(const TranscriptParser& p, std::int64_t t) {
    const auto effort = p.modes().attribute(ModeKind::Effort);
    ModelSpan entry{t, p.model(), effort.known() ? std::optional<std::string>(effort.mode) : std::nullopt};
    if (!models_.empty()) {
        const auto& last = models_.back();
        if (last.model == entry.model && last.effort == entry.effort && t >= last.t) return;
    }
    models_.push_back(std::move(entry));
}

Phase effectivePhase(Phase phase, std::optional<std::int64_t> lastLineMs, std::int64_t now) {
    constexpr std::int64_t kStaleMs = 300'000;
    return phase != Phase::Idle && lastLineMs && now - *lastLineMs > kStaleMs ? Phase::Idle : phase;
}

AgentRow agentRow(const ScoredAgent& agent, std::int64_t now, const Models& models) {
    static const std::vector<Event> kNone;
    const auto& events = agent.events ? *agent.events : kNone;
    AgentRow row;
    row.agent = agent;
    row.value = agentValue(events, agent.phase, now);
    if (const auto task = currentTask(events, now)) {
        row.task = evaluateTask(task->events, now);
        row.taskStartMs = task->startMs;
    }
    row.predictions = predictions(events, now, models);
    row.models = models;
    return row;
}

SetValues setValues(const std::vector<AgentRow>& rows, std::int64_t now,
                    const std::optional<std::vector<std::optional<std::vector<SourceKind>>>>& sets) {
    constexpr std::int64_t kListedMs = 600'000;
    std::map<std::string, const AgentRow*> byId;
    for (const auto& r : rows) byId[r.agent.id] = &r;
    std::set<std::string> chosen;
    for (const auto& r : rows) {
        const auto& a = r.agent;
        if (a.phase == Phase::Generating || (a.lastLineMs && now - *a.lastLineMs <= kListedMs)) chosen.insert(a.id);
    }
    for (const auto& id : std::vector<std::string>(chosen.begin(), chosen.end())) {
        std::set<std::string> seen{id};
        auto current = byId[id]->agent.parentId;
        while (current && byId.contains(*current) && !seen.contains(*current)) {
            chosen.insert(*current);
            seen.insert(*current);
            current = byId[*current]->agent.parentId;
        }
    }
    struct Shown {
        const AgentRow* row;
        std::optional<std::string> parent;
    };
    std::vector<Shown> shown;
    for (const auto& r : rows) {
        if (!chosen.contains(r.agent.id)) continue;
        const auto& parent = r.agent.parentId;
        shown.push_back({&r, parent && chosen.contains(*parent) ? parent : std::nullopt});
    }
    std::stable_sort(shown.begin(), shown.end(), [](const Shown& a, const Shown& b) { return ordinalLess(a.row->agent.id, b.row->agent.id); });
    const auto valued = [](const std::vector<const AgentRow*>& members) {
        std::vector<ValuedAgent> out;
        for (const auto* r : members) out.push_back({r->agent.id, r->agent.lastLineMs.value_or(0), r->value});
        return out;
    };
    SetValues out;
    for (const auto& s : shown) out.listed.push_back(s.row->agent.id);
    std::vector<std::optional<std::vector<SourceKind>>> chosenSets;
    if (sets) {
        chosenSets = *sets;
    } else {
        chosenSets.push_back(std::nullopt);
        for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
            if (isScoredSource(static_cast<SourceKind>(k))) chosenSets.push_back(std::vector<SourceKind>{static_cast<SourceKind>(k)});
        }
    }
    for (const auto& sources : chosenSets) {
        std::vector<const AgentRow*> members;
        for (const auto& s : shown) {
            if (!sources || std::find(sources->begin(), sources->end(), s.row->agent.source) != sources->end()) members.push_back(s.row);
        }
        if (sources && members.empty() && !sets) continue;
        out.sets.push_back({sources, highest(valued(members))});
    }
    std::vector<engine::Agent> tree;
    for (const auto& s : shown) {
        engine::Agent a;
        a.id = s.row->agent.id;
        a.parentId = s.parent;
        tree.push_back(a);
    }
    for (auto& [root, members] : engine::boardGroups(tree)) {
        std::vector<const AgentRow*> part;
        for (const auto& m : members) part.push_back(byId[m]);
        out.groups.push_back({root, members, highest(valued(part))});
    }
    return out;
}

}  // namespace agentspeed::tasks
