#include "agentspeed/gemini_parser.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <fstream>
#include <mutex>
#include <unordered_map>

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed {

namespace gemini {

namespace {

std::vector<std::string> components(const std::string& path) {
    std::vector<std::string> parts;
    std::string current;
    for (char c : path) {
        if (c == '/' || c == '\\') {
            if (!current.empty()) parts.push_back(std::move(current));
            current.clear();
        } else {
            current += c;
        }
    }
    if (!current.empty()) parts.push_back(std::move(current));
    return parts;
}

// A regular file (never a symlink) of at most 1 MiB.
std::optional<std::string> readSmall(const std::string& path) {
    struct stat st {};
    if (lstat(path.c_str(), &st) != 0 || !S_ISREG(st.st_mode) || st.st_size > (1 << 20)) return std::nullopt;
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::nullopt;
    std::string content((std::istreambuf_iterator<char>(in)), std::istreambuf_iterator<char>());
    if (content.size() > (1u << 20)) return std::nullopt;
    return content;
}

std::optional<std::string> basenameOrNull(const std::string& path) {
    auto name = path_text::basename(path);
    return name.empty() ? std::nullopt : std::optional<std::string>(name);
}

std::string parentOf(const std::string& path) {
    const auto cut = path.find_last_of("/\\");
    return cut == std::string::npos ? std::string() : path.substr(0, cut);
}

std::optional<std::string> registeredRoot(const std::string& text, const std::string& slug) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) return std::nullopt;
    const auto projects = doc.root()["projects"];
    if (!projects.isObject()) return std::nullopt;
    // Members in first-occurrence order with the last value of a repeated root.
    std::vector<std::pair<std::string, std::optional<std::string>>> entries;
    for (const auto& [root, value] : projects.members()) {
        std::optional<std::string> s = value.isString() ? value.string() : std::nullopt;
        auto known = std::find_if(entries.begin(), entries.end(), [&](const auto& e) { return e.first == root; });
        if (known != entries.end()) {
            known->second = s;
        } else {
            entries.emplace_back(root, s);
        }
    }
    for (const auto& [root, value] : entries) {
        if (value == slug) return root;
    }
    return std::nullopt;
}

}  // namespace

std::string sanitize(std::string_view id) {
    std::string out;
    for (std::size_t i = 0; i < id.size();) {
        const char c = id[i];
        if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-') {
            out += c;
            ++i;
            continue;
        }
        text::nextCodePoint(id, i);
        out += '_';
    }
    return out;
}

bool isMainSessionPath(const std::string& path) {
    const auto parts = components(path);
    return parts.size() >= 2 && parts[parts.size() - 2] == "chats";
}

std::optional<std::string> parentDirectoryOf(const std::string& path) {
    const auto parts = components(path);
    if (parts.size() >= 3 && parts[parts.size() - 3] == "chats") return parts[parts.size() - 2];
    return std::nullopt;
}

std::optional<std::string> projectDirectoryOf(const std::string& path) {
    const auto file = path.find_last_of("/\\");
    if (file == std::string::npos) return std::nullopt;
    const std::string parent = path.substr(0, file);
    auto cut = parent.find_last_of("/\\");
    if (cut == std::string::npos) return std::nullopt;
    if (parent.substr(cut + 1) != "chats") {
        const std::string grandparent = parent.substr(0, cut);
        cut = grandparent.find_last_of("/\\");
        if (cut == std::string::npos || grandparent.substr(cut + 1) != "chats") return std::nullopt;
    }
    if (cut == 0) return std::nullopt;
    return path.substr(0, cut);
}

std::optional<std::string> projectOf(const std::string& sessionPath) {
    const auto directory = projectDirectoryOf(sessionPath);
    if (!directory) return std::nullopt;
    static std::mutex mutex;
    static std::unordered_map<std::string, std::optional<std::string>> cache;
    {
        std::lock_guard lock(mutex);
        if (auto it = cache.find(*directory); it != cache.end()) return it->second;
    }
    std::optional<std::string> project;
    if (auto root = readSmall(*directory + "/.project_root")) {
        project = basenameOrNull(text::strip(*root));
    } else {
        const std::string registryFolder = parentOf(parentOf(*directory));
        if (!registryFolder.empty()) {
            if (auto registry = readSmall(registryFolder + "/projects.json")) {
                if (auto registered = registeredRoot(*registry, path_text::basename(*directory))) {
                    project = basenameOrNull(*registered);
                }
            }
        }
    }
    std::lock_guard lock(mutex);
    if (cache.size() >= 4096) cache.clear();
    cache[*directory] = project;
    return project;
}

}  // namespace gemini

