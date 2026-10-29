#define PCRE2_CODE_UNIT_WIDTH 8
#include "pcre.hpp"

#include <pcre2.h>

#include <algorithm>
#include <stdexcept>

namespace agentspeed::tasks::rx {

Regex::Regex(std::string pattern, unsigned flags) : pattern_(std::move(pattern)), flags_(flags) {}

Regex::~Regex() { pcre2_code_free(static_cast<pcre2_code*>(code_)); }

void* Regex::code() const {
    std::call_once(once_, [this] {
        int error = 0;
        PCRE2_SIZE offset = 0;
        std::uint32_t options = PCRE2_UTF;
        if (flags_ & kFull) options |= PCRE2_ANCHORED | PCRE2_ENDANCHORED;
        if (flags_ & kCaseless) options |= PCRE2_CASELESS;
        auto* compiled = pcre2_compile(reinterpret_cast<PCRE2_SPTR>(pattern_.data()), pattern_.size(), options, &error, &offset, nullptr);
        if (!compiled) throw std::runtime_error("invalid pattern at offset " + std::to_string(offset));
        pcre2_jit_compile(compiled, PCRE2_JIT_COMPLETE);
        code_ = compiled;
    });
    return code_;
}

Match Regex::run(std::string_view text, std::size_t at, unsigned options) const {
    auto* compiled = static_cast<pcre2_code*>(code());
    pcre2_match_data* data = pcre2_match_data_create_from_pattern(compiled, nullptr);
    const int rc = pcre2_match(compiled, reinterpret_cast<PCRE2_SPTR>(text.data()), text.size(), at, options, data, nullptr);
    Match m;
    if (rc > 0) {
        std::uint32_t captures = 0;
        pcre2_pattern_info(compiled, PCRE2_INFO_CAPTURECOUNT, &captures);
        const auto* ovector = pcre2_get_ovector_pointer(data);
        for (std::uint32_t i = 0; i <= captures; ++i) {
            if (i >= static_cast<std::uint32_t>(rc) || ovector[2 * i] == PCRE2_UNSET) {
                m.groups.emplace_back(std::nullopt);
            } else {
                m.groups.emplace_back(std::string(text.substr(ovector[2 * i], ovector[2 * i + 1] - ovector[2 * i])));
            }
        }
        m.start = ovector[0];
        m.end = ovector[1];
    }
    pcre2_match_data_free(data);
    return m;
}

Match Regex::fullmatch(std::string_view text) const { return run(text, 0, PCRE2_ANCHORED | PCRE2_ENDANCHORED); }
Match Regex::match(std::string_view text) const { return run(text, 0, PCRE2_ANCHORED); }
Match Regex::search(std::string_view text, std::size_t at) const { return run(text, at, 0); }

std::string Regex::sub(std::string_view text, const std::function<std::string(const Match&)>& replace) const {
    std::string out;
    std::size_t at = 0, copied = 0;
    unsigned options = 0;  // the subject's UTF-8 is checked by the first search only
    while (at <= text.size()) {
        const auto m = run(text, at, options);
        if (!m) break;
        options = PCRE2_NO_UTF_CHECK;
        out.append(text.substr(copied, m.start - copied));
        out += replace(m);
        copied = m.end;
        if (m.end > m.start) {
            at = m.end;
            continue;
        }
        if (m.end >= text.size()) break;
        std::size_t next = m.end + 1;
        while (next < text.size() && (static_cast<unsigned char>(text[next]) & 0xC0) == 0x80) ++next;
        out.append(text.substr(m.end, next - m.end));
        copied = at = next;
    }
    out.append(text.substr(std::min(copied, text.size())));
    return out;
}

std::string Regex::sub(std::string_view text, std::string_view replacement) const {
    return sub(text, [&](const Match& m) {
        std::string out;
        for (std::size_t i = 0; i < replacement.size(); ++i) {
            if (replacement[i] == '\\' && i + 1 < replacement.size() && replacement[i + 1] >= '1' && replacement[i + 1] <= '9') {
                out += m.group(static_cast<std::size_t>(replacement[++i] - '0'));
            } else {
                out += replacement[i];
            }
        }
        return out;
    });
}

}  // namespace agentspeed::tasks::rx
