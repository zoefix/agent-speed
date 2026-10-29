#include "internal.hpp"

// The shell tokenizer, segments, prefix commands and runner wrappers (TASK-PARSERS 3.2–3.4).
namespace agentspeed::tasks::detail {

namespace {

std::pair<std::string, std::size_t> readHeredoc(std::string_view text, std::size_t i, const std::string& delim, bool stripTabs) {
    std::string body;
    const auto n = text.size();
    while (i < n) {
        const auto end = text.find('\n', i);
        const auto line = end == std::string_view::npos ? text.substr(i) : text.substr(i, end - i);
        i = end == std::string_view::npos ? n : end + 1;
        if ((stripTabs ? lstrip(line, "\t") : std::string(line)) == delim) return {body, i};
        body += line;
        body += '\n';
    }
    return {body, n};
}

// REDIR = ([0-9]*)(<<<|<<-|<<|>>|>&|<&|>\||<>|&>>|&>|>|<) at i: the operator and the end.
std::optional<std::pair<std::string, std::size_t>> redirectAt(std::string_view text, std::size_t i) {
    std::size_t j = i;
    while (j < text.size() && text[j] >= '0' && text[j] <= '9') ++j;
    for (const char* op : {"<<<", "<<-", "<<", ">>", ">&", "<&", ">|", "<>", "&>>", "&>", ">", "<"}) {
        const std::string_view o(op);
        if (text.substr(j, o.size()) == o) return std::make_pair(std::string(o), j + o.size());
    }
    return std::nullopt;
}

}  // namespace

std::pair<std::string, std::size_t> readWord(std::string_view text, std::size_t i) {
    const auto n = text.size();
    std::string out;
    while (i < n) {
        const char c = text[i];
        if (std::string_view(" \t\n;&|()<>").find(c) != std::string_view::npos) break;
        if (c == '\\') {
            if (i + 1 < n) {
                if (text[i + 1] != '\n') out += text[i + 1];
                i += 2;
            } else {
                i += 1;
            }
            continue;
        }
        if (c == '\'') {
            auto end = text.find('\'', i + 1);
            if (end == std::string_view::npos) end = n;
            out += text.substr(i + 1, end - i - 1);
            i = end + 1;
            continue;
        }
        if (c == '$' && i + 1 < n && text[i + 1] == '\'') {
            std::size_t j = i + 2;
            while (j < n && text[j] != '\'') {
                if (text[j] == '\\' && j + 1 < n) {
                    out += text[j + 1];
                    j += 2;
                    continue;
                }
                out += text[j];
                ++j;
            }
            i = j + 1;
            continue;
        }
        if (c == '"') {
            std::size_t j = i + 1;
            while (j < n && text[j] != '"') {
                if (text[j] == '\\' && j + 1 < n && std::string_view("\"\\$`\n").find(text[j + 1]) != std::string_view::npos) {
                    if (text[j + 1] != '\n') out += text[j + 1];
                    j += 2;
                    continue;
                }
                out += text[j];
                ++j;
            }
            i = j + 1;
            continue;
        }
        if (c == '$' && i + 1 < n && (text[i + 1] == '(' || text[i + 1] == '{')) {
            const char open = text[i + 1];
            const char close = open == '(' ? ')' : '}';
            int depth = 0;
            std::size_t j = i + 1;
            while (j < n) {
                if (text[j] == open) {
                    ++depth;
                } else if (text[j] == close) {
                    if (--depth == 0) break;
                }
                ++j;
            }
            out += text.substr(i, std::min(j + 1, n) - i);
            i = j + 1;
            continue;
        }
        if (c == '`') {
            auto end = text.find('`', i + 1);
            if (end == std::string_view::npos) end = n;
            out += text.substr(i, std::min(end + 1, n) - i);
            i = end + 1;
            continue;
        }
        out += c;
        ++i;
    }
    return {out, i};
}

std::vector<Token> tokenize(std::string_view text) {
    std::vector<Token> items;
    struct Pending {
        std::string delim;
        bool strip;
        std::size_t index;
    };
    std::vector<Pending> pending;
    std::size_t i = 0;
    const auto n = text.size();
    while (i < n) {
        const char c = text[i];
        if (c == ' ' || c == '\t') {
            ++i;
            continue;
        }
        if (c == '\\' && i + 1 < n && text[i + 1] == '\n') {
            i += 2;
            continue;
        }
        if (c == '\n') {
            items.push_back({Token::Kind::Op, ";", {}});
            ++i;
            for (const auto& p : pending) {
                auto [body, next] = readHeredoc(text, i, p.delim, p.strip);
                i = next;
                items[p.index] = {Token::Kind::Heredoc, body, {}};
            }
            pending.clear();
            continue;
        }
        if (c == '#') {
            const auto end = text.find('\n', i);
            i = end == std::string_view::npos ? n : end;
            continue;
        }
        const auto two = text.substr(i, 2);
        if (two == "&&" || two == "||" || two == "|&" || two == ";;") {
            items.push_back({Token::Kind::Op, two == "|&" ? "|" : two == ";;" ? ";" : std::string(two), {}});
            i += 2;
            continue;
        }
        if (const auto redirect = redirectAt(text, i)) {
            const auto op = redirect->first;
            i = redirect->second;
            while (i < n && (text[i] == ' ' || text[i] == '\t')) ++i;
            auto [word, next] = readWord(text, i);
            i = next;
            if (op == "<<" || op == "<<-") {
                items.push_back({Token::Kind::Heredoc, "", {}});
                pending.push_back({word, op == "<<-", items.size() - 1});
                continue;
            }
            items.push_back({Token::Kind::Redir, op, word});
            continue;
        }
        if (c == ';' || c == '&' || c == '|' || c == '(' || c == ')') {
            items.push_back({Token::Kind::Op, std::string(1, c), {}});
            ++i;
            continue;
        }
        auto [word, next] = readWord(text, i);
        i = next;
        items.push_back({Token::Kind::Word, word, {}});
    }
    for (const auto& p : pending) items[p.index] = {Token::Kind::Heredoc, "", {}};
    return items;
}

std::pair<std::vector<Segment>, Words> splitSegments(const std::vector<Token>& items) {
    std::vector<Segment> segments;
    Segment current;
    Words ops;
    for (const auto& item : items) {
        switch (item.kind) {
            case Token::Kind::Word: current.words.push_back(item.text); break;
            case Token::Kind::Redir: current.redirs.emplace_back(item.text, item.target); break;
            case Token::Kind::Heredoc: current.heredocs.push_back(item.text); break;
            case Token::Kind::Op:
                if (!current.words.empty() || !current.heredocs.empty()) {
                    current.ops = ops;
                    segments.push_back(current);
                    ops.clear();
                }
                current = Segment();
                ops.push_back(item.text);
                break;
        }
    }
    if (!current.words.empty() || !current.heredocs.empty()) {
        current.ops = ops;
        segments.push_back(current);
        ops.clear();
    }
    return {segments, ops};
}

std::string prog(std::string_view word) {
    const auto slash = word.find_last_of("/\\");
    auto base = lowerAscii(slash == std::string_view::npos ? word : word.substr(slash + 1));
    for (const char* ext : {".exe", ".cmd", ".bat", ".ps1"}) {
        if (endsWith(base, ext)) {
            base.resize(base.size() - std::string_view(ext).size());
            break;
        }
    }
    if (fullMatch(base, "python[0-9.]*|py|pypy[0-9.]*")) return "python";
    if (fullMatch(base, "(gcc|g\\+\\+|cc|c\\+\\+|clang|clang\\+\\+)(-[0-9.]+)?")) return "cc";
    static const std::map<std::string, std::string> kAliases = {{"gradlew", "gradle"}, {"mvnw", "mvn"},     {"py.test", "pytest"},
                                                               {"nodejs", "node"},    {"gmake", "make"},   {"bazelisk", "bazel"},
                                                               {"msbuild", "msbuild"}, {"pnpx", "pnpx"}};
    const auto it = kAliases.find(base);
    return it == kAliases.end() ? base : it->second;
}

std::size_t dropOptions(const Words& words, std::size_t i, Names withValue, bool stopDashdash) {
    while (i < words.size()) {
        const auto& w = words[i];
        if (w == "--" && stopDashdash) return i + 1;
        if (!startsWith(w, "-") || w == "-") return i;
        i += in(w, withValue) ? 2 : 1;
    }
    return i;
}

void stripPrefixes(Segment& seg) {
    const auto& words = seg.words;
    std::size_t i = 0;
    while (i < words.size()) {
        const auto& w = words[i];
        if (prefixMatch(w, "[A-Za-z_][A-Za-z0-9_]*=")) {
            ++i;
            continue;
        }
        const auto p = prog(w);
        if (p == "env") {
            i = dropOptions(words, i + 1, {"-u", "--unset", "-C", "--chdir", "-S", "--split-string"});
        } else if (p == "sudo" || p == "doas") {
            i = dropOptions(words, i + 1, {"-u", "-g", "-h", "-p", "-C", "-D", "-r", "-t", "-U", "--user", "--group"});
        } else if (p == "time") {
            i = dropOptions(words, i + 1);
        } else if (p == "nice") {
            i = dropOptions(words, i + 1, {"-n", "--adjustment"});
        } else if (p == "nohup" || p == "setsid") {
            seg.background = true;
            i = dropOptions(words, i + 1);
        } else if (in(p, {"exec", "command", "builtin", "noglob", "unbuffer", "chronic"})) {
            i = dropOptions(words, i + 1);
        } else if (p == "stdbuf") {
            i = dropOptions(words, i + 1, {"-i", "-o", "-e"});
        } else if (p == "caffeinate") {
            i = dropOptions(words, i + 1, {"-t", "-w"});
        } else if (p == "xvfb-run") {
            i = dropOptions(words, i + 1,
                            {"-s", "--server-args", "-n", "--server-num", "-e", "--error-file", "-f", "--auth-file", "-p", "--xauth-protocol"});
        } else if (p == "timeout" || p == "gtimeout") {
            i = dropOptions(words, i + 1, {"-s", "--signal", "-k", "--kill-after"}) + 1;
        } else {
            break;
        }
    }
    seg.words = slice(words, i);
}

void dropKeywords(Segment& seg) {
    auto& words = seg.words;
    std::size_t start = 0;
    while (start < words.size() && in(words[start], {"!", "if", "then", "else", "elif", "do", "while", "until", "{"})) ++start;
    words = slice(words, start);
    while (!words.empty() && words.back() == "}") words.pop_back();
}

std::optional<std::string> shellScript(const Segment& seg) {
    if (seg.words.empty()) return std::nullopt;
    const auto p = prog(seg.words[0]);
    const auto rest = slice(seg.words, 1);
    if (in(p, {"bash", "sh", "zsh", "dash", "ksh", "fish"})) {
        for (std::size_t k = 0; k < rest.size(); ++k) {
            if (!startsWith(rest[k], "-")) return std::nullopt;
            if (fullMatch(rest[k], "-[A-Za-z]*c[A-Za-z]*") && k + 1 < rest.size()) return rest[k + 1];
        }
        return std::nullopt;
    }
    if (p == "pwsh" || p == "powershell") {
        for (std::size_t k = 0; k < rest.size(); ++k) {
            const auto lower = lowerAscii(rest[k]);
            if ((lower == "-command" || lower == "-c") && k + 1 < rest.size()) return join(slice(rest, k + 1), " ");
        }
        return std::nullopt;
    }
    if (p == "cmd") {
        for (std::size_t k = 0; k < rest.size(); ++k) {
            if (lowerAscii(rest[k]) == "/c" && k + 1 < rest.size()) return join(slice(rest, k + 1), " ");
        }
    }
    return std::nullopt;
}

std::size_t skipUntilModule(const Words& words, Names valueOpts) {
    std::size_t k = 0;
    while (k < words.size() && startsWith(words[k], "-") && words[k] != "-m" && words[k] != "-") k += in(words[k], valueOpts) ? 2 : 1;
    return k;
}

Words unwrap(Words words) {
    static constexpr Names kNpx = {"-p", "--package", "-c", "--call", "--cache", "--userconfig"};
    for (int round = 0; round < 8; ++round) {
        if (words.empty()) return words;
        const auto p = prog(words[0]);
        const auto rest = slice(words, 1);
        const auto first = rest.empty() ? std::string() : rest[0];
        if (in(p, {"npx", "pnpx", "bunx"})) {
            words = slice(rest, dropOptions(rest, 0, kNpx));
            continue;
        }
        if (in(p, {"npm", "pnpm", "yarn", "bun"}) && in(first, {"exec", "dlx", "x"}) && !rest.empty()) {
            const auto tail = slice(rest, 1);
            words = slice(tail, dropOptions(tail, 0, kNpx));
            continue;
        }
        if (p == "mise" && (first == "exec" || first == "x") && !rest.empty()) {
            std::size_t k = 1;
            for (std::size_t j = 0; j < rest.size(); ++j) {
                if (rest[j] == "--") {
                    k = j + 1;
                    break;
                }
            }
            words = slice(rest, k);
            continue;
        }
        if (in(p, {"uv", "poetry", "pipenv", "pdm", "rye", "pixi", "conda", "mamba", "micromamba", "hatch"}) && first == "run" && !rest.empty()) {
            const auto tail = slice(rest, 1);
            words = slice(tail, dropOptions(tail, 0,
                                            {"--with", "--with-requirements", "--with-editable", "--python", "-p", "--extra", "--group",
                                             "--only-group", "--package", "--directory", "--project", "--env-file", "--index", "--index-url",
                                             "--default-index", "--extra-index-url", "-C", "--config-setting", "--no-group", "--script",
                                             "--gui-script", "-n", "--name", "-p", "--prefix", "--cwd"}));
            continue;
        }
        if (p == "python" || (p == "coverage" && first == "run" && !rest.empty())) {
            const auto opts = p == "python" ? rest : slice(rest, 1);
            const auto k = p == "python" ? skipUntilModule(opts, {"-W", "-X", "--check-hash-based-pycs"})
                                         : skipUntilModule(opts, {"--source", "--rcfile", "--data-file", "--concurrency", "--include",
                                                                  "--omit", "--context"});
            if (k + 1 < opts.size() && opts[k] == "-m") {
                words = slice(opts, k + 1);
                continue;
            }
            return words;
        }
        if (p == "bundle" && first == "exec" && !rest.empty()) {
            words = slice(rest, 1);
            continue;
        }
        if (p == "xcrun") {
            words = slice(rest, dropOptions(rest, 0, {"--sdk", "-sdk", "--toolchain", "-toolchain"}));
            continue;
        }
        if (p == "dotnet" && rest.size() >= 2 && rest[0] == "tool" && rest[1] == "run") {
            words = slice(rest, 2);
            continue;
        }
        if (p == "php" && !rest.empty() && !startsWith(rest[0], "-") && in(prog(rest[0]), {"phpunit", "pest", "phpstan", "psalm"})) {
            words = rest;
            continue;
        }
        return words;
    }
    return words;
}

}  // namespace agentspeed::tasks::detail