namespace {

using Message = GeminiParser::Message;
using MessageKind = GeminiParser::MessageKind;
using ToolCall = GeminiParser::ToolCall;

std::optional<std::int64_t> timestamp(const json::Value& v) {
    if (!v.isString()) return std::nullopt;
    const auto s = v.string();
    return s ? parseTimestamp(*s) : std::nullopt;
}

std::optional<std::string> anyString(const json::Value& v) { return v.isString() ? v.string() : std::nullopt; }

std::optional<std::int64_t> maxOf(std::optional<std::int64_t> a, std::optional<std::int64_t> b) {
    if (a && b) return std::max(*a, *b);
    return a ? a : b;
}

std::optional<std::int64_t> minOf(std::optional<std::int64_t> a, std::optional<std::int64_t> b) {
    if (a && b) return std::min(*a, *b);
    return a ? a : b;
}

TokenCounts readTokens(const json::Value& t) {
    const auto input = t["input"].count(), output = t["output"].count(), cached = t["cached"].count();
    const auto thoughts = t["thoughts"].count(), tool = t["tool"].count();
    TokenCounts c;
    c.input = TokenCounts::saturatingAdd(input, tool);
    c.cachedInput = cached;
    c.output = TokenCounts::saturatingAdd(output, thoughts);
    c.reasoning = thoughts;
    return c;
}

struct Fields {
    std::optional<std::string> id;
    enum class Type { Other, User, Gemini, Error, Info } type = Type::Other;
    std::optional<std::int64_t> timestampMs;
    bool hasTokens = false;
    TokenCounts tokens;
    std::optional<std::string> model;
    bool cancelledText = false, hasVisibleText = false, hasFunctionCall = false;
    std::optional<std::vector<ToolCall>> toolCalls;
    std::optional<std::int64_t> thoughtsMs;

