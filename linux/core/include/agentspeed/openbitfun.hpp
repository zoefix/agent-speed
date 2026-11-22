#pragma once

#include <map>
#include <set>
#include <tuple>

#include "agentspeed/openbitfun_ledger.hpp"
#include "agentspeed/tailer.hpp"

namespace agentspeed::openbitfun {

std::string projectsOf(const std::string& path);
std::optional<fsx::Stat> statOf(const std::string& metadata);

class Parser : public TranscriptParser {
public:
    Parser(std::string path, Deduper& deduper) : TranscriptParser(std::move(path), deduper, SourceKind::OpenBitFun) {}
    void feed(std::string_view text, ResponseSink& sink) override;
    void reset() override;
    AgentLink link() const override { return {agentId(), LinkKind::OpenBitFunSession, session_, parent_, created_, 0}; }

    std::int64_t contentSignature() const { return usage_ ? usage_->signature : 0; }
    bool setUsage(std::shared_ptr<const UsageSnapshot> usage);
    void setBaseline(std::set<RoundId> baseline) { baseline_ = std::move(baseline); }
    void applyRounds(ResponseSink& sink);

private:
    struct Round {
        std::optional<std::string> model;
        std::optional<std::int64_t> start, end, duration;
        bool completed;
        std::int64_t attempts;
    };
    std::shared_ptr<const UsageSnapshot> usage_;
    std::map<RoundId, Round> rounds_;
    std::set<RoundId> baseline_;
    void upsert(const ParsedResponse& record, ResponseSink& sink);

    std::optional<std::string> session_, parent_;
    std::int64_t created_ = 0;
    std::map<std::string, ParsedResponse> responses_;
    struct Turn { std::optional<std::int64_t> start, end; bool closed; };
    struct Trace {
        std::optional<std::string> turn, operation, round, model;
        std::optional<std::int64_t> start, end;
        bool completed = false;
        std::optional<ParsedResponse> record;
    };
    std::map<std::string, Turn> turns_;
    std::map<std::string, Trace> traces_;
    std::vector<std::string> traceOrder_;
    void withdraw(ParsedResponse record, ResponseSink& sink);
};

class SessionTailer : public Tailer {
public:
    explicit SessionTailer(std::string path, UsageLedger& ledger = UsageLedger::shared())
        : Tailer(std::move(path)), ledger_(ledger) {}
    void startAt(std::int64_t offset, Alignment alignment) override;
    TailResult read(TranscriptParser& parser, ResponseSink& sink) override;

private:
    using Signature = std::tuple<std::int64_t, std::int64_t, std::int64_t, std::int64_t>;
    std::map<std::string, Signature> known_;
    UsageLedger& ledger_;
    std::set<RoundId> baseline_;
};

}  // namespace agentspeed::openbitfun
