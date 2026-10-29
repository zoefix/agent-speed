#include "internal.hpp"

// Edit hunks (TASK-PARSERS 6): the file / old / new digests of what an edit call changed, in input order.
namespace agentspeed::tasks::detail {

namespace {

constexpr const char* kPatchHeader = "\\*\\*\\* (Add File|Delete File|Update File|Move to): (.+)";
constexpr const char* kHashlineHeader = "\\[(.+)#([0-9A-Za-z]{4})\\]";

Words textLines(const std::string& text) {
    Words lines;
    for (const auto& x : split(lf(text), '\n')) lines.push_back(rstrip(x, " \t"));
    std::size_t start = 0;
    while (start < lines.size() && lines[start].empty()) ++start;
    lines = slice(lines, start);
    while (!lines.empty() && lines.back().empty()) lines.pop_back();
    return lines;
}

std::pair<std::string, std::string> hunkTexts(Words a, Words b) {
    std::size_t front = 0;
    while (front < a.size() && front < b.size() && a[front] == b[front]) ++front;
    a = slice(a, front);
    b = slice(b, front);
    while (!a.empty() && !b.empty() && a.back() == b.back()) {
        a.pop_back();
        b.pop_back();
    }
    return {join(a, "\n"), join(b, "\n")};
}

std::string normEditPath(const std::string& path, const std::optional<std::string>& cwd) {
    auto p = replaceAll(path, "\\", "/");
    if (startsWith(p, "file://")) p = p.substr(7);
    p = cwdRelative(p, cwd).first;
    while (contains(p, "//")) p = replaceAll(p, "//", "/");
    while (startsWith(p, "./")) p = p.substr(2);
    while (contains(p, "/./")) p = replaceAll(p, "/./", "/");
    return p;
}

EditHunk hunk(const std::string& path, const std::string& old, const std::string& now, const std::optional<std::string>& cwd) {
    const auto [o, n] = hunkTexts(textLines(old), textLines(now));
    return {sha16(normEditPath(path, cwd)), sha16(o), sha16(n)};
}

EditHunk hunkLines(const std::string& path, const Words& oldLines, const Words& newLines, const std::optional<std::string>& cwd) {
    const auto [o, n] = hunkTexts(textLines(join(oldLines, "\n")), textLines(join(newLines, "\n")));
    return {sha16(normEditPath(path, cwd)), sha16(o), sha16(n)};
}

EditHunk whole(const std::string& path, const std::string& content, const std::optional<std::string>& cwd) {
    return {sha16(normEditPath(path, cwd)), std::nullopt, sha16(join(textLines(content), "\n"))};
}

EditHunk deleted(const std::string& path, const std::optional<std::string>& cwd) {
    return {sha16(normEditPath(path, cwd)), std::nullopt, sha16("")};
}

std::vector<std::pair<Words, Words>> updateChunks(const Words& lines) {
    std::vector<std::pair<Words, Words>> chunks;
    Words old, now;
    bool changed = false;
    for (const auto& line : lines) {
        if (startsWith(line, "@@")) {
            if (changed) chunks.emplace_back(old, now);
            old.clear();
            now.clear();
            changed = false;
        } else if (line == "*** End of File") {
            continue;
        } else if (startsWith(line, "-")) {
            old.push_back(line.substr(1));
            changed = true;
        } else if (startsWith(line, "+")) {
            now.push_back(line.substr(1));
            changed = true;
        } else {
            const auto text = startsWith(line, " ") ? line.substr(1) : line;
            old.push_back(text);
            now.push_back(text);
        }
    }
    if (changed) chunks.emplace_back(old, now);
    return chunks;
}

std::vector<EditHunk> codexPatch(const std::string& text, const std::optional<std::string>& cwd) {
    const auto lines = split(lf(text), '\n');
    std::size_t start = 0;
    for (std::size_t k = 0; k < lines.size(); ++k) {
        if (strip(lines[k], " \t") == "*** Begin Patch") {
            start = k + 1;
            break;
        }
    }
    struct Section {
        std::string op, path;
        std::optional<std::string> moveTo;
        Words body;
    };
    std::vector<Section> sections;
    for (std::size_t k = start; k < lines.size(); ++k) {
        const auto& line = lines[k];
        if (strip(line, " \t") == "*** End Patch") break;
        const auto r = fullGroups(rstrip(line, " \t"), kPatchHeader);
        if (!r.empty() && *r[1] == "Move to" && !sections.empty() && sections.back().op == "Update File") {
            sections.back().moveTo = strip(*r[2], " \t");
            continue;
        }
        if (!r.empty() && *r[1] != "Move to") {
            sections.push_back({*r[1], strip(*r[2], " \t"), std::nullopt, {}});
            continue;
        }
        if (!sections.empty()) sections.back().body.push_back(line);
    }
    std::vector<EditHunk> hunks;
    for (const auto& s : sections) {
        if (s.op == "Add File") {
            Words added;
            for (const auto& x : s.body) {
                if (startsWith(x, "+")) added.push_back(x.substr(1));
            }
            hunks.push_back(whole(s.path, join(added, "\n"), cwd));
        } else if (s.op == "Delete File") {
            hunks.push_back(deleted(s.path, cwd));
        } else {
            auto target = s.path;
            if (s.moveTo && !s.moveTo->empty()) {
                hunks.push_back(deleted(s.path, cwd));
                target = *s.moveTo;
            }
            for (const auto& [old, now] : updateChunks(s.body)) hunks.push_back(hunkLines(target, old, now, cwd));
        }
    }
    return hunks;
}

std::vector<EditHunk> hashlinePatch(const std::string& text, const std::optional<std::string>& cwd) {
    const auto lines = split(lf(text), '\n');
    std::vector<EditHunk> hunks;
    std::optional<std::string> path;
    std::size_t k = 0;
    while (k < lines.size()) {
        const auto line = rstrip(lines[k], " \t");
        ++k;
        if (const auto r = fullGroups(line, kHashlineHeader); !r.empty()) {
            path = strip(*r[1], " \t");
            continue;
        }
        if (!path) continue;
        if (fullMatch(line, "PUT (?:<[0-9]+|>[0-9]+\\*?|>\\$):")) {
            Words body;
            while (k < lines.size() && startsWith(lines[k], "+")) body.push_back(lines[k++].substr(1));
            if (!body.empty()) hunks.push_back(hunkLines(*path, {}, body, cwd));
        } else if (line == "REM") {
            hunks.push_back(deleted(*path, cwd));
        } else if (const auto move = fullGroups(line, "MV (.+)"); !move.empty()) {
            hunks.push_back(deleted(*path, cwd));
            path = strip(*move[1], " \t");
        }
    }
    return hunks;
}

bool anyLine(const std::string& text, const char* pattern) {
    for (const auto& x : split(lf(text), '\n')) {
        if (fullMatch(rstrip(x, " \t"), pattern)) return true;
    }
    return false;
}

std::vector<EditHunk> patchInput(const std::string& text, const std::optional<std::string>& cwd) {
    if (anyLine(text, kPatchHeader)) return codexPatch(text, cwd);
    if (anyLine(text, kHashlineHeader)) return hashlinePatch(text, cwd);
    return {};
}

std::vector<std::pair<std::string, std::string>> piEdits(const json::Value& args) {
    std::vector<std::pair<std::string, std::string>> pairs;
    auto edits = args["edits"];
    std::unique_ptr<OwnedJson> holder;
    if (edits.isString()) {
        holder = parseJson(*edits.string());
        edits = holder ? holder->root() : json::Value();
    }
    std::vector<json::Value> list;
    if (edits.isObject()) {
        list = {edits};
    } else if (edits.isArray()) {
        list = edits.elements();
    }
    for (const auto& e : list) {
        if (e.isObject() && e["oldText"].isString() && e["newText"].isString()) pairs.emplace_back(*e["oldText"].string(), *e["newText"].string());
    }
    if (args["oldText"].isString() && args["newText"].isString()) {
        const auto legacy = std::make_pair(*args["oldText"].string(), *args["newText"].string());
        if (std::find(pairs.begin(), pairs.end(), legacy) == pairs.end()) pairs.push_back(legacy);
    }
    return pairs;
}

std::vector<EditHunk> ompPatchMode(const json::Value& args, const std::string& path, const std::optional<std::string>& cwd) {
    std::vector<EditHunk> hunks;
    for (const auto& entry : args["edits"].elements()) {
        if (!entry.isObject()) continue;
        const auto op = entry["op"].nonEmptyString().value_or("update");
        const auto diff = entry["diff"].isString() ? *entry["diff"].string() : std::string();
        if (op == "delete") {
            hunks.push_back(deleted(path, cwd));
        } else if (op == "create") {
            auto body = split(lf(diff), '\n');
            bool allPlus = true;
            for (const auto& x : body) allPlus &= x.empty() || startsWith(x, "+");
            if (allPlus) {
                for (auto& x : body) x = x.empty() ? x : x.substr(1);
            }
            hunks.push_back(whole(path, join(body, "\n"), cwd));
        } else {
            auto target = path;
            if (const auto rename = entry["rename"].nonEmptyString()) {
                hunks.push_back(deleted(path, cwd));
                target = *rename;
            }
            for (const auto& [old, now] : updateChunks(split(lf(diff), '\n'))) hunks.push_back(hunkLines(target, old, now, cwd));
        }
    }
    return hunks;
}

std::optional<std::pair<std::string, std::string>> droidPair(const json::Value& entry) {
    for (const auto& [oldKey, newKey] : {std::pair{"old_str", "new_str"}, std::pair{"old_string", "new_string"}}) {
        if (entry[oldKey].isString() && entry[newKey].isString()) return std::make_pair(*entry[oldKey].string(), *entry[newKey].string());
    }
    return std::nullopt;
}

std::vector<EditHunk> droidHunks(const std::string& name, const json::Value& args, const std::optional<std::string>& cwd) {
    if (!args["file_path"].isString()) return {};
    const auto path = *args["file_path"].string();
    if (name == "Create") return args["content"].isString() ? std::vector<EditHunk>{whole(path, *args["content"].string(), cwd)} : std::vector<EditHunk>{};
    if (name == "Edit") {
        const auto pair = droidPair(args);
        return pair ? std::vector<EditHunk>{hunk(path, pair->first, pair->second, cwd)} : std::vector<EditHunk>{};
    }
    std::vector<EditHunk> hunks;
    if (args["edits"].isArray()) {
        for (const auto& e : args["edits"].elements()) {
            if (!e.isObject()) continue;
            if (const auto pair = droidPair(e)) hunks.push_back(hunk(path, pair->first, pair->second, cwd));
        }
    }
    return hunks;
}

}  // namespace

}  // namespace agentspeed::tasks::detail