    Message toMessage() const {
        std::optional<std::int64_t> latest, first;
        if (toolCalls) {
            for (const auto& c : *toolCalls) {
                latest = maxOf(latest, c.timestampMs);
                first = minOf(first, c.timestampMs);
            }
        }
        Message m;
        m.id = id.value_or("");
        switch (type) {
            case Type::User: m.kind = MessageKind::User; break;
            case Type::Gemini: m.kind = MessageKind::Gemini; break;
            case Type::Error: m.kind = MessageKind::Error; break;
            case Type::Info: m.kind = cancelledText ? MessageKind::Cancelled : MessageKind::Other; break;
            case Type::Other: m.kind = MessageKind::Other; break;
        }
        m.timestampMs = timestampMs;
        m.detailMs = maxOf(latest, thoughtsMs);
        m.firstCallMs = first;
        m.hasTokens = hasTokens;
        m.tokens = tokens;
        m.model = model;
        m.hasVisibleText = hasVisibleText;
        m.hasFunctionCall = hasFunctionCall;
        m.toolCalls = toolCalls;
        return m;
    }
};

void readPart(const json::Value& part, Fields& f) {
    bool visible = false, thought = false, call = false;
    if (const auto fc = part["functionCall"]; fc.valid()) call = fc.isObject();
    if (const auto text = part["text"]; text.isString()) visible = text::hasVisibleText(*text.string());
    thought = part["thought"].isTrue();
    f.hasFunctionCall |= call;
    f.hasVisibleText |= visible && !thought;
}

void readContent(const json::Value& content, Fields& f) {
    f.hasVisibleText = f.hasFunctionCall = f.cancelledText = false;
    if (content.isString()) {
        const auto s = content.string();
        f.cancelledText = s == "Request cancelled.";
        f.hasVisibleText = s && text::hasVisibleText(*s);
    } else if (content.isObject()) {
        readPart(content, f);
    } else if (content.isArray()) {
        for (const auto& item : content.elements()) {
            if (item.isString()) {
                f.hasVisibleText |= text::hasVisibleText(*item.string());
            } else if (item.isObject()) {
                readPart(item, f);
            }
        }
    }
}

std::optional<std::vector<ToolCall>> readToolCalls(const json::Value& v) {
    if (!v.isArray()) return std::nullopt;
    std::vector<ToolCall> calls;
    for (const auto& item : v.elements()) {
        if (!item.isObject()) continue;
        auto id = anyString(item["id"]);
        if (!id) continue;
        const bool completeTask = item["name"].equals("complete_task");
        const auto status = item["status"];
        const bool success = status.equals("success");
        ToolCall call;
        call.id = *id;
        call.timestampMs = timestamp(item["timestamp"]);
        call.completesTask = completeTask && success;
        call.cancelled = status.equals("cancelled");
        calls.push_back(std::move(call));
    }
    if (calls.empty()) return std::nullopt;
    return calls;
}

Fields readFields(const json::Value& o, bool full) {
    Fields f;
    f.id = anyString(o["id"]);
    if (const auto type = o["type"]; type.isString()) {
        f.type = type.equals("gemini")  ? Fields::Type::Gemini
                 : type.equals("user")  ? Fields::Type::User
                 : type.equals("info")  ? Fields::Type::Info
                 : type.equals("error") ? Fields::Type::Error
                                        : Fields::Type::Other;
    }
    f.timestampMs = timestamp(o["timestamp"]);
    if (const auto tokens = o["tokens"]; tokens.isObject()) {
        f.hasTokens = true;
        f.tokens = readTokens(tokens);
    }
    f.model = o["model"].nonEmptyString();
    if (full) {
        if (const auto content = o["content"]; content.valid()) readContent(content, f);
        f.toolCalls = readToolCalls(o["toolCalls"]);
        if (const auto thoughts = o["thoughts"]; thoughts.isArray()) {
            for (const auto& t : thoughts.elements()) {
                if (t.isObject()) f.thoughtsMs = maxOf(f.thoughtsMs, timestamp(t["timestamp"]));
            }
        }
    }
    return f;
}

std::vector<Message> readMessages(const json::Value& v) {
    std::vector<Message> out;
    if (!v.isArray()) return out;
    for (const auto& item : v.elements()) {
        if (!item.isObject()) continue;
        Fields f = readFields(item, true);
        if (f.id) out.push_back(f.toMessage());
    }
    return out;
}

}  // namespace

GeminiParser::GeminiParser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Gemini) {
    const std::string& id = this->agentId();
    auto lowerEnds = [&](std::string_view suffix) {
        if (id.size() < suffix.size()) return false;
        for (std::size_t i = 0; i < suffix.size(); ++i) {
            char c = id[id.size() - suffix.size() + i];
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
            if (c != suffix[i]) return false;
        }
        return true;
    };
    isDocument_ = lowerEnds(".json");
    isMainSession_ = gemini::isMainSessionPath(id);
    parentDirectory_ = gemini::parentDirectoryOf(id);
    pathIsSubagent_ = parentDirectory_.has_value();
    pathProject_ = gemini::projectOf(id);
    restorePathState();
}

void GeminiParser::restorePathState() {
    setSource(SourceKind::Gemini);
    setProject(pathProject_);
    setSubagent(pathIsSubagent_);
}

void GeminiParser::reset() {
    TranscriptParser::reset();
    responses_.clear();
    seenToolCalls_.clear();
    pendingStartMs_.reset();
    marker_.reset();
    sessionId_.reset();
    startTimeMs_.reset();
    restorePathState();
}

// Main sessions are keyed by their chats folder plus sanitized session id; sub-agents name <chats>/<dir>.
AgentLink GeminiParser::link() const {
    const std::string& path = agentId();
    auto folderOf = [&](int levels) -> std::optional<std::string> {
        std::size_t end = path.size();
        for (int i = 0; i < levels; ++i) {
            const auto cut = path.find_last_of("/\\", end == 0 ? 0 : end - 1);
            if (cut == std::string::npos || end == 0) return std::nullopt;
            end = cut;
        }
        return path.substr(0, end);
    };
    std::optional<std::string> thread, parent;
    if (isMainSession_ && sessionId_ && !sessionId_->empty()) {
        if (auto chats = folderOf(1)) thread = *chats + "\n" + gemini::sanitize(*sessionId_);
    }
    if (parentDirectory_ && !parentDirectory_->empty()) {
        if (auto chats = folderOf(2)) parent = *chats + "\n" + *parentDirectory_;
    }
    return {path, LinkKind::GeminiSession, thread, parent, startTimeMs_.value_or(0), isDocument_ ? 1 : 0};
}

