#include "internal.hpp"

// Command parsing, command_info, shell_kind and masked runs (TASK-PARSERS 3, 3.7, 3.9, 3.10).
namespace agentspeed::tasks::detail {

namespace {

constexpr int kMaxWrapperDepth = 4;
constexpr Names kClosing = {"fi", "done", "esac", "}"};
constexpr Names kConstructs = {"for", "case", "select", "function"};
constexpr Names kNeutral = {"cd", "pushd", "popd", "export", "set", "unset", "source", ".", "true", ":", "echo", "printf", "sleep", "clear",
                            "alias", "shopt", "ulimit", "umask", "wait", "trap", "exit", "return", "local", "declare", "typeset", "read",
                            "false"};

struct ParseState {
    std::optional<std::string> cwd;
    std::string ctx;
    Words parens, dirs, carry;
};

bool hasPatchLine(const std::string& text) {
    for (const auto& line : split(text, '\n')) {
        if (fullMatch(line, "[ \\t]*\\*\\*\\* Begin Patch[ \\t]*")) return true;
    }
    return false;
}

void applyParens(const Words& ops, ParseState& st) {
    for (const auto& op : ops) {
        if (op == "(") {
            st.parens.push_back(st.ctx);
        } else if (op == ")" && !st.parens.empty()) {
            st.ctx = st.parens.back();
            st.parens.pop_back();
        }
    }
}

std::string normDir(const std::string& target, const std::string& ctx, const std::optional<std::string>& cwd) {
    const auto t = replaceAll(target, "\\", "/");
    if (t.empty()) return ctx;
    const auto [rel, inside] = cwdRelative(t, cwd);
    std::string joined;
    if (inside) {
        joined = rel;
    } else if (t == "~" || startsWith(t, "~/") || startsWith(t, "/") || prefixMatch(t, "[A-Za-z]:/") || t == "-") {
        joined = t;
    } else {
        joined = ctx.empty() ? t : ctx + "/" + t;
    }
    const bool absolute = startsWith(joined, "/");
    Words parts;
    for (const auto& part : split(joined, '/')) {
        if (part.empty() || part == ".") continue;
        if (part == ".." && !parts.empty() && parts.back() != "..") {
            parts.pop_back();
            continue;
        }
        parts.push_back(part);
    }
    const auto result = (absolute ? std::string("/") : std::string()) + join(parts, "/");
    return result.empty() || result == "." ? std::string() : result;
}

void parseInto(const std::string& text, Parsed& parsed, ParseState& st, int depth) {
    if (depth > kMaxWrapperDepth) return;
    auto [segments, tail] = splitSegments(tokenize(lf(text)));
    for (auto& seg : segments) {
        applyParens(seg.ops, st);
        auto ops = st.carry;
        ops.insert(ops.end(), seg.ops.begin(), seg.ops.end());
        st.carry.clear();
        dropKeywords(seg);
        stripPrefixes(seg);
        dropKeywords(seg);
        if (!seg.words.empty() && (in(seg.words[0], kClosing) || in(seg.words[0], kConstructs))) seg.words.clear();
        const auto script = shellScript(seg);
        for (const auto& h : seg.heredocs) {
            if (hasPatchLine(h)) parsed.patches.push_back(h);
        }
        if (script) {
            st.carry = ops;
            const auto before = parsed.segments.size();
            parseInto(*script, parsed, st, depth + 1);
            if (seg.background && parsed.segments.size() > before) parsed.segments.back().background = true;
            continue;
        }
        for (const auto& w : seg.words) {
            if (contains(w, "\n") && hasPatchLine(w)) parsed.patches.push_back(w);
        }
        seg.words = unwrap(seg.words);
        seg.ctx = st.ctx;
        if (!seg.words.empty()) {
            const auto p = prog(seg.words[0]);
            if (p == "apply_patch" || p == "applypatch") parsed.applyPatch = true;
            if (p == "set") {
                for (std::size_t k = 0; k + 1 < seg.words.size(); ++k) {
                    if (fullMatch(seg.words[k], "-[A-Za-z]*o") && seg.words[k + 1] == "pipefail") parsed.pipefail = true;
                }
            }
            if (p == "cd" || p == "pushd") {
                if (p == "pushd") st.dirs.push_back(st.ctx);
                std::string target = "~";
                for (std::size_t k = 1; k < seg.words.size(); ++k) {
                    if (!startsWith(seg.words[k], "-") || seg.words[k] == "-") {
                        target = seg.words[k];
                        break;
                    }
                }
                st.ctx = normDir(target, st.ctx, st.cwd);
            } else if (p == "popd") {
                if (!st.dirs.empty()) {
                    st.ctx = st.dirs.back();
                    st.dirs.pop_back();
                }
            }
        }
        if (!seg.words.empty() || !seg.heredocs.empty()) {
            seg.ops = ops;
            parsed.segments.push_back(seg);
        } else {
            st.carry = ops;
        }
    }
    applyParens(tail, st);
    st.carry.insert(st.carry.end(), tail.begin(), tail.end());
}

std::optional<std::string> lastOperator(const Words& ops) {
    std::optional<std::string> last;
    for (const auto& op : ops) {
        if (op != "(" && op != ")") last = op;
    }
    return last;
}

std::optional<std::string> firstOperator(const Words& ops) {
    for (const auto& op : ops) {
        if (op != "(" && op != ")") return op;
    }
    return std::nullopt;
}

Words segmentTokens(const Segment& seg) {
    const auto& words = seg.words;
    const auto p = prog(words[0]);
    Words tokens{p};
    for (std::size_t k = 1; k < words.size(); ++k) {
        const auto& w = words[k];
        if (in(w, {"-q", "-qq", "-v", "-vv", "-vvv", "--quiet", "--verbose", "--color", "--no-color", "--colors", "--no-colors", "--ansi",
                   "--no-ansi", "--nocapture", "--no-capture", "--show-output", "--silent"}) ||
            startsWith(w, "--color=") || startsWith(w, "--colour=") || startsWith(w, "--colors=") || startsWith(w, "--tb=")) {
            continue;
        }
        if (p == "pytest" && (w == "-s" || fullMatch(w, "-r[A-Za-z]+") || w == "--capture=no")) continue;
        tokens.push_back(w);
    }
    if (in(p, {"npm", "pnpm", "yarn"}) && tokens.size() > 1 && in(tokens[1], {"test", "t", "tst"})) {
        Words t{p, "run", "test"};
        t.insert(t.end(), tokens.begin() + 2, tokens.end());
        tokens = t;
    } else if (in(p, {"npm", "pnpm", "yarn", "bun"}) && tokens.size() > 1 && in(tokens[1], {"run-script", "rum", "urn"})) {
        Words t{p, "run"};
        t.insert(t.end(), tokens.begin() + 2, tokens.end());
        tokens = t;
    } else if ((p == "pnpm" || p == "yarn") && tokens.size() > 1 && !in(tokens[1], kNpmBuiltins) && !startsWith(tokens[1], "-")) {
        Words t{p, "run"};
        t.insert(t.end(), tokens.begin() + 1, tokens.end());
        tokens = t;
    }
    if (!seg.ctx.empty()) tokens.insert(tokens.begin(), "@" + seg.ctx);
    return tokens;
}

std::size_t pipelineEnd(const std::vector<Segment>& segs, std::size_t k) {
    while (k + 1 < segs.size() && segs[k].opAfter == "|") ++k;
    return k;
}

std::size_t pipelineStart(const std::vector<Segment>& segs, std::size_t k) {
    while (k > 0 && segs[k].opBefore == "|") --k;
    return k;
}

std::optional<Words> grepPatterns(const Words& words) {
    static constexpr Names kValue = {"-e", "-A", "-B", "-C", "-m", "--regexp", "--after-context", "--before-context", "--context", "--max-count"};
    static constexpr Names kLong = {"--ignore-case", "--extended-regexp", "--line-number", "--word-regexp", "--fixed-strings", "--perl-regexp",
                                    "--text", "--no-messages", "--no-filename", "--with-filename", "--only-matching"};
    Words patterns, positional;
    std::size_t i = 1;
    while (i < words.size()) {
        const auto& w = words[i];
        if (w == "--") {
            positional.insert(positional.end(), words.begin() + static_cast<std::ptrdiff_t>(i) + 1, words.end());
            break;
        }
        const auto eq = w.find('=');
        const auto name = eq == std::string::npos ? w : w.substr(0, eq);
        const bool hasEq = eq != std::string::npos;
        if (in(w, kValue) || (startsWith(w, "--") && hasEq && in(name, kValue))) {
            std::string arg;
            if (hasEq) {
                arg = w.substr(eq + 1);
            } else if (i + 1 < words.size()) {
                arg = words[++i];
            } else {
                return std::nullopt;
            }
            if (name == "-e" || name == "--regexp") patterns.push_back(arg);
        } else if (w.size() > 2 && in(w.substr(0, 2), {"-e", "-A", "-B", "-C", "-m"}) && !startsWith(w, "--")) {
            if (w.substr(0, 2) == "-e") patterns.push_back(w.substr(2));
        } else if (in(w, kLong) || name == "--color" || name == "--colour") {
        } else if (startsWith(w, "-") && w.size() > 1 && !startsWith(w, "--")) {
            for (std::size_t c = 1; c < w.size(); ++c) {
                if (std::string_view("iEnwFPashHo").find(w[c]) == std::string_view::npos) return std::nullopt;
            }
        } else if (startsWith(w, "--")) {
            return std::nullopt;
        } else {
            positional.push_back(w);
        }
        ++i;
    }
    if (patterns.empty()) {
        if (positional.empty()) return std::nullopt;
        patterns = {positional[0]};
        positional = slice(positional, 1);
    }
    if (!positional.empty()) return std::nullopt;
    return patterns;
}

bool errorFilter(const std::vector<Segment>& segs, std::size_t k) {
    const auto end = pipelineEnd(segs, k);
    if (end == k || end != segs.size() - 1 || segs[k].opAfter != "|") return false;
    const auto& words = segs[k + 1].words;
    if (words.empty() || !in(prog(words[0]), {"grep", "egrep"})) return false;
    const auto patterns = grepPatterns(words);
    if (!patterns || patterns->empty()) return false;
    bool error = false;
    for (const auto& pat : *patterns) error |= contains(lowerAscii(pat), "error");
    if (!error) return false;
    for (std::size_t j = k + 2; j <= end; ++j) {
        if (segs[j].words.empty() || !in(prog(segs[j].words[0]), {"head", "tail"})) return false;
    }
    return true;
}

}  // namespace

std::string lf(std::string_view text) { return replaceAll(replaceAll(std::string(text), "\r\n", "\n"), "\r", "\n"); }

Parsed parseCommand(const std::string& text, const std::optional<std::string>& cwd) {
    Parsed parsed;
    ParseState st;
    st.cwd = cwd;
    parseInto(text, parsed, st, 0);
    auto& segs = parsed.segments;
    for (std::size_t k = 0; k < segs.size(); ++k) {
        segs[k].opBefore = k > 0 ? lastOperator(segs[k].ops) : std::nullopt;
        const auto& following = k + 1 < segs.size() ? segs[k + 1].ops : st.carry;
        segs[k].opAfter = lastOperator(following);
        if (firstOperator(following) == "&") segs[k].background = true;
    }
    parsed.tail = st.carry;
    return parsed;
}

bool isNeutral(const Segment& seg) { return seg.words.empty() || in(prog(seg.words[0]), kNeutral); }

Choice choose(const std::vector<Segment>& segs, const std::optional<std::string>& cwd) {
    Choice choice;
    for (std::size_t k = 0; k < segs.size(); ++k) {
        const auto result = isNeutral(segs[k]) ? Classified{} : classify(segs[k].words, cwd);
        choice.classes.push_back(result);
        if (classRank(result.cls) > classRank(choice.result.cls)) {
            choice.best = k;
            choice.result = result;
        }
    }
    return choice;
}

CommandInfo commandInfoOf(const Parsed& parsed, const std::optional<std::string>& cwd) {
    const auto& segs = parsed.segments;
    const auto choice = choose(segs, cwd);
    std::vector<Words> kept;
    for (std::size_t k = 0; k < segs.size(); ++k) {
        if (choice.classes[k].cls != "other") kept.push_back(segmentTokens(segs[k]));
    }
    if (kept.empty()) {
        for (const auto& seg : segs) {
            if (!isNeutral(seg)) kept.push_back(segmentTokens(seg));
        }
    }
    if (kept.empty()) {
        for (const auto& seg : segs) {
            if (!seg.words.empty()) kept.push_back(segmentTokens(seg));
        }
    }
    Words joined;
    for (const auto& t : kept) joined.push_back(join(t, "\x1f"));
    CommandInfo info;
    info.key = sha16(join(joined, "\x1e"));
    if (!choice.best) {
        for (const auto& s : segs) info.background |= s.background;
        return info;
    }
    const auto k = *choice.best;
    const auto& chosen = segs[k];
    bool laterSeparated = false;
    for (std::size_t j = k + 1; j < segs.size(); ++j) laterSeparated |= segs[j].opBefore && in(*segs[j].opBefore, {";", "||", "&"});
    info.cls = choice.result.cls;
    info.suite = choice.result.suite;
    info.targeted = choice.result.targeted;
    info.masked = k + 1 < segs.size() && ((chosen.opAfter == "|" && !parsed.pipefail) || laterSeparated);
    info.background = chosen.background;
    info.errorFilter = (info.cls == "build" || info.cls == "check") && errorFilter(segs, k);
    return info;
}

std::string shellKind(const Parsed& parsed, const std::optional<std::string>& cwd) {
    if (parsed.applyPatch || !parsed.patches.empty()) return "edit";
    if (commandInfoOf(parsed, cwd).cls != "other") return "command";
    for (const auto& seg : parsed.segments) {
        for (const auto& [op, target] : seg.redirs) {
            if (in(op, {">", ">>", ">|", "&>", "&>>", "<>"}) && !in(target, {"/dev/null", "NUL", "nul"}) && !fullMatch(target, "[0-9-]+")) {
                return "command";
            }
        }
    }
    bool any = false, search = false;
    for (const auto& seg : parsed.segments) {
        if (isNeutral(seg)) continue;
        any = true;
        const auto p = prog(seg.words[0]);
        const auto args = slice(seg.words, 1);
        if (p == "git") {
            const auto k = dropOptions(args, 0, {"-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path", "--config-env"});
            const auto subcommand = k < args.size() ? args[k] : std::string();
            if (in(subcommand, {"status", "diff", "log", "show", "blame", "rev-parse", "ls-tree", "cat-file", "describe", "shortlog", "reflog",
                                "branch", "remote", "tag", "config"})) {
            } else if (subcommand == "grep" || subcommand == "ls-files") {
                search = true;
            } else {
                return "command";
            }
        } else if (p == "sed" && std::any_of(args.begin(), args.end(), [](const std::string& a) {
                       return startsWith(a, "-i") || startsWith(a, "--in-place");
                   })) {
            return "command";
        } else if (in(p, {"cat", "head", "tail", "less", "more", "nl", "wc", "ls", "tree", "stat", "file", "pwd", "bat", "batcat", "sed", "awk",
                          "jq", "yq", "realpath", "readlink", "du", "basename", "dirname", "od", "xxd", "hexdump", "strings", "diff", "cmp",
                          "sort", "uniq", "cut", "tr", "column", "which", "type", "whereis", "printenv", "date", "env", "exa", "eza", "lsd"})) {
        } else if (in(p, {"rg", "grep", "egrep", "fgrep", "ag", "ack", "find", "fd", "fdfind", "locate", "mdfind"})) {
            if (p == "find" && std::any_of(args.begin(), args.end(), [](const std::string& a) {
                    return in(a, {"-delete", "-exec", "-execdir", "-ok", "-okdir", "-fprint", "-fprintf", "-fls"});
                })) {
                return "command";
            }
            search = true;
        } else {
            return "command";
        }
    }
    if (!any) return "command";
    return search ? "search" : "read";
}

namespace {

struct StatusText {
    std::string prefix;
    std::optional<std::string> suffix;
    std::optional<std::size_t> index;
};

std::optional<std::string> echoText(const Segment& seg) {
    const auto& w = seg.words;
    if (w.size() < 2 || prog(w[0]) != "echo" || !seg.redirs.empty() || !seg.heredocs.empty() || startsWith(w[1], "-")) return std::nullopt;
    return join(slice(w, 1), " ");
}

// STATUS_REF = \$\?|\$\{\?\}|\$\{PIPESTATUS\[([0-9]+)\]\}|\$PIPESTATUS
struct Ref {
    std::size_t start, end;
    std::optional<std::size_t> index;
    bool bare;
};

std::vector<Ref> statusRefs(const std::string& text) {
    std::vector<Ref> refs;
    std::size_t i = 0;
    while (i < text.size()) {
        if (text[i] != '$') {
            ++i;
            continue;
        }
        if (text.compare(i, 2, "$?") == 0) {
            refs.push_back({i, i + 2, std::nullopt, false});
            i += 2;
        } else if (text.compare(i, 4, "${?}") == 0) {
            refs.push_back({i, i + 4, std::nullopt, false});
            i += 4;
        } else if (text.compare(i, 13, "${PIPESTATUS[") == 0) {
            std::size_t j = i + 13;
            while (j < text.size() && std::isdigit(static_cast<unsigned char>(text[j]))) ++j;
            if (j > i + 13 && text.compare(j, 2, "]}") == 0) {
                refs.push_back({i, j + 2, static_cast<std::size_t>(std::stoull(text.substr(i + 13, j - i - 13))), false});
                i = j + 2;
            } else {
                ++i;
            }
        } else if (text.compare(i, 11, "$PIPESTATUS") == 0) {
            refs.push_back({i, i + 11, 0, true});
            i += 11;
        } else {
            ++i;
        }
    }
    return refs;
}

std::optional<StatusText> statusText(const std::string& text) {
    const auto refs = statusRefs(text);
    if (refs.size() > 1) return std::nullopt;
    if (refs.empty()) {
        if (contains(text, "$") || contains(text, "`") || strip(text, " \t").empty()) return std::nullopt;
        return StatusText{text, std::nullopt, std::nullopt};
    }
    const auto& m = refs[0];
    const auto rest = text.substr(0, m.start) + text.substr(m.end);
    if (contains(rest, "$") || contains(rest, "`")) return std::nullopt;
    return StatusText{text.substr(0, m.start), text.substr(m.end), m.index};
}

std::optional<std::int64_t> printedValue(const std::string& line, const std::string& rawPrefix, const std::string& rawSuffix) {
    const auto prefix = lstrip(rawPrefix, " \t");
    const auto suffix = rstrip(rawSuffix, " \t");
    if (line.size() < prefix.size() + suffix.size() || !startsWith(line, prefix) || !endsWith(line, suffix)) return std::nullopt;
    const auto middle = line.substr(prefix.size(), line.size() - prefix.size() - suffix.size());
    if (!fullMatch(middle, "-?[0-9]+")) return std::nullopt;
    return num(middle);
}

struct StatusEcho {
    std::string form;
    std::string prefix, suffix, ok;
    std::pair<std::string, std::optional<std::string>> fail;
};

std::optional<StatusEcho> statusEcho(const Parsed& parsed, std::size_t k) {
    const auto& segs = parsed.segments;
    const auto end = pipelineEnd(segs, k);
    const bool own = end == k || parsed.pipefail;
    std::vector<StatusText> texts;
    std::vector<const Segment*> after;
    for (std::size_t j = end + 1; j < segs.size(); ++j) {
        after.push_back(&segs[j]);
        const auto t = statusText(echoText(segs[j]).value_or(""));
        if (!t) return std::nullopt;
        texts.push_back(*t);
    }
    if (after.size() == 1 && after[0]->opBefore == ";") {
        const auto& t = texts[0];
        if (!t.suffix) return std::nullopt;
        const bool matches = !t.index ? own : *t.index == k - pipelineStart(segs, k);
        if (!matches) return std::nullopt;
        StatusEcho echo;
        echo.form = "printed";
        echo.prefix = t.prefix;
        echo.suffix = *t.suffix;
        return echo;
    }
    if (!own) return std::nullopt;
    for (const auto& t : texts) {
        if (t.index) return std::nullopt;
    }
    if (after.size() == 2 && after[0]->opBefore == "&&" && after[1]->opBefore == "||") {
        const auto& ok = texts[0];
        const auto& fail = texts[1];
        if (ok.suffix || (!fail.suffix && strip(fail.prefix, " \t") == strip(ok.prefix, " \t"))) return std::nullopt;
        StatusEcho echo;
        echo.form = "branches";
        echo.ok = ok.prefix;
        echo.fail = {fail.prefix, fail.suffix};
        return echo;
    }
    if (after.size() == 1 && after[0]->opBefore == "||") {
        StatusEcho echo;
        echo.form = "else";
        echo.fail = {texts[0].prefix, texts[0].suffix};
        return echo;
    }
    return std::nullopt;
}

bool silent(const std::string& text) {
    const auto stripped = strip(text, " \t\r\n");
    return stripped.empty() || stripped == "(Bash completed with no output)";
}

}  // namespace

}  // namespace agentspeed::tasks::detail

