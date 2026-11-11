#include <gtest/gtest.h>

#include "fixture_support.hpp"
#include "protocol.hpp"
#include "quota_accounts.hpp"
#include "quota_vendor_support.hpp"

// QUOTAS 10.1 and 10.2 on a host: the account keys against the fixture's HMACs, the plan and tier cleaning, the
// sign-in files read for their ids and nothing else, and Codex's plan carried over a null `plan_type`.
using namespace agentspeed;
using namespace agentspeed::relay;

namespace {

json::Value accounts() {
    static const fixtures::JsonFile file(fixtures::fixtureDir() / "quota-estimate-cases.json");
    return file.root()["accounts"];
}

Salt fixtureSalt() { return *saltOf(*accounts()["salt"].string()); }

// The fixture's key of (vendor, id).
std::string fixtureKey(const std::string& vendor, const std::string& id) {
    for (const auto& k : accounts()["keys"].elements()) {
        if (*k["vendor"].string() == vendor && *k["id"].string() == id) return *k["expected"].string();
    }
    ADD_FAILURE() << "no fixture key for " << vendor << ":" << id;
    return {};
}

// A document with the text it points into.
struct Parsed {
    std::string text;
    json::Document doc;
    explicit Parsed(std::string t) : text(std::move(t)) { EXPECT_TRUE(doc.parse(text)) << text; }
    Parsed(const Parsed&) = delete;
    Parsed& operator=(const Parsed&) = delete;
    json::Value root() const { return doc.root(); }
};

std::string rateLimits(const std::string& plan) {
    return R"({"limit_id":"codex","plan_type":)" + plan + R"(,"primary":{"used_percent":23.0,"window_minutes":10080,"resets_at":1794897479}})";
}

}  // namespace

TEST(RelayQuotaAccounts, KeysEqualTheFixtures) {
    const auto keys = accounts()["keys"];
    ASSERT_GT(keys.size(), 0u);
    for (const auto& k : keys.elements()) {
        const auto key = accountKey(fixtureSalt(), *k["vendor"].string(), *k["id"].string());
        ASSERT_TRUE(key);
        EXPECT_EQ(*key, *k["expected"].string()) << *k["vendor"].string();
        EXPECT_TRUE(isAccountKey(*key));
    }
    Salt other = fixtureSalt();
    other[31] ^= 1;
    EXPECT_NE(accountKey(other, "codex", "acct-1"), accountKey(fixtureSalt(), "codex", "acct-1"));
}

TEST(RelayQuotaAccounts, PlansAndTiersAreCleaned) {
    for (const auto* plan : {"claude_max", "default_claude_max_20x", "pro", "plus.2", "a-b", "0"}) EXPECT_TRUE(isPlanText(plan)) << plan;
    EXPECT_TRUE(isPlanText(std::string(64, 'p')));
    for (const auto* plan : {"", "Pro", "claude max", "pro\n", "pro/1", "\xC3\xA9"}) EXPECT_FALSE(isPlanText(plan)) << plan;
    EXPECT_FALSE(isPlanText(std::string(65, 'p')));
    EXPECT_FALSE(isAccountKey("5CDBE01CE30740BC"));
    EXPECT_FALSE(isAccountKey("5cdbe01ce30740b"));

    const Parsed doc(R"({"a":"promax","b":null,"c":7,"d":"Team Plan","e":")" + std::string(1, '\\') + R"(u0070ro"})");
    EXPECT_EQ(cleanPlan(doc.root()["a"]), "promax");
    EXPECT_FALSE(cleanPlan(doc.root()["b"]));
    EXPECT_FALSE(cleanPlan(doc.root()["c"]));
    EXPECT_FALSE(cleanPlan(doc.root()["d"]));
    EXPECT_EQ(cleanPlan(doc.root()["e"]), "pro");
    EXPECT_FALSE(cleanPlan(doc.root()["missing"]));
}