void GeminiParser::feed(std::string_view text, ResponseSink& sink) {
    if (!isDocument_ && json::isBlank(text)) return;
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) {
        // A legacy document caught mid-write keeps its previous state.
        countMalformed();
        return;
    }
    const auto root = doc.root();
    const auto sessionId = anyString(root["sessionId"]);
    auto header = [&](const std::string& id) {
        Header h;
        h.sessionId = id;
        h.subagentKind = root["kind"].equals("subagent");
        h.startMs = timestamp(root["startTime"]);
        h.lastUpdatedMs = timestamp(root["lastUpdated"]);
        h.messages = readMessages(root["messages"]);
        return h;
    };
    if (isDocument_) {
        if (!sessionId) {
            countMalformed();
            return;
        }
        reset();
        applyHeader(header(*sessionId), sink);
        return;
    }

    // Classification order of Gemini's own loader.
    if (root["$rewindTo"].isString()) {
        idle(lastLineMs().value_or(0));
        return;
    }
    if (const auto patch = root["$patch"]; patch.isObject()) {
        if (phase() == Phase::Generating && marker_) {
            if (const auto removed = patch["removeIds"]; removed.isArray()) {
                for (const auto& item : removed.elements()) {
                    if (item.isString() && item.string() == marker_) {
                        idle(lastLineMs().value_or(0));
                        break;
                    }
                }
            }
        }
        return;
    }
    Fields fields = readFields(root, true);
    if (fields.id) {
        applyMessage(fields.toMessage(), sink);
        return;
    }
    if (const auto set = root["$set"]; set.isObject()) {
        if (auto updated = timestamp(set["lastUpdated"])) touch(*updated);
        if (set["kind"].equals("subagent")) setSubagent(true);
        if (auto id = anyString(set["sessionId"])) setSessionId(*id);
        if (const auto messages = set["messages"]; messages.isArray()) {
            std::vector<std::string> ids;
            std::vector<CheckpointResponse> responses;
            for (const auto& item : messages.elements()) {
                if (!item.isObject()) continue;
                Fields f = readFields(item, false);
                if (!f.id) continue;
                ids.push_back(*f.id);
                if (f.type == Fields::Type::Gemini && f.hasTokens && f.timestampMs) {
                    responses.push_back({*f.id, *f.timestampMs, f.tokens, f.model});
                }
            }
            applyCheckpoint(ids, responses, sink);
        }
        // A sub-agent writes its summary when it finished.
        if (const auto summary = set["summary"]; summary.isString() && !summary.raw().empty()) idle(lastLineMs().value_or(0));
        return;
    }
    if (sessionId && root["projectHash"].isString()) applyHeader(header(*sessionId), sink);
}

void GeminiParser::applyHeader(const Header& h, ResponseSink& sink) {
    setSessionId(h.sessionId);
    if (h.subagentKind) setSubagent(true);
    if (h.startMs) {
        if (!startTimeMs_) {
            startTimeMs_ = h.startMs;
            markChanged();
        }
        touch(*h.startMs);
    }
    if (h.lastUpdatedMs) touch(*h.lastUpdatedMs);
    for (const auto& m : h.messages) applyMessage(m, sink);
}

void GeminiParser::applyCheckpoint(const std::vector<std::string>& ids, const std::vector<CheckpointResponse>& responses,
                                   ResponseSink& sink) {
    for (const auto& r : responses) {
        const Ownership ownership = claim(r.id);
        if (ownership == Ownership::OtherFile) continue;
        if (auto it = responses_.find(r.id); it != responses_.end()) {
            update(it->second, r.timestampMs, r.tokens, r.model, sink);
        } else {
            emitResponse(r.id, std::nullopt, r.timestampMs, r.tokens, r.model, ownership == Ownership::New, sink);
        }
    }
    if (phase() == Phase::Generating && (!marker_ || std::find(ids.begin(), ids.end(), *marker_) == ids.end())) {
        idle(lastLineMs().value_or(0));
    }
}

