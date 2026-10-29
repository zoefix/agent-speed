#include <openssl/rand.h>
#include <unicode/normalizer2.h>
#include <unicode/uchar.h>
#include <unicode/unistr.h>

#include <vector>

#include "agentspeed/report.hpp"
#include "unicode.hpp"

namespace agentspeed::report {

namespace {

constexpr int kMaxMarksInARow = 4;
constexpr std::u32string_view kApostrophes = U"'‘’ʼ`";
constexpr std::string_view kSafeAlphabet = "23456789abcdefghjkmnpqrstuvwxyz";

icu::UnicodeString unicode(std::string_view text) {
    return icu::UnicodeString::fromUTF8(icu::StringPiece(text.data(), static_cast<std::int32_t>(text.size())));
}

std::string utf8(const icu::UnicodeString& text) {
    std::string out;
    text.toUTF8String(out);
    return out;
}

std::u32string codePoints(const icu::UnicodeString& text) {
    std::u32string out;
    for (std::int32_t i = 0; i < text.length(); i = text.moveIndex32(i, 1)) out += static_cast<char32_t>(text.char32At(i));
    return out;
}

icu::UnicodeString fromPoints(std::u32string_view text) {
    icu::UnicodeString out;
    for (const auto c : text) out.append(static_cast<UChar32>(c));
    return out;
}

icu::UnicodeString normalized(const icu::UnicodeString& text, bool compatibilityDecomposed = false) {
    UErrorCode status = U_ZERO_ERROR;
    const auto* normalizer = compatibilityDecomposed ? icu::Normalizer2::getNFKDInstance(status) : icu::Normalizer2::getNFKCInstance(status);
    if (U_FAILURE(status)) return text;
    auto out = normalizer->normalize(text, status);
    return U_FAILURE(status) ? text : out;
}

bool letter(char32_t c) { return (U_GET_GC_MASK(static_cast<UChar32>(c)) & U_GC_L_MASK) != 0; }
bool mark(char32_t c) { return (U_GET_GC_MASK(static_cast<UChar32>(c)) & U_GC_M_MASK) != 0; }
bool number(char32_t c) { return (U_GET_GC_MASK(static_cast<UChar32>(c)) & U_GC_N_MASK) != 0; }
bool asciiAlnum(char32_t c) { return (c >= U'0' && c <= U'9') || (c >= U'a' && c <= U'z') || (c >= U'A' && c <= U'Z'); }

bool letterOrNumber(char32_t c, const UsernameRules& rules) { return rules.ascii ? asciiAlnum(c) : letter(c) || number(c); }

bool allowed(char32_t c, const UsernameRules& rules) {
    if (c == U' ') return rules.spaces;
    if (c == U'_' || c == U'-' || c == U'.') return true;
    return rules.ascii ? asciiAlnum(c) : letter(c) || mark(c) || number(c);
}

}  // namespace

namespace detail {

bool pythonSpace(char32_t c) {
    const auto direction = u_charDirection(static_cast<UChar32>(c));
    return u_charType(static_cast<UChar32>(c)) == U_SPACE_SEPARATOR || direction == U_WHITE_SPACE_NEUTRAL || direction == U_BLOCK_SEPARATOR ||
           direction == U_SEGMENT_SEPARATOR;
}

std::u32string points(std::string_view text) { return codePoints(unicode(text)); }

std::string utf8(std::u32string_view text) { return report::utf8(fromPoints(text)); }

std::string plainText(std::string_view text, std::size_t limit) {
    std::u32string kept;
    for (const auto c : points(text)) {
        const auto type = u_charType(static_cast<UChar32>(c));
        if ((type != U_CONTROL_CHAR && type != U_FORMAT_CHAR) || detail::pythonSpace(c)) kept += c;
    }
    std::u32string joined;
    for (std::size_t i = 0; i < kept.size();) {
        while (i < kept.size() && pythonSpace(kept[i])) ++i;
        if (i == kept.size()) break;
        if (!joined.empty()) joined += U' ';
        while (i < kept.size() && !pythonSpace(kept[i])) joined += kept[i++];
    }
    joined = joined.substr(0, limit);
    while (!joined.empty() && pythonSpace(joined.back())) joined.pop_back();
    return utf8(joined);
}

}  // namespace detail

std::optional<std::string> validateUsername(std::string_view name, const UsernameRules& rules) {
    const auto text = unicode(name);
    if (utf8(text) != name || normalized(text) != text) return "characters";
    const auto chars = codePoints(text);
    if (static_cast<int>(chars.size()) < rules.minLength || static_cast<int>(chars.size()) > rules.maxLength) return "length";
    int marks = 0;
    for (const auto c : chars) {
        if (detail::pythonSpace(c) && c != U' ') return "spaces";
        if (!allowed(c, rules)) return c == U' ' ? "spaces" : "characters";
        marks = mark(c) ? marks + 1 : 0;
        if (marks > kMaxMarksInARow) return "characters";
    }
    if (!letterOrNumber(chars.front(), rules)) return "start";
    if (chars.back() == U' ' || chars.find(U"  ") != std::u32string::npos) return "spaces";
    return std::nullopt;
}

std::optional<std::string> fitUsername(std::string_view computerName, const UsernameRules& rules) {
    std::u32string text;
    for (const auto c : codePoints(normalized(unicode(computerName))))
        if (kApostrophes.find(c) == std::u32string_view::npos) text += c;
    if (rules.ascii) {
        std::u32string kept;
        for (const auto c : codePoints(normalized(fromPoints(text), true)))
            if (!mark(c)) kept += c;
        text = kept;
    }
    const char32_t separator = rules.spaces ? U' ' : U'-';
    for (auto& c : text)
        if (detail::pythonSpace(c) || !allowed(c, rules)) c = separator;
    // Runs of spaces and hyphens that hold a hyphen become one hyphen; other runs of spaces become one space.
    std::u32string joined;
    for (std::size_t i = 0; i < text.size();) {
        if (text[i] != U' ' && text[i] != U'-') {
            joined += text[i++];
            continue;
        }
        const auto start = i;
        while (i < text.size() && (text[i] == U' ' || text[i] == U'-')) ++i;
        const auto run = std::u32string_view(text).substr(start, i - start);
        joined += run.find(U'-') != std::u32string_view::npos ? std::u32string(U"-") : run.size() >= 2 ? std::u32string(U" ") : std::u32string(run);
    }
    std::size_t first = 0;
    while (first < joined.size() && !letterOrNumber(joined[first], rules)) ++first;
    joined = joined.substr(first, static_cast<std::size_t>(rules.maxLength));
    while (!joined.empty() && std::u32string_view(U" -_.").find(joined.back()) != std::u32string_view::npos) joined.pop_back();
    auto result = utf8(fromPoints(joined));
    if (validateUsername(result, rules)) return std::nullopt;
    return result;
}

std::string randomUsername() {
    std::string out = "racer-";
    while (out.size() < 12) {
        unsigned char byte = 0;
        RAND_bytes(&byte, 1);
        if (byte < 248) out += kSafeAlphabet[byte % kSafeAlphabet.size()];
    }
    return out;
}

std::string usernameKey(std::string_view name) {
    auto folded = normalized(unicode(name));
    folded.foldCase(U_FOLD_CASE_DEFAULT);
    return utf8(normalized(folded));
}

}  // namespace agentspeed::report
