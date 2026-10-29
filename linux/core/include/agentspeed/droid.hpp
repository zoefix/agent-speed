#pragma once

#include <array>
#include <set>
#include <vector>

#include "agentspeed/transcript_parser.hpp"

// METRICS 4.16: Factory Droid — calls from the transcript, tokens from the sidecar's cumulative counters allocated to
// the calls written since by the bytes of their lines.
namespace agentspeed::droid {

constexpr std::int64_t kMaxWeight = 8'388'608;
constexpr std::int64_t kContextBase = 65'536;

struct Counters {
    std::array<std::int64_t, 5> f{};  // input, cacheCreation, cacheRead, output, thinking
    bool zero() const { return f == std::array<std::int64_t, 5>{}; }
    static bool inputField(int field) { return field < 3; }
    static Counters minus(const Counters& a, const Counters& b);
    static Counters max(const Counters& a, const Counters& b);
    static Counters add(const Counters& a, const Counters& b);
    TokenCounts tokens() const;
    bool operator==(const Counters&) const = default;
};

// Largest-remainder split of `total` by `weights`, ties to the earlier call.
std::vector<std::int64_t> shares(std::int64_t total, const std::vector<std::int64_t>& weights);

struct SidecarSignature {
    std::int64_t length = 0, writeNs = 0, device = 0, inode = 0;
    bool hasIdentity = false;
    bool operator==(const SidecarSignature&) const = default;
};

enum class SidecarStatus { Missing, Unchanged, Unreadable, Readable };

struct SidecarRead {
    SidecarStatus status = SidecarStatus::Missing;
    std::optional<SidecarSignature> signature;
    std::optional<Counters> counters;
    std::optional<std::string> model;
};

SidecarRead readSidecar(const std::string& path, const std::optional<SidecarSignature>& known = std::nullopt);
SidecarRead parseSidecar(std::string_view data, const std::optional<SidecarSignature>& signature = std::nullopt);

// Paths.
std::string defaultRoot(const std::string& home);
std::string rootOf(const std::string& setting);
bool isSessionId(std::string_view value);
bool isTranscriptName(std::string_view name);
std::string sessionIdOf(const std::string& transcript);
std::string sidecarOf(const std::string& transcript);
std::string siblingTranscript(const std::string& transcript, const std::string& sessionId);
std::string siblingSidecar(const std::string& transcript, const std::string& sessionId);

// The `parent` of a transcript's session_start, read from its first 64 KiB (the history identity, METRICS 8).
std::optional<std::string> parentOfTranscript(const std::string& transcript);
// A file's size and modification time as "size:ns", "-" when it is missing or not a regular file.
std::string stampText(const std::string& path);

struct Line;

class Parser : public TranscriptParser {
public:
    Parser(std::string agentId, Deduper& deduper);
    // Lines must come with positions: use feedPositioned. feed() treats the line as starting at the next offset.
    void feed(std::string_view line, ResponseSink& sink) override;
    void feedPositioned(std::string_view line, std::int64_t offset, std::int64_t length);
    // Before the first lines of a read: where reading started (0 for a file read from the start).
    void beginRead(std::int64_t startOffset);
    // A read cycle after the new lines: the sidecar (when read) and the allocation of P to the open calls.
    void applyCycle(const std::optional<SidecarRead>& sidecar, ResponseSink& sink);
    // The history rule's end: growth left without a call as `droid:<session>:session`.
    void finishHistory(ResponseSink& sink);
    void reset() override;
    AgentLink link() const override;
    void startAtEnd() { baselinePending_ = true; }

    const std::optional<std::string>& parentSessionId() const { return parentSessionId_; }
    const std::optional<std::string>& callingSessionId() const { return callingSessionId_; }
    std::optional<std::string> parentPath() const;
    const Counters& mark() const { return mark_; }
    const Counters& pending() const { return pending_; }
    std::size_t openCallCount() const { return open_.size(); }
    const std::optional<SidecarSignature>& knownSidecar() const { return sidecar_; }
    bool countersUnknown() const { return countersUnknown_; }
    std::optional<SidecarSignature> unreadableParent;

private:
    struct OpenCall {
        std::string key;
        std::optional<std::int64_t> startMs;
        std::int64_t endMs;
        std::optional<std::string> modelId;
        ExecutionModes modes;
        std::int64_t inputWeight, outputWeight;
    };
    void applyLine(const Line& line);
    void applyCall(const Line& line, std::int64_t ts);
    void applySidecar(const SidecarRead& read);
    std::optional<Counters> own(const Counters& counters);
    void allocate(ResponseSink& sink);
    void emitResponse(const std::string& key, std::optional<std::int64_t> startMs, std::int64_t endMs, const Counters& share,
                      const std::optional<std::string>& modelId, const ExecutionModes& modes, bool completion, ResponseSink& sink);
    void setAgentModel(const std::optional<std::string>& model);

    std::vector<OpenCall> open_;
    std::set<std::string> seen_, emitted_;
    ExecutionModes context_;
    std::optional<std::string> sessionModel_, callModel_;
    std::optional<std::int64_t> pendingStartMs_, compactionOffset_;
    std::int64_t readFrom_ = 0;
    std::int64_t nextOffset_ = 0;
    bool readStarted_ = false, headerSeen_ = false, baselinePending_ = false, countersUnknown_ = false;
    std::optional<SidecarSignature> sidecar_;
    std::optional<std::string> parentSessionId_, callingSessionId_;
    Counters mark_, pending_;
};

}  // namespace agentspeed::droid