TEST(RelayQuotaAccounts, CodexKeyFromTheAuthFileOnly) {
    const auto salt = fixtureSalt();
    const std::string auth =
        R"({"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{"id_token":"never","access_token":"never","refresh_token":"never",)"
        R"("account_id":"acct-1"},"last_refresh":"2026-11-10T00:00:00Z"})";
    auto facts = accountFacts(AccountKind::Codex, auth, salt);
    EXPECT_EQ(facts.account, fixtureKey("codex", "acct-1"));
    EXPECT_FALSE(facts.plan);
    EXPECT_FALSE(facts.tier);
    ASSERT_TRUE(facts.identity);
    EXPECT_NE(facts.identity, facts.account);
    // No salt: no key, but the same account.
    auto unsalted = accountFacts(AccountKind::Codex, auth, std::nullopt);
    EXPECT_FALSE(unsalted.account);
    EXPECT_EQ(unsalted.identity, facts.identity);
    EXPECT_NE(accountFacts(AccountKind::Codex, R"({"tokens":{"account_id":"acct-2"}})", salt).identity, facts.identity);
    // No id or no file that parses: nothing.
    EXPECT_EQ(accountFacts(AccountKind::Codex, R"({"auth_mode":"chatgpt","tokens":{"account_id":""}})", salt), AccountFacts{});
    EXPECT_EQ(accountFacts(AccountKind::Codex, R"({"auth_mode":"apikey","OPENAI_API_KEY":"sk-never"})", salt), AccountFacts{});
    EXPECT_EQ(accountFacts(AccountKind::Codex, R"({"tokens":{"account_id":7}})", salt), AccountFacts{});
    EXPECT_EQ(accountFacts(AccountKind::Codex, "{not json", salt), AccountFacts{});
}

TEST(RelayQuotaAccounts, ClaudeCodeKeyPlanAndTierFromTheOauthAccount) {
    const auto salt = fixtureSalt();
    const std::string file = R"({"numStartups":4,"oauthAccount":{"accountUuid":"user-1","emailAddress":"never@example.com",)"
                             R"("organizationUuid":"org-1","organizationType":"claude_max","organizationRateLimitTier":"default_claude_max_20x",)"
                             R"("displayName":"never"},"projects":{}})";
    auto facts = accountFacts(AccountKind::ClaudeCode, file, salt);
    EXPECT_EQ(facts.account, fixtureKey("claude", "org-1:user-1"));
    EXPECT_EQ(facts.plan, "claude_max");
    EXPECT_EQ(facts.tier, "default_claude_max_20x");
    // The plan and tier need no salt; the key needs both uuids.
    const auto identity = facts.identity;
    ASSERT_TRUE(identity);
    facts = accountFacts(AccountKind::ClaudeCode, file, std::nullopt);
    EXPECT_FALSE(facts.account);
    EXPECT_EQ(facts.identity, identity);
    EXPECT_EQ(facts.plan, "claude_max");
    facts = accountFacts(AccountKind::ClaudeCode, R"({"oauthAccount":{"accountUuid":"user-1","organizationType":"Claude Max"}})", salt);
    EXPECT_EQ(facts, AccountFacts{});
    EXPECT_EQ(accountFacts(AccountKind::ClaudeCode, R"({"oauthAccount":"org-1:user-1"})", salt), AccountFacts{});
}

