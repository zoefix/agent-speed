#include "failures.hpp"

#include <algorithm>
#include <cctype>
#include <set>

#include "internal.hpp"
#include "pcre.hpp"

namespace agentspeed::tasks::detail {

namespace {

constexpr std::size_t kHeadBytes = 65'536;
constexpr std::size_t kTailBytes = 196'608;
constexpr std::size_t kMaxLine = 2'000;
constexpr std::size_t kMaxMessage = 200;
constexpr std::size_t kMaxFingerprints = 256;
constexpr int kWorkspaceMin = 3;
constexpr const char* kNotInPath = R"re((?<![A-Za-z0-9._~/\\-]))re";

const rx::Regex kAnsiOsc{R"re(\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)?)re"};
const rx::Regex kAnsiCsi{R"re(\x1b\[[0-?]*[ -/]*[@-~])re"};
const rx::Regex kAnsiFe{R"re(\x1b[@-Z\\-_])re"};
const rx::Regex kControl{R"re([\x00-\x08\x0b\x0c\x0e-\x1f\x7f])re"};
const rx::Regex kWorkspacePrefix{R"re(((?:@[A-Za-z0-9._-]+/|[A-Za-z0-9._-]*/)[A-Za-z0-9._/-]* [A-Za-z][A-Za-z0-9:._-]*|@?[A-Za-z0-9._/-]+:[A-Za-z][A-Za-z0-9._-]*): ?)re"};
const rx::Regex kTmpPath{R"re((?<![A-Za-z0-9._~/\\-])(?:(?:/private)?/var/folders/|(?:/private)?/tmp/|[A-Za-z]:[\\/]Users[\\/][^\\/ \t]+[\\/]AppData[\\/]Local[\\/]Temp[\\/])[^ \t'\"()\[\]\{\}<>,;|`]*)re"};
const rx::Regex kHomePath{R"re((?<![A-Za-z0-9._~/\\-])(?:/Users/[^/ \t'\"]+/|/home/[^/ \t'\"]+/|[A-Za-z]:[\\/]Users[\\/][^\\/ \t'\"]+[\\/]))re"};
const rx::Regex kUuid{R"re([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})re"};
const rx::Regex kIsoTime{R"re([0-9]{4}-[0-9]{2}-[0-9]{2}[T ][0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:[.,][0-9]+)?)?(?:Z|[+-][0-9]{2}:?[0-9]{2})?)re"};
const rx::Regex kTimeOfDay{R"re((?<![0-9])[0-9]{1,2}:[0-9]{2}:[0-9]{2}(?:[.,][0-9]+)?(?![0-9]))re"};
const rx::Regex kDate{R"re((?<![0-9])[0-9]{4}-[0-9]{2}-[0-9]{2}(?![0-9]))re"};
const rx::Regex kHexAddr{R"re(0x[0-9A-Fa-f]+)re"};
const rx::Regex kFilePos{R"re((\.[A-Za-z][A-Za-z0-9+]{0,9}):[0-9]+(?::[0-9]+)?)re"};
const rx::Regex kFilePosParen{R"re((\.[A-Za-z][A-Za-z0-9+]{0,9})\([0-9]+(?:,[0-9]+)?\))re"};
const rx::Regex kFilePosBracket{R"re((\.[A-Za-z][A-Za-z0-9+]{0,9}):\[[0-9]+,[0-9]+\])re"};
const rx::Regex kLineWord{R"re((?<![A-Za-z0-9_])(line|Line|LINE|lines|col|column|Column|col\.)( ?)[0-9]+(?![0-9]))re"};
const rx::Regex kDuration{R"re((?<![A-Za-z0-9_.])[0-9]+(?:\.[0-9]+)? ?(?:ns|us|\x{00b5}s|ms|s|sec|secs|seconds|second|m|min|mins|minutes|minute|h|hr|hrs|hours|hour)(?![A-Za-z0-9_]))re"};
const rx::Regex kLongHex{R"re((?<![A-Za-z0-9_])[0-9A-Fa-f]{8,}(?![A-Za-z0-9_]))re"};
const rx::Regex kPidPort{R"re((?<![A-Za-z0-9_])(pid|PID|port)([ =:#]+)[0-9]+)re"};
const rx::Regex kSpaces{R"re([ \t]+)re"};
const rx::Regex kCountPart{R"re([ \t]*([0-9]+) ([A-Za-z][A-Za-z ]*?)[ \t]*)re", rx::kFull};

const rx::Regex kGenericFile{R"re((.+?\.[A-Za-z0-9]+):([0-9]+)(?::([0-9]+))?:? (?:fatal )?error(?:\[([^\]]+)\])?:? (.+))re", rx::kFull};
const rx::Regex kGenericPrefix{R"re((?:error|ERROR|Error|fatal|FATAL)(?:\[([^\]]+)\])?: (.+))re", rx::kFull};
const rx::Regex kGenericException{R"re((?:Uncaught )?([A-Za-z_][A-Za-z0-9_.]*(?:Error|Exception)): (.*))re", rx::kFull};
const rx::Regex kCompleteCargo{R"re(Finished .* target\(s\) in .+)re", rx::kFull};
const rx::Regex kCompleteSwiftpm{R"re(Build complete!(?: \(.*\))?)re", rx::kFull};
const rx::Regex kCompleteXcodebuild{R"re(\*\* [A-Z]+(?: [A-Z]+)* SUCCEEDED \*\*(?: \[.*\])?)re", rx::kFull};
const rx::Regex kCompleteGradle{R"re(BUILD SUCCESSFUL(?: in .+)?)re", rx::kFull};
const rx::Regex kCompleteMaven{R"re(\[INFO\] BUILD SUCCESS)re", rx::kFull};
const rx::Regex kCompletePrettier{R"re(All matched files use Prettier code style!)re", rx::kFull};
const rx::Regex kCompleteVite{R"re(\x{2713} built in [0-9]+(?:\.[0-9]+)?(?:ms|s))re", rx::kFull};

// Length of the valid UTF-8 sequence at text[i], or 0.
std::size_t utf8Length(std::string_view text, std::size_t i) {
    const auto b = static_cast<unsigned char>(text[i]);
    std::size_t n = 0;
    std::uint32_t cp = 0;
    if (b < 0x80) return 1;
    if (b >= 0xC2 && b <= 0xDF) n = 2, cp = b & 0x1F;
    else if (b >= 0xE0 && b <= 0xEF) n = 3, cp = b & 0x0F;
    else if (b >= 0xF0 && b <= 0xF4) n = 4, cp = b & 0x07;
    else return 0;
    if (i + n > text.size()) return 0;
    for (std::size_t k = 1; k < n; ++k) {
        const auto c = static_cast<unsigned char>(text[i + k]);
        if ((c & 0xC0) != 0x80) return 0;
        cp = (cp << 6) | (c & 0x3F);
    }
    if ((n == 3 && (cp < 0x800 || (cp >= 0xD800 && cp <= 0xDFFF))) || (n == 4 && (cp < 0x10000 || cp > 0x10FFFF))) return 0;
    return n;
}

// The text with every invalid UTF-8 byte replaced by U+FFFD.
std::string scalars(std::string_view text) {
    std::string out;
    out.reserve(text.size());
    for (std::size_t i = 0; i < text.size();) {
        const auto n = utf8Length(text, i);
        if (n == 0) {
            out += "\xEF\xBF\xBD";
            ++i;
        } else {
            out.append(text.substr(i, n));
            i += n;
        }
    }
    return out;
}

bool continuation(char c) { return (static_cast<unsigned char>(c) & 0xC0) == 0x80; }

// The whole output, or a head and a tail cut on character and line boundaries (5.1).
Lines budget(const std::string& text) {
    if (text.size() <= kHeadBytes + kTailBytes) return {text};
    std::size_t cut = kHeadBytes;
    while (cut > 0 && continuation(text[cut])) --cut;
    std::string head = text.substr(0, cut);
    std::size_t start = text.size() - kTailBytes;
    while (start < text.size() && continuation(text[start])) ++start;
    std::string tail = text.substr(start);
    if (const auto nl = head.rfind('\n'); nl != std::string::npos) head.resize(nl + 1);
    if (const auto nl = tail.find('\n'); nl != std::string::npos) tail.erase(0, nl + 1);
    return {head, tail};
}

// Removes the `<package> <script>: ` prefixes that pnpm, yarn and turbo put on interleaved lines (5.1).
Lines stripWorkspacePrefixes(Lines lines) {
    const auto spaced = [](const std::string& line, std::size_t prefix) {
        return prefix + 1 >= line.size() || line[prefix + 1] == ' ';
    };
    std::map<std::string, int> counts;
    for (const auto& line : lines) {
        const auto r = kWorkspacePrefix.match(line);
        if (r && (r.end == line.size() || spaced(line, r.group(1).size()))) ++counts[r.group(1)];
    }
    std::set<std::string> common;
    for (const auto& [prefix, n] : counts) {
        if (n >= kWorkspaceMin) common.insert(prefix);
    }
    if (common.empty()) return lines;
    for (auto& line : lines) {
        const auto r = kWorkspacePrefix.match(line);
        if (!r || !common.contains(r.group(1)) || !spaced(line, r.group(1).size())) continue;
        auto rest = line.substr(std::min(line.size(), r.group(1).size() + 1));
        line = startsWith(rest, " ") ? rest.substr(1) : rest;
    }
    return lines;
}

Lines cleanLines(std::string_view segment) {
    auto text = kAnsiOsc.sub(segment, "");
    text = kAnsiCsi.sub(text, "");
    text = kAnsiFe.sub(text, "");
    text = replaceAll(replaceAll(text, "\r\n", "\n"), "\r", "\n");
    text = kControl.sub(text, "");
    for (const auto* sep : {"\u0085", " ", " "}) text = replaceAll(text, sep, " ");
    Lines lines;
    for (const auto& line : split(text, '\n')) lines.push_back(headChars(rstrip(line, kWs), kMaxLine));
    return stripWorkspacePrefixes(std::move(lines));
}

// Removes every `cwd/` that does not follow a path character (5.2).
std::string stripCwd(const std::string& text, const std::optional<std::string>& cwd) {
    const auto c = normCwd(cwd);
    if (!c) return text;
    thread_local std::string cachedCwd;
    thread_local std::unique_ptr<rx::Regex> cached;
    if (!cached || cachedCwd != *c) {
        std::string body = kNotInPath;
        for (const char ch : *c) {
            const auto u = static_cast<unsigned char>(ch);
            if (ch == '/') {
                body += R"([\\/])";
            } else if (u < 0x80 && !std::isalnum(u) && ch != '_') {
                body += '\\';
                body += ch;
            } else {
                body += ch;
            }
        }
        body += R"([\\/])";
        const bool drive = c->size() >= 2 && std::isalpha(static_cast<unsigned char>((*c)[0])) && (*c)[1] == ':' &&
                           (c->size() == 2 || (*c)[2] == '/');
        cached = std::make_unique<rx::Regex>(body, drive ? rx::kCaseless : rx::kNone);
        cachedCwd = *c;
    }
    return cached->sub(text, "");
}

// The framework of the first line that says a tool finished cleanly (5.4).
std::optional<std::string> completion(const std::vector<std::pair<Lines, std::vector<bool>>>& segments) {
    static const std::pair<const char*, const rx::Regex*> kCompletions[] = {
        {"cargo", &kCompleteCargo},   {"swiftpm", &kCompleteSwiftpm},   {"xcodebuild", &kCompleteXcodebuild}, {"gradle", &kCompleteGradle},
        {"maven", &kCompleteMaven},   {"prettier", &kCompletePrettier}, {"vite", &kCompleteVite}};
    for (const auto& [lines, claimed] : segments) {
        for (const auto& line : lines) {
            const auto trimmed = strip(line, kWs);
            for (const auto& [name, pattern] : kCompletions) {
                if (pattern->fullmatch(trimmed)) return name;
            }
        }
    }
    return std::nullopt;
}

// Error lines no recognizer claimed, when a failed command left no fingerprint (5.6).
void generic(Found& found, const Lines& lines, const std::vector<bool>& claimed) {
    for (std::size_t i = 0; i < lines.size(); ++i) {
        if (claimed[i]) continue;
        if (const auto r = kGenericFile.fullmatch(lines[i])) {
            found.error("generic", r.group(1), r.group(4), r.group(5));
        } else if (const auto p = kGenericPrefix.fullmatch(lines[i])) {
            found.error("generic", "", p.group(1), p.group(2));
        } else if (const auto e = kGenericException.fullmatch(lines[i])) {
            found.error("generic", "", e.group(1), e.group(2));
        }
    }
}

}  // namespace

std::string headChars(std::string_view text, std::size_t count) {
    std::size_t i = 0;
    for (std::size_t n = 0; i < text.size(); ++i) {
        if (!continuation(text[i]) && n++ == count) break;
    }
    return std::string(text.substr(0, i));
}

std::vector<std::string> splitOn(std::string_view text, std::string_view sep) {
    std::vector<std::string> out;
    std::size_t start = 0;
    while (true) {
        const auto at = text.find(sep, start);
        out.emplace_back(text.substr(start, at == std::string_view::npos ? std::string_view::npos : at - start));
        if (at == std::string_view::npos) return out;
        start = at + sep.size();
    }
}

std::int64_t countOf(const Counts& counts, const std::string& key) {
    const auto it = counts.find(key);
    return it == counts.end() ? 0 : it->second;
}

Counts parseCounts(std::string_view text, std::string_view sep) {
    Counts counts;
    for (const auto& part : splitOn(text, sep)) {
        if (const auto r = kCountPart.fullmatch(part)) counts[r.group(2)] += num(r.group(1));
    }
    return counts;
}

std::string mask(std::string_view text, const std::optional<std::string>& cwd) {
    auto t = kTmpPath.sub(text, "<tmp>");
    t = stripCwd(t, cwd);
    t = kHomePath.sub(t, "~/");
    t = kUuid.sub(t, "<uuid>");
    t = kIsoTime.sub(t, "<time>");
    t = kTimeOfDay.sub(t, "<time>");
    t = kDate.sub(t, "<date>");
    t = kHexAddr.sub(t, "0x?");
    t = kFilePosBracket.sub(t, R"(\1:[N])");
    t = kFilePos.sub(t, R"(\1:N)");
    t = kFilePosParen.sub(t, R"(\1(N))");
    t = kLineWord.sub(t, R"(\1\2N)");
    t = kDuration.sub(t, "<dur>");
    t = kLongHex.sub(t, "<hex>");
    t = kPidPort.sub(t, R"(\1 N)");
    return headChars(strip(kSpaces.sub(t, " "), " "), kMaxMessage);
}

std::string maskName(std::string_view text, const std::optional<std::string>& cwd) {
    auto t = kTmpPath.sub(text, "<tmp>");
    t = stripCwd(t, cwd);
    t = kHomePath.sub(t, "~/");
    return headChars(strip(kSpaces.sub(t, " "), " "), kMaxMessage);
}

std::string normFile(std::string_view path, const std::optional<std::string>& cwd) {
    auto p = strip(strip(path, kWs), "'\"`");
    if (startsWith(p, "file://")) p.erase(0, 7);
    p = replaceAll(p, "\\", "/");
    p = kTmpPath.sub(p, "<tmp>");
    p = cwdRelative(p, cwd).first;
    p = kHomePath.sub(p, "~/");
    while (contains(p, "//")) p = replaceAll(p, "//", "/");
    while (startsWith(p, "./")) p.erase(0, 2);
    return p;
}

void Found::fp(const std::string& framework, std::string_view kind, std::string_view file, std::string_view code, std::string_view message) {
    const auto normalized = file.empty() ? std::string() : normFile(file, cwd_);
    const auto masked = message.empty() ? std::string() : mask(message, cwd_);
    fps.push_back(framework + "|" + std::string(kind) + "|" + normalized + "|" + std::string(code) + "|" + masked);
}

void Found::test(const std::string& framework, std::string_view name, std::string_view file) {
    fp(framework, "test", file, maskName(name, cwd_), "");
}

void Found::error(const std::string& framework, std::string_view file, std::string_view code, std::string_view message) {
    fp(framework, "error", file, code, message);
}

void Found::summary(const std::string& framework, const std::string& family, std::int64_t failed, std::optional<std::int64_t> passed,
                    bool sum) {
    summaries.push_back({framework, family, failed, passed, sum});
}

}  // namespace agentspeed::tasks::detail