namespace agentspeed::tasks {

using namespace detail;

std::vector<EditHunk> editHunks(SourceKind source, const std::optional<std::string>& tool, const json::Value& value,
                                const std::optional<std::string>& cwd) {
    const auto family = familyOf(source);
    if (family.empty() || !tool) return {};
    const auto name = toolName(family, *tool);
    const auto kind = kindOf(family, name);
    if (kind == "command") {
        const auto text = commandText(source, tool, value);
        if (!text) return {};
        std::vector<EditHunk> hunks;
        for (const auto& p : parseCommand(*text, cwd).patches) {
            auto more = codexPatch(p, cwd);
            hunks.insert(hunks.end(), more.begin(), more.end());
        }
        return hunks;
    }
    if (kind != "edit") return {};
    std::unique_ptr<OwnedJson> holder;
    const auto args = decodeArgs(value, holder);
    if (family == "codex") {
        if (value.isString() && !args.valid()) return codexPatch(*value.string(), cwd);
        if (!args.valid()) return {};
        const auto text = args["input"].isString() ? args["input"] : args["patch"];
        return text.isString() ? codexPatch(*text.string(), cwd) : std::vector<EditHunk>{};
    }
    if (!args.valid()) return {};
    if (family == "droid") return droidHunks(name, args, cwd);
    if (family == "claude") {
        const auto path = args["file_path"].isString() ? args["file_path"].string() : std::nullopt;
        if (name == "Edit" && path && args["old_string"].isString() && args["new_string"].isString()) {
            return {hunk(*path, *args["old_string"].string(), *args["new_string"].string(), cwd)};
        }
        if (name == "MultiEdit" && path) {
            std::vector<EditHunk> hunks;
            if (args["edits"].isArray()) {
                for (const auto& e : args["edits"].elements()) {
                    if (e.isObject() && e["old_string"].isString() && e["new_string"].isString()) {
                        hunks.push_back(hunk(*path, *e["old_string"].string(), *e["new_string"].string(), cwd));
                    }
                }
            }
            return hunks;
        }
        if (name == "Write" && path && args["content"].isString()) return {whole(*path, *args["content"].string(), cwd)};
        if (name == "NotebookEdit" && args["notebook_path"].isString()) {
            const auto nb = *args["notebook_path"].string();
            std::string cell;
            if (args["cell_id"].isString()) {
                cell = *args["cell_id"].string();
            } else if (const auto number = jsonInt(args["cell_number"])) {
                cell = std::to_string(*number);
            }
            const auto mode = args["edit_mode"].nonEmptyString().value_or("replace");
            const auto sourceText = args["new_source"].isString() ? *args["new_source"].string() : std::string();
            if (mode == "insert") return {hunk(nb, "", sourceText, cwd)};
            if (mode == "delete") return {deleted(nb + "#" + cell, cwd)};
            return {whole(nb + "#" + cell, sourceText, cwd)};
        }
        return {};
    }
    const auto path = args["path"].isString() ? args["path"].string() : std::nullopt;
    if (name == "write") {
        return path && args["content"].isString() ? std::vector<EditHunk>{whole(*path, *args["content"].string(), cwd)} : std::vector<EditHunk>{};
    }
    if (name == "apply_patch") return args["input"].isString() ? codexPatch(*args["input"].string(), cwd) : std::vector<EditHunk>{};
    if (name == "edit") {
        if (args["input"].isString()) return patchInput(*args["input"].string(), cwd);
        if (family == "omp" && path && !path->empty() && args["old_string"].isString() && args["new_string"].isString()) {
            return {hunk(*path, *args["old_string"].string(), *args["new_string"].string(), cwd)};
        }
        if (family == "omp" && path && !path->empty() && args["edits"].isArray()) {
            bool patchMode = false;
            for (const auto& e : args["edits"].elements()) patchMode |= e.isObject() && (e["diff"].valid() || e["op"].valid());
            if (patchMode) return ompPatchMode(args, *path, cwd);
        }
        if (path && !path->empty()) {
            std::vector<EditHunk> hunks;
            for (const auto& [o, n] : piEdits(args)) hunks.push_back(hunk(*path, o, n, cwd));
            return hunks;
        }
    }
    return {};
}

}  // namespace agentspeed::tasks
