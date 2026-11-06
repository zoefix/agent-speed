#include <algorithm>
#include <cctype>

#include "agentspeed/remote_ssh.hpp"

// REMOTE 4.2 step 1: what the probe printed, and the hash line of the upload.
namespace agentspeed::remote {

namespace {

// Printable ASCII only, at most 64 characters, trimmed.
std::string displayable(std::string_view text) {
    std::string out;
    for (const char c : text) {
        if (out.size() >= 64) break;
        if (c >= 0x20 && c <= 0x7E) out += c;
    }
    const auto first = out.find_first_not_of(' ');
    if (first == std::string::npos) return {};
    return out.substr(first, out.find_last_not_of(' ') - first + 1);
}

std::vector<std::string_view> linesOf(std::string_view output) {
    std::vector<std::string_view> lines;
    output = output.substr(0, ProbeResult::kMaxOutputBytes);
    while (!output.empty()) {
        const auto end = output.find_first_of("\r\n");
        if (end != 0) lines.push_back(output.substr(0, end));
        if (end == std::string_view::npos) break;
        output.remove_prefix(end + 1);
    }
    return lines;
}

std::string lower(std::string_view text) {
    std::string out(text);
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return out;
}

}  // namespace

ProbeResult ProbeResult::parse(std::string_view output) {
    ProbeResult result;
    for (const auto line : linesOf(output)) {
        if (line.starts_with("system ")) {
            result.system = displayable(line.substr(7));
            std::vector<std::string> words;
            std::string_view rest = result.system;
            while (!rest.empty()) {
                const auto space = rest.find(' ');
                if (space != 0) words.emplace_back(rest.substr(0, space));
                if (space == std::string_view::npos) break;
                rest.remove_prefix(space + 1);
            }
            result.kernel = words.empty() ? std::string() : words.front();
            result.machine = words.size() > 1 ? words.back() : std::string();
        } else if (line.starts_with("tool ")) {
            const auto tool = line.substr(5);
            result.hashTool = tool == "none" ? std::nullopt : std::optional<std::string>(displayable(tool));
        } else if (line.starts_with("hash ")) {
            const auto rest = line.substr(5);
            const auto space = rest.find(' ');
            if (space == std::string_view::npos) continue;
            const auto digest = lower(rest.substr(0, space));
            const auto name = std::string(rest.substr(space + 1));
            if (command::isDigest(digest) && relay_name::isSafe(name) && name.starts_with(relay_name::kInstalledPrefix)) {
                result.hashes[name] = digest;
            }
        } else if (line == "end") {
            result.complete = true;
        }
    }
    return result;
}

ProbeResult::Verdict ProbeResult::verdict() const {
    if (!complete || kernel.empty()) return Verdict::Malformed;
    if (kernel != "Linux" || !arch()) return Verdict::UnsupportedSystem;
    if (!hashTool) return Verdict::CannotVerify;
    return Verdict::Supported;
}

bool ProbeResult::hasInstalled(const std::string& name, std::string_view sha256) const {
    const auto it = hashes.find(name);
    return it != hashes.end() && it->second == lower(sha256);
}

std::optional<std::string> ProbeResult::uploadedHash(std::string_view output) {
    for (const auto line : linesOf(output)) {
        if (!line.starts_with("hash ")) continue;
        auto digest = lower(line.substr(5));
        if (command::isDigest(digest)) return digest;
    }
    return std::nullopt;
}

}  // namespace agentspeed::remote