void GeminiParser::applyMessage(const Message& m, ResponseSink& sink) {
    if (!m.timestampMs) return;
    const std::int64_t ts = *m.timestampMs;
    touch(ts);
    if (m.detailMs) touch(*m.detailMs);
    switch (m.kind) {
        case MessageKind::User:
            generating(ts, m.id);
            return;
        case MessageKind::Error:
        case MessageKind::Cancelled:
            idle(ts);
            return;
        case MessageKind::Gemini:
            break;
        case MessageKind::Other:
            return;
    }
    const std::string& key = m.id;
    if (!m.hasTokens) {
        if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    } else {
        const Ownership ownership = claim(key);
        if (ownership == Ownership::OtherFile) return;
        if (auto it = responses_.find(key); it != responses_.end()) {
            update(it->second, ts, m.tokens, m.model, sink);
        } else {
            // Created when its tools finished (<= v0.31): the stream end is unknown.
            const auto start = m.firstCallMs && ts >= *m.firstCallMs ? std::nullopt : pendingStartMs_;
            emitResponse(key, start, ts, m.tokens, m.model, ownership == Ownership::New, sink);
            pendingStartMs_.reset();
            marker_.reset();
            setPhase(m.hasFunctionCall || !m.hasVisibleText ? Phase::Tools : Phase::Idle, ts, std::nullopt);
        }
    }
    applyToolCalls(m, ts);
}

void GeminiParser::applyToolCalls(const Message& m, std::int64_t ts) {
    if (!m.toolCalls) return;
    bool anyNew = false, completesTask = false, allCancelled = true;
    std::optional<std::int64_t> latest;
    for (const auto& call : *m.toolCalls) {
        if (seenToolCalls_.contains(call.id)) continue;
        anyNew = true;
        completesTask |= call.completesTask;
        allCancelled &= call.cancelled;
        if (call.timestampMs && (!latest || *call.timestampMs > *latest)) latest = call.timestampMs;
    }
    if (!anyNew) return;
    for (const auto& call : *m.toolCalls) seenToolCalls_.insert(call.id);
    if (completesTask || allCancelled) {
        idle(ts);
    } else {
        generating(latest.value_or(ts), m.id);
    }
}

void GeminiParser::emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs,
                                const TokenCounts& tokens, const std::optional<std::string>& model, bool completion,
                                ResponseSink& sink) {
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Gemini;
    r.agentId = agentId();
    r.model = model;
    r.startMs = startMs;
    r.endMs = endMs;
    r.tokens = tokens;
    responses_[key] = r;
    setModel(model);
    sink.onResponse(r, completion);
}

void GeminiParser::update(const ParsedResponse& existing, std::int64_t endMs, const TokenCounts& tokens,
                          const std::optional<std::string>& model, ResponseSink& sink) {
    ParsedResponse updated = existing;
    updated.tokens = tokens;
    updated.endMs = std::max(existing.endMs, endMs);
    if (model) updated.model = model;
    setModel(updated.model);
    if (updated.tokens == existing.tokens && updated.endMs == existing.endMs && updated.model == existing.model) return;
    responses_[existing.key] = updated;
    sink.onResponse(updated, false);
}

GeminiParser::Ownership GeminiParser::claim(const std::string& key) {
    std::string owner;
    if (deduper().tryClaim(key, agentId(), &owner)) return Ownership::New;
    return owner == agentId() ? Ownership::ThisFile : Ownership::OtherFile;
}

void GeminiParser::generating(std::int64_t startMs, const std::string& marker) {
    pendingStartMs_ = startMs;
    marker_ = marker;
    setPhase(Phase::Generating, startMs, startMs);
}

void GeminiParser::idle(std::int64_t timestampMs) {
    pendingStartMs_.reset();
    marker_.reset();
    setPhase(Phase::Idle, timestampMs, std::nullopt);
}

void GeminiParser::setSessionId(const std::string& id) {
    if (sessionId_ != id) {
        sessionId_ = id;
        markChanged();
    }
}

}  // namespace agentspeed
