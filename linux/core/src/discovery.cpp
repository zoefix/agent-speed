#include "agentspeed/discovery.hpp"

#include <algorithm>
#include <deque>
#include <map>

#include "agentspeed/droid.hpp"
#include "agentspeed/fs_util.hpp"
#include "agentspeed/grok_parser.hpp"
#include "agentspeed/path_text.hpp"
#include "agentspeed/sqlite.hpp"

namespace agentspeed::discovery {

namespace {

constexpr std::size_t kMaxScanned = 200'000;

DiscoveredFile fileOf(const fsx::Entry& e) { return {e.path, e.stat.length, e.stat.writeNs}; }

void keepNewest(std::vector<DiscoveredFile>& files, std::size_t limit) {
    if (files.size() <= limit) return;
    std::stable_sort(files.begin(), files.end(), [](const auto& a, const auto& b) { return a.writeNs > b.writeNs; });
    files.resize(limit);
}

std::size_t capFor(std::size_t limit) { return limit == kUnlimited ? kUnlimited : kMaxScanned; }

bool isDshName(std::string_view name) {
    constexpr std::string_view prefix = "session.v";
    if (name.size() < prefix.size() || !fsx::equalsNoCase(name.substr(0, prefix.size()), prefix)) return false;
    auto rest = name.substr(prefix.size());
    std::size_t digits = 0;
    while (digits < rest.size() && rest[digits] >= '0' && rest[digits] <= '9') ++digits;
    rest = rest.substr(digits);
    return digits > 0 && (fsx::equalsNoCase(rest, ".jsonl") || fsx::equalsNoCase(rest, ".jsonl.zstd"));
}

bool isOmpName(std::string_view name) { return fsx::endsWithNoCase(name, ".jsonl") && name.front() != '.'; }
bool isWorkBuddyName(std::string_view name) {
    return name.size() >= 6 && name.substr(name.size() - 6) == ".jsonl" && name.front() != '.';
}
bool isGeminiName(std::string_view name) { return fsx::endsWithNoCase(name, ".jsonl") || fsx::endsWithNoCase(name, ".json"); }

// Breadth-first listing to kMaxDepth folders below the root, links never followed.
template <typename Include, typename Recurse>
std::vector<DiscoveredFile> walk(const std::string& root, std::size_t cap, Include include, Recurse recurse) {
    std::vector<DiscoveredFile> files;
    std::deque<std::pair<std::string, int>> queue{{root, 0}};
    while (!queue.empty()) {
        const auto [folder, depth] = queue.front();
        queue.pop_front();
        for (const auto& e : fsx::list(folder)) {
            if (e.stat.link) continue;
            if (e.stat.directory) {
                if (depth < kMaxDepth && recurse(e)) queue.emplace_back(e.path, depth + 1);
            } else if (e.stat.regular && include(e, depth)) {
                files.push_back(fileOf(e));
                if (files.size() >= cap) return files;
            }
        }
    }
    return files;
}

std::vector<DiscoveredFile> gemini(const std::string& root, std::size_t limit) {
    std::vector<DiscoveredFile> files;
    for (const auto& project : fsx::list(root)) {
        if (!project.stat.directory) continue;
        const auto chats = fsx::join(project.path, "chats");
        if (!fsx::isPlainDirectory(chats)) continue;
        for (const auto& e : fsx::list(chats)) {
            if (e.stat.directory) {
                for (const auto& nested : fsx::list(e.path)) {
                    if (nested.stat.regular && isGeminiName(nested.name)) files.push_back(fileOf(nested));
                }
            } else if (e.stat.regular && isGeminiName(e.name)) {
                files.push_back(fileOf(e));
            }
            if (files.size() >= kMaxScanned) goto done;
        }
    }
done:
    keepNewest(files, limit);
    return files;
}

std::vector<DiscoveredFile> antigravity(const std::string& conversations, std::size_t limit) {
    std::vector<DiscoveredFile> files;
    for (const auto& e : fsx::list(conversations)) {
        if (!e.stat.regular || !paths::isAntigravityDatabaseName(e.name)) continue;
        if (const auto stamp = sqlite::stampOf(e.path)) files.push_back({e.path, stamp->length(), stamp->lastWriteNs()});
    }
    keepNewest(files, limit);
    return files;
}

std::vector<DiscoveredFile> workBuddy(const std::string& root, std::size_t limit) {
    const auto cap = capFor(limit);
    std::vector<DiscoveredFile> files;
    const auto add = [&](const std::string& folder) {
        for (const auto& e : fsx::list(folder)) {
            if (files.size() >= cap) return;
            if (e.stat.regular && isWorkBuddyName(e.name)) files.push_back(fileOf(e));
        }
    };
    for (const auto& slug : fsx::list(root)) {
        if (!slug.stat.directory || slug.name.front() == '.' || slug.name == "cloud") continue;
        add(slug.path);
        for (const auto& session : fsx::list(slug.path)) {
            if (files.size() >= cap) break;
            if (!session.stat.directory) continue;
            const auto agents = fsx::join(session.path, "subagents");
            if (session.name.front() != '.' && fsx::isPlainDirectory(agents)) add(agents);
        }
        if (files.size() >= cap) break;
    }
    keepNewest(files, limit);
    return files;
}

std::vector<DiscoveredFile> harness(const std::string& root, bool kimi, std::size_t limit) {
    const auto cap = limit == kUnlimited ? kUnlimited : kMaxScanned;
    std::size_t visited = 0;
    const auto folders = [&](const std::string& path) {
        std::vector<std::string> out;
        if (const auto s = fsx::statOf(path); !s || s->link) return out;
        for (const auto& e : fsx::list(path)) {
            if (visited >= cap) break;
            ++visited;
            if (e.stat.directory && e.name.front() != '.') out.push_back(e.path);
        }
        return out;
    };
    std::vector<DiscoveredFile> files;
    const auto add = [&](const std::string& path) {
        if (!isAcceptablePath(root, path, kimi ? RootFamily::Kimi : RootFamily::GrokBuild)) return;
        const auto s = fsx::statOf(path, true);
        if (!s || !s->regular) return;
        files.push_back({path, s->length, kimi ? s->writeNs : grokModifiedNs(path, s->writeNs)});
    };
    for (const auto& workspace : folders(root)) {
        for (const auto& session : folders(workspace)) {
            if (kimi) {
                for (const auto& agent : folders(fsx::join(session, "agents"))) add(fsx::join(agent, "wire.jsonl"));
            } else {
                add(fsx::join(session, "updates.jsonl"));
            }
        }
        if (visited >= cap) break;
    }
    keepNewest(files, limit);
    return files;
}

std::vector<DiscoveredFile> droidSessions(const std::string& root, std::size_t limit) {
    const auto cap = capFor(limit);
    std::size_t scanned = 0;
    std::vector<DiscoveredFile> files;
    const auto listFolder = [&](const std::string& folder) {
        std::vector<std::string> folders;
        std::vector<fsx::Entry> transcripts;
        std::map<std::string, std::int64_t> sidecars;
        for (const auto& e : fsx::list(folder)) {
            if (e.stat.link || e.name.front() == '.') continue;
            if (++scanned > cap) break;
            if (e.stat.directory) {
                folders.push_back(e.path);
            } else if (droid::isTranscriptName(e.name)) {
                transcripts.push_back(e);
            } else if (e.name.size() > 14 && e.name.compare(e.name.size() - 14, 14, ".settings.json") == 0) {
                sidecars[e.name] = e.stat.writeNs;
            }
        }
        for (const auto& t : transcripts) {
            const auto sidecar = t.name.substr(0, t.name.size() - 6) + ".settings.json";
            const auto it = sidecars.find(sidecar);
            files.push_back({t.path, t.stat.length, it != sidecars.end() && it->second > t.stat.writeNs ? it->second : t.stat.writeNs});
        }
        std::sort(folders.begin(), folders.end());
        return folders;
    };
    for (const auto& folder : listFolder(root)) {
        if (scanned >= cap) break;
        listFolder(folder);
    }
    keepNewest(files, limit);
    return files;
}

bool insideWithin(const std::string& root, const std::string& path, std::size_t maxParts) {
    const auto parts = fsx::partsBelow(root, path);
    return parts && parts->size() <= maxParts && fsx::noLinkBelow(root, path);
}

bool noDotPart(const std::vector<std::string>& parts) {
    return std::none_of(parts.begin(), parts.end(), [](const std::string& p) { return p.front() == '.'; });
}

}  // namespace

bool isTranscriptName(std::string_view name, RootFamily family) {
    if (name.empty()) return false;
    switch (family) {
        case RootFamily::DeepSeek: return isDshName(name);
        case RootFamily::Codex: return fsx::endsWithNoCase(name, ".jsonl") || fsx::endsWithNoCase(name, ".jsonl.zst");
        case RootFamily::Gemini: return isGeminiName(name);
        case RootFamily::OpenCode:
        case RootFamily::ZCode:
        case RootFamily::OpenClaw:
        case RootFamily::Hermes: return false;
        case RootFamily::Kimi: return name == "wire.jsonl";
        case RootFamily::GrokBuild: return name == "updates.jsonl";
        case RootFamily::Omp:
        case RootFamily::Pi: return isOmpName(name);
        case RootFamily::Antigravity: return paths::isAntigravityDatabaseName(name);
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: return isWorkBuddyName(name);
        case RootFamily::Droid: return droid::isTranscriptName(name);
        case RootFamily::Claude: break;
    }
    return fsx::endsWithNoCase(name, ".jsonl");
}

std::vector<DiscoveredFile> enumerate(const std::string& root, RootFamily family, std::size_t limit) {
    if (root.empty()) return {};
    switch (family) {
        case RootFamily::Gemini: return gemini(root, limit);
        case RootFamily::OpenCode:
        case RootFamily::ZCode:
        case RootFamily::OpenClaw:
        case RootFamily::Hermes: return {};
        case RootFamily::Omp:
        case RootFamily::Pi: {
            const bool flat = family == RootFamily::Pi;
            auto files = walk(
                root, capFor(limit), [&](const fsx::Entry& e, int depth) { return isOmpName(e.name) && (flat || depth > 0); },
                [](const fsx::Entry& e) { return e.name.front() != '.'; });
            keepNewest(files, limit);
            return files;
        }
        case RootFamily::Antigravity: return antigravity(root, limit);
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: return workBuddy(root, limit);
        case RootFamily::Kimi: return harness(root, true, limit);
        case RootFamily::GrokBuild: return harness(root, false, limit);
        case RootFamily::Droid: return droidSessions(root, limit);
        default: break;
    }
    auto files = walk(
        root, capFor(limit), [&](const fsx::Entry& e, int) { return isTranscriptName(e.name, family); },
        [](const fsx::Entry&) { return true; });
    keepNewest(files, limit);
    return files;
}

bool isAcceptablePath(const std::string& root, const std::string& path, RootFamily family) {
    const auto name = path_text::basename(path);
    switch (family) {
        case RootFamily::Gemini: {
            if (!isGeminiName(name)) return false;
            const auto parts = fsx::partsBelow(root, path);
            if (!parts || (parts->size() != 3 && parts->size() != 4) || (*parts)[1] != "chats") return false;
            const auto s = fsx::statOf(path);
            return s && !s->link && !s->directory && fsx::noLinkBelow(root, path);
        }
        case RootFamily::Omp:
        case RootFamily::Pi: {
            if (!isAcceptablePath(root, path, RootFamily::Claude) || !isOmpName(name)) return false;
            const auto parts = fsx::partsBelow(root, path);
            return parts->size() >= (family == RootFamily::Pi ? 1u : 2u) && noDotPart(*parts);
        }
        case RootFamily::Antigravity: {
            const auto database = paths::antigravityDatabaseOf(path);
            if (!paths::isAntigravityDatabaseName(path_text::basename(database))) return false;
            if (fsx::trimTrailing(fsx::parentOf(fsx::normalize(database))) != fsx::trimTrailing(fsx::normalize(root))) return false;
            const auto s = fsx::statOf(database);
            return s && s->regular;
        }
        case RootFamily::WorkBuddy:
        case RootFamily::WorkBuddyAi: {
            if (!isAcceptablePath(root, path, RootFamily::Claude) || !isWorkBuddyName(name)) return false;
            const auto parts = *fsx::partsBelow(root, path);
            const auto slug = [](const std::string& p) { return p.front() != '.' && p != "cloud"; };
            if (parts.size() == 2) return slug(parts[0]);
            return parts.size() == 4 && slug(parts[0]) && parts[1].front() != '.' && parts[2] == "subagents";
        }
        case RootFamily::Kimi:
        case RootFamily::GrokBuild: {
            if (!isAcceptablePath(root, path, RootFamily::Claude)) return false;
            const auto parts = *fsx::partsBelow(root, path);
            if (!noDotPart(parts)) return false;
            return family == RootFamily::Kimi ? parts.size() == 5 && parts[2] == "agents" && parts[4] == "wire.jsonl"
                                              : parts.size() == 3 && parts[2] == "updates.jsonl";
        }
        case RootFamily::Droid: {
            if (!droid::isTranscriptName(name)) return false;
            const auto parts = fsx::partsBelow(root, path);
            return parts && parts->size() <= 2 && noDotPart(*parts) && fsx::noLinkBelow(root, path);
        }
        case RootFamily::OpenCode:
        case RootFamily::ZCode:
        case RootFamily::OpenClaw:
        case RootFamily::Hermes: return false;
        default: break;
    }
    return isTranscriptName(name, family) && insideWithin(root, path, kMaxDepth + 1);
}

std::string droidTranscriptOfSidecar(const std::string& root, const std::string& path) {
    const auto name = path_text::basename(path);
    constexpr std::string_view suffix = ".settings.json";
    if (name.size() <= suffix.size() || name.compare(name.size() - suffix.size(), suffix.size(), suffix) != 0 || name.front() == '.') {
        return {};
    }
    const auto parts = fsx::partsBelow(root, path);
    if (!parts || parts->size() > 2 || !noDotPart(*parts) || !fsx::noLinkBelow(root, path)) return {};
    return path.substr(0, path.size() - suffix.size()) + ".jsonl";
}

}  // namespace agentspeed::discovery
