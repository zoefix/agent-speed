#pragma once

#include <optional>
#include <string>
#include <string_view>

#include "agentspeed/quota/estimate/accounts.hpp"
#include "agentspeed/quota/estimate/engine.hpp"

// QUOTAS 10.1 and 10.4 on disk: `<data>/quota/estimate-salt` (32 random bytes) and `<data>/quota/estimate.json`, both
// 0600, written to a temporary file and renamed. Neither the salt nor a key is ever logged; ids are never stored.
namespace agentspeed::quota::estimate {

std::string storeJson(const Store& store);
// nullopt when the text is not a store document (a file that does not parse is replaced); any version is returned.
std::optional<Store> parseStore(std::string_view text);

// The salt of `folder` (`<data>/quota`), created when missing or not 32 bytes; nullopt when it can be neither read nor
// written.
std::optional<Salt> loadOrCreateSalt(const std::string& folder);
std::optional<Store> readStore(const std::string& file);
bool writeStore(const std::string& file, const Store& store);

inline constexpr std::string_view kSaltFileName = "estimate-salt";
inline constexpr std::string_view kStoreFileName = "estimate.json";

}  // namespace agentspeed::quota::estimate
