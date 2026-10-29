#include "agentspeed/droid.hpp"

#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <algorithm>
#include <fstream>
#include <numeric>

#include "agentspeed/json.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/rfc3339.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::droid {

namespace {

constexpr std::size_t kMaxSidecarBytes = 1'048'576;

std::optional<std::string> token(const json::Value& v) { return v.isString() ? v.string() : std::nullopt; }

std::optional<std::string> trimmedNonBlank(const std::optional<std::string>& v) {
    if (!v) return std::nullopt;
    auto t = text::strip(*v);
    return t.empty() ? std::nullopt : std::optional<std::string>(t);
}

bool validUtf8(std::string_view s) {
    for (std::size_t i = 0; i < s.size();) {
        const auto b = static_cast<unsigned char>(s[i]);
        std::size_t n = b < 0x80 ? 1 : (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 0;
        if (n == 0 || i + n > s.size()) return false;
        std::uint32_t cp = n == 1 ? b : n == 2 ? (b & 0x1F) : n == 3 ? (b & 0x0F) : (b & 0x07);
        for (std::size_t k = 1; k < n; ++k) {
            const auto c = static_cast<unsigned char>(s[i + k]);
            if ((c & 0xC0) != 0x80) return false;
            cp = (cp << 6) | (c & 0x3F);
        }
        if ((n == 2 && cp < 0x80) || (n == 3 && cp < 0x800) || (n == 4 && (cp < 0x10000 || cp > 0x10FFFF)) ||
            (cp >= 0xD800 && cp <= 0xDFFF)) {
            return false;
        }
        i += n;
    }
    return true;
}

std::string directoryOf(const std::string& path) {
    const auto cut = path.find_last_of('/');
    return cut == std::string::npos ? std::string() : path.substr(0, cut);
}

}  // namespace

enum class LineKind { Other, Message, SessionStart, Compaction, TurnOutcome };
enum class Role { Other, User, Assistant };

struct Line {
    enum class Status { Blank, Valid, Malformed } status = Status::Valid;
    LineKind kind = LineKind::Other;
    std::optional<std::string> id, cwd, parent, callingSessionId, modelId, effort;
    std::optional<std::int64_t> timestampMs;
    Role role = Role::Other;
    bool userOnly = false, hasToolUse = false;
    std::int64_t offset = 0, length = 0;
};

namespace {

Line readLine(std::string_view raw, std::int64_t offset, std::int64_t length) {
    Line line;
    line.offset = offset;
    line.length = length;
    if (json::isBlank(raw)) {
        line.status = Line::Status::Blank;
        return line;
    }
    // Like json.loads on the line's bytes: invalid UTF-8 anywhere makes it malformed; a leading BOM is accepted.
    if (!validUtf8(raw)) {
        line.status = Line::Status::Malformed;
        return line;
    }
    if (raw.substr(0, 3) == "\xEF\xBB\xBF") raw.remove_prefix(3);
    json::Document doc;
    if (!doc.parse(raw) || !doc.root().isObject()) {
        line.status = Line::Status::Malformed;
        return line;
    }
    const auto root = doc.root();
    const auto type = root["type"];
    LineKind kind = type.equals("message")            ? LineKind::Message
                    : type.equals("session_start")    ? LineKind::SessionStart
                    : type.equals("compaction_state") ? LineKind::Compaction
                    : type.equals("agent_turn_outcome") ? LineKind::TurnOutcome
                                                        : LineKind::Other;
    line.id = root["id"].nonEmptyString();
    if (const auto ts = root["timestamp"]; ts.isString()) line.timestampMs = parseTimestamp(*ts.string());
    line.cwd = token(root["cwd"]);
    if (auto parent = token(root["parent"]); parent && isSessionId(*parent)) line.parent = parent;
    if (auto calling = token(root["callingSessionId"]); calling && isSessionId(*calling)) line.callingSessionId = calling;
    const auto message = root["message"];
    const bool hasMessage = message.isObject();
    if (hasMessage) {
        const auto role = message["role"];
        line.role = role.equals("user") ? Role::User : role.equals("assistant") ? Role::Assistant : Role::Other;
        line.userOnly = message["visibility"].equals("user_only");
        line.modelId = trimmedNonBlank(token(message["modelId"]));
        line.effort = token(message["reasoningEffort"]);
        if (const auto content = message["content"]; content.isArray()) {
            for (const auto& block : content.elements()) {
                if (block.isObject() && block["type"].equals("tool_use")) line.hasToolUse = true;
            }
        }
    }
    line.kind = kind == LineKind::Message && !hasMessage ? LineKind::Other : kind;
    return line;
}

}  // namespace

Counters Counters::minus(const Counters& a, const Counters& b) {
    Counters c;
    for (std::size_t i = 0; i < 5; ++i) c.f[i] = std::max<std::int64_t>(0, a.f[i] - b.f[i]);
    return c;
}

Counters Counters::max(const Counters& a, const Counters& b) {
    Counters c;
    for (std::size_t i = 0; i < 5; ++i) c.f[i] = std::max(a.f[i], b.f[i]);
    return c;
}

Counters Counters::add(const Counters& a, const Counters& b) {
    Counters c;
    for (std::size_t i = 0; i < 5; ++i) c.f[i] = a.f[i] + b.f[i];
    return c;
}

TokenCounts Counters::tokens() const {
    TokenCounts t;
    t.input = std::min(TokenCounts::kMax, f[0] + f[1] + f[2]);
    t.cachedInput = f[2];
    t.cacheWriteInput = f[1];
    t.output = f[3];
    t.reasoning = f[4];
    return t;
}

__extension__ using Wide = __int128;

std::vector<std::int64_t> shares(std::int64_t total, const std::vector<std::int64_t>& weights) {
    std::vector<std::int64_t> out(weights.size(), 0);
    if (weights.empty()) return out;
    const std::int64_t whole = std::accumulate(weights.begin(), weights.end(), std::int64_t{0});
    if (whole <= 0) return out;
    std::vector<std::int64_t> remainders(weights.size());
    std::int64_t given = 0;
    for (std::size_t i = 0; i < weights.size(); ++i) {
        const Wide product = static_cast<Wide>(total) * weights[i];
        out[i] = static_cast<std::int64_t>(product / whole);
        remainders[i] = static_cast<std::int64_t>(product % whole);
        given += out[i];
    }
    std::int64_t left = total - given;
    if (left <= 0) return out;
    std::vector<std::size_t> order(weights.size());
    std::iota(order.begin(), order.end(), 0);
    std::stable_sort(order.begin(), order.end(), [&](std::size_t a, std::size_t b) {
        return remainders[a] != remainders[b] ? remainders[a] > remainders[b] : a < b;
    });
    for (std::size_t i = 0; i < order.size() && left > 0; ++i, --left) ++out[order[i]];
    return out;
}

SidecarRead parseSidecar(std::string_view data, const std::optional<SidecarSignature>& signature) {
    SidecarRead unreadable{SidecarStatus::Unreadable, signature, std::nullopt, std::nullopt};
    if (data.size() > kMaxSidecarBytes || !validUtf8(data)) return unreadable;
    if (data.substr(0, 3) == "\xEF\xBB\xBF") {
        data.remove_prefix(3);
        if (data.substr(0, 3) == "\xEF\xBB\xBF") return unreadable;
    }
    json::Document doc;
    if (!doc.parse(data) || !doc.root().isObject()) return unreadable;
    const auto root = doc.root();
    SidecarRead read{SidecarStatus::Readable, signature, std::nullopt, std::nullopt};
    const auto model = trimmedNonBlank(token(root["model"]));
    std::optional<std::string> routed;
    if (const auto router = root["effectiveFactoryRouterModel"]; router.isObject()) routed = trimmedNonBlank(token(router["modelId"]));
    if (routed) {
        read.model = routed;
    } else if (model) {
        std::string lower = *model;
        for (char& c : lower) {
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        }
        if (lower != "auto") read.model = model;
    }
    if (const auto usage = root["tokenUsage"]; usage.isObject()) {
        Counters c;
        c.f[0] = usage["inputTokens"].count();
        c.f[1] = usage["cacheCreationTokens"].count();
        c.f[2] = usage["cacheReadTokens"].count();
        c.f[3] = usage["outputTokens"].count();
        c.f[4] = usage["thinkingTokens"].count();
        read.counters = c;
    }
    return read;
}

SidecarRead readSidecar(const std::string& path, const std::optional<SidecarSignature>& known) {
    struct stat link {};
    if (lstat(path.c_str(), &link) != 0) return {SidecarStatus::Missing, std::nullopt, std::nullopt, std::nullopt};
    if (S_ISLNK(link.st_mode)) {
        SidecarSignature s{static_cast<std::int64_t>(link.st_size),
                           static_cast<std::int64_t>(link.st_mtim.tv_sec) * 1'000'000'000 + link.st_mtim.tv_nsec, 0, 0, false};
        return {SidecarStatus::Unreadable, s, std::nullopt, std::nullopt};
    }
    const int fd = open(path.c_str(), O_RDONLY | O_CLOEXEC);
    if (fd < 0) {
        if (errno == ENOENT || errno == ENOTDIR) return {SidecarStatus::Missing, std::nullopt, std::nullopt, std::nullopt};
        SidecarSignature s{static_cast<std::int64_t>(link.st_size),
                           static_cast<std::int64_t>(link.st_mtim.tv_sec) * 1'000'000'000 + link.st_mtim.tv_nsec, 0, 0, false};
        return {SidecarStatus::Unreadable, s, std::nullopt, std::nullopt};
    }
    struct stat st {};
    fstat(fd, &st);
    const SidecarSignature signature{static_cast<std::int64_t>(st.st_size),
                                     static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec,
                                     static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino), true};
    if (known && signature == *known) {
        close(fd);
        return {SidecarStatus::Unchanged, signature, std::nullopt, std::nullopt};
    }
    if (static_cast<std::size_t>(st.st_size) > kMaxSidecarBytes) {
        close(fd);
        return {SidecarStatus::Unreadable, signature, std::nullopt, std::nullopt};
    }
    std::string data(static_cast<std::size_t>(st.st_size) + 1, '\0');
    std::size_t total = 0;
    while (total < data.size()) {
        const auto got = pread(fd, data.data() + total, data.size() - total, static_cast<off_t>(total));
        if (got <= 0) break;
        total += static_cast<std::size_t>(got);
    }
    close(fd);
    data.resize(total);
    if (total > kMaxSidecarBytes) return {SidecarStatus::Unreadable, signature, std::nullopt, std::nullopt};
    return parseSidecar(data, signature);
}

std::optional<std::string> parentOfTranscript(const std::string& transcript) {
    std::ifstream in(transcript, std::ios::binary);
    if (!in) return std::nullopt;
    std::string data(64 * 1024, '\0');
    in.read(data.data(), static_cast<std::streamsize>(data.size()));
    data.resize(static_cast<std::size_t>(in.gcount()));
    std::size_t start = 0;
    while (start < data.size()) {
        const auto newline = data.find('\n', start);
        if (newline == std::string::npos) return std::nullopt;
        const auto line = readLine(std::string_view(data).substr(start, newline - start), static_cast<std::int64_t>(start),
                                   static_cast<std::int64_t>(newline - start + 1));
        if (line.status == Line::Status::Valid && line.kind == LineKind::SessionStart) return line.parent;
        start = newline + 1;
    }
    return std::nullopt;
}

std::string stampText(const std::string& path) {
    struct stat st {};
    if (::lstat(path.c_str(), &st) != 0 || !S_ISREG(st.st_mode)) return "-";
    const auto ns = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1'000'000'000 + st.st_mtim.tv_nsec;
    return std::to_string(st.st_size) + ":" + std::to_string(ns);
}

std::string defaultRoot(const std::string& home) { return home + "/.factory/sessions"; }

std::string rootOf(const std::string& setting) {
    const std::string nested = setting + "/sessions";
    struct stat st {};
    return lstat(nested.c_str(), &st) == 0 && S_ISDIR(st.st_mode) ? nested : setting;
}

bool isSessionId(std::string_view v) {
    auto alnum = [](char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9'); };
    if (v.empty() || v.size() > 128 || !alnum(v[0])) return false;
    return std::all_of(v.begin(), v.end(), [&](char c) { return alnum(c) || c == '_' || c == '-'; });
}

bool isTranscriptName(std::string_view name) {
    return name.size() >= 6 && name.substr(name.size() - 6) == ".jsonl" && name[0] != '.';
}

std::string sessionIdOf(const std::string& transcript) {
    auto name = path_text::basename(transcript);
    if (name.size() >= 6 && name.compare(name.size() - 6, 6, ".jsonl") == 0) name.resize(name.size() - 6);
    return name;
}

std::string sidecarOf(const std::string& transcript) {
    if (transcript.size() >= 6 && transcript.compare(transcript.size() - 6, 6, ".jsonl") == 0) {
        return transcript.substr(0, transcript.size() - 6) + ".settings.json";
    }
    return transcript + ".settings.json";
}

std::string siblingTranscript(const std::string& transcript, const std::string& sessionId) {
    return directoryOf(transcript) + "/" + sessionId + ".jsonl";
}

std::string siblingSidecar(const std::string& transcript, const std::string& sessionId) {
    return directoryOf(transcript) + "/" + sessionId + ".settings.json";
}

Parser::Parser(std::string agentId, Deduper& deduper) : TranscriptParser(std::move(agentId), deduper, SourceKind::Droid) {
    setSource(SourceKind::Droid);
}

std::optional<std::string> Parser::parentPath() const {
    return callingSessionId_ ? std::optional<std::string>(siblingTranscript(agentId(), *callingSessionId_)) : std::nullopt;
}

AgentLink Parser::link() const { return {agentId(), LinkKind::DroidPath, std::nullopt, parentPath(), std::nullopt, 0}; }

void Parser::reset() {
    TranscriptParser::reset();
    open_.clear();
    seen_.clear();
    context_ = ExecutionModes();
    sessionModel_.reset();
    callModel_.reset();
    pendingStartMs_.reset();
    compactionOffset_.reset();
    readFrom_ = 0;
    nextOffset_ = 0;
    readStarted_ = true;
    headerSeen_ = false;
    baselinePending_ = false;
    sidecar_.reset();
    unreadableParent.reset();
    countersUnknown_ = false;
    parentSessionId_.reset();
    callingSessionId_.reset();
    mark_ = Counters();
    pending_ = Counters();
    setSource(SourceKind::Droid);
}

void Parser::beginRead(std::int64_t startOffset) {
    if (!readStarted_) {
        readFrom_ = startOffset;
        readStarted_ = true;
    }
    nextOffset_ = startOffset;
}

void Parser::feed(std::string_view text, ResponseSink&) {
    feedPositioned(text, nextOffset_, static_cast<std::int64_t>(text.size()));
}

void Parser::feedPositioned(std::string_view text, std::int64_t offset, std::int64_t length) {
    if (!readStarted_) {
        readFrom_ = offset;
        readStarted_ = true;
    }
    nextOffset_ = offset + length + 1;
    applyLine(readLine(text, offset, length));
}

void Parser::applyLine(const Line& line) {
    if (line.status == Line::Status::Malformed) {
        countMalformed();
        return;
    }
    if (line.status != Line::Status::Valid) return;
    if (line.kind == LineKind::SessionStart) {
        if (headerSeen_) return;
        headerSeen_ = true;
        setProjectFromCwd(line.cwd);
        parentSessionId_ = line.parent;
        callingSessionId_ = line.callingSessionId;
        setSubagent(line.callingSessionId.has_value());
        markChanged();
        return;
    }
    if (line.kind == LineKind::TurnOutcome) {
        pendingStartMs_.reset();
        setPhase(Phase::Idle, lastLineMs().value_or(0), std::nullopt);
        return;
    }
    if (!line.timestampMs) return;
    const std::int64_t ts = *line.timestampMs;
    touch(ts);
    if (line.kind == LineKind::Message && line.role == Role::User) {
        if (!line.userOnly) {
            pendingStartMs_ = ts;
            setPhase(Phase::Generating, ts, ts);
        }
    } else if (line.kind == LineKind::Message && line.role == Role::Assistant) {
        applyCall(line, ts);
    } else if (line.kind == LineKind::Compaction) {
        compactionOffset_ = line.offset;
        if (phase() == Phase::Generating) {
            pendingStartMs_ = ts;
            setPhase(Phase::Generating, ts, ts);
        }
    }
}

void Parser::applyCall(const Line& line, std::int64_t ts) {
    if (!line.id) return;
    const std::string key = "droid:" + *line.id;
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    if (seen_.contains(key)) return;
    deduper().tryClaim(key, agentId());
    seen_.insert(key);
    context_ = ExecutionModes::of(native::effort(line.effort, ModeEvidence::Recorded, ModeScope::Request));
    setModes(context_);
    const std::int64_t start = compactionOffset_.value_or(readFrom_);
    open_.push_back({key, pendingStartMs_, ts, line.modelId, context_,
                     std::min(kContextBase + line.offset - start, kMaxWeight), std::min(line.length, kMaxWeight)});
    callModel_ = line.modelId;
    setAgentModel(line.modelId ? line.modelId : sessionModel_);
    pendingStartMs_.reset();
    setPhase(line.hasToolUse ? Phase::Tools : Phase::Idle, ts, std::nullopt);
}

void Parser::applyCycle(const std::optional<SidecarRead>& sidecar, ResponseSink& sink) {
    if (sidecar) applySidecar(*sidecar);
    allocate(sink);
}

void Parser::applySidecar(const SidecarRead& read) {
    countersUnknown_ = false;
    switch (read.status) {
        case SidecarStatus::Missing:
            baselinePending_ = false;
            return;
        case SidecarStatus::Unreadable:
            sidecar_.reset();
            return;
        case SidecarStatus::Unchanged:
            return;
        case SidecarStatus::Readable:
            break;
    }
    std::optional<Counters> ownCounters;
    if (read.counters) {
        ownCounters = own(*read.counters);
        if (!ownCounters) {
            sidecar_.reset();
            countersUnknown_ = true;
            return;
        }
    }
    sidecar_ = read.signature;
    sessionModel_ = read.model;
    setAgentModel(callModel_ ? callModel_ : sessionModel_);
    const bool baseline = baselinePending_;
    baselinePending_ = false;
    if (!ownCounters) return;
    if (!baseline) pending_ = Counters::add(pending_, Counters::minus(*ownCounters, mark_));
    mark_ = Counters::max(mark_, *ownCounters);
}

std::optional<Counters> Parser::own(const Counters& counters) {
    Counters result = counters;
    if (parentSessionId_) {
        const SidecarRead read = readSidecar(siblingSidecar(agentId(), *parentSessionId_));
        switch (read.status) {
            case SidecarStatus::Missing:
                unreadableParent.reset();
                break;
            case SidecarStatus::Readable:
                unreadableParent.reset();
                if (read.counters) result = Counters::minus(counters, *read.counters);
                break;
            default:
                if (!read.signature || read.signature != unreadableParent) {
                    unreadableParent = read.signature;
                    return std::nullopt;
                }
                break;
        }
    }
    result.f[4] = std::min(result.f[4], result.f[3]);
    return result;
}

void Parser::allocate(ResponseSink& sink) {
    if (open_.empty() || pending_.zero()) return;
    std::vector<std::array<std::int64_t, 5>> fields(open_.size());
    for (std::size_t field = 0; field < 5; ++field) {
        std::vector<std::int64_t> weights;
        for (const auto& call : open_) {
            weights.push_back(Counters::inputField(static_cast<int>(field)) ? call.inputWeight : call.outputWeight);
        }
        const auto split = shares(pending_.f[field], weights);
        for (std::size_t i = 0; i < split.size(); ++i) fields[i][field] = split[i];
    }
    for (std::size_t i = 0; i < open_.size(); ++i) {
        Counters share;
        share.f = fields[i];
        const auto& call = open_[i];
        emitResponse(call.key, call.startMs, call.endMs, share, call.modelId, call.modes, emitted_.insert(call.key).second, sink);
    }
    open_.clear();
    pending_ = Counters();
    markChanged();
}

void Parser::finishHistory(ResponseSink& sink) {
    if (pending_.zero() || !lastLineMs()) return;
    const std::string key = "droid:" + sessionIdOf(agentId()) + ":session";
    const bool first = deduper().tryClaim(key, agentId());
    emitResponse(key, std::nullopt, *lastLineMs(), pending_, std::nullopt, ExecutionModes(), first && emitted_.insert(key).second,
                 sink);
    pending_ = Counters();
}

void Parser::emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs, const Counters& share,
                          const std::optional<std::string>& modelId, const ExecutionModes& modes, bool completion,
                          ResponseSink& sink) {
    const auto model = modelId ? modelId : sessionModel_;
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Droid;
    r.agentId = agentId();
    r.model = model;
    r.startMs = startMs;
    r.endMs = endMs;
    r.tokens = share.tokens();
    if (model && model->size() >= 7) {
        std::string prefix = model->substr(0, 7);
        for (char& c : prefix) {
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        }
        if (prefix == "custom:") r.provider = "custom";
    }
    r.modes = modes;
    sink.onResponse(r, completion);
}

// Unlike setModel, the agent's model can become unknown again (a call without modelId and no session model).
void Parser::setAgentModel(const std::optional<std::string>& model) {
    if (model != this->model()) {
        if (model) {
            setModel(model);
        } else {
            clearModel();
        }
    }
}

}  // namespace agentspeed::droid
