#include <gtest/gtest.h>
#include <sys/stat.h>
#include <unistd.h>

#include <chrono>
#include <filesystem>
#include <fstream>
#include <set>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/estimate/estimator.hpp"
#include "agentspeed/quota/estimate/sign_in.hpp"
#include "agentspeed/quota/estimate/store_file.hpp"
#include "agentspeed/report_body.hpp"

// QUOTAS 10 in the app: the salt and estimate.json on disk, the sign-in keys, the feeds and remote hosts' accounts, and
// the `quotas` section of a report (REPORTING-EXTENSIONS 11).
namespace {

namespace fs = std::filesystem;
namespace est = agentspeed::quota::estimate;
using agentspeed::quota::Kind;
using agentspeed::quota::makeWindow;

constexpr std::int64_t kMin = 60'000, kHour = 60 * kMin, kDay = 24 * kHour;

std::int64_t nowMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count();
}

class Folder {
public:
    Folder() : path_(fs::temp_directory_path() / ("as-estimate-" + std::to_string(::getpid()) + "-" + std::to_string(counter_++))) {
        fs::remove_all(path_);
    }
    ~Folder() { fs::remove_all(path_); }
    std::string quota() const { return (path_ / "data/quota").string(); }

private:
    static inline int counter_ = 0;
    fs::path path_;
};

mode_t modeOf(const std::string& path) {
    struct stat st {};
    ::stat(path.c_str(), &st);
    return st.st_mode & 0777;
}

TEST(QuotaEstimator, SaltAndStoreAreOwnerOnlyAndTheSaltStays) {
    Folder folder;
    std::string salt;
    {
        est::Estimator estimator(folder.quota());
        ASSERT_TRUE(estimator.available());
        salt = estimator.saltText();
        EXPECT_EQ(salt.size(), 64u);
        const auto file = folder.quota() + "/estimate-salt";
        EXPECT_EQ(fs::file_size(file), 32u);
        EXPECT_EQ(modeOf(file), 0600u);
        estimator.build(nowMs(), {});
        estimator.save(nowMs(), true);
        const auto store = folder.quota() + "/estimate.json";
        ASSERT_TRUE(fs::exists(store));
        EXPECT_EQ(modeOf(store), 0600u);
        const auto parsed = est::readStore(store);
        ASSERT_TRUE(parsed);
        EXPECT_EQ(parsed->version, 1);
        // No key or id text of the salt is in the store.
        std::ifstream in(store);
        const std::string text((std::istreambuf_iterator<char>(in)), {});
        EXPECT_EQ(text.find(salt), std::string::npos);
    }
    est::Estimator again(folder.quota());
    EXPECT_EQ(again.saltText(), salt);
}

TEST(QuotaEstimator, SignInFilesGiveKeysOnly) {
    const est::Salt salt(32, 7);
    const auto codex = est::signedInAccount(est::AccountSource::Codex, R"({"auth_mode":"chatgpt","tokens":{"access_token":"t","account_id":"acct-1"}})", salt);
    ASSERT_TRUE(codex);
    EXPECT_EQ(codex->key, est::accountKey(salt, "codex", "acct-1"));
    const auto claude = est::signedInAccount(est::AccountSource::ClaudeCode,
                                             R"({"oauthAccount":{"organizationUuid":"org-1","accountUuid":"user-1","organizationType":"claude_max",)"
                                             R"("organizationRateLimitTier":"Default Max"}})",
                                             salt);
    ASSERT_TRUE(claude);
    EXPECT_EQ(claude->key, est::accountKey(salt, "claude", "org-1:user-1"));
    EXPECT_EQ(claude->orgKey, est::accountKey(salt, "claudeDesktop", "org-1"));
    EXPECT_EQ(claude->plan, "claude_max");
    EXPECT_FALSE(claude->tier);  // not plan text
    EXPECT_FALSE(est::signedInAccount(est::AccountSource::Codex, R"({"tokens":{}})", salt));
    EXPECT_EQ(est::desktopOrgKey("https://claude.ai/api/organizations/org-1/usage?x=1", salt), est::accountKey(salt, "claudeDesktop", "org-1"));
    EXPECT_FALSE(est::desktopOrgKey("https://claude.ai/api/usage", salt));
}

// A Codex response on this computer, the token count line that follows it, and its sign-in.
void localCodex(est::Estimator& e, std::int64_t t0, std::int64_t r5, std::int64_t r7, const std::string& key) {
    e.setCodexEnabled(true);
    e.setCodexAccount(t0, est::SignedInAccount{key, {}, std::nullopt, std::nullopt});
    est::Response r;
    r.source = "codex";
    r.file = "agent-a";
    r.startMs = t0 + 10 * kMin;
    r.endMs = t0 + 11 * kMin;
    r.model = "gpt-5.5";
    r.cost = 20'000'000;
    r.tokens = 1000;
    r.provider = "openai";
    e.putLive({{"local\x1fresp-1", r}});
    est::CodexLine line;
    line.file = "agent-a";
    line.ts = r.endMs + 1'000;
    line.windows = {makeWindow(Kind::FiveHour, 10, r5), makeWindow(Kind::SevenDay, 20, r7)};
    line.plan = "pro";
    e.addCodexLine(line);
}

