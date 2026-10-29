#pragma once

#include <string>
#include <string_view>

namespace agentspeed::report::detail {

// Python's str.isspace, which is also what `\s` matches in a Python str pattern.
bool pythonSpace(char32_t c);
std::u32string points(std::string_view text);
std::string utf8(std::u32string_view text);
// REPORTING 2.3: control and format characters removed, white space collapsed, at most `limit` characters.
std::string plainText(std::string_view text, std::size_t limit);

}  // namespace agentspeed::report::detail
