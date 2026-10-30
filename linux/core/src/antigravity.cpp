#include "agentspeed/antigravity.hpp"

#include <algorithm>
#include <cstring>

#include "agentspeed/path_text.hpp"
#include "agentspeed/sqlite.hpp"
#include "agentspeed/text.hpp"

namespace agentspeed::antigravity {

namespace {

constexpr std::int64_t kUserInput = 14;
constexpr std::int64_t kPlannerResponse = 15;
constexpr std::int64_t kDone = 3;
constexpr std::uint64_t kMaxTokens = 10'000'000;
constexpr std::int64_t kCopiedHistoryMs = 2000;

bool isOpen(std::int64_t status) { return status == 1 || status == 2 || status == 8 || status == 9 || status == 11; }
bool isFailed(std::int64_t status) { return status == 6 || status == 7 || status == 12; }

// A hand-written protobuf walker (METRICS 4.8, Protobuf wire format).
class Proto {
public:
    static constexpr int kVarint = 0, kFixed64 = 1, kLength = 2, kFixed32 = 5;

    explicit Proto(std::string_view data) : data_(data) {
        std::size_t position = 0;
        while (position < data.size()) {
            const std::uint64_t key = readVarint(data, position);
            const std::uint64_t number = key >> 3;
            const int wire = static_cast<int>(key & 7);
            if (number == 0) throw FormatError("field number 0");
            const std::int64_t known = number <= static_cast<std::uint64_t>(INT64_MAX) ? static_cast<std::int64_t>(number) : -1;
            switch (wire) {
                case kVarint:
                    fields_.push_back({known, wire, readVarint(data, position), 0, 0});
                    break;
                case kFixed64:
                case kFixed32: {
                    const std::size_t size = wire == kFixed64 ? 8 : 4;
                    if (data.size() - position < size) throw FormatError("truncated fixed field");
                    std::uint64_t value = 0;
                    for (std::size_t i = 0; i < size; ++i) {
                        value |= static_cast<std::uint64_t>(static_cast<unsigned char>(data[position + i])) << (8 * i);
                    }
                    fields_.push_back({known, wire, value, position, size});
                    position += size;
                    break;
                }
                case kLength: {
                    const std::uint64_t length = readVarint(data, position);
                    if (length > data.size() - position) throw FormatError("truncated length-delimited field");
                    fields_.push_back({known, wire, length, position, static_cast<std::size_t>(length)});
                    position += static_cast<std::size_t>(length);
                    break;
                }
                default:
                    throw FormatError("unsupported wire type");
            }
        }
    }

    std::uint64_t uint(int number) const {
        const auto* f = last(number, kVarint);
        return f ? f->value : 0;
    }
    std::int64_t integer(int number) const { return static_cast<std::int64_t>(uint(number)); }
    std::string string(int number, bool first = false) const {
        const auto* f = first ? firstOf(number, kLength) : last(number, kLength);
        return f && f->length > 0 ? text::sanitizeUtf8(data_.substr(f->start, f->length)) : std::string();
    }
    std::vector<std::string> strings(int number) const {
        std::vector<std::string> values;
        for (const auto& f : fields_) {
            if (f.number == number && f.wire == kLength) values.push_back(text::sanitizeUtf8(data_.substr(f.start, f.length)));
        }
        return values;
    }
    bool has(int number, int wire) const { return last(number, wire) != nullptr; }
    std::optional<Proto> message(int number, bool first = false) const {
        const auto* f = first ? firstOf(number, kLength) : last(number, kLength);
        if (!f) return std::nullopt;
        return Proto(data_.substr(f->start, f->length));
    }
    std::optional<std::int64_t> timestamp(int number) const {
        const auto t = message(number);
        if (!t) return std::nullopt;
        const std::int64_t seconds = t->integer(1);
        const std::int64_t nanos = t->integer(2);
        const std::int64_t fraction = nanos >= 0 && nanos <= 999'999'999 ? nanos / 1'000'000 : 0;
        if (seconds <= 0 || seconds > (INT64_MAX - fraction) / 1000) return std::nullopt;
        return seconds * 1000 + fraction;
    }
    std::vector<std::uint64_t> repeatedUint(int number) const {
        std::vector<std::uint64_t> values;
        for (const auto& f : fields_) {
            if (f.number != number) continue;
            if (f.wire == kVarint) {
                values.push_back(f.value);
            } else if (f.wire == kLength) {
                const auto packed = data_.substr(f.start, f.length);
                std::size_t position = 0;
                while (position < packed.size()) values.push_back(readVarint(packed, position));
            }
        }
        return values;
    }

private:
    struct Field {
        std::int64_t number;
        int wire;
        std::uint64_t value;
        std::size_t start, length;
    };