TEST(RelayQuotaAccounts, TheFileIsReadAtMostOnceAMinuteAndWhenItOrTheSaltChanges) {
    quota2_test::TempHome home;
    const auto path = home.write(".codex/auth.json", R"({"tokens":{"account_id":"acct-1"}})");
    const auto salt = fixtureSalt();
    AccountFile file(AccountKind::Codex, path);
    std::int64_t now = 1794338773000;
    file.refresh(now, false, salt);
    EXPECT_EQ(file.reads(), 0);
    file.refresh(now, true, std::nullopt);
    EXPECT_EQ(file.reads(), 1);
    EXPECT_FALSE(file.facts().account);
    EXPECT_TRUE(file.facts().identity);
    // A new salt gives new keys at once.
    file.refresh(now + 1'000, true, salt);
    EXPECT_EQ(file.reads(), 2);
    EXPECT_EQ(file.facts().account, fixtureKey("codex", "acct-1"));
    file.refresh(now + 2'000, true, salt);
    EXPECT_EQ(file.reads(), 2);
    // A changed file waits for the minute, an unchanged one is not read again.
    home.write(".codex/auth.json", R"({"tokens":{"account_id":"acct-2","refresh_token":"never"}})");
    home.touch(".codex/auth.json", 5);
    file.refresh(now + 30'000, true, salt);
    EXPECT_EQ(file.reads(), 2);
    EXPECT_EQ(file.facts().account, fixtureKey("codex", "acct-1"));
    file.refresh(now + 61'000, true, salt);
    EXPECT_EQ(file.reads(), 3);
    EXPECT_EQ(file.facts().account, accountKey(salt, "codex", "acct-2"));
    file.refresh(now + 200'000, true, salt);
    EXPECT_EQ(file.reads(), 3);
    // Gone, or the source switched off: no account.
    std::filesystem::remove(path);
    file.refresh(now + 300'000, true, salt);
    EXPECT_EQ(file.facts(), AccountFacts{});
    home.write(".codex/auth.json", R"({"tokens":{"account_id":"acct-1"}})");
    file.refresh(now + 400'000, true, salt);
    EXPECT_EQ(file.facts().account, fixtureKey("codex", "acct-1"));
    file.refresh(now + 400'000, false, salt);
    EXPECT_EQ(file.facts(), AccountFacts{});
}

TEST(RelayQuotaAccounts, CodexPlanOfTheSnapshotElseTheAccountsLatest) {
    CodexPlans plans;
    const Parsed pro(rateLimits(R"("pro")"));
    const Parsed plus(rateLimits(R"("plus")"));
    const Parsed none(rateLimits("null"));
    const Parsed odd(rateLimits(R"("Pro Max")"));
    const Parsed windowless(R"({"limit_id":"codex","plan_type":"team","primary":null,"secondary":null})");
    const Parsed premium(R"({"limit_id":"premium","plan_type":"team","primary":{"used_percent":1,"window_minutes":300}})");

    // Off (the source's switch): nothing counts.
    plans.observe(pro.root(), 1000);
    EXPECT_FALSE(plans.plan(1000));
    plans.setEnabled(true);
    plans.setAccount("aaaaaaaaaaaaaaaa");
    plans.observe(pro.root(), 2000);
    EXPECT_EQ(plans.plan(2000), "pro");
    // Newer Codex builds write null: the account's latest plan stays.
    plans.observe(none.root(), 3000);
    EXPECT_EQ(plans.plan(3000), "pro");
    // A line without a window or of another bucket says nothing.
    plans.observe(windowless.root(), 4000);
    plans.observe(premium.root(), 4000);
    EXPECT_EQ(plans.plan(3000), "pro");
    // An older line read later (another rollout) is not the snapshot, but it is the account's nearest earlier plan.
    plans.observe(plus.root(), 2500);
    EXPECT_EQ(plans.plan(3000), "plus");
    plans.observe(odd.root(), 2800);
    EXPECT_EQ(plans.plan(3000), "plus");
    plans.observe(pro.root(), 2200);
    EXPECT_EQ(plans.plan(3000), "plus");
    // Another account has no plan of its own yet.
    plans.setAccount("bbbbbbbbbbbbbbbb");
    plans.observe(none.root(), 5000);
    EXPECT_FALSE(plans.plan(5000));
    plans.setAccount("aaaaaaaaaaaaaaaa");
    EXPECT_EQ(plans.plan(5000), "plus");
    plans.setEnabled(false);
    plans.observe(pro.root(), 6000);
    plans.setEnabled(true);
    plans.setAccount("bbbbbbbbbbbbbbbb");
    EXPECT_FALSE(plans.plan(6000));
}
