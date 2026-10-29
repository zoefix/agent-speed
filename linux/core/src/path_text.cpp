#include "agentspeed/path_text.hpp"

namespace agentspeed::path_text {

namespace {
bool isSeparator(char c) { return c == '/' || c == '\\'; }
}

std::string basename(std::string_view path) {
    std::size_t end = path.size();
    while (end > 0 && isSeparator(path[end - 1])) --end;
    if (end == 0) return {};
    std::size_t start = end;
    while (start > 0 && !isSeparator(path[start - 1])) --start;
    return std::string(path.substr(start, end - start));
}

bool hasDirectoryComponent(std::string_view path, std::string_view name) {
    std::size_t last = std::string_view::npos;
    for (std::size_t i = path.size(); i > 0; --i) {
        if (isSeparator(path[i - 1])) {
            last = i - 1;
            break;
        }
    }
    if (last == std::string_view::npos) return false;
    std::size_t start = 0;
    for (std::size_t i = 0; i <= last; ++i) {
        if (i == last || isSeparator(path[i])) {
            if (path.substr(start, i - start) == name) return true;
            start = i + 1;
        }
    }
    return false;
}

}  // namespace agentspeed::path_text