namespace agentspeed::tasks {

using namespace detail;

FailureReport failures(const std::optional<CommandInfo>& info, const std::string& output, const std::optional<std::string>& cwd,
                       std::optional<bool> failed) {
    Found found(cwd);
    std::vector<std::pair<Lines, std::vector<bool>>> segments;
    for (const auto& segment : budget(scalars(output))) {
        auto lines = cleanLines(segment);
        std::vector<bool> claimed(lines.size(), false);
        const auto recognizers = makeRecognizers(found);
        for (std::size_t i = 0; i < lines.size();) {
            std::size_t used = 0;
            for (const auto& rec : recognizers) {
                if ((used = rec->step(lines, i))) break;
            }
            if (!used) {
                ++i;
                continue;
            }
            for (std::size_t k = i; k < std::min(i + used, lines.size()); ++k) claimed[k] = true;
            i += used;
        }
        for (const auto& rec : recognizers) rec->finish();
        segments.emplace_back(std::move(lines), std::move(claimed));
    }
    if (found.fps.empty() && failed == true) {
        for (const auto& [lines, claimed] : segments) generic(found, lines, claimed);
    }

    const std::string suite = info && info->suite ? *info->suite : "";
    const bool hasTsc = std::any_of(found.fps.begin(), found.fps.end(), [](const auto& fp) { return startsWith(fp, "tsc|"); });
    std::set<std::string> present;
    for (auto& s : found.summaries) {
        if (s.framework == "found-errors") s.framework = hasTsc || suite == "tsc" || suite == "vue-tsc" ? "tsc" : "ruff";
        present.insert(s.framework);
    }
    struct Combined {
        std::string family;
        std::int64_t failed;
        std::optional<std::int64_t> passed;
    };
    std::map<std::string, Combined> combined;
    std::vector<std::string> order;
    for (const auto& s : found.summaries) {
        if (s.framework == "cargo" && present.contains("nextest")) continue;
        const auto it = combined.find(s.framework);
        if (it == combined.end()) {
            order.push_back(s.framework);
            combined[s.framework] = {s.family, s.failed, s.passed};
        } else if (s.sum) {
            auto& c = it->second;
            c.failed += s.failed;
            c.passed = !c.passed && !s.passed ? std::nullopt : std::optional<std::int64_t>(c.passed.value_or(0) + s.passed.value_or(0));
        } else {
            it->second = {s.family, s.failed, s.passed};
        }
    }

    FailureReport report;
    report.canonical = found.fps;
    std::sort(report.canonical.begin(), report.canonical.end());
    report.canonical.erase(std::unique(report.canonical.begin(), report.canonical.end()), report.canonical.end());
    std::vector<std::string> tests, errors;
    for (const auto& fw : order) (combined[fw].family == "test" ? tests : errors).push_back(fw);
    const auto& chosen = tests.empty() ? errors : tests;
    if (!chosen.empty()) {
        std::int64_t failedCount = 0, passedSum = 0;
        bool anyPassed = false;
        for (const auto& fw : chosen) {
            failedCount += combined[fw].failed;
            if (combined[fw].passed) anyPassed = true, passedSum += *combined[fw].passed;
        }
        report.failed = failedCount;
        if (!tests.empty() && anyPassed) report.passed = passedSum;
        for (const auto& fw : chosen) {
            if (combined[fw].failed) {
                report.framework = fw;
                break;
            }
        }
        if (!report.framework) {
            for (const auto& fw : chosen) {
                if (combined[fw].passed.value_or(0)) {
                    report.framework = fw;
                    break;
                }
            }
        }
        if (!report.framework) report.framework = chosen.front();
    } else if (!report.canonical.empty()) {
        report.failed = static_cast<std::int64_t>(report.canonical.size());
        report.framework = found.fps.front().substr(0, found.fps.front().find('|'));
    } else {
        report.framework = completion(segments);
        if (report.framework) {
            report.failed = 0;
        } else if (failed == false && info && info->errorFilter && silent(output)) {
            report.failed = 0;
        }
    }
    std::set<std::string> digests;
    for (const auto& c : report.canonical) digests.insert(sha16(c));
    for (const auto& d : digests) {
        if (report.fingerprints.size() == kMaxFingerprints) break;
        report.fingerprints.push_back(d);
    }
    return report;
}

}  // namespace agentspeed::tasks
