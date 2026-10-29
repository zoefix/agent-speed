#pragma once

#include <cstdint>
#include <string>
#include <string_view>

// Unicode helpers over UTF-8 text.
namespace agentspeed::text {

// Decodes one code point at `i` (advancing it); invalid bytes decode as U+FFFD one byte at a time.
std::uint32_t nextCodePoint(std::string_view s, std::size_t& i);
// Python's str.isspace() code points (Gemini's own `trim()` agrees for these).
bool isPythonSpace(std::uint32_t cp);
// True when the text has a code point outside isPythonSpace.
bool hasVisibleText(std::string_view s);
std::string strip(std::string_view s);
// Bytes as UTF-8 with invalid sequences replaced by U+FFFD.
std::string sanitizeUtf8(std::string_view bytes);
void appendUtf8(std::string& out, std::uint32_t cp);

}  // namespace agentspeed::text