est::SnapshotEvent hostCodex(const std::string& host, std::int64_t at, std::int64_t r7, const std::string& key) {
    est::SnapshotEvent s;
    s.source = "codex";
    s.machine = host;
    s.observedMs = at;
    s.windows = {makeWindow(Kind::SevenDay, 30, r7)};
    s.account = key;
    s.plan = "pro";
    return s;
}

// QUOTAS 10.1 Remote hosts: equal keys are one account unless the fingerprint contradicts it.
TEST(QuotaEstimator, AHostSharesTheLocalAccountOfItsKeyUnlessItsResetsContradict) {
    Folder folder;
    est::Estimator e(folder.quota());
    ASSERT_TRUE(e.available());
    const auto now = nowMs();
    const auto t0 = now - 3 * kHour;
    const auto r5 = t0 + 5 * kHour, r7 = t0 + 6 * kDay;
    const auto key = est::accountKey(e.salt(), "codex", "acct-1");
    localCodex(e, t0, r5, r7, key);
    e.addSnapshot(hostCodex("host-same", t0 + 2 * kHour, r7 + 30'000, key));
    e.addSnapshot(hostCodex("host-other", t0 + 2 * kHour, r7 + 19 * kHour, key));
    const auto shown = e.build(now, {{"local", "codex"}, {"host-same", "codex"}, {"host-other", "codex"}});
    ASSERT_TRUE(shown.contains("codex"));
    ASSERT_TRUE(shown.contains("codex@host-same"));
    ASSERT_TRUE(shown.contains("codex@host-other"));
    EXPECT_EQ(shown.at("codex").front().account, key);
    EXPECT_EQ(shown.at("codex@host-same").front().account, key);
    EXPECT_NE(shown.at("codex@host-other").front().account, key);
    // The shared 7-day window lists one account-window with both values; this computer's spend counts in it.
    const auto& local = shown.at("codex");
    const auto week = std::find_if(local.begin(), local.end(), [](const est::Entry& x) { return x.kind == "sevenDay"; });
    ASSERT_NE(week, local.end());
    EXPECT_EQ(week->usedMicroUsd, 20'000'000);
    EXPECT_EQ(week->plan, "pro");
    // The report takes the shown accounts' entries, at most 16, without keys in its JSON.
    const auto entries = e.entries(now);
    ASSERT_FALSE(entries.empty());
    std::set<std::pair<std::vector<std::string>, std::string>> identities;
    for (const auto& x : entries) {
        EXPECT_EQ(x.json().compact().find(key), std::string::npos);
        // REPORTING-EXTENSIONS 11.4: (sources, window) once, though host-other is an account of its own.
        EXPECT_TRUE(identities.insert({x.sources, x.kind}).second) << x.kind;
    }
}

// History before the cut, the live engine after it: a response is never counted twice.
TEST(QuotaEstimator, HistoryHoldsTheSpendBeforeTheCutAndTheLiveEngineAfterIt) {
    Folder folder;
    est::Estimator e(folder.quota());
    const auto now = nowMs();
    const auto t0 = now - 3 * kHour;
    const auto r7 = t0 + 6 * kDay;
    const auto key = est::accountKey(e.salt(), "codex", "acct-1");
    localCodex(e, t0, t0 + 5 * kHour, r7, key);
    agentspeed::history::EstimateFile history;
    agentspeed::history::EstimateResponse h;
    h.source = agentspeed::SourceKind::Codex;
    h.agentId = "agent-a";
    h.startMs = t0 + 10 * kMin;
    h.endMs = t0 + 11 * kMin;
    h.model = "gpt-5.5";
    h.costMicroUsd = 20'000'000;
    h.tokens = 1000;
    h.provider = "openai";
    history.responses.push_back(h);
    history.lines.push_back({"agent-a", h.endMs + 1'000,
                             R"({"limit_id":"codex","plan_type":"pro","secondary":{"used_percent":20,"window_minutes":10080,"resets_at":)" +
                                 std::to_string(r7 / 1000) + "}}"});
    e.setHistory(history, t0 + 30 * kMin);
    const auto shown = e.build(now, {{"local", "codex"}});
    ASSERT_TRUE(shown.contains("codex"));
    for (const auto& x : shown.at("codex")) EXPECT_EQ(x.usedMicroUsd, 20'000'000) << x.kind;
}

TEST(QuotaEstimatorReport, QuotasFollowPerfScoreAndGoFirstWhenTooLarge) {
    using agentspeed::report::Json;
    agentspeed::report::Envelope envelope;
    agentspeed::report::Options options;
    options.quotas = std::vector<Json>{Json::object({{"source", Json::of("codex")}})};
    const auto fitted = agentspeed::report::fitted(envelope, {}, {}, options);
    EXPECT_FALSE(fitted.quotasDropped);
    EXPECT_NE(fitted.data.find(R"("quotas":[{"source":"codex"}])"), std::string::npos);
    options.quotas = std::vector<Json>{Json::object({{"padding", Json::of(std::string(20'000, 'x'))}})};
    const auto dropped = agentspeed::report::fitted(envelope, {}, {}, options);
    EXPECT_TRUE(dropped.quotasDropped);
    EXPECT_EQ(dropped.data.find("quotas"), std::string::npos);
    options.quotas.reset();
    EXPECT_EQ(agentspeed::report::fitted(envelope, {}, {}, options).data.find("quotas"), std::string::npos);
}

}  // namespace
