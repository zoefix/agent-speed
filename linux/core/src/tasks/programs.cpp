#include <functional>

#include "internal.hpp"

// Classification of known programs (TASK-PARSERS 3.5): class, suite and whether the run is narrowed.
namespace agentspeed::tasks::detail {

int classRank(std::string_view cls) {
    if (cls == "check") return 1;
    if (cls == "build") return 2;
    if (cls == "test") return 3;
    return 0;
}

bool hasExtension(std::string_view word) {
    const auto dot = word.rfind('.');
    if (dot == std::string_view::npos) return false;
    const auto ext = word.substr(dot + 1);
    if (ext.empty() || ext.size() > 10) return false;
    for (const char c : ext) {
        if (!std::isalnum(static_cast<unsigned char>(c))) return false;
    }
    return true;
}

bool isBroadPath(const std::string& arg, const std::optional<std::string>& cwd) {
    auto a = cwdRelative(replaceAll(arg, "\\", "/"), cwd).first;
    while (startsWith(a, "./")) a = a.substr(2);
    a = rstrip(a, "/");
    if (a.empty() || a == ".") return true;
    if (contains(a, "*") || contains(a, "?") || contains(a, "::") || startsWith(a, "/") || prefixMatch(a, "[A-Za-z]:")) return false;
    return !contains(a, "/") && !hasExtension(a);
}

Opts parseOpts(const Words& args, Names valueOpts, bool stopDashdash) {
    Opts out;
    for (std::size_t k = 0; k < args.size(); ++k) {
        const auto& w = args[k];
        if (w == "--" && stopDashdash) {
            out.after = slice(args, k + 1);
            break;
        }
        if (startsWith(w, "-") && w != "-") {
            const auto eq = w.find('=');
            if (eq != std::string::npos && startsWith(w, "--")) {
                out.opts[w.substr(0, eq)] = w.substr(eq + 1);
            } else if (in(w, valueOpts) && k + 1 < args.size()) {
                out.opts[w] = args[k + 1];
                ++k;
            } else {
                std::optional<std::string> matched;
                for (const auto o : valueOpts) {
                    if (o.size() == 2 && startsWith(w, o) && w.size() > 2) {
                        matched = std::string(o);
                        break;
                    }
                }
                if (matched) {
                    out.opts[*matched] = w.substr(2);
                } else {
                    out.opts[w] = std::nullopt;
                }
            }
        } else {
            out.pos.push_back(w);
        }
    }
    return out;
}

bool pathTargeted(const Words& pos, const std::optional<std::string>& cwd) {
    for (const auto& p : pos) {
        if (!isBroadPath(p, cwd)) return true;
    }
    return false;
}

bool nameTargeted(const Words& pos, const std::optional<std::string>& cwd) {
    for (const auto& p : pos) {
        const auto [a, inside] = cwdRelative(rstrip(replaceAll(p, "\\", "/"), "/"), cwd);
        if (a != "" && a != "." && !(inside && a.empty())) return true;
    }
    return false;
}

namespace {

using Handler = std::function<Classified(const Words&, const std::optional<std::string>&)>;
const Classified kOther{};

Classified make(std::string cls, std::optional<std::string> suite, bool targeted) { return {std::move(cls), std::move(suite), targeted}; }

Classified cPytest(const Words& args, const std::optional<std::string>& cwd) {
    const auto o = parseOpts(args, {"-k", "-m", "-p", "-c", "-o", "-n", "-W", "-r", "--maxfail", "--tb", "--rootdir", "--basetemp", "--junitxml",
                                    "--junit-xml", "--durations", "--deselect", "--ignore", "--ignore-glob", "--log-level", "--log-cli-level",
                                    "--timeout", "--confcutdir", "--import-mode", "--dist", "--cov", "--cov-report", "--cov-config",
                                    "--cov-fail-under", "--reruns", "--reruns-delay", "--count", "-p", "--override-ini", "--capture",
                                    "--html", "--randomly-seed"});
    return make("test", "pytest",
                o.any({"-k", "-m", "--lf", "--last-failed", "--sw", "--stepwise", "--deselect", "--stepwise-skip"}) ||
                    pathTargeted(o.posAndAfter(), cwd));
}

Classified cUnittest(const Words& args, const std::optional<std::string>&) {
    const auto o = parseOpts(args, {"-s", "-t", "-p", "-k", "--start-directory", "--top-level-directory", "--pattern"});
    bool any = false;
    for (const auto& p : o.pos) any |= p != "discover";
    return make("test", "unittest", any || o.has("-k"));
}

Classified cCargo(const Words& args, const std::optional<std::string>&) {
    static constexpr Names kGlobal = {"--color", "--config", "-Z", "-C", "--manifest-path"};
    static constexpr Names kValue = {"-p", "--package", "--test", "--bin", "--example", "--bench", "-F", "--features", "--target",
                                     "--target-dir", "--manifest-path", "-j", "--jobs", "--profile", "--exclude", "--color",
                                     "--message-format", "--config", "-Z", "--timings"};
    static constexpr Names kNarrow = {"-p", "--package", "--test", "--lib", "--bin", "--bins", "--example", "--examples", "--doc", "--bench",
                                      "--benches"};
    std::size_t k = 0;
    while (k < args.size() && (startsWith(args[k], "-") || startsWith(args[k], "+"))) k += in(args[k], kGlobal) ? 2 : 1;
    if (k >= args.size()) return kOther;
    const auto& sub = args[k];
    const auto rest = slice(args, k + 1);
    if (sub == "test" || sub == "t") {
        const auto o = parseOpts(rest, kValue);
        const auto a = parseOpts(o.after, {"--skip", "--test-threads", "--format", "--color", "-Z", "--logfile", "--report-time"}, false);
        return make("test", "cargo test", o.any(kNarrow) || !o.pos.empty() || !a.pos.empty() || a.any({"--exact", "--skip"}));
    }
    if (sub == "nextest" && !rest.empty() && (rest[0] == "run" || rest[0] == "r")) {
        const auto o = parseOpts(slice(rest, 1), {"-E", "--filterset", "--filter-expr", "-p", "--package", "--test", "--bin", "--profile", "-P",
                                                  "-j", "--test-threads", "--retries", "--partition", "--archive-file", "--cargo-profile",
                                                  "-F", "--features", "--target-dir", "--manifest-path"});
        return make("test", "cargo nextest",
                    !o.pos.empty() || o.any({"-E", "--filterset", "--filter-expr", "-p", "--package", "--test", "--lib", "--bin"}));
    }
    const auto o = parseOpts(rest, kValue);
    const bool narrow = o.any(kNarrow);
    if (in(sub, {"build", "b", "rustc"})) return make("build", "cargo build", narrow);
    if (sub == "check" || sub == "c") return make("check", "cargo check", narrow);
    if (sub == "clippy") return make("check", "cargo clippy", narrow);
    if (sub == "fmt" && containsWord(rest, "--check")) return make("check", "cargo fmt", false);
    return kOther;
}

Classified cSwift(const Words& args, const std::optional<std::string>&) {
    if (args.empty()) return kOther;
    const auto rest = slice(args, 1);
    if (args[0] == "test") {
        const auto o = parseOpts(rest, {"--filter", "--skip", "-s", "--specifier", "-c", "--configuration", "--package-path", "--scratch-path",
                                        "-j", "--jobs", "--num-workers", "--xunit-output", "--test-product", "--sanitize", "--triple", "--sdk"});
        return make("test", "swift test", o.any({"--filter", "--skip", "-s", "--specifier"}));
    }
    if (args[0] == "build") {
        const auto o = parseOpts(rest, {"--target", "--product", "-c", "--configuration", "--package-path", "--scratch-path", "-j", "--jobs",
                                        "--triple", "--sdk", "--arch"});
        return make("build", "swift build", o.any({"--target", "--product"}));
    }
    return kOther;
}

Classified cXcodebuild(const Words& args, const std::optional<std::string>&) {
    static constexpr Names kActions = {"build", "test", "build-for-testing", "test-without-building", "analyze", "archive", "clean",
                                       "install", "installsrc", "docbuild"};
    static constexpr Names kFlags = {"-quiet", "-verbose", "-json", "-allowProvisioningUpdates", "-allowProvisioningDeviceRegistration",
                                     "-skipUnavailableActions", "-hideShellScriptEnvironment", "-retry-tests-on-failure",
                                     "-skipPackagePluginValidation", "-skipMacroValidation", "-disableAutomaticPackageResolution",
                                     "-onlyUsePackageVersionsFromResolvedFile", "-showTestPlans", "-dry-run", "-UseModernBuildSystem",
                                     "-enableCodeCoverage", "-parallel-testing-enabled"};
    static constexpr Names kInfo = {"-list", "-showBuildSettings", "-version", "-showsdks", "-showdestinations", "-usage", "-help",
                                    "-resolvePackageDependencies", "-showTestPlans", "-showComponent"};
    Words actions;
    bool narrow = false;
    std::size_t k = 0;
    while (k < args.size()) {
        const auto& w = args[k];
        if (in(w, kInfo)) return kOther;
        if (startsWith(w, "-only-testing") || startsWith(w, "-skip-testing")) {
            narrow = true;
            k += contains(w, ":") ? 1 : 2;
            continue;
        }
        if (startsWith(w, "-")) {
            k += in(w, kFlags) || contains(w, ":") || contains(w, "=") ? 1 : 2;
            continue;
        }
        if (in(w, kActions)) actions.push_back(w);
        ++k;
    }
    if (containsWord(actions, "test") || containsWord(actions, "test-without-building")) return make("test", "xcodebuild test", narrow);
    if (containsWord(actions, "analyze")) return make("check", "xcodebuild analyze", false);
    if (!actions.empty() && std::all_of(actions.begin(), actions.end(), [](const std::string& a) { return a == "clean"; })) return kOther;
    return make("build", "xcodebuild build", false);
}

Classified cGo(const Words& args, const std::optional<std::string>&) {
    if (args.empty()) return kOther;
    const auto& sub = args[0];
    auto rest = slice(args, 1);
    for (std::size_t k = 0; k < rest.size(); ++k) {
        if (rest[k] == "-args") {
            rest.resize(k);
            break;
        }
    }
    Words norm;
    for (auto w : rest) {
        if (startsWith(w, "--") && w.size() > 2) w = w.substr(1);
        norm.push_back(w);
    }
    auto o = parseOpts(norm, {"-run", "-skip", "-bench", "-count", "-timeout", "-tags", "-p", "-parallel", "-cpu", "-coverprofile", "-coverpkg",
                              "-covermode", "-o", "-exec", "-benchtime", "-blockprofile", "-cpuprofile", "-memprofile", "-mutexprofile",
                              "-outputdir", "-trace", "-shuffle", "-fuzz", "-fuzztime", "-vet", "-ldflags", "-gcflags", "-mod", "-modfile",
                              "-C", "-asmflags", "-buildmode", "-compiler", "-gccgoflags", "-installsuffix", "-pkgdir", "-toolexec",
                              "-overlay", "-pgo"},
                       false);
    std::map<std::string, std::optional<std::string>> opts;
    for (const auto& [k, v] : o.opts) opts[split(k, '=')[0]] = v;
    o.opts = opts;
    bool narrow = false;
    for (const auto& p : o.pos) narrow |= !in(p, {"./...", "...", "all", "."});
    if (sub == "test") return make("test", "go test", narrow || o.any({"-run", "-skip", "-fuzz"}));
    if (sub == "build" || sub == "install") return make("build", "go build", narrow);
    if (sub == "vet") return make("check", "go vet", narrow);
    return kOther;
}

Classified cDotnet(const Words& args, const std::optional<std::string>&) {
    if (args.empty()) return kOther;
    const auto& sub = args[0];
    const auto o = parseOpts(slice(args, 1), {"--filter", "-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-o", "--output", "-v",
                                              "--verbosity", "-s", "--settings", "-l", "--logger", "--results-directory", "-a",
                                              "--test-adapter-path", "--collect", "-p", "--property"});
    if (sub == "test") return make("test", "dotnet test", o.has("--filter"));
    if (in(sub, {"build", "publish", "pack", "msbuild"})) return make("build", "dotnet " + sub, false);
    if (sub == "format" && o.has("--verify-no-changes")) return make("check", "dotnet format", false);
    return kOther;
}

std::string gradleTaskClass(const std::string& base) {
    if (base == "test" || fullMatch(base, "test[A-Z0-9].*") || endsWith(base, "Test") || endsWith(base, "Tests")) return "test";
    if (base == "check" || base == "build") return "test";
    if (in(base, {"assemble", "jar", "classes", "installDist", "bootJar", "shadowJar"}) || startsWith(base, "assemble") ||
        startsWith(base, "compile")) {
        return "build";
    }
    if (startsWith(base, "lint") || in(base, {"detekt", "ktlintCheck", "spotlessCheck"}) || startsWith(base, "checkstyle") ||
        startsWith(base, "pmd") || startsWith(base, "spotbugs")) {
        return "check";
    }
    return "other";
}

Classified cGradle(const Words& args, const std::optional<std::string>&) {
    const auto o = parseOpts(args, {"-p", "--project-dir", "-b", "--build-file", "-c", "--settings-file", "-x", "--exclude-task", "--tests",
                                    "--max-workers", "-I", "--init-script", "-g", "--gradle-user-home", "--console", "--warning-mode",
                                    "--priority", "-D", "-P", "--include-build"});
    Words excluded;
    for (std::size_t k = 0; k < args.size(); ++k) {
        const auto& w = args[k];
        if ((w == "-x" || w == "--exclude-task") && k + 1 < args.size()) {
            excluded.push_back(split(args[k + 1], ':').back());
        } else if (startsWith(w, "--exclude-task=")) {
            excluded.push_back(split(w.substr(w.find('=') + 1), ':').back());
        }
    }
    static const std::map<std::string, std::string> kSuites = {
        {"test", "gradle test"}, {"check", "gradle check"}, {"build", "gradle build"}, {"assemble", "gradle assemble"}, {"lint", "gradle lint"}};
    Classified best;
    for (const auto& task : o.pos) {
        const auto base = split(task, ':').back();
        auto cls = gradleTaskClass(base);
        if (cls == "test" && (base == "check" || base == "build") && containsWord(excluded, "test")) cls = "build";
        if (classRank(cls) > classRank(best.cls)) {
            const auto stripped = strip(task, ":");
            const bool module = std::count(stripped.begin(), stripped.end(), ':') > 0;
            const auto suite = kSuites.find(base);
            best = make(cls, suite == kSuites.end() ? std::nullopt : std::optional<std::string>(suite->second), module || o.has("--tests"));
        }
    }
    return best;
}

Classified cMvn(const Words& args, const std::optional<std::string>&) {
    static constexpr Names kTest = {"test", "integration-test", "verify", "install", "deploy", "package"};
    static constexpr Names kBuild = {"compile", "test-compile", "process-classes"};
    const auto o = parseOpts(args, {"-pl", "--projects", "-f", "--file", "-s", "--settings", "-P", "--activate-profiles", "-T", "--threads",
                                    "-rf", "--resume-from", "-gs", "--global-settings", "-t", "--toolchains", "-l", "--log-file"});
    bool skip = false, narrow = o.any({"-pl", "--projects"});
    for (const auto& w : args) {
        skip |= in(w, {"-DskipTests", "-DskipTests=true", "-Dmaven.test.skip=true", "-Dmaven.test.skip"});
        narrow |= startsWith(w, "-Dtest=") || startsWith(w, "-Dit.test=");
    }
    Classified best;
    for (const auto& goal : o.pos) {
        std::string cls;
        if (in(goal, kTest) || goal == "surefire:test") {
            cls = skip ? "build" : "test";
        } else if (in(goal, kBuild)) {
            cls = "build";
        } else if (endsWith(goal, ":check")) {
            cls = "check";
        } else {
            continue;
        }
        if (classRank(cls) > classRank(best.cls)) {
            best = make(cls, in(goal, kTest) || in(goal, kBuild) ? std::optional<std::string>("mvn " + goal) : std::nullopt, narrow);
        }
    }
    return best;
}

Classified cCc(const Words& args, const std::optional<std::string>&) {
    if (containsWord(args, "-E") || containsWord(args, "--version") || (!args.empty() && args[0] == "-v")) return kOther;
    if (containsWord(args, "-fsyntax-only")) return make("check", "cc", false);
    return make("build", "cc", false);
}

Classified cCmake(const Words& args, const std::optional<std::string>&) {
    if (!containsWord(args, "--build")) return kOther;
    const auto o = parseOpts(args, {"--build", "--target", "-t", "-j", "--parallel", "--config"});
    auto target = o.value("--target");
    if (!target) target = o.value("-t");
    if (target && in(*target, {"test", "check", "RUN_TESTS"})) return make("test", "cmake --build", true);
    return make("build", "cmake --build", target.has_value());
}

Classified cCtest(const Words& args, const std::optional<std::string>&) {
    const auto o = parseOpts(args, {"--test-dir", "-C", "--build-config", "-j", "--parallel", "--timeout", "--output-log", "-R", "--tests-regex",
                                    "-E", "--exclude-regex", "-L", "--label-regex", "-LE", "--label-exclude", "-I", "--tests-information",
                                    "--repeat", "--stop-time", "-O", "--resource-spec-file", "--repeat-until-fail"});
    return make("test", "ctest",
                o.any({"-R", "--tests-regex", "-E", "--exclude-regex", "-L", "--label-regex", "-LE", "--label-exclude", "-I",
                       "--tests-information", "--rerun-failed"}));
}

Classified cJest(const Words& args, const std::optional<std::string>& cwd) {
    const auto o = parseOpts(args, {"-c", "--config", "-w", "--maxWorkers", "--testTimeout", "--reporters", "--coverageReporters", "--rootDir",
                                    "--roots", "--env", "--testEnvironment", "--outputFile", "--seed", "-t", "--testNamePattern",
                                    "--testPathPattern", "--testPathPatterns", "--testPathIgnorePatterns", "--changedSince",
                                    "--selectProjects", "--shard", "--coverageDirectory", "--collectCoverageFrom", "--json"});
    return make("test", "jest",
                o.any({"-t", "--testNamePattern", "--testPathPattern", "--testPathPatterns", "--findRelatedTests", "-o", "--onlyChanged",
                       "--changedSince", "--lastCommit", "--selectProjects", "--shard", "--onlyFailures", "-f"}) ||
                    nameTargeted(o.posAndAfter(), cwd));
}

Classified cVitest(const Words& args, const std::optional<std::string>& cwd) {
    const auto sub = !args.empty() && in(args[0], {"run", "watch", "dev", "related", "bench", "list", "init", "typecheck"})
                         ? std::optional<std::string>(args[0])
                         : std::nullopt;
    const auto rest = sub ? slice(args, 1) : args;
    if (sub && in(*sub, {"bench", "list", "init"})) return kOther;
    const auto o = parseOpts(rest, {"-c", "--config", "--root", "-r", "--dir", "--reporter", "--outputFile", "--environment", "--pool",
                                    "--project", "--shard", "-t", "--testNamePattern", "--maxWorkers", "--minWorkers", "--testTimeout",
                                    "--mode", "--exclude"});
    return make("test", "vitest",
                sub == "related" || o.any({"-t", "--testNamePattern", "--changed", "--shard", "--project"}) ||
                    nameTargeted(o.posAndAfter(), cwd));
}

Classified cMocha(const Words& args, const std::optional<std::string>& cwd) {
    auto o = parseOpts(args, {"-r", "--require", "-R", "--reporter", "-t", "--timeout", "-s", "--slow", "-u", "--ui", "--config", "--package",
                              "--extension", "--ignore", "--file", "-g", "--grep", "-f", "--fgrep", "--spec", "--retries", "-j", "--jobs",
                              "--reporter-option", "-O"});
    if (const auto spec = o.value("--spec")) o.pos.push_back(*spec);
    return make("test", "mocha", o.any({"-g", "--grep", "-f", "--fgrep"}) || pathTargeted(o.posAndAfter(), cwd));
}

Opts parseWith(const Words& args, const Words& valueOpts, bool stopDashdash = true) {
    Opts o;
    for (std::size_t k = 0; k < args.size(); ++k) {
        const auto& w = args[k];
        if (w == "--" && stopDashdash) {
            o.after = slice(args, k + 1);
            break;
        }
        if (startsWith(w, "-") && w != "-") {
            const auto eq = w.find('=');
            const bool isValue = containsWord(valueOpts, w);
            if (eq != std::string::npos && startsWith(w, "--")) {
                o.opts[w.substr(0, eq)] = w.substr(eq + 1);
            } else if (isValue && k + 1 < args.size()) {
                o.opts[w] = args[++k];
            } else {
                std::optional<std::string> matched;
                for (const auto& v : valueOpts) {
                    if (v.size() == 2 && startsWith(w, v) && w.size() > 2) {
                        matched = v;
                        break;
                    }
                }
                if (matched) {
                    o.opts[*matched] = w.substr(2);
                } else {
                    o.opts[w] = std::nullopt;
                }
            }
        } else {
            o.pos.push_back(w);
        }
    }
    return o;
}

Handler pathTool(std::string cls, std::string suite, Words valueOpts, Words filters) {
    return [=](const Words& args, const std::optional<std::string>& cwd) {
        const auto o = parseWith(args, valueOpts);
        bool filtered = false;
        for (const auto& f : filters) filtered |= o.has(f);
        return make(cls, suite, filtered || pathTargeted(o.posAndAfter(), cwd));
    };
}

Handler checkFlag(std::string suite, Words flags, Words valueOpts) {
    return [=](const Words& args, const std::optional<std::string>& cwd) {
        bool any = false;
        for (const auto& a : args) any |= containsWord(flags, a);
        if (!any) return kOther;
        const auto o = parseWith(args, valueOpts);
        return make("check", suite, pathTargeted(o.posAndAfter(), cwd));
    };
}

Handler tsc(std::string name) {
    return [=](const Words& args, const std::optional<std::string>& cwd) {
        for (const auto& a : args) {
            if (in(a, {"--watch", "-w", "--version", "-v", "--init", "--help", "-h", "--showConfig"})) return kOther;
        }
        const auto o = parseOpts(args, {"-p", "--project", "--outDir", "--target", "--module", "--lib", "--jsx", "--moduleResolution",
                                        "--rootDir", "--declarationDir", "--tsBuildInfoFile"});
        if (containsWord(args, "-b") || containsWord(args, "--build")) return make("check", name, false);
        return make("check", name, pathTargeted(o.posAndAfter(), cwd));
    };
}

struct SubRule {
    std::string cls, suite;
    Words filters;
    bool pathRule;
};

Handler sub(std::map<std::string, SubRule> table, Words valueOpts = {}) {
    return [=](const Words& args, const std::optional<std::string>& cwd) {
        if (args.empty()) return kOther;
        const auto it = table.find(args[0]);
        if (it == table.end()) return kOther;
        const auto& rule = it->second;
        const auto o = parseWith(slice(args, 1), valueOpts);
        bool filtered = false;
        for (const auto& f : rule.filters) filtered |= o.has(f);
        return make(rule.cls, rule.suite, filtered || (rule.pathRule && pathTargeted(o.posAndAfter(), cwd)));
    };
}

Handler named(std::string cls, std::string suite) {
    return [=](const Words&, const std::optional<std::string>&) { return make(cls, suite, false); };
}

Handler posargs(std::string name) {
    return [=](const Words& args, const std::optional<std::string>&) {
        for (std::size_t k = 0; k < args.size(); ++k) {
            if (args[k] == "--") return make("test", name, k < args.size() - 1);
        }
        return make("test", name, false);
    };
}

Classified cRuff(const Words& args, const std::optional<std::string>& cwd) {
    static const Words kValue = {"--select", "--ignore", "--extend-select", "--extend-ignore", "--config", "--output-format", "--target-version",
                                 "--exclude", "--extend-exclude", "--line-length", "--per-file-ignores", "--cache-dir", "--stdin-filename",
                                 "--fixable", "--unfixable"};
    const auto subcommand = !args.empty() && in(args[0], {"check", "format", "rule", "config", "linter", "clean", "server", "analyze", "version"})
                                ? std::optional<std::string>(args[0])
                                : std::nullopt;
    const auto rest = subcommand ? slice(args, 1) : args;
    if (!subcommand || *subcommand == "check") return make("check", "ruff", pathTargeted(parseWith(rest, kValue).posAndAfter(), cwd));
    if (*subcommand == "format" && (containsWord(rest, "--check") || containsWord(rest, "--diff"))) {
        return make("check", "ruff format", pathTargeted(parseWith(rest, kValue).posAndAfter(), cwd));
    }
    return kOther;
}

Classified cBazel(const Words& args, const std::optional<std::string>&) {
    if (args.empty() || (args[0] != "test" && args[0] != "build")) return kOther;
    const auto o = parseOpts(slice(args, 1), {"--config", "--test_filter", "--test_output", "--jobs", "-j"});
    bool narrow = false;
    for (const auto& p : o.pos) narrow |= !in(p, {"//...", "...", "//:all"});
    for (const auto& a : args) narrow |= startsWith(a, "--test_filter");
    return args[0] == "test" ? make("test", "bazel test", narrow) : make("build", "bazel build", narrow);
}

Classified cMeson(const Words& args, const std::optional<std::string>&) {
    if (args.empty()) return kOther;
    if (args[0] == "test") {
        const auto o = parseOpts(slice(args, 1),
                                 {"-C", "--suite", "--no-suite", "--num-processes", "--repeat", "--timeout-multiplier", "--setup", "--wrapper"});
        return make("test", "meson test", !o.pos.empty() || o.has("--suite"));
    }
    if (args[0] == "compile") return make("build", "meson compile", false);
    return kOther;
}

Classified cPythonScript(const Words& all, const std::optional<std::string>&) {
    const auto args = slice(all, skipUntilModule(all, {"-W", "-X", "--check-hash-based-pycs"}));
    const auto base = [](const std::string& w) { return split(replaceAll(w, "\\", "/"), '/').back(); };
    if (args.size() >= 2 && base(args[0]) == "manage.py" && args[1] == "test") {
        const auto o = parseOpts(slice(args, 2), {"-k", "--settings", "--parallel", "--tag", "--exclude-tag", "--pattern", "-p", "-v", "--verbosity"});
        return make("test", "django test", !o.pos.empty() || o.any({"-k", "--tag"}));
    }
    if (args.size() >= 2 && base(args[0]) == "setup.py" && args[1] == "test") return make("test", "setup.py test", false);
    return kOther;
}

Classified cNode(const Words& args, const std::optional<std::string>& cwd) {
    if (!containsWord(args, "--test")) return kOther;
    Words without;
    for (const auto& a : args) {
        if (a != "--test") without.push_back(a);
    }
    const auto o = parseOpts(without, {"--test-name-pattern", "--test-reporter", "--test-reporter-destination", "--test-concurrency", "--require",
                                       "-r", "--import", "--loader"});
    return make("test", "node --test", o.any({"--test-name-pattern", "--test-only"}) || pathTargeted(o.posAndAfter(), cwd));
}

Classified cSwiftlint(const Words& args, const std::optional<std::string>&) {
    if ((!args.empty() && (args[0] == "autocorrect" || args[0] == "analyze")) || containsWord(args, "--fix")) return kOther;
    return make("check", "swiftlint", false);
}

const std::map<std::string, Handler>& programs() {
    static const Words kEslint = {"-c", "--config", "--ext", "-f", "--format", "-o", "--output-file", "--rule", "--parser",
                                  "--resolve-plugins-relative-to", "--max-warnings", "--ignore-path", "--ignore-pattern", "--cache-location",
                                  "--env", "--global", "--plugin", "--rulesdir", "--parser-options",
                                  "--report-unused-disable-directives-severity", "--cache-strategy"};
    static const Words kMypy = {"--config-file", "-p", "--package", "-m", "--module", "--python-version", "--cache-dir", "--exclude",
                                "--follow-imports", "--platform", "--junit-xml", "--python-executable"};
    static const Words kPyright = {"-p", "--project", "--pythonpath", "--pythonversion", "--level", "--threads", "--pythonplatform", "-t",
                                   "--typeshedpath", "--venvpath"};
    static const Words kRspec = {"-e", "--example", "-E", "--example-matches", "-t", "--tag", "-f", "--format", "-o", "--out", "-r", "--require",
                                 "-I", "--seed", "--order", "-P", "--pattern", "--exclude-pattern", "--default-path"};
    static const Words kPhpunit = {"--filter", "--group", "--exclude-group", "--testsuite", "-c", "--configuration", "--bootstrap", "--log-junit",
                                   "--order-by", "--cache-directory", "--coverage-html", "--coverage-clover", "--coverage-text",
                                   "--test-suffix", "--exclude-testsuite"};
    static const std::map<std::string, Handler> table = {
        {"pytest", cPytest},
        {"unittest", cUnittest},
        {"cargo", cCargo},
        {"swift", cSwift},
        {"swiftc", named("build", "swiftc")},
        {"xcodebuild", cXcodebuild},
        {"go", cGo},
        {"dotnet", cDotnet},
        {"msbuild", named("build", "msbuild")},
        {"vstest.console", named("test", "vstest")},
        {"gradle", cGradle},
        {"mvn", cMvn},
        {"javac", named("build", "javac")},
        {"kotlinc", named("build", "kotlinc")},
        {"cc", cCc},
        {"cmake", cCmake},
        {"ctest", cCtest},
        {"jest", cJest},
        {"vitest", cVitest},
        {"mocha", cMocha},
        {"ava", pathTool("test", "ava", {"--match", "-m", "--timeout", "-T"}, {"--match", "-m"})},
        {"tap", pathTool("test", "tap", {"--grep", "-g", "--reporter", "-R"}, {"--grep", "-g"})},
        {"node", cNode},
        {"playwright", sub({{"test", {"test", "playwright test", {"-g", "--grep", "--last-failed", "--only-changed"}, true}}},
                           {"-g", "--grep", "--project", "-c", "--config", "--reporter", "--workers", "-j", "--shard", "--retries", "--timeout",
                            "--grep-invert"})},
        {"cypress", sub({{"run", {"test", "cypress run", {"--spec", "-s"}, false}}},
                        {"--spec", "-s", "--browser", "-b", "--config", "-c", "--env", "-e", "--project", "-P"})},
        {"tsc", tsc("tsc")},
        {"vue-tsc", tsc("vue-tsc")},
        {"eslint", pathTool("check", "eslint", kEslint, {})},
        {"oxlint", pathTool("check", "oxlint", {"-c", "--config", "-A", "-D", "-W", "--format", "-f"}, {})},
        {"biome", sub({{"check", {"check", "biome", {}, true}}, {"lint", {"check", "biome", {}, true}}, {"ci", {"check", "biome", {}, true}}},
                      {"--config-path", "--reporter", "--max-diagnostics"})},
        {"prettier", checkFlag("prettier", {"--check", "-c", "--list-different", "-l"}, {"--config", "--ignore-path", "--plugin", "--parser"})},
        {"stylelint", pathTool("check", "stylelint", {"--config", "-c", "--formatter", "-f", "--ignore-path"}, {})},
        {"svelte-check", named("check", "svelte-check")},
        {"astro", sub({{"check", {"check", "astro check", {}, false}}})},
        {"ruff", cRuff},
        {"mypy", pathTool("check", "mypy", kMypy, {})},
        {"dmypy", named("check", "mypy")},
        {"pyright", pathTool("check", "pyright", kPyright, {})},
        {"basedpyright", pathTool("check", "basedpyright", kPyright, {})},
        {"flake8",
         pathTool("check", "flake8", {"--config", "--select", "--ignore", "--extend-ignore", "--max-line-length", "--exclude", "--format"}, {})},
        {"pylint",
         pathTool("check", "pylint", {"--rcfile", "--disable", "--enable", "-j", "--jobs", "--output-format", "--load-plugins"}, {})},
        {"black", checkFlag("black", {"--check", "--diff"},
                            {"--config", "-l", "--line-length", "-t", "--target-version", "--include", "--exclude"})},
        {"isort", checkFlag("isort", {"--check", "--check-only", "-c", "--diff"}, {"--settings-path", "--sp", "--profile"})},
        {"tox", posargs("tox")},
        {"nox", posargs("nox")},
        {"rspec", pathTool("test", "rspec", kRspec,
                           {"-e", "--example", "-E", "--example-matches", "-t", "--tag", "--only-failures", "--next-failure", "-P", "--pattern"})},
        {"rails", sub({{"test", {"test", "rails test", {"-n", "--name"}, true}}}, {"-n", "--name", "-e", "--environment", "--seed"})},
        {"rubocop",
         pathTool("check", "rubocop", {"-c", "--config", "-f", "--format", "--only", "--except", "--require", "-r", "-o", "--out"}, {})},
        {"phpunit", pathTool("test", "phpunit", kPhpunit, {"--filter", "--group", "--testsuite"})},
        {"pest", pathTool("test", "pest", kPhpunit, {"--filter", "--group", "--testsuite"})},
        {"phpstan", sub({{"analyse", {"check", "phpstan", {}, true}}, {"analyze", {"check", "phpstan", {}, true}}},
                        {"-c", "--configuration", "-l", "--level", "--memory-limit", "--error-format"})},
        {"psalm", pathTool("check", "psalm", {"-c", "--config", "--threads", "--output-format"}, {})},
        {"mix", sub({{"test", {"test", "mix test", {"--only", "--failed", "--stale", "--include"}, true}},
                     {"compile", {"build", "mix compile", {}, false}},
                     {"credo", {"check", "mix credo", {}, false}},
                     {"dialyzer", {"check", "mix dialyzer", {}, false}}},
                    {"--only", "--include", "--exclude", "--seed", "--max-failures", "--partitions"})},
        {"zig", sub({{"test", {"test", "zig test", {}, false}}, {"build", {"build", "zig build", {}, false}}})},
        {"stack", sub({{"test", {"test", "stack test", {"--ta", "--test-arguments"}, false}}, {"build", {"build", "stack build", {}, false}}},
                      {"--ta", "--test-arguments"})},
        {"cabal", sub({{"test", {"test", "cabal test", {"--test-options", "--test-option"}, false}},
                       {"build", {"build", "cabal build", {}, false}}},
                      {"--test-options", "--test-option"})},
        {"dart", sub({{"test", {"test", "dart test", {"-n", "--name", "-N", "--plain-name", "-t", "--tags"}, true}},
                      {"analyze", {"check", "dart analyze", {}, false}}},
                     {"-n", "--name", "-N", "--plain-name", "-t", "--tags", "-p", "--platform", "-j", "--concurrency", "-r", "--reporter"})},
        {"flutter", sub({{"test", {"test", "flutter test", {"-n", "--name", "--plain-name", "-t", "--tags"}, true}},
                         {"analyze", {"check", "flutter analyze", {}, false}},
                         {"build", {"build", "flutter build", {}, false}}},
                        {"-n", "--name", "--plain-name", "-t", "--tags", "-r", "--reporter", "--flavor"})},
        {"golangci-lint",
         sub({{"run", {"check", "golangci-lint", {}, true}}}, {"-c", "--config", "--timeout", "-E", "--enable", "-D", "--disable", "--out-format"})},
        {"staticcheck", pathTool("check", "staticcheck", {"-checks", "-f", "-tags"}, {})},
        {"swiftlint", cSwiftlint},
        {"swift-format", sub({{"lint", {"check", "swift-format", {}, false}}})},
        {"clang-tidy", named("check", "clang-tidy")},
        {"cppcheck", named("check", "cppcheck")},
        {"shellcheck", named("check", "shellcheck")},
        {"hadolint", named("check", "hadolint")},
        {"terraform", sub({{"validate", {"check", "terraform validate", {}, false}}})},
        {"pre-commit", sub({{"run", {"check", "pre-commit", {"--files"}, false}}}, {"--files", "--hook-stage", "--from-ref", "--to-ref"})},
        {"bazel", cBazel},
        {"meson", cMeson},
        {"vite", sub({{"build", {"build", "vite build", {}, false}}})},
        {"next", sub({{"build", {"build", "next build", {}, false}}, {"lint", {"check", "next lint", {}, false}}})},
        {"nuxt", sub({{"build", {"build", "nuxt build", {}, false}}})},
        {"ng", sub({{"build", {"build", "ng build", {}, false}}, {"test", {"test", "ng test", {"--include"}, false}}, {"lint", {"check", "ng lint", {}, false}}},
                   {"--include", "--configuration", "-c", "--project"})},
        {"webpack", named("build", "webpack")},
        {"rollup", named("build", "rollup")},
        {"esbuild", named("build", "esbuild")},
        {"tsup", named("build", "tsup")},
        {"rustc", named("build", "rustc")},
    };
    return table;
}

}  // namespace

std::optional<Classified> classifyProgram(const std::string& program, const Words& args, const std::optional<std::string>& cwd) {
    if (program == "python") return cPythonScript(args, cwd);
    if (auto scripted = classifyScripted(program, args, cwd)) return scripted;
    const auto& table = programs();
    const auto it = table.find(program);
    if (it == table.end()) return std::nullopt;
    return it->second(args, cwd);
}

bool isKnownProgram(const std::string& program) {
    return programs().contains(program) || in(program, {"npm", "pnpm", "yarn", "bun", "make", "ninja", "just", "task", "rake", "mise", "turbo",
                                                         "nx", "composer", "deno"});
}

}  // namespace agentspeed::tasks::detail