namespace agentspeed::tasks {

using namespace detail;

std::optional<std::string> commandText(SourceKind source, const std::optional<std::string>& tool, const json::Value& value) {
    const auto family = familyOf(source);
    if (family.empty() || !tool) return std::nullopt;
    const auto name = toolName(family, *tool);
    if (kindOf(family, name) != "command") return std::nullopt;
    std::unique_ptr<OwnedJson> holder;
    const auto args = decodeArgs(value, holder);
    if (family == "codex" && name == "exec") {
        if (args.valid()) {
            for (const char* f : {"cmd", "command"}) {
                if (args[f].isString()) return args[f].string();
            }
            return std::nullopt;
        }
        return value.isString() ? value.string() : std::nullopt;
    }
    if (!args.valid()) return std::nullopt;
    const auto command = args[family == "codex" && name == "exec_command" ? "cmd" : "command"];
    if (command.isString()) return command.string();
    if (family == "codex" && in(name, {"shell", "container.exec", "local_shell"}) && command.isArray() && command.size() > 0) {
        Words parts;
        for (const auto& a : command.elements()) {
            if (!a.isString()) return std::nullopt;
            const auto arg = *a.string();
            if (fullMatch(arg, "[A-Za-z0-9_@%+=:,./-]+")) {
                parts.push_back(arg);
            } else {
                parts.push_back("'" + replaceAll(arg, "'", "'\"'\"'") + "'");
            }
        }
        return join(parts, " ");
    }
    return std::nullopt;
}

std::string actionKind(SourceKind source, const std::optional<std::string>& tool, const json::Value& input, const std::optional<std::string>& cwd) {
    const auto family = familyOf(source);
    if (family.empty() || !tool) return "other";
    auto kind = kindOf(family, toolName(family, *tool));
    if (kind.empty()) kind = "other";
    if (kind != "command") return kind;
    const auto text = commandText(source, tool, input);
    return text ? shellKind(parseCommand(*text, cwd), cwd) : "command";
}

CommandInfo commandInfo(const std::string& text, const std::optional<std::string>& cwd) { return commandInfoOf(parseCommand(text, cwd), cwd); }

std::optional<MaskedStatus> maskedStatus(const std::string& text, const std::string& output, const std::optional<std::string>& cwd) {
    const auto parsed = parseCommand(text, cwd);
    const auto info = commandInfoOf(parsed, cwd);
    if (!info.masked) return std::nullopt;
    const auto k = choose(parsed.segments, cwd).best;
    std::optional<std::string> last;
    for (const auto& x : split(lf(output), '\n')) {
        const auto t = strip(x, " \t");
        if (!t.empty()) last = t;
    }
    const auto echo = k ? statusEcho(parsed, *k) : std::nullopt;
    if (echo && last) {
        if (echo->form == "printed") {
            if (const auto value = printedValue(*last, echo->prefix, echo->suffix)) return MaskedStatus{value, *value != 0};
        } else {
            if (echo->form == "branches" && *last == strip(echo->ok, " \t")) return MaskedStatus{0, false};
            const auto& [prefix, suffix] = echo->fail;
            if (!suffix) {
                if (*last == strip(prefix, " \t")) return MaskedStatus{std::nullopt, true};
            } else if (const auto value = printedValue(*last, prefix, *suffix); value && *value != 0) {
                return MaskedStatus{value, true};
            }
        }
    }
    if (info.errorFilter && silent(output)) return MaskedStatus{std::nullopt, false};
    return std::nullopt;
}

}  // namespace agentspeed::tasks
