#pragma once

#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Python `re` compatible regular expressions for the failure parsers: PCRE2 in UTF mode (lookbehind, lazy
// quantifiers, code-point classes), compiled on first use.
namespace agentspeed::tasks::rx {

enum Flags : unsigned { kNone = 0, kFull = 1, kCaseless = 2 };

struct Match {
    std::vector<std::optional<std::string>> groups;  // [0] the whole match
    std::size_t start = 0, end = 0;
    explicit operator bool() const { return !groups.empty(); }
    std::string group(std::size_t i) const { return i < groups.size() && groups[i] ? *groups[i] : std::string(); }
    bool has(std::size_t i) const { return i < groups.size() && groups[i].has_value(); }
};

class Regex {
public:
    // kFull compiles the pattern anchored at both ends, for patterns that are only ever full-matched.
    explicit Regex(std::string pattern, unsigned flags = kNone);
    ~Regex();
    Regex(const Regex&) = delete;
    Regex& operator=(const Regex&) = delete;

    Match fullmatch(std::string_view text) const;
    Match match(std::string_view text) const;
    Match search(std::string_view text, std::size_t at = 0) const;
    // re.sub: every non-overlapping match, left to right, replaced by `replace(match)`.
    std::string sub(std::string_view text, const std::function<std::string(const Match&)>& replace) const;
    // re.sub with a template that may refer to groups as \1 … \9.
    std::string sub(std::string_view text, std::string_view replacement) const;

private:
    void* code() const;
    Match run(std::string_view text, std::size_t at, unsigned options) const;

    std::string pattern_;
    unsigned flags_;
    mutable std::once_flag once_;
    mutable void* code_ = nullptr;
};

}  // namespace agentspeed::tasks::rx
