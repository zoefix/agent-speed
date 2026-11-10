#include "agentspeed/quota/routes/credentials.hpp"

#include "agentspeed/json.hpp"
#include "agentspeed/quota/vendors/parsers.hpp"
#include "agentspeed/quota/vendors/values.hpp"

namespace agentspeed::quota {

namespace {

std::optional<Token> tokenOf(const json::Value& object, std::initializer_list<std::string_view> names,
                             std::optional<std::int64_t> expires) {
    for (const auto name : names) {
        if (auto value = object[name].nonEmptyString()) return Token{Secret(std::move(*value)), expires};
    }
    return std::nullopt;
}

std::string_view trimmed(std::string_view text) {
    const auto first = text.find_first_not_of(" \t\r\n");
    if (first == std::string_view::npos) return {};
    return text.substr(first, text.find_last_not_of(" \t\r\n") - first + 1);
}

}  // namespace

std::optional<Token> grokToken(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject() || doc.root().size() == 0) return std::nullopt;
    const auto entry = doc.root().members().front().second;
    if (!entry.isObject()) return std::nullopt;
    std::optional<std::int64_t> expires;
    if (const auto at = entry["expires_at"]; at.isString()) expires = vendors::isoMs(at.string().value_or(""));
    return tokenOf(entry, {"key", "access_token"}, expires);
}

std::optional<Token> kimiToken(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    std::optional<std::int64_t> expires;
    if (const auto at = vendors::number(root["expires_at"])) expires = vendors::epochMs(*at);
    return tokenOf(root, {"access_token"}, expires);
}

std::optional<Token> geminiToken(std::string_view text) {
    json::Document doc;
    if (!doc.parse(text) || !doc.root().isObject()) return std::nullopt;
    const auto root = doc.root();
    std::optional<std::int64_t> expires;
    if (const auto at = vendors::number(root["expiry_date"])) expires = vendors::truncated(*at);
    return tokenOf(root, {"access_token"}, expires);
}

std::string grokCredentialFile(const VendorEnvironment& env) {
    return fsx::join(env.folder("GROK_HOME").value_or(env.inHome(".grok")), "auth.json");
}

std::optional<std::string> kimiCredentialFile(const VendorEnvironment& env) {
    std::vector<std::string> candidates;
    if (auto home = env.folder("KIMI_CODE_HOME")) candidates.push_back(*home);
    candidates.push_back(env.inHome(".kimi-code"));
    candidates.push_back(env.inHome(".kimi"));
    for (const auto& folder : candidates) {
        const auto file = fsx::join(folder, "credentials/kimi-code.json");
        if (const auto info = env.fs().stat(file); info && info->regular) return file;
    }
    return std::nullopt;
}

std::string geminiCredentialFile(const VendorEnvironment& env) {
    return fsx::join(env.folder("GEMINI_CLI_HOME").value_or(env.home), ".gemini/oauth_creds.json");
}

std::string droidEnvFile(const VendorEnvironment& env) { return env.inHome(".factory/.env"); }

std::optional<Token> readToken(Files& files, const std::string& path, std::optional<Token> (*parse)(std::string_view)) {
    auto text = files.read(path, kCredentialFileBytes);
    if (!text) return std::nullopt;
    auto token = parse(*text);
    wipe(*text);
    return token;
}

std::optional<Secret> droidKey(const VendorEnvironment& env) {
    if (auto value = env.get("FACTORY_API_KEY")) {
        const auto key = trimmed(*value);
        std::optional<Secret> out;
        if (!key.empty()) out = Secret(std::string(key));
        wipe(*value);
        if (out) return out;
    }
    auto text = env.fs().read(droidEnvFile(env), kCredentialFileBytes);
    if (!text) return std::nullopt;
    auto key = vendors::droidKeyFromEnvFile(*text);
    wipe(*text);
    if (!key) return std::nullopt;
    return Secret(std::move(*key));
}

}  // namespace agentspeed::quota
