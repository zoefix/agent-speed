#pragma once

#include <string>
#include <string_view>

namespace agentspeed::path_text {

// The last path component, with '/' and '\' both separators and trailing separators ignored.
std::string basename(std::string_view path);
// True when a directory component (not the file name) equals `name`.
bool hasDirectoryComponent(std::string_view path, std::string_view name);

}  // namespace agentspeed::path_text
