#include "agentspeed/monitor.hpp"

#include <sys/stat.h>

#include <algorithm>
#include <cstdlib>
#include <fstream>
#include <set>

#include "agentspeed/claude_parser.hpp"
#include "agentspeed/codex_parser.hpp"
#include "agentspeed/file_reader.hpp"
#include "agentspeed/parents.hpp"

namespace fs = std::filesystem;

namespace agentspeed {

namespace {

constexpr int kMaxDepth = 6;
constexpr std::size_t kMaxFilesPerRoot = 20'000;
constexpr std::int64_t kStartupSlackMs = 15 * 60'000;
constexpr std::int64_t kRetentionMs = 30 * 60'000;
constexpr std::int64_t kRediscoveryMs = 10'000;
constexpr std::int64_t kHotWindowMs = 24LL * 3600 * 1000;
constexpr std::int64_t kLargeFileBytes = 256LL * 1024 * 1024;
constexpr std::int64_t kTailBytes = 64LL * 1024 * 1024;

struct FileStat {
    std::int64_t size = 0;
    std::int64_t mtimeMs = 0;
    std::uint64_t device = 0;
    std::uint64_t inode = 0;
    bool regular = false;
};

std::optional<FileStat> statFile(const fs::path& path) {
    struct stat st {};
    if (::lstat(path.c_str(), &st) != 0) return std::nullopt;
    FileStat out;
    out.regular = S_ISREG(st.st_mode);
    out.size = static_cast<std::int64_t>(st.st_size);
    out.mtimeMs = static_cast<std::int64_t>(st.st_mtim.tv_sec) * 1000 + st.st_mtim.tv_nsec / 1'000'000;
    out.device = static_cast<std::uint64_t>(st.st_dev);
    out.inode = static_cast<std::uint64_t>(st.st_ino);
    return out;
}

bool endsWith(const std::string& text, std::string_view suffix) {
    return text.size() >= suffix.size() && text.compare(text.size() - suffix.size(), suffix.size(), suffix) == 0;
}

std::optional<fs::path> envPath(const char* name) {
    const char* value = std::getenv(name);
    if (!value || !*value) return std::nullopt;
    return fs::path(value);
}

fs::path home() {
    if (auto h = envPath("HOME")) return *h;
    return fs::path("/");
}

}  // namespace

MonitorConfig MonitorConfig::fromEnvironment() {
    MonitorConfig config;
    config.codexRoots.push_back(envPath("CODEX_HOME").value_or(home() / ".codex") / "sessions");
    config.claudeRoots.push_back(envPath("CLAUDE_CONFIG_DIR").value_or(home() / ".claude") / "projects");
    return config;
}

struct Monitor::Tracked {
    fs::path path;
    std::unique_ptr<TranscriptParser> parser;
    LineSplitter splitter;
    std::int64_t offset = 0;
    FileStat stat;
    bool compressed = false;
    bool skipFirstLine = false;
    std::int64_t lastChangeMs = 0;
};

// Keeps every response of today and of the retention window, keyed by response key (METRICS 4.4 upserts).
class Monitor::Store : public ResponseSink {
public:
    void onResponse(const ParsedResponse& r, bool) override {
        engine::Response& e = byKey_[r.key];
        e.key = r.key;
        e.agentId = r.agentId;
        e.source = r.source;
        e.model = r.model;
        e.startMs = r.startMs;
        e.endMs = r.endMs;
        e.input = r.tokens.input;
        e.output = r.tokens.output;
        e.final = r.final;
        e.requestCount = r.requestCount;
    }
    void prune(std::int64_t keepFromMs) {
        for (auto it = byKey_.begin(); it != byKey_.end();) {
            it = it->second.endMs < keepFromMs ? byKey_.erase(it) : std::next(it);
        }
    }
    std::vector<engine::Response> all() const {
        std::vector<engine::Response> out;
        out.reserve(byKey_.size());
        for (const auto& [_, r] : byKey_) out.push_back(r);
        return out;
    }

private:
    std::map<std::string, engine::Response> byKey_;
};

Monitor::Monitor(MonitorConfig config) : config_(std::move(config)), store_(std::make_unique<Store>()) {}
Monitor::~Monitor() = default;

void Monitor::start(std::int64_t nowMs, std::int64_t todayStartMs) {
    discover(nowMs, todayStartMs, true);
    lastDiscoveryMs_ = nowMs;
}

void Monitor::track(const fs::path& path, SourceKind family, std::int64_t todayStartMs, bool startup) {
    const auto stat = statFile(path);
    if (!stat || !stat->regular) return;
    auto file = std::make_unique<Tracked>();
    file->path = path;
    file->stat = *stat;
    file->compressed = endsWith(path.filename().string(), ".zst");
    file->lastChangeMs = stat->mtimeMs;
    const std::string id = path.string();
    if (family == SourceKind::Codex) {
        file->parser = std::make_unique<CodexParser>(id, deduper_);
    } else {
        file->parser = std::make_unique<ClaudeParser>(id, deduper_);
    }
    const bool recent = stat->mtimeMs >= todayStartMs - kStartupSlackMs;
    if (!startup || recent) {
        if (file->compressed) {
            readWhole(*file);
        } else if (startup && stat->size > kLargeFileBytes) {
            file->offset = stat->size - kTailBytes;
            file->skipFirstLine = true;
            readAppended(*file);
        } else {
            readAppended(*file);
        }
    } else {
        // Older files start at their current size (METRICS 5).
        file->offset = stat->size;
        if (!file->compressed && stat->size > 0) {
            std::ifstream in(path, std::ios::binary);
            in.seekg(stat->size - 1);
            char last = '\n';
            in.get(last);
            file->skipFirstLine = last != '\n';
        }
    }
    files_[id] = std::move(file);
}

void Monitor::discover(std::int64_t nowMs, std::int64_t todayStartMs, bool startup) {
    struct Found {
        fs::path path;
        SourceKind family;
        std::int64_t mtimeMs;
    };
    std::vector<Found> found;
    auto scan = [&](const fs::path& root, SourceKind family) {
        std::error_code ec;
        if (!fs::is_directory(root, ec)) return;
        std::size_t count = 0;
        fs::recursive_directory_iterator it(root, fs::directory_options::skip_permission_denied, ec), end;
        for (; it != end && !ec; it.increment(ec)) {
            if (it.depth() >= kMaxDepth) it.disable_recursion_pending();
            if (it->is_symlink(ec)) {
                it.disable_recursion_pending();
                continue;
            }
            if (!it->is_regular_file(ec)) continue;
            const auto name = it->path().filename().string();
            const bool jsonl = endsWith(name, ".jsonl");
            const bool zst = family == SourceKind::Codex && endsWith(name, ".jsonl.zst");
            if (!jsonl && !zst) continue;
            if (++count > kMaxFilesPerRoot) break;
            if (files_.contains(it->path().string())) continue;
            const auto st = statFile(it->path());
            if (st) found.push_back({it->path(), family, st->mtimeMs});
        }
    };
    for (const auto& root : config_.codexRoots) scan(root, SourceKind::Codex);
    for (const auto& root : config_.claudeRoots) scan(root, SourceKind::ClaudeCode);
    // Startup order decides which file owns a duplicated key: modification time, then path (METRICS 5).
    std::sort(found.begin(), found.end(), [](const Found& a, const Found& b) {
        return a.mtimeMs != b.mtimeMs ? a.mtimeMs < b.mtimeMs : a.path.string() < b.path.string();
    });
    for (const auto& f : found) track(f.path, f.family, todayStartMs, startup);
    // Files that no longer exist leave the agents list; their responses stay in the totals.
    for (auto it = files_.begin(); it != files_.end();) {
        std::error_code ec;
        it = fs::exists(it->second->path, ec) ? std::next(it) : files_.erase(it);
    }
    (void)nowMs;
}

void Monitor::readAppended(Tracked& file) {
    std::ifstream in(file.path, std::ios::binary);
    if (!in) return;
    in.seekg(file.offset);
    std::vector<char> buffer(1 << 20);
    while (in) {
        in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const auto got = in.gcount();
        if (got <= 0) break;
        file.offset += got;
        file.splitter.feed(std::string_view(buffer.data(), static_cast<std::size_t>(got)), [&](std::string_view line) {
            if (file.skipFirstLine) {
                file.skipFirstLine = false;
                return;
            }
            file.parser->feed(line, *store_);
        });
    }
}

void Monitor::readWhole(Tracked& file) {
    file.parser->reset();
    file.splitter.reset();
    const auto result = readWholeFile(file.path, [&](std::string_view line) { file.parser->feed(line, *store_); });
    file.parser->addMalformed(result.decodeErrors);
    file.offset = file.stat.size;
}

void Monitor::poll(std::int64_t nowMs, std::int64_t todayStartMs) {
    for (auto& [_, file] : files_) {
        if (nowMs - file->lastChangeMs > kHotWindowMs && nowMs - lastDiscoveryMs_ < kRediscoveryMs) continue;
        const auto st = statFile(file->path);
        if (!st) continue;
        const bool replaced = st->inode != file->stat.inode || st->device != file->stat.device;
        const bool changed = st->size != file->stat.size || st->mtimeMs != file->stat.mtimeMs || replaced;
        if (!changed) continue;
        const bool shrunk = st->size < file->offset;
        file->stat = *st;
        file->lastChangeMs = nowMs;
        if (file->compressed) {
            readWhole(*file);
        } else {
            if (shrunk || replaced) {
                // METRICS 5: reset the offset and the parser state, never the dedupe set.
                file->parser->reset();
                file->splitter.reset();
                file->offset = 0;
                file->skipFirstLine = false;
            }
            readAppended(*file);
        }
    }
    if (nowMs - lastDiscoveryMs_ >= kRediscoveryMs) {
        discover(nowMs, todayStartMs, false);
        lastDiscoveryMs_ = nowMs;
    }
    prune(nowMs, todayStartMs);
}

void Monitor::prune(std::int64_t nowMs, std::int64_t todayStartMs) {
    store_->prune(std::min(todayStartMs, nowMs - kRetentionMs));
}

Snapshot Monitor::snapshot(std::int64_t nowMs, std::int64_t todayStartMs) const {
    Snapshot snap;
    snap.nowMs = nowMs;
    const auto responses = store_->all();
    std::vector<engine::Response> recent;
    for (const auto& r : responses) {
        if (r.endMs >= nowMs - kRetentionMs) recent.push_back(r);
    }
    std::vector<ParentFacts> facts;
    for (const auto& [id, file] : files_) {
        ParentFacts fact;
        fact.agentId = id;
        fact.source = file->parser->source();
        if (const auto* codex = dynamic_cast<const CodexParser*>(file->parser.get())) {
            fact.codexThreadId = codex->metaThreadId();
            fact.codexParentThreadId = codex->parentThreadId();
            fact.createdMs = codex->createdMs();
        }
        facts.push_back(std::move(fact));
    }
    const auto parents = resolveParents(facts);
    std::vector<engine::Agent> agents;
    std::map<std::string, const Tracked*> byId;
    for (const auto& [id, file] : files_) {
        const auto& p = *file->parser;
        engine::Agent a;
        a.id = id;
        a.source = p.source();
        a.model = p.model();
        a.phase = p.phase();
        a.inFlightStartMs = p.inFlightStartMs();
        a.lastLineMs = p.lastLineMs();
        if (auto it = parents.find(id); it != parents.end()) a.parentId = it->second;
        agents.push_back(a);
        byId[id] = file.get();
    }
    const auto live = engine::liveRate(recent, agents, nowMs);
    snap.liveOutput = live.output;
    snap.liveTotal = live.total;
    snap.estimatedShare = live.estimatedShare;
    const auto today = engine::todayStats(responses, todayStartMs, nowMs);
    snap.todayOutput = today.output;
    snap.todayInput = today.input;
    snap.todayRequests = today.requests;
    snap.todayAverage = today.average;
    snap.peakToday = std::max(today.peak, live.output);
    const std::int64_t lastBucket = (nowMs / 1000) * 1000;
    const std::int64_t firstBucket = lastBucket - 119'000;
    const auto measured = engine::buckets(recent, firstBucket, lastBucket);
    snap.history.reserve(120);
    for (std::int64_t k = firstBucket; k <= lastBucket; k += 1000) {
        const auto it = measured.find(k);
        snap.history.push_back(it == measured.end() ? 0.0 : it->second);
    }
    snap.history.back() = std::max(snap.history.back(), live.output);

    const auto list = engine::agentList(recent, agents, nowMs);
    std::map<std::string, int> depth;
    for (const auto& id : list.order) {
        auto parent = list.parents.find(id);
        depth[id] = parent == list.parents.end() ? 0 : depth[parent->second] + 1;
        const auto* file = byId[id];
        const auto& p = *file->parser;
        AgentRow row;
        row.id = id;
        row.source = p.source();
        row.project = p.project();
        row.model = p.model();
        row.phase = p.phase();
        if (row.phase == Phase::Generating && p.lastLineMs() && nowMs - *p.lastLineMs() > 300'000) row.phase = Phase::Idle;
        std::vector<engine::Response> own;
        for (const auto& r : recent) {
            if (r.agentId == id) own.push_back(r);
        }
        engine::Agent self;
        for (const auto& a : agents) {
            if (a.id == id) self = a;
        }
        if (row.phase == Phase::Idle) self.phase = Phase::Idle;
        row.liveOutput = engine::liveRate(own, {self}, nowMs).output;
        row.depth = depth[id];
        row.subagent = p.isSubagent();
        if (row.phase == Phase::Generating) ++snap.generating;
        if (row.phase == Phase::Tools) ++snap.tools;
        snap.agents.push_back(std::move(row));
    }
    std::map<SourceKind, SourceRow> sources;
    for (const auto& r : responses) {
        if (r.endMs < todayStartMs) continue;
        auto& row = sources[r.source];
        row.source = r.source;
        row.todayOutput += r.output;
    }
    for (auto& [source, row] : sources) {
        std::vector<engine::Response> resp;
        std::vector<engine::Agent> ags;
        for (const auto& r : recent) {
            if (r.source == source) resp.push_back(r);
        }
        for (const auto& a : agents) {
            if (a.source == source) ags.push_back(a);
        }
        row.liveOutput = engine::liveRate(resp, ags, nowMs).output;
        snap.sources.push_back(row);
    }
    return snap;
}

}  // namespace agentspeed
