#include "agentspeed/text.hpp"

namespace agentspeed::text {

std::uint32_t nextCodePoint(std::string_view s, std::size_t& i) {
    const auto b0 = static_cast<unsigned char>(s[i]);
    if (b0 < 0x80) {
        ++i;
        return b0;
    }
    int length = 0;
    std::uint32_t cp = 0;
    if ((b0 & 0xE0) == 0xC0) {
        length = 2;
        cp = b0 & 0x1F;
    } else if ((b0 & 0xF0) == 0xE0) {
        length = 3;
        cp = b0 & 0x0F;
    } else if ((b0 & 0xF8) == 0xF0) {
        length = 4;
        cp = b0 & 0x07;
    } else {
        ++i;
        return 0xFFFD;
    }
    if (i + static_cast<std::size_t>(length) > s.size()) {
        ++i;
        return 0xFFFD;
    }
    for (int k = 1; k < length; ++k) {
        const auto b = static_cast<unsigned char>(s[i + static_cast<std::size_t>(k)]);
        if ((b & 0xC0) != 0x80) {
            ++i;
            return 0xFFFD;
        }
        cp = (cp << 6) | (b & 0x3F);
    }
    i += static_cast<std::size_t>(length);
    return cp;
}

bool isPythonSpace(std::uint32_t cp) {
    return (cp >= 0x09 && cp <= 0x0D) || (cp >= 0x1C && cp <= 0x20) || cp == 0x85 || cp == 0xA0 || cp == 0x1680 ||
           (cp >= 0x2000 && cp <= 0x200A) || cp == 0x2028 || cp == 0x2029 || cp == 0x202F || cp == 0x205F ||
           cp == 0x3000;
}

bool hasVisibleText(std::string_view s) {
    for (std::size_t i = 0; i < s.size();) {
        if (!isPythonSpace(nextCodePoint(s, i))) return true;
    }
    return false;
}

std::string strip(std::string_view s) {
    std::size_t start = 0, end = s.size();
    while (start < s.size()) {
        std::size_t next = start;
        if (!isPythonSpace(nextCodePoint(s, next))) break;
        start = next;
    }
    // Trailing: walk code points from the start, remembering the end of the last visible one.
    std::size_t lastVisibleEnd = start;
    for (std::size_t i = start; i < s.size();) {
        const auto cp = nextCodePoint(s, i);
        if (!isPythonSpace(cp)) lastVisibleEnd = i;
    }
    end = lastVisibleEnd;
    return std::string(s.substr(start, end - start));
}

}  // namespace agentspeed::text
