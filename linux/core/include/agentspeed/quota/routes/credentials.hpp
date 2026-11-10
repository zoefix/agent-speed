#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/routes/fingerprint.hpp"

// QUOTAS 9.2, 9.5-9.7: the agents' own sign-ins. Each file is read right before a request, only its named members are
// decoded (a refresh token is never read), and nothing of it is kept, written or logged.
namespace agentspeed::quota {

struct Token {
    Secret value;
    std::optional<std::int64_t> expiresAtMs;  // nullopt when the file names none
    bool expired(std::int64_t nowMs, std::int64_t marginMs = 0) const {
        return expiresAtMs && nowMs >= *expiresAtMs - marginMs;
    }
};

// The parsers, over the file's text.
// Grok: the first entry of `auth.json` (an object keyed by scope URLs): its `key`, else `access_token`, with
// `expires_at` (ISO 8601).
std::optional<Token> grokToken(std::string_view text);
// Kimi: `access_token` with `expires_at` in seconds (above 1e11, milliseconds).
std::optional<Token> kimiToken(std::string_view text);
// Gemini: `access_token` with `expiry_date` in milliseconds.
std::optional<Token> geminiToken(std::string_view text);

// Where they are. Kimi's is the first that exists of `$KIMI_CODE_HOME/credentials/kimi-code.json`,
// `~/.kimi-code/credentials/kimi-code.json` and the legacy `~/.kimi/credentials/kimi-code.json`.
std::string grokCredentialFile(const VendorEnvironment& env);
std::optional<std::string> kimiCredentialFile(const VendorEnvironment& env);
std::string geminiCredentialFile(const VendorEnvironment& env);
std::string droidEnvFile(const VendorEnvironment& env);

constexpr std::size_t kCredentialFileBytes = 1 << 20;

// Reads the file and parses it; its text is wiped right after.
std::optional<Token> readToken(Files& files, const std::string& path, std::optional<Token> (*parse)(std::string_view));
// `FACTORY_API_KEY` from the environment, else that one line of `~/.factory/.env` (`settings.json` is never read).
std::optional<Secret> droidKey(const VendorEnvironment& env);

}  // namespace agentspeed::quota
