#include "internal.hpp"

// Package scripts and task runners (TASK-PARSERS 3.6).
namespace agentspeed::tasks::detail {

const Names kNpmBuiltins = {
    "install", "i", "ci", "add", "uninstall", "remove", "rm", "update", "up", "outdated", "ls", "list", "link", "publish", "pack", "init",
    "create", "audit", "config", "cache", "version", "view", "info", "why", "explain", "dedupe", "prune", "rebuild", "start", "restart",
    "stop", "store", "fetch", "patch", "import", "deploy", "licenses", "doctor", "setup", "env", "approve-builds", "self-update",
    "workspaces", "workspace", "dlx", "exec", "x", "set", "plugin", "constraints", "unplug", "stage", "npm", "node", "upgrade", "pm",
    "repl", "help", "login", "logout", "whoami", "search", "owner", "access", "token", "team", "org", "profile", "star", "stars",
    "unstar", "fund", "sbom", "query", "run-script", "run", "rum", "urn", "test", "t", "tst", "build", "global"};

namespace {

constexpr Names kPmNarrow = {"-w", "--workspace", "--filter", "-F"};
constexpr Names kPmValue = {"-w", "--workspace", "--filter", "-F", "--prefix", "-C", "--dir", "--cwd"};

std::pair<std::string, std::string> scriptLabel(const std::string& runner) {
    static const std::map<std::string, std::pair<std::string, std::string>> kLabels = {
        {"npm", {"npm test", "npm run "}},       {"pnpm", {"pnpm test", "pnpm run "}},   {"yarn", {"yarn test", "yarn run "}},
        {"bun", {"bun run test", "bun run "}},   {"deno", {"deno task test", "deno task "}}, {"composer", {"composer test", "composer "}},
        {"make", {"make test", "make "}},        {"ninja", {"ninja test", "ninja "}},    {"just", {"just test", "just "}},
        {"task", {"task test", "task "}},        {"rake", {"rake test", "rake "}},       {"mise", {"mise run test", "mise run "}},
        {"turbo", {"turbo test", "turbo "}},     {"nx", {"nx test", "nx "}}};
    return kLabels.at(runner);
}

std::pair<std::string, std::optional<std::string>> scriptClass(const std::string& runner, const std::string& name) {
    static const std::map<std::string, std::string> kAliases = {{"t", "test"},          {"tst", "test"},          {"tests", "test"},
                                                               {"type-check", "typecheck"}, {"types", "typecheck"}, {"compile", "build"}};
    static const std::map<std::string, std::string> kCanon = {
        {"test", "test"}, {"build", "build"}, {"lint", "check"}, {"typecheck", "check"}, {"check", "check"}};
    const auto n = lowerAscii(name);
    const auto alias = kAliases.find(n);
    const auto canon = alias == kAliases.end() ? n : alias->second;
    if ((runner == "make" || runner == "ninja") && canon == "check") return {"test", runner == "make" ? "make check" : "ninja check"};
    if ((runner == "make" || runner == "ninja") && canon == "all") return {"build", runner};
    if (const auto it = kCanon.find(canon); it != kCanon.end()) {
        const auto [testLabel, prefix] = scriptLabel(runner);
        return {it->second, canon == "test" ? testLabel : prefix + canon};
    }
    std::string first;
    for (const char c : n) {
        if (std::string_view(":._/-").find(c) != std::string_view::npos) break;
        first += c;
    }
    if (in(first, {"test", "tests", "spec", "specs", "unit", "e2e", "integration", "coverage", "cov", "jest", "vitest", "mocha", "pytest",
                   "ctest", "playwright", "cypress"})) {
        return {"test", std::nullopt};
    }
    if (in(first, {"build", "compile", "bundle", "dist", "all"})) return {"build", std::nullopt};
    if (in(first, {"lint", "typecheck", "types", "tsc", "check", "eslint", "ruff", "mypy", "pyright", "clippy", "vet", "analyze", "analyse",
                   "verify", "validate"})) {
        return {"check", std::nullopt};
    }
    return {"other", std::nullopt};
}

bool forwardedTargeted(const Words& args, const std::optional<std::string>& cwd) {
    static constexpr Names kFilters = {"-t", "--testNamePattern", "-k", "--grep", "-g", "--filter", "-run", "--run", "--testPathPattern", "--spec",
                                       "--tests", "-E", "-R", "--findRelatedTests", "--only-testing", "-only-testing", "--skip", "--exact", "-p",
                                       "--package", "-m"};
    static constexpr Names kValue = {"--reporter", "-r", "--config", "-c", "--maxWorkers", "-w", "--timeout", "--testTimeout", "--env",
                                     "--outputFile", "--coverageReporters", "--pool", "--environment", "--seed", "--tb", "-n", "--workers",
                                     "-j", "--jobs"};
    Words words;
    for (const auto& a : args) {
        if (a != "--") words.push_back(a);
    }
    std::size_t k = 0;
    while (k < words.size()) {
        const auto& w = words[k];
        bool filter = in(w, kFilters);
        for (const auto f : kFilters) filter |= startsWith(f, "--") && startsWith(w, std::string(f) + "=");
        if (filter) return true;
        if (startsWith(w, "-")) {
            k += in(w, kValue) ? 2 : 1;
            continue;
        }
        if (nameTargeted({w}, cwd)) return true;
        ++k;
    }
    return false;
}

Classified packageManager(const std::string& runner, const Words& args, const std::optional<std::string>& cwd) {
    std::size_t k = 0;
    bool narrow = false;
    while (k < args.size() && startsWith(args[k], "-")) {
        const auto& w = args[k];
        if (in(w, kPmValue)) {
            narrow = narrow || in(w, kPmNarrow);
            k += 2;
            continue;
        }
        if (startsWith(w, "--workspace=") || startsWith(w, "--filter=")) narrow = true;
        ++k;
    }
    auto rest = slice(args, k);
    if (rest.empty()) return {};
    const auto sub = rest[0];
    if (runner == "yarn" && sub == "workspace" && rest.size() >= 3) {
        auto inner = packageManager(runner, slice(rest, 2), cwd);
        inner.targeted = true;
        return inner;
    }
    if (runner == "bun" && sub == "test") {
        const auto o = parseOpts(slice(rest, 1), {"-t", "--test-name-pattern", "--timeout", "--preload", "--coverage-reporter", "--reporter",
                                                  "--rerun-each"});
        return {"test", "bun test", o.any({"-t", "--test-name-pattern"}) || nameTargeted(o.posAndAfter(), cwd)};
    }
    if (runner == "bun" && sub == "build") return {"build", "bun build", false};
    std::size_t dashdash = rest.size();
    for (std::size_t j = 0; j < rest.size(); ++j) {
        if (rest[j] == "--") {
            dashdash = j;
            break;
        }
    }
    const auto before = slice(rest, 1, dashdash);
    bool narrowBefore = false;
    for (const auto& w : before) narrowBefore |= in(w, kPmNarrow) || startsWith(w, "--workspace=") || startsWith(w, "--filter=");
    if (narrowBefore) {
        narrow = true;
        Words kept;
        for (std::size_t j = 0; j < rest.size(); ++j) {
            const auto& w = rest[j];
            const bool drop = in(w, kPmNarrow) || (j > 0 && in(rest[j - 1], kPmNarrow) && j <= before.size()) || startsWith(w, "--workspace=") ||
                              startsWith(w, "--filter=");
            if (!drop) kept.push_back(w);
        }
        rest = kept;
    }
    std::string name;
    Words forwarded;
    if (in(sub, {"test", "t", "tst"})) {
        name = "test";
        forwarded = slice(rest, 1);
    } else if (in(sub, {"run", "run-script", "rum", "urn"})) {
        if (rest.size() < 2) return {};
        const auto j = dropOptions(slice(rest, 1), 0, kPmNarrow, false) + 1;
        if (j >= rest.size()) return {};
        name = rest[j];
        forwarded = slice(rest, j + 1);
    } else if (in(runner, {"pnpm", "yarn", "bun"}) && !in(sub, kNpmBuiltins) && !hasExtension(sub)) {
        const auto p = prog(sub);
        if (isKnownProgram(p) && !isScriptRunner(p)) return classify(rest, cwd);
        name = sub;
        forwarded = slice(rest, 1);
    } else {
        return {};
    }
    const auto [cls, suite] = scriptClass(runner, name);
    if (cls == "other") return {};
    return {cls, suite, narrow || forwardedTargeted(forwarded, cwd)};
}

Classified runner(const std::string& runnerName, const Words& args, const std::optional<std::string>& cwd, Names valueOpts,
                  std::optional<std::pair<std::string, std::string>> fallback = std::nullopt) {
    static const std::map<std::string, Words> kSubcommands = {
        {"mise", {"run", "r"}}, {"deno", {"task"}}, {"turbo", {"run"}}, {"composer", {"run-script", "run"}}};
    auto rest = args;
    if (const auto it = kSubcommands.find(runnerName); it != kSubcommands.end() && !rest.empty() && containsWord(it->second, rest[0])) {
        rest = slice(rest, 1);
    }
    if (runnerName == "nx" && !rest.empty() && (rest[0] == "run-many" || rest[0] == "affected")) {
        const auto o = parseOpts(slice(rest, 1), {"-t", "--target", "--targets", "-p", "--projects"});
        auto target = o.value("-t");
        if (!target) target = o.value("--target");
        if (!target) target = o.value("--targets");
        if (!target) return {};
        const auto [cls, suite] = scriptClass(runnerName, split(*target, ',')[0]);
        if (cls == "other") return {};
        return {cls, suite, o.value("-p").has_value() || o.value("--projects").has_value()};
    }
    if (runnerName == "nx" && !rest.empty() && rest[0] == "run" && rest.size() >= 2 && contains(rest[1], ":")) {
        const auto [cls, suite] = scriptClass(runnerName, split(rest[1], ':')[1]);
        if (cls == "other") return {};
        return {cls, suite, true};
    }
    Words pos, assigns;
    std::size_t k = 0;
    while (k < rest.size()) {
        const auto& w = rest[k];
        if (startsWith(w, "-")) {
            if (in(w, valueOpts)) {
                k += runnerName == "just" && w == "--set" ? 3 : 2;
                continue;
            }
            ++k;
            continue;
        }
        if (prefixMatch(w, "[A-Za-z_][A-Za-z0-9_]*=")) {
            assigns.push_back(w.substr(0, w.find('=')));
        } else {
            pos.push_back(w);
        }
        ++k;
    }
    static constexpr Names kComposerBuiltins = {
        "install", "update", "require", "remove", "dump-autoload", "dumpautoload", "show", "outdated", "validate", "create-project", "init",
        "config", "global", "exec", "search", "why", "why-not", "licenses", "audit", "status", "archive", "diagnose", "self-update",
        "depends", "prohibits", "fund", "reinstall", "bump", "check-platform-reqs", "clear-cache", "browse", "suggests"};
    if (runnerName == "composer" && !pos.empty() && in(pos[0], kComposerBuiltins)) return {};
    if (pos.empty()) {
        if (!fallback) return {};
        return {fallback->first, fallback->second, false};
    }
    std::string cls;
    std::optional<std::string> suite;
    bool narrow = false;
    if (in(runnerName, {"make", "ninja", "rake"})) {
        std::optional<std::pair<std::string, std::optional<std::string>>> best;
        for (const auto& target : pos) {
            auto candidate = scriptClass(runnerName, target);
            if (!best || classRank(candidate.first) > classRank(best->first)) best = candidate;
        }
        cls = best->first;
        suite = best->second;
        for (const auto& a : assigns) {
            narrow |= fullMatch(upperAscii(a),
                                "TESTS?|T|TEST_?ARGS|TEST_?FILTER|FILTER|RUN|ONLY|K|PKG|PACKAGE|FILES?|SPEC|ARGS|PYTEST_?ARGS|TESTNAME|CASE|TESTOPTS");
        }
    } else {
        std::tie(cls, suite) = scriptClass(runnerName, pos[0]);
        narrow = pos.size() > 1 && (runnerName == "just" || forwardedTargeted(slice(pos, 1), cwd));
        if (runnerName == "nx" && pos.size() > 1) narrow = true;
    }
    if (cls == "other") return {};
    return {cls, suite, narrow};
}

Classified deno(const Words& args, const std::optional<std::string>& cwd) {
    if (args.empty()) return {};
    const auto& sub = args[0];
    const auto rest = slice(args, 1);
    if (sub == "test") {
        const auto o = parseOpts(rest, {"--filter", "--allow-read", "--allow-net", "--allow-env", "--config", "-c", "--reporter", "--coverage",
                                        "--jobs", "--shuffle", "--seed"});
        return {"test", "deno test", o.has("--filter") || pathTargeted(o.posAndAfter(), cwd)};
    }
    if (sub == "check") return {"check", "deno check", false};
    if (sub == "lint") return {"check", "deno lint", false};
    if (sub == "fmt" && containsWord(rest, "--check")) return {"check", "deno fmt", false};
    if (sub == "task") return runner("deno", args, cwd, {});
    return {};
}

}  // namespace

bool isScriptRunner(const std::string& program) {
    return in(program, {"npm", "pnpm", "yarn", "bun", "deno", "composer", "make", "ninja", "just", "task", "rake", "mise", "turbo", "nx"});
}

std::optional<Classified> classifyScripted(const std::string& p, const Words& args, const std::optional<std::string>& cwd) {
    if (in(p, {"npm", "pnpm", "yarn", "bun"})) return packageManager(p, args, cwd);
    if (p == "make") {
        return runner(p, args, cwd, {"-C", "--directory", "-f", "--file", "--makefile", "-I", "--include-dir", "-o", "--old-file", "-W", "--what-if", "-j", "-l"},
                      std::make_pair(std::string("build"), std::string("make")));
    }
    if (p == "ninja") return runner(p, args, cwd, {"-C", "-f", "-j", "-k", "-l", "-d", "-t", "-w"}, std::make_pair(std::string("build"), std::string("ninja")));
    if (p == "just") {
        return runner(p, args, cwd, {"-f", "--justfile", "-d", "--working-directory", "--set", "--dotenv-path", "--dotenv-filename", "--shell",
                                     "--shell-arg", "--chooser", "--color", "--command", "--list-heading", "--list-prefix"});
    }
    if (p == "task") return runner(p, args, cwd, {"-d", "--dir", "-t", "--taskfile", "-o", "--output", "-c", "--color"});
    if (p == "rake") return runner(p, args, cwd, {"-f", "--rakefile", "-I", "--libdir", "-r", "--require", "-C", "--directory"});
    if (p == "mise") return runner(p, args, cwd, {"-C", "--cd", "-E", "--env", "-j", "--jobs"});
    if (p == "turbo") return runner(p, args, cwd, {"--filter", "-F", "--concurrency", "--output-logs", "--cache-dir"});
    if (p == "nx") return runner(p, args, cwd, {"-c", "--configuration", "--parallel", "--output-style"});
    if (p == "composer") return runner(p, args, cwd, {"-d", "--working-dir"});
    if (p == "deno") return deno(args, cwd);
    return std::nullopt;
}

Classified classify(const Words& words, const std::optional<std::string>& cwd) {
    if (words.empty()) return {};
    const auto result = classifyProgram(prog(words[0]), slice(words, 1), cwd);
    return result ? *result : Classified{};
}

}  // namespace agentspeed::tasks::detail
