#pragma once

#include <initializer_list>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/task_parsers.hpp"

// Shared pieces of the task-score parsers (spec/TASK-PARSERS.md, spec/tools/task_parsers.py).
namespace agentspeed::tasks::detail {

using Words = std::vector<std::string>;
using Names = std::initializer_list<std::string_view>;

inline bool in(std::string_view value, Names names) {
    for (const auto n : names) {
        if (n == value) return true;
    }
    return false;
}
inline bool startsWith(std::string_view text, std::string_view prefix) { return text.substr(0, prefix.size()) == prefix; }
inline bool endsWith(std::string_view text, std::string_view suffix) {
    return text.size() >= suffix.size() && text.substr(text.size() - suffix.size()) == suffix;
}
inline bool contains(std::string_view text, std::string_view part) { return text.find(part) != std::string_view::npos; }
inline bool containsWord(const Words& words, std::string_view w) {
    for (const auto& x : words) {
        if (x == w) return true;
    }
    return false;
}
std::string lowerAscii(std::string_view text);
std::string upperAscii(std::string_view text);
std::string replaceAll(std::string text, std::string_view from, std::string_view to);
std::string rstrip(std::string_view text, std::string_view chars);
std::string lstrip(std::string_view text, std::string_view chars);
std::string strip(std::string_view text, std::string_view chars);
std::vector<std::string> split(std::string_view text, char sep);
std::string join(const Words& words, std::string_view sep);
Words slice(const Words& words, std::size_t from, std::size_t to = std::string::npos);

struct OwnedJson;

std::string sha16(std::string_view text);
std::optional<std::string> normCwd(const std::optional<std::string>& cwd);
std::pair<std::string, bool> cwdRelative(const std::string& path, const std::optional<std::string>& cwd);
std::int64_t num(std::string_view text);
bool fullMatch(std::string_view text, const char* pattern);
bool prefixMatch(std::string_view text, const char* pattern);
// Capture groups (index 0: the whole match) of a full, prefix or first-anywhere match; empty when none.
using Groups = std::vector<std::optional<std::string>>;
Groups fullGroups(std::string_view text, const char* pattern);
Groups prefixGroups(std::string_view text, const char* pattern);
Groups searchGroups(std::string_view text, const char* pattern);
// A JSON integer literal in the signed 64-bit range (never a boolean).
std::optional<std::int64_t> jsonInt(const json::Value& value);
// The JSON object that starts the text (after JSON whitespace), ignoring what follows it.
std::unique_ptr<OwnedJson> leadingObject(std::string_view text);
std::string blocksText(const json::Value& content);

// The source family of the tool tables (claude, codex, omp, pi, openclaw, droid); empty for other sources.
std::string familyOf(SourceKind source);
std::string toolName(const std::string& family, const std::string& tool);
std::string kindOf(const std::string& family, const std::string& tool);

// An owned JSON value: a parsed document and its root (decode_args of a JSON string).
struct OwnedJson {
    std::string text;
    json::Document document;
    json::Value root() const { return document.root(); }
};
std::unique_ptr<OwnedJson> parseJson(std::string_view text);
// The input as an object: an object as is, a string holding a JSON object parsed; else invalid.
json::Value decodeArgs(const json::Value& value, std::unique_ptr<OwnedJson>& holder);

// Shell tokens (3.2).
struct Token {
    enum class Kind { Word, Op, Redir, Heredoc } kind = Kind::Word;
    std::string text;    // word, op, redir op, heredoc body
    std::string target;  // redir target
};
std::pair<std::string, std::size_t> readWord(std::string_view text, std::size_t i);
std::vector<Token> tokenize(std::string_view text);

struct Segment {
    Words words;
    std::vector<std::pair<std::string, std::string>> redirs;
    Words heredocs;
    Words ops;
    std::optional<std::string> opBefore, opAfter;
    bool background = false;
    std::string ctx;
};
std::pair<std::vector<Segment>, Words> splitSegments(const std::vector<Token>& items);
std::string prog(std::string_view word);
std::size_t dropOptions(const Words& words, std::size_t i, Names withValue = {}, bool stopDashdash = true);
void stripPrefixes(Segment& seg);
void dropKeywords(Segment& seg);
std::optional<std::string> shellScript(const Segment& seg);
Words unwrap(Words words);

// Classification (3.5).
struct Classified {
    std::string cls = "other";
    std::optional<std::string> suite;
    bool targeted = false;
};
int classRank(std::string_view cls);
bool hasExtension(std::string_view word);
bool isBroadPath(const std::string& arg, const std::optional<std::string>& cwd);
struct Opts {
    Words pos;
    std::map<std::string, std::optional<std::string>> opts;  // nullopt: a flag
    Words after;
    bool has(std::string_view name) const { return opts.contains(std::string(name)); }
    bool any(Names names) const {
        for (const auto n : names) {
            if (has(n)) return true;
        }
        return false;
    }
    std::optional<std::string> value(std::string_view name) const {
        const auto it = opts.find(std::string(name));
        return it == opts.end() ? std::nullopt : it->second;
    }
    Words posAndAfter() const {
        auto all = pos;
        all.insert(all.end(), after.begin(), after.end());
        return all;
    }
};
Opts parseOpts(const Words& args, Names valueOpts = {}, bool stopDashdash = true);
bool pathTargeted(const Words& pos, const std::optional<std::string>& cwd);
bool nameTargeted(const Words& pos, const std::optional<std::string>& cwd);
std::optional<Classified> classifyProgram(const std::string& program, const Words& args, const std::optional<std::string>& cwd);
// Package managers, task runners and deno (3.6); nullopt for any other program.
std::optional<Classified> classifyScripted(const std::string& program, const Words& args, const std::optional<std::string>& cwd);
bool isKnownProgram(const std::string& program);
bool isScriptRunner(const std::string& program);
// (class, suite, targeted) of one segment's words after prefixes and runners are removed.
Classified classify(const Words& words, const std::optional<std::string>& cwd);
extern const Names kNpmBuiltins;

struct Parsed {
    std::vector<Segment> segments;
    Words patches;
    bool applyPatch = false;
    bool pipefail = false;
    Words tail;
};
std::string lf(std::string_view text);
Parsed parseCommand(const std::string& text, const std::optional<std::string>& cwd);
bool isNeutral(const Segment& seg);
struct Choice {
    std::vector<Classified> classes;
    std::optional<std::size_t> best;
    Classified result;
};
Choice choose(const std::vector<Segment>& segs, const std::optional<std::string>& cwd);
CommandInfo commandInfoOf(const Parsed& parsed, const std::optional<std::string>& cwd);
std::string shellKind(const Parsed& parsed, const std::optional<std::string>& cwd);
// No output at all: nothing but whitespace, or Claude Code's placeholder for it.
bool silent(const std::string& text);
std::size_t skipUntilModule(const Words& words, Names valueOpts);

}  // namespace agentspeed::tasks::detail