    static std::uint64_t readVarint(std::string_view data, std::size_t& position) {
        std::uint64_t result = 0;
        for (int i = 0; i < 10; ++i) {
            if (position >= data.size()) throw FormatError("truncated varint");
            const auto b = static_cast<unsigned char>(data[position++]);
            result |= static_cast<std::uint64_t>(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw FormatError("varint longer than 10 bytes");
    }
    const Field* last(int number, int wire) const {
        for (auto it = fields_.rbegin(); it != fields_.rend(); ++it) {
            if (it->number == number && it->wire == wire) return &*it;
        }
        return nullptr;
    }
    const Field* firstOf(int number, int wire) const {
        for (const auto& f : fields_) {
            if (f.number == number && f.wire == wire) return &f;
        }
        return nullptr;
    }

    std::string_view data_;
    std::vector<Field> fields_;
};

std::optional<std::string> nonEmpty(std::string value) {
    return value.empty() ? std::nullopt : std::optional<std::string>(std::move(value));
}

// "[A-Za-z][A-Za-z0-9+.-]+:" → its length including the colon, else 0.
std::size_t schemeLength(const std::string& value) {
    auto letter = [](char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'); };
    if (value.size() < 3 || !letter(value[0])) return 0;
    std::size_t i = 1;
    while (i < value.size() && (letter(value[i]) || (value[i] >= '0' && value[i] <= '9') || value[i] == '+' ||
                                value[i] == '.' || value[i] == '-')) {
        ++i;
    }
    return i >= 2 && i < value.size() && value[i] == ':' ? i + 1 : 0;
}

int hexValue(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

// Python's urllib.parse.unquote(encoding="utf-8", errors="replace").
std::string percentDecode(const std::string& value) {
    if (value.find('%') == std::string::npos) return value;
    std::string result, bytes;
    auto flush = [&] {
        if (!bytes.empty()) {
            result += text::sanitizeUtf8(bytes);
            bytes.clear();
        }
    };
    for (std::size_t i = 0; i < value.size();) {
        const auto c = static_cast<unsigned char>(value[i]);
        if (c == '%' && i + 2 < value.size() && hexValue(value[i + 1]) >= 0 && hexValue(value[i + 2]) >= 0) {
            bytes += static_cast<char>((hexValue(value[i + 1]) << 4) | hexValue(value[i + 2]));
            i += 3;
        } else if (c < 0x80) {
            bytes += static_cast<char>(c);
            ++i;
        } else {
            flush();
            const std::size_t start = i;
            text::nextCodePoint(value, i);
            result.append(value, start, i - start);
        }
    }
    flush();
    return result;
}

}  // namespace

TokenCounts Usage::tokens() const {
    TokenCounts t;
    t.input = input + cacheRead;
    t.cachedInput = cacheRead;
    t.output = output > 0 ? output : thinking + response;
    t.reasoning = thinking;
    return t;
}

std::vector<std::int64_t> Step::times() const {
    std::vector<std::int64_t> out;
    for (const auto& t : {createdMs, viewableMs, finishedGeneratingMs, completedMs, lastChunkMs, startedMs}) {
        if (t) out.push_back(*t);
    }
    return out;
}

std::optional<std::int64_t> Step::latestTime() const {
    const auto all = times();
    if (all.empty()) return std::nullopt;
    return *std::max_element(all.begin(), all.end());
}

Step decodeStep(std::string_view blob) {
    const Proto m(blob);
    Step s;
    s.createdMs = m.timestamp(1);
    s.viewableMs = m.timestamp(6);
    s.finishedGeneratingMs = m.timestamp(7);
    s.completedMs = m.timestamp(8);
    s.lastChunkMs = m.timestamp(22);
    s.startedMs = m.timestamp(32);
    if (const auto stats = m.message(9)) {
        const auto input = stats->uint(2), output = stats->uint(3), cache = stats->uint(5);
        const auto thinking = stats->uint(9), response = stats->uint(10);
        if (input > kMaxTokens || output > kMaxTokens || cache > kMaxTokens || thinking > kMaxTokens || response > kMaxTokens) {
            throw FormatError("implausible token count");
        }
        Usage u;
        u.input = static_cast<std::int64_t>(input);
        u.output = static_cast<std::int64_t>(output);
        u.cacheRead = static_cast<std::int64_t>(cache);
        u.thinking = static_cast<std::int64_t>(thinking);
        u.response = static_cast<std::int64_t>(response);
        u.key = nonEmpty(stats->string(11));
        if (!u.key) u.key = nonEmpty(stats->string(7));
        u.tier = nonEmpty(stats->string(13));
        s.usage = u;
    }
    return s;
}

Generation decodeGeneration(std::int64_t idx, std::string_view blob) {
    const Proto m(blob);
    Generation g;
    g.idx = idx;
    if (const auto chat = m.message(1)) {
        g.responseModel = nonEmpty(chat->string(19));
        g.label = nonEmpty(chat->string(21));
        g.model = g.responseModel ? g.responseModel : g.label;
    }
    for (auto v : m.repeatedUint(2)) g.stepIndices.insert(v);
    return g;
}

Executor decodeExecutor(std::int64_t idx, std::string_view blob) {
    const Proto m(blob);
    Executor e;
    e.idx = idx;
    if (const auto cascade = m.message(10)) {
        if (const auto planner = cascade->message(1)) {
            e.model = nonEmpty(planner->string(28));
            if (const auto override = planner->message(56)) {
                switch (override->integer(19)) {
                    case 1: e.thinking = "low"; break;
                    case 2: e.thinking = "medium"; break;
                    case 3: e.thinking = "high"; break;
                    default: break;
                }
            }
        }
    }
    e.terminationReason = m.integer(1);
    e.lastStepIdx = m.integer(3);
    return e;
}

Trajectory decodeTrajectory(std::string_view blob) {
    const Proto m(blob);
    std::optional<std::string> workspaceUri;
    if (const auto workspace = m.message(1, true)) workspaceUri = workspace->string(1);
    const auto uris = m.strings(7);
    Trajectory t;
    t.project = uris.empty() ? std::nullopt : uriBasename(uris[0]);
    if (!t.project && workspaceUri) t.project = uriBasename(*workspaceUri);
    t.createdMs = m.timestamp(2);
    t.parentConversationId = nonEmpty(m.string(5));
    t.isSubagent = t.parentConversationId.has_value() || m.integer(17) > 0 || m.has(8, Proto::kLength);
    return t;
}

std::optional<std::string> uriBasename(const std::string& input) {
    if (input.empty()) return std::nullopt;
    std::string value = input;
    if (const auto scheme = schemeLength(value); scheme > 0) {
        std::string rest = value.substr(scheme);
        if (rest.rfind("//", 0) == 0) {
            const auto slash = rest.find('/', 2);
            rest = slash == std::string::npos ? std::string() : rest.substr(slash);
        }
        value = percentDecode(rest);
    }
    auto name = path_text::basename(value);
    return name.empty() ? std::nullopt : std::optional<std::string>(name);
}

std::string conversationId(const std::string& databasePath) {
    const auto name = path_text::basename(databasePath);
    if (name.size() >= 3) {
        std::string tail = name.substr(name.size() - 3);
        for (char& c : tail) {
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        }
        if (tail == ".db") return name.substr(0, name.size() - 3);
    }
    return name;
}

std::optional<std::string> storeNameOf(const std::string& databasePath) {
    const auto cut = databasePath.find_last_of("/\\");
    if (cut == std::string::npos) return std::nullopt;
    const std::string conversations = databasePath.substr(0, cut);
    std::string folder = path_text::basename(conversations);
    for (char& c : folder) {
        if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
    }
    if (folder != "conversations") return std::nullopt;
    const auto up = conversations.find_last_of("/\\");
    const std::string store = path_text::basename(up == std::string::npos ? std::string() : conversations.substr(0, up));
    return store.empty() ? std::nullopt : std::optional<std::string>(store);
}

Parser::Parser(std::string agentId, Deduper& deduper)
    : TranscriptParser(std::move(agentId), deduper, SourceKind::Antigravity), conversationId_(conversationId(this->agentId())) {
    setSource(SourceKind::Antigravity);
}

void Parser::reset() {
    TranscriptParser::reset();
    generations_.clear();
    executors_.clear();
    responses_.clear();
    latest_.reset();
    createdMs_.reset();
    parentConversationId_.reset();
    unsupported_ = false;
    setSource(SourceKind::Antigravity);
}

AgentLink Parser::link() const {
    auto lower = [](std::string s) {
        for (char& c : s) {
            if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
        }
        return s;
    };
    std::optional<std::string> parent;
    if (parentConversationId_) parent = lower(*parentConversationId_);
    return {agentId(), LinkKind::AntigravityConversation, lower(conversationId_), parent, createdMs_, 0};
}

void Parser::apply(const Batch& batch, ResponseSink& sink) {
    for (int i = 0; i < batch.malformed; ++i) countMalformed();
    if (batch.unsupported != unsupported_) {
        unsupported_ = batch.unsupported;
        markChanged();
    }
    if (batch.trajectory) {
        const auto& t = *batch.trajectory;
        createdMs_ = t.createdMs;
        if (t.project && t.project != project()) setProject(t.project);
        setSubagent(t.isSubagent);
        parentConversationId_ = t.parentConversationId;
        markChanged();
        if (t.createdMs) touch(*t.createdMs);
    }
    bool modelsChanged = false;
    for (const auto& g : batch.generations) {
        generations_[g.idx] = g;
        modelsChanged = true;
    }
    for (const auto& e : batch.executors) {
        executors_[e.idx] = e;
        modelsChanged = true;
    }
    for (const auto& row : batch.steps) applyStep(row, sink);
    if (modelsChanged) resolveModels(sink);
    updateAgentModel();
    updatePhase();
}

void Parser::applyStep(const StepRow& row, ResponseSink& sink) {
    if (!row.step) return;
    const Step& step = *row.step;
    if (createdMs_ && step.createdMs && *step.createdMs < *createdMs_ - kCopiedHistoryMs) return;
    for (auto t : step.times()) touch(t);
    if (!latest_ || row.idx >= latest_->idx) latest_ = row;
    if (row.stepType != kPlannerResponse || isOpen(row.status) || !step.usage || !step.usage->hasTokens()) return;
    const std::string key = step.usage->key ? *step.usage->key : "antigravity:" + conversationId_ + ":" + std::to_string(row.idx);
    if (auto owner = deduper().owner(key); owner && *owner != agentId()) return;
    std::optional<std::int64_t> end = step.finishedGeneratingMs ? step.finishedGeneratingMs
                                      : step.lastChunkMs        ? step.lastChunkMs
                                                                : step.completedMs;
    std::optional<std::int64_t> start = step.createdMs;
    if (!end) {
        const auto latest = step.latestTime();
        if (!latest) return;
        end = latest;
        start.reset();
    }
    if (isFailed(row.status)) start.reset();
    const bool completion = deduper().tryClaim(key, agentId());
    ParsedResponse r;
    r.key = key;
    r.source = SourceKind::Antigravity;
    r.agentId = agentId();
    r.model = modelFor(row.idx);
    r.startMs = start;
    r.endMs = *end;
    r.tokens = step.usage->tokens();
    r.modes = modesFor(row.idx, *step.usage);
    emitResponse(r, row.idx, *step.usage, completion, sink);
}

// The lowest generation listing the step.
const Generation* Parser::generationOf(std::int64_t idx) const {
    if (idx < 0) return nullptr;
    for (const auto& [_, g] : generations_) {
        if (g.stepIndices.contains(static_cast<std::uint64_t>(idx))) return &g;
    }
    return nullptr;
}

// The executor run with the smallest `last_step_idx >=` the step (ties: lowest idx).
const Executor* Parser::executorCovering(std::int64_t idx) const {
    const Executor* covering = nullptr;
    for (const auto& [_, e] : executors_) {
        if (e.lastStepIdx >= idx && (!covering || e.lastStepIdx < covering->lastStepIdx ||
                                     (e.lastStepIdx == covering->lastStepIdx && e.idx < covering->idx))) {
            covering = &e;
        }
    }
    return covering;
}

std::optional<std::string> Parser::modelFor(std::int64_t idx) const {
    if (const auto* g = generationOf(idx); g && g->model) return g->model;
    const auto* covering = executorCovering(idx);
    return covering ? covering->model : std::nullopt;
}

// The display name first; else the executor's suffix or pass-through level, unless another model answered
// (EXECUTION-MODES, Antigravity).
ExecutionModes Parser::modesFor(std::int64_t idx, const Usage& usage) const {
    const auto* generation = generationOf(idx);
    const auto* executor = executorCovering(idx);
    auto modes = native::googleTier(usage.tier, ModeEvidence::Served, ModeScope::Response);
    if (generation && generation->label) {
        if (const auto word = native::antigravityLabelEffort(*generation->label)) {
            return modes.with(native::effort(word, ModeEvidence::Recorded, ModeScope::Request));
        }
    }
    if (!executor || !executor->model) return modes;
    const auto split = native::antigravityModelEffort(*executor->model);
    if (generation && generation->responseModel) {
        std::string answered = *generation->responseModel;
        constexpr std::string_view kServedVariant = "-exp-b";
        if (answered.ends_with(kServedVariant)) answered.resize(answered.size() - kServedVariant.size());
        if (answered != *executor->model && (!split || answered != split->first)) return modes;
    }
    return modes.with(native::effort(split ? std::optional<std::string>(split->second) : executor->thinking, ModeEvidence::Recorded, ModeScope::Turn));
}

void Parser::resolveModels(ResponseSink& sink) {
    std::vector<Emitted> all;
    for (const auto& [_, e] : responses_) all.push_back(e);
    for (const auto& e : all) {
        const auto model = modelFor(e.idx);
        const auto modes = modesFor(e.idx, e.usage);
        if (model != e.response.model || modes != e.response.modes) {
            ParsedResponse updated = e.response;
            updated.model = model;
            updated.modes = modes;
            emitResponse(updated, e.idx, e.usage, false, sink);
        }
    }
}

void Parser::emitResponse(const ParsedResponse& r, std::int64_t idx, const Usage& usage, bool completion, ResponseSink& sink) {
    if (auto it = responses_.find(r.key); it != responses_.end()) {
        const auto& p = it->second.response;
        if (it->second.idx == idx && p.startMs == r.startMs && p.endMs == r.endMs && p.tokens == r.tokens && p.model == r.model &&
            p.modes == r.modes) {
            return;
        }
    }
    responses_[r.key] = {r, idx, usage};
    sink.onResponse(r, completion);
}

void Parser::updateAgentModel() {
    const Emitted* best = nullptr;
    const Emitted* latest = nullptr;
    for (const auto& [_, e] : responses_) {
        if (e.response.model && (!best || e.idx > best->idx)) best = &e;
        if (!latest || e.idx > latest->idx) latest = &e;
    }
    if (best) setModel(best->response.model);
    setModes(latest ? latest->response.modes : ExecutionModes());
}

void Parser::updatePhase() {
    const std::int64_t since = lastLineMs().value_or(0);
    const bool ended = latest_ && std::any_of(executors_.begin(), executors_.end(), [&](const auto& item) {
                           return item.second.terminationReason != 0 && item.second.lastStepIdx >= latest_->idx;
                       });
    if (!latest_ || !latest_->step || ended) {
        setPhase(Phase::Idle, since, std::nullopt);
        return;
    }
    const Step& step = *latest_->step;
    const bool open = isOpen(latest_->status);
    switch (latest_->stepType) {
        case kPlannerResponse:
            setPhase(open ? Phase::Generating : Phase::Idle, since, open ? step.createdMs : std::nullopt);
            break;
        case kUserInput: {
            const bool failed = isFailed(latest_->status);
            setPhase(failed ? Phase::Idle : Phase::Generating, since, failed ? std::nullopt : step.createdMs);
            break;
        }
        default:
            if (open) {
                setPhase(Phase::Tools, since, std::nullopt);
            } else if (latest_->status == kDone) {
                setPhase(Phase::Generating, since, step.completedMs ? step.completedMs : step.createdMs);
            } else {
                setPhase(Phase::Idle, since, std::nullopt);
            }
            break;
    }
}

Database::Database(std::unique_ptr<sqlite::Connection> connection, bool immutable, bool supported)
    : connection_(std::move(connection)), immutable_(immutable), supported_(supported) {}

Database::~Database() = default;

std::unique_ptr<Database> Database::open(const std::string& path) {
    bool immutable = false;
    auto connection = std::make_unique<sqlite::Connection>(sqlite::openReadOnly(path, immutable));
    std::set<std::string> columns;
    try {
        auto statement = connection->prepare("PRAGMA table_info(steps)");
        while (statement.step()) {
            if (auto name = statement.getString(1)) columns.insert(*name);
        }
    } catch (const sqlite::Error&) {
        columns.clear();
    }
    const bool supported = columns.contains("idx") && columns.contains("step_type") && columns.contains("status") &&
                           columns.contains("metadata");
    return std::unique_ptr<Database>(new Database(std::move(connection), immutable, supported));
}

std::int64_t Database::read(Cursors& cursors, Batch& batch) {
    const std::string blob = std::to_string(kMaxBlobBytes);
    std::int64_t bytes = 0;
    connection_->execute("BEGIN");
    try {
        if (!cursors.trajectoryFound) {
            try {
                auto s = connection_->prepare("SELECT CASE WHEN length(data) <= " + blob +
                                              " THEN data END FROM trajectory_metadata_blob ORDER BY rowid LIMIT 1");
                if (s.step() && !s.isNull(0)) {
                    const auto data = s.getBlob(0);
                    bytes += static_cast<std::int64_t>(data.size());
                    try {
                        batch.trajectory = decodeTrajectory(data);
                        cursors.trajectoryFound = true;
                    } catch (const FormatError&) {
                        if (!cursors.trajectoryMalformed) {
                            cursors.trajectoryMalformed = true;
                            ++batch.malformed;
                        }
                    }
                }
            } catch (const sqlite::Error&) {
            }
        }
        try {
            auto s = connection_->prepare("SELECT idx, CASE WHEN typeof(size) = 'integer' AND size <= " + blob +
                                          " AND length(data) <= " + blob +
                                          " THEN data END FROM gen_metadata WHERE idx > ?1 ORDER BY idx, rowid");
            s.bind(1, cursors.generations);
            while (s.step()) {
                const auto idx = s.getInt64(0);
                cursors.generations = std::max(cursors.generations, idx);
                if (s.isNull(1)) continue;
                const auto data = s.getBlob(1);
                bytes += static_cast<std::int64_t>(data.size());
                try {
                    batch.generations.push_back(decodeGeneration(idx, data));
                } catch (const FormatError&) {
                    ++batch.malformed;
                }
            }
        } catch (const sqlite::Error&) {
        }
        try {
            auto s = connection_->prepare("SELECT idx, CASE WHEN length(data) <= " + blob +
                                          " THEN data END FROM executor_metadata WHERE idx > ?1 ORDER BY idx, rowid");
            s.bind(1, cursors.executors);
            while (s.step()) {
                const auto idx = s.getInt64(0);
                cursors.executors = std::max(cursors.executors, idx);
                if (s.isNull(1)) continue;
                const auto data = s.getBlob(1);
                bytes += static_cast<std::int64_t>(data.size());
                try {
                    batch.executors.push_back(decodeExecutor(idx, data));
                } catch (const FormatError&) {
                    ++batch.malformed;
                }
            }
        } catch (const sqlite::Error&) {
        }
        const std::vector<std::int64_t> reread(cursors.openSteps.begin(), cursors.openSteps.end());
        try {
            std::string sql = "SELECT idx, step_type, status, CASE WHEN length(metadata) <= " + blob +
                              " THEN metadata END FROM steps WHERE idx > ?1";
            if (!reread.empty()) {
                sql += " OR idx IN (";
                for (std::size_t i = 0; i < reread.size(); ++i) sql += (i == 0 ? "?" : ",?") + std::to_string(i + 2);
                sql += ")";
            }
            sql += " ORDER BY idx, rowid";
            auto s = connection_->prepare(sql);
            s.bind(1, cursors.steps);
            for (std::size_t i = 0; i < reread.size(); ++i) s.bind(static_cast<int>(i + 2), reread[i]);
            std::int64_t cursor = cursors.steps;
            std::vector<std::int64_t> counted;
            while (s.step()) {
                StepRow row;
                row.idx = s.getInt64(0);
                row.stepType = s.getInt64(1);
                row.status = s.getInt64(2);
                cursor = std::max(cursor, row.idx);
                if (isOpen(row.status)) {
                    cursors.openSteps.insert(row.idx);
                } else {
                    cursors.openSteps.erase(row.idx);
                }
                if (!s.isNull(3)) {
                    const auto data = s.getBlob(3);
                    bytes += static_cast<std::int64_t>(data.size());
                    try {
                        row.step = decodeStep(data);
                    } catch (const FormatError&) {
                        // An open step is read again on every change; its malformed metadata counts once.
                        if (!cursors.malformedSteps.contains(row.idx)) {
                            ++batch.malformed;
                            counted.push_back(row.idx);
                        }
                    }
                }
                batch.steps.push_back(std::move(row));
            }
            cursors.steps = cursor;
            cursors.malformedSteps.insert(counted.begin(), counted.end());
        } catch (const sqlite::Error&) {
        }
        while (cursors.openSteps.size() > Cursors::kMaxOpenSteps) cursors.openSteps.erase(cursors.openSteps.begin());
    } catch (...) {
        try {
            connection_->execute("COMMIT");
        } catch (const sqlite::Error&) {
        }
        throw;
    }
    try {
        connection_->execute("COMMIT");
    } catch (const sqlite::Error&) {
    }
    return bytes;
}

}  // namespace agentspeed::antigravity
