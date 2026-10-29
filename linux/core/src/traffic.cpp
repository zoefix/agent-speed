#include "agentspeed/traffic.hpp"

#include <algorithm>
#include <charconv>
#include <cmath>
#include <regex>
#include <set>

namespace agentspeed::traffic {

namespace {

constexpr std::int64_t kRateWindowMs = 3'000;
constexpr std::int64_t kGapMs = 15'000;
constexpr int kForgetAfter = 3;
constexpr double kMaxBytesPerSecond = 12'500'000'000.0;

// ---- METRICS 10.11 formatting on the value's shortest decimal form (as Python's Decimal(str(x))).

struct Decimal {
    std::string digits = "0";  // no leading zeros
    int exp = 0;               // value = digits × 10^exp

    static Decimal of(double value) {
        char buffer[64];
        const auto r = std::to_chars(buffer, buffer + sizeof buffer, std::max(0.0, value));
        std::string text(buffer, r.ptr);
        Decimal d;
        int exponent = 0;
        if (const auto e = text.find_first_of("eE"); e != std::string::npos) {
            exponent = std::stoi(text.substr(e + 1));
            text.resize(e);
        }
        std::string digits;
        int fraction = 0;
        bool afterPoint = false;
        for (const char c : text) {
            if (c == '.') {
                afterPoint = true;
                continue;
            }
            digits += c;
            if (afterPoint) ++fraction;
        }
        const auto first = digits.find_first_not_of('0');
        d.digits = first == std::string::npos ? "0" : digits.substr(first);
        d.exp = d.digits == "0" ? 0 : exponent - fraction;
        return d;
    }
    // The value rounded half up to `places` decimals, as text with exactly that many decimals.
    std::string quantize(int places) const {
        std::string integer;  // value × 10^places rounded
        const int shift = exp + places;
        if (shift >= 0) {
            integer = digits == "0" ? "0" : digits + std::string(static_cast<std::size_t>(shift), '0');
        } else {
            const auto cut = static_cast<std::size_t>(-shift);
            std::string kept = cut >= digits.size() ? "0" : digits.substr(0, digits.size() - cut);
            const char next = cut > digits.size() ? '0' : digits[digits.size() - cut];
            if (next >= '5') {
                int i = static_cast<int>(kept.size()) - 1;
                while (i >= 0 && kept[static_cast<std::size_t>(i)] == '9') kept[static_cast<std::size_t>(i--)] = '0';
                if (i < 0) kept.insert(kept.begin(), '1');
                else ++kept[static_cast<std::size_t>(i)];
            }
            integer = kept;
        }
        const auto nonZero = integer.find_first_not_of('0');
        integer = nonZero == std::string::npos ? "0" : integer.substr(nonZero);
        if (places == 0) return integer;
        while (static_cast<int>(integer.size()) <= places) integer.insert(integer.begin(), '0');
        return integer.substr(0, integer.size() - static_cast<std::size_t>(places)) + "." + integer.substr(integer.size() - static_cast<std::size_t>(places));
    }
    double approx() const { return std::stod(digits) * std::pow(10.0, exp); }
};

bool below(const std::string& shown, double limit) { return std::stod(shown) < limit; }

std::string formatIn(double value, const std::vector<const char*>& units) {
    auto x = Decimal::of(value);
    const std::string nbsp = " ";
    for (std::size_t unit = 0;; ++unit) {
        std::string shown;
        if (unit == 0) {
            shown = x.quantize(0);
        } else {
            // Thresholds on the exact decimal value: 9.995 and 99.95.
            const double v = x.approx();
            const auto lessThan = [&](const char* limit) {
                const auto l = Decimal::of(std::stod(limit));
                // Compare digit strings aligned on the smaller exponent.
                const int e = std::min(x.exp, l.exp);
                const std::string a = x.digits + std::string(static_cast<std::size_t>(x.exp - e), '0');
                const std::string b = l.digits + std::string(static_cast<std::size_t>(l.exp - e), '0');
                return a.size() != b.size() ? a.size() < b.size() : a < b;
            };
            (void)v;
            shown = lessThan("9.995") ? x.quantize(2) : lessThan("99.95") ? x.quantize(1) : x.quantize(0);
        }
        if (below(shown, 1000) || unit == units.size() - 1) return shown + nbsp + units[unit];
        x.exp -= 3;
    }
}

// ---- METRICS 10.5 attribution.

const std::set<std::string> kScriptHosts = {"node", "nodejs", "bun", "deno"};
const std::regex kPythonHost("python(?:w|[0-9][0-9.]*)?");
const std::regex kDesktopPackageApp("/windowsapps/(claude_|openai\\.codex_|openai\\.chatgpt)[^/]*/app/[^/]+$");
constexpr const char* kHermesMarkers[] = {"hermes_bootstrap", "hermes_cli", "acp_adapter"};
const std::set<std::string> kHermesScripts = {"hermes", "hermes-agent", "hermes-acp"};
constexpr const char* kWorkBuddyCli = "/app.asar.unpacked/cli/";

std::string lower(std::string text) {
    for (auto& c : text) c = static_cast<char>(std::tolower(static_cast<unsigned char>(c)));
    return text;
}

bool contains(const std::string& text, const std::string& part) { return text.find(part) != std::string::npos; }
bool startsWith(const std::string& text, const std::string& prefix) { return text.rfind(prefix, 0) == 0; }

std::vector<std::string> claudeDesktopCode(Platform p) {
    if (p == Platform::MacOS) return {"~/Library/Application Support/Claude/claude-code/"};
    if (p == Platform::Windows) return {"%LOCALAPPDATA%/Claude/claude-code/", "%LOCALAPPDATA%/Claude-3p/claude-code/", "%APPDATA%/Claude/claude-code/"};
    return {};
}
std::vector<std::string> claudeDesktopApp(Platform p) {
    if (p == Platform::MacOS) return {"/Applications/Claude.app/Contents/MacOS/Claude"};
    if (p == Platform::Windows) return {"%LOCALAPPDATA%/AnthropicClaude/"};
    return {};
}
std::vector<std::string> desktopApps(Platform p) {
    if (p == Platform::MacOS) {
        return {"/Applications/Claude.app/Contents/MacOS/Claude", "/Applications/ChatGPT.app/Contents/MacOS/ChatGPT",
                "/Applications/Codex.app/Contents/MacOS/Codex", "/Applications/OpenCode.app/Contents/MacOS/OpenCode",
                "/Applications/Antigravity.app/Contents/MacOS/Antigravity"};
    }
    if (p == Platform::Windows) return {"%LOCALAPPDATA%/AnthropicClaude/"};
    return {};
}
std::vector<std::string> deepseekApp(Platform p) {
    if (p == Platform::MacOS) return {"/Applications/DeepSeek Harness.app/Contents/MacOS/DeepSeek Harness"};
    return {};
}
std::optional<SourceKind> workBuddyEdition(Platform p, const std::string& key) {
    if (p == Platform::MacOS) {
        if (key == "com.tencent.workbuddy.mac") return SourceKind::WorkBuddy;
        if (key == "com.workbuddy.workbuddy-ai") return SourceKind::WorkBuddyAi;
    } else if (p == Platform::Windows) {
        if (key == "workbuddy") return SourceKind::WorkBuddy;
        if (key == "workbuddyai") return SourceKind::WorkBuddyAi;
    }
    return std::nullopt;
}

bool under(const Env& env, const std::string& path, const std::vector<std::string>& prefixes) {
    const auto p = env.norm(path) + "/";
    for (const auto& prefix : prefixes) {
        const auto base = env.norm(prefix) + "/";
        if (startsWith(p, base)) return true;
    }
    return false;
}

std::optional<std::string> packageApp(const Env& env, const std::string& exe) {
    if (env.platform != Platform::Windows) return std::nullopt;
    std::smatch m;
    const auto normalized = env.norm(exe);
    if (std::regex_search(normalized, m, kDesktopPackageApp)) return m[1].str();
    return std::nullopt;
}

bool isClaudeDesktopApp(const Env& env, const Process& p) {
    if (under(env, p.exe, claudeDesktopApp(env.platform))) return true;
    return env.platform == Platform::Windows && env.name(p.exe) == "claude" && packageApp(env, p.exe) == "claude_";
}

bool isDesktopApp(const Env& env, const Process& p) {
    return under(env, p.exe, desktopApps(env.platform)) || packageApp(env, p.exe).has_value();
}

std::string scriptArgs(const Env& env, const Process& p) {
    if (!kScriptHosts.contains(lower(env.name(p.exe)))) return "";
    std::string out;
    for (std::size_t i = 1; i < p.args.size(); ++i) {
        const auto& a = p.args[i];
        if (i > 1) out += " ";
        out += contains(a, "/") || contains(a, "\\") ? env.norm(a) : a;
    }
    return out;
}

bool isHermes(const Env& env, const Process& p) {
    auto exeName = lower(env.name(p.exe));
    if (exeName.size() > 4 && exeName.ends_with(".exe")) exeName.resize(exeName.size() - 4);
    if (!std::regex_match(exeName, kPythonHost)) return false;
    for (std::size_t i = 1; i < p.args.size(); ++i) {
        for (const auto* marker : kHermesMarkers) {
            if (contains(p.args[i], marker)) return true;
        }
        if (kHermesScripts.contains(env.name(p.args[i]))) return true;
    }
    return false;
}

bool hasArg(const Process& p, const std::string& arg) { return std::find(p.args.begin(), p.args.end(), arg) != p.args.end(); }

}  // namespace

std::string formatRate(double bytesPerSecond, bool bits) {
    if (bits) return formatIn(bytesPerSecond * 8, {"b/s", "Kb/s", "Mb/s", "Gb/s"});
    return formatIn(bytesPerSecond, {"B/s", "KB/s", "MB/s", "GB/s"});
}

std::string formatTotal(double bytes) { return formatIn(bytes, {"B", "KB", "MB", "GB", "TB"}); }

std::string Env::norm(const std::string& path) const {
    if (path.empty()) return "";
    std::string p = path;
    std::replace(p.begin(), p.end(), '\\', '/');
    if (startsWith(p, "~/")) {
        auto h = home;
        std::replace(h.begin(), h.end(), '\\', '/');
        p = h + p.substr(1);
    }
    for (const auto& [key, value] : vars) {
        auto v = value;
        std::replace(v.begin(), v.end(), '\\', '/');
        const auto token = "%" + key + "%";
        for (std::size_t at = p.find(token); at != std::string::npos; at = p.find(token, at + v.size())) p.replace(at, token.size(), v);
    }
    while (!p.empty() && p.back() == '/') p.pop_back();
    return platform == Platform::Windows ? lower(p) : p;
}

std::string Env::name(const std::string& exe) const {
    std::string base = exe;
    std::replace(base.begin(), base.end(), '\\', '/');
    if (const auto slash = base.rfind('/'); slash != std::string::npos) base = base.substr(slash + 1);
    if (platform == Platform::Windows) {
        base = lower(base);
        if (base.ends_with(".exe")) base.resize(base.size() - 4);
    }
    return base;
}

std::optional<SourceKind> matchSource(const Env& env, const Process& p, const std::vector<const Process*>& ancestors) {
    if (isDesktopApp(env, p)) return std::nullopt;
    const auto name = env.name(p.exe);
    const auto script = scriptArgs(env, p);
    if (name == "claude") {
        if (under(env, p.exe, claudeDesktopCode(env.platform)) ||
            std::any_of(ancestors.begin(), ancestors.end(), [&](const Process* a) { return isClaudeDesktopApp(env, *a); })) {
            return SourceKind::ClaudeDesktop;
        }
        if (hasArg(p, "--chrome-native-host")) return std::nullopt;
        return SourceKind::ClaudeCode;
    }
    if (under(env, p.exe, {"~/.local/share/claude/versions"})) {
        return hasArg(p, "--chrome-native-host") ? std::nullopt : std::optional<SourceKind>(SourceKind::ClaudeCode);
    }
    if (contains(script, "@anthropic-ai/claude-code/")) return SourceKind::ClaudeCode;
    const auto anyOf = [&](std::initializer_list<const char*> keys) {
        for (const auto* k : keys) {
            if (contains(name, k)) return true;
        }
        return false;
    };
    if (name == "codex" || (startsWith(name, "codex-") && anyOf({"x86_64", "aarch64", "apple-darwin", "linux", "windows"}))) return SourceKind::Codex;
    if (contains(script, "@openai/codex/bin/codex.js")) return SourceKind::Codex;
    if (name == "dsh" || under(env, p.exe, deepseekApp(env.platform)) || (env.platform == Platform::Windows && name == "deepseek harness")) {
        return SourceKind::Deepseek;
    }
    if (name == "opencode" || name == "opencode-cli" || (startsWith(name, "opencode-") && anyOf({"darwin", "linux", "windows"}))) {
        return SourceKind::OpenCode;
    }
    if (contains(script, "opencode-ai/bin/opencode")) return SourceKind::OpenCode;
    if (contains(script, "/@google/gemini-cli/") || name == "gemini") return SourceKind::Gemini;
    if (name == "omp" || contains(script, "/@oh-my-pi/") || contains(script, "/oh-my-pi/")) return SourceKind::Omp;
    if (name == "agy" || startsWith(name, "language_server_")) return SourceKind::Antigravity;
    if (name == "zcode" || name == "zcode-cli" || contains(script, "/@zcode/cli/")) return SourceKind::ZCode;
    if (name == "kimi" || name == "kimi-code" || contains(script, "/@moonshot-ai/kimi-code/")) return SourceKind::Kimi;
    if (name == "grok" || name == "grok-build" || name == "xai-grok-pager") return SourceKind::GrokBuild;
    if (name == "pi" || contains(script, "/@earendil-works/pi-coding-agent/") || contains(script, "/@mariozechner/pi-coding-agent/")) {
        return SourceKind::Pi;
    }
    if (name == "openclaw" || contains(script, "/openclaw.mjs") || contains(script, "/openclaw/dist/")) return SourceKind::OpenClaw;
    if (isHermes(env, p)) return SourceKind::Hermes;
    if (name == "droid") return SourceKind::Droid;
    const auto edition = workBuddyEdition(env.platform, env.platform == Platform::MacOS ? p.bundle.value_or("") : name);
    if (edition) {
        for (std::size_t i = 1; i < p.args.size(); ++i) {
            if (contains(env.norm(p.args[i]), kWorkBuddyCli)) return edition;
        }
    }
    return std::nullopt;
}

void ProcessTable::refresh(const std::vector<Process>& processes) {
    std::map<std::int64_t, const Process*> byPid;
    for (const auto& p : processes) {
        byPid[p.pid] = &p;
        processes_[{p.pid, p.start}] = p;
    }
    const auto parentOf = [&](const Process& p) -> const Process* {
        const auto it = byPid.find(p.ppid);
        if (it == byPid.end() || it->second == &p || it->second->start > p.start) return nullptr;
        return it->second;
    };
    std::vector<const Process*> order;
    for (const auto& p : processes) order.push_back(&p);
    std::stable_sort(order.begin(), order.end(), [](const Process* a, const Process* b) {
        return a->start != b->start ? a->start < b->start : a->pid < b->pid;
    });
    for (const auto* p : order) {
        const Key key{p->pid, p->start};
        if (known_.contains(key)) continue;
        std::vector<const Process*> ancestors;
        std::set<Key> seen{key};
        for (const auto* cursor = parentOf(*p); cursor && !seen.contains({cursor->pid, cursor->start}); cursor = parentOf(*cursor)) {
            seen.insert({cursor->pid, cursor->start});
            ancestors.push_back(cursor);
        }
        const Classification* nearest = nullptr;
        for (const auto* a : ancestors) {
            const auto it = known_.find({a->pid, a->start});
            if (it != known_.end() && (it->second.role == Classification::Role::Agent || it->second.role == Classification::Role::Self)) {
                nearest = &it->second;
                break;
            }
        }
        const auto source = matchSource(env_, *p, ancestors);
        Classification c;
        if (source && (!nearest || nearest->source != source)) {
            c = {Classification::Role::Agent, source, key};
        } else if (source) {
            c = {Classification::Role::Self, source, nearest->root};
        } else if (nearest) {
            c = {Classification::Role::Tool, nearest->source, nearest->root};
        }
        known_[key] = c;
    }
}

const Classification* ProcessTable::classify(const Key& key) const {
    const auto it = known_.find(key);
    return it == known_.end() ? nullptr : &it->second;
}

const Process* ProcessTable::process(const Key& key) const {
    const auto it = processes_.find(key);
    return it == processes_.end() ? nullptr : &it->second;
}

void ProcessTable::forget(const std::vector<Key>& keys) {
    for (const auto& k : keys) {
        processes_.erase(k);
        known_.erase(k);
    }
}

void Buckets::add(std::int64_t t, std::int64_t intervalMs, std::uint64_t rx, std::uint64_t tx) {
    if (intervalMs <= 0) return;
    const double seconds = static_cast<double>(intervalMs) / 1000.0;
    values[(t / 1000) * 1000] = {static_cast<double>(rx) / seconds, static_cast<double>(tx) / seconds};
    const std::int64_t oldest = (t / 1000) * 1000 - (kSeconds - 1) * 1000;
    while (!values.empty() && values.begin()->first < oldest) values.erase(values.begin());
}

void Engine::Series::add(std::int64_t t, std::int64_t rx, std::int64_t tx, bool gap) {
    if (gap) {
        points = {{t, 0, 0}};
        return;
    }
    points.push_back({t, rx, tx});
    while (points.size() > 2 && points[1][0] <= t - kRateWindowMs) points.erase(points.begin());
}

std::optional<Rate> Engine::Series::rate() const {
    if (points.size() < 2) return std::nullopt;
    const auto t = points.back()[0];
    std::size_t start = 0;
    for (std::size_t i = 0; i < points.size(); ++i) {
        if (points[i][0] <= t - kRateWindowMs) start = i;
    }
    const auto t0 = points[start][0];
    double rx = 0, tx = 0;
    for (std::size_t i = start + 1; i < points.size(); ++i) {
        rx += static_cast<double>(points[i][1]);
        tx += static_cast<double>(points[i][2]);
    }
    const double seconds = static_cast<double>(t - t0) / 1000.0;
    return Rate{rx / seconds, tx / seconds};
}

void Engine::SeriesPair::add(std::int64_t t, const std::array<std::uint64_t, 4>& v, bool gap) {
    self.add(t, static_cast<std::int64_t>(v[0]), static_cast<std::int64_t>(v[1]), gap);
    all.add(t, static_cast<std::int64_t>(v[0] + v[2]), static_cast<std::int64_t>(v[1] + v[3]), gap);
}

RatePair Engine::SeriesPair::rates() const { return {all.rate(), self.rate()}; }

Engine::Engine(Env env, std::function<std::string(std::int64_t)> dateOf) : dateOf_(std::move(dateOf)), table_(std::move(env)) {}

std::uint64_t Engine::delta(std::uint64_t prev, std::uint64_t cur, std::optional<std::int64_t> dt) {
    if (cur < prev) return 0;
    const auto d = cur - prev;
    if (dt && static_cast<double>(d) > kMaxBytesPerSecond * static_cast<double>(std::max<std::int64_t>(*dt, 1)) / 1000.0) return 0;
    return d;
}

std::string Engine::rootOf(const std::string& id, const std::map<std::string, const AgentFacts*>& byId) const {
    std::set<std::string> seen;
    const AgentFacts* a = byId.at(id);
    while (a->parent && byId.contains(*a->parent) && !seen.contains(a->id)) {
        seen.insert(a->id);
        a = byId.at(*a->parent);
    }
    return a->id;
}

std::vector<std::string> Engine::candidates(SourceKind source, const Key& root, std::int64_t start, std::int64_t end,
                                            const std::vector<AgentFacts>& agents) const {
    std::map<std::string, const AgentFacts*> byId;
    for (const auto& a : agents) byId[a.id] = &a;
    std::set<std::string> roots;
    for (const auto& a : agents) {
        if (a.source == source) roots.insert(rootOf(a.id, byId));
    }
    const auto active = [&](const std::string& r) {
        for (const auto& a : agents) {
            if (rootOf(a.id, byId) != r) continue;
            for (const auto& [lo, hi] : a.active) {
                if (lo <= end && hi >= start - 2000) return true;
            }
        }
        return false;
    };
    std::vector<std::string> cands;
    for (const auto& r : roots) {
        if (active(r)) cands.push_back(r);
    }
    if (cands.empty()) return cands;
    const auto& env = table_.env();
    std::set<std::string> openFiles;
    for (const auto& [key, c] : table_.known()) {
        if (c.root != root || (c.role != Classification::Role::Agent && c.role != Classification::Role::Self)) continue;
        if (const auto* p = table_.process(key)) {
            for (const auto& f : p->openFiles) openFiles.insert(env.norm(f));
        }
    }
    if (!openFiles.empty()) {
        std::vector<std::string> owned;
        for (const auto& r : cands) {
            for (const auto& a : agents) {
                if (rootOf(a.id, byId) == r && openFiles.contains(env.norm(a.transcript.value_or("")))) {
                    owned.push_back(r);
                    break;
                }
            }
        }
        if (!owned.empty()) cands = owned;
    }
    if (const auto* p = table_.process(root); p && p->cwd && !p->cwd->empty()) {
        std::vector<std::string> same;
        for (const auto& r : cands) {
            if (env.norm(byId.at(r)->cwdPath.value_or("")) == env.norm(*p->cwd)) same.push_back(r);
        }
        if (!same.empty()) cands = same;
    }
    return cands;
}

void Engine::tick(const Sample& sample, const std::vector<AgentFacts>& agents) {
    const auto t = sample.t;
    const std::optional<std::int64_t> dt = prevT_ ? std::optional<std::int64_t>(t - *prevT_) : std::nullopt;
    const bool gap = !dt || *dt > kGapMs;
    prevT_ = t;
    if (sample.processes) table_.refresh(*sample.processes);
    auto& day = days_[dateOf_(t)];

    if (sample.interfaces) {
        std::uint64_t rx = 0, tx = 0;
        for (const auto& [name, value] : *sample.interfaces) {
            if (const auto it = interfaces_.find(name); it != interfaces_.end()) {
                rx += delta(it->second.first, value.first, dt);
                tx += delta(it->second.second, value.second, dt);
            }
        }
        interfaces_ = *sample.interfaces;
        day.machine.rx += rx;
        day.machine.tx += tx;
        if (dt && *dt <= kGapMs) day.machine.sampledMs += *dt;
        machine_.add(t, static_cast<std::int64_t>(rx), static_cast<std::int64_t>(tx), gap);
        if (!gap) machineBuckets_.add(t, *dt, rx, tx);
    }

    if (!sample.counters) return;
    const auto& counters = *sample.counters;
    const std::optional<std::int64_t> cdt = prevCounterT_ ? std::optional<std::int64_t>(t - *prevCounterT_) : std::nullopt;
    const bool cgap = !cdt || *cdt > kGapMs;
    prevCounterT_ = t;
    const bool newStream = counters.stream != stream_ && !counters.continues;
    stream_ = counters.stream;
    if (newStream) {
        counters_.clear();
        missing_.clear();
    }
    if (cdt && *cdt <= kGapMs && !newStream) day.processSampledMs += *cdt;
    struct Acc {
        SourceKind source;
        std::array<std::uint64_t, 4> v{};  // rxSelf, txSelf, rxTools, txTools
    };
    std::map<Key, Acc> perRoot;
    std::set<Key> seen;
    for (const auto& row : counters.rows) {
        const Key key{row.pid, row.start};
        if (!table_.process(key)) continue;
        seen.insert(key);
        std::uint64_t dRx = 0, dTx = 0;
        if (newStream) {
        } else if (const auto it = counters_.find(key); it != counters_.end()) {
            dRx = delta(it->second.first, row.rx, cdt);
            dTx = delta(it->second.second, row.tx, cdt);
        } else {
            dRx = row.rx;
            dTx = row.tx;
        }
        counters_[key] = {row.rx, row.tx};
        const auto* c = table_.classify(key);
        if (!c || c->role == Classification::Role::None || (dRx == 0 && dTx == 0)) continue;
        const bool tools = c->role == Classification::Role::Tool;
        auto [it, inserted] = perRoot.try_emplace(*c->root, Acc{*c->source});
        it->second.v[tools ? 2 : 0] += dRx;
        it->second.v[tools ? 3 : 1] += dTx;
    }
    for (auto it = counters_.begin(); it != counters_.end();) {
        if (seen.contains(it->first)) {
            missing_.erase(it->first);
            ++it;
            continue;
        }
        if (++missing_[it->first] >= kForgetAfter) {
            missing_.erase(it->first);
            it = counters_.erase(it);
        } else {
            ++it;
        }
    }

    std::map<SourceKind, std::array<std::uint64_t, 4>> totals;
    std::map<std::string, std::array<std::uint64_t, 4>> agentTotals;
    const auto start = cdt ? t - *cdt : t;
    const auto date = dateOf_(t);
    for (const auto& [root, acc] : perRoot) {
        auto& src = day.sources[acc.source];
        src.rxSelf += acc.v[0];
        src.txSelf += acc.v[1];
        src.rxTools += acc.v[2];
        src.txTools += acc.v[3];
        auto& total = totals[acc.source];
        for (int i = 0; i < 4; ++i) total[static_cast<std::size_t>(i)] += acc.v[static_cast<std::size_t>(i)];
        const auto cands = candidates(acc.source, root, start, t, agents);
        if (cands.size() == 1) {
            auto& a = agentDays_[date][cands.front()];
            a.rxSelf += acc.v[0];
            a.txSelf += acc.v[1];
            a.rxTools += acc.v[2];
            a.txTools += acc.v[3];
            auto& at = agentTotals[cands.front()];
            for (int i = 0; i < 4; ++i) at[static_cast<std::size_t>(i)] += acc.v[static_cast<std::size_t>(i)];
        } else {
            src.unassignedRx += acc.v[0] + acc.v[2];
            src.unassignedTx += acc.v[1] + acc.v[3];
        }
    }
    for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
        const auto source = static_cast<SourceKind>(k);
        const auto it = totals.find(source);
        const std::array<std::uint64_t, 4> v = it == totals.end() ? std::array<std::uint64_t, 4>{} : it->second;
        sources_[source].add(t, v, cgap);
        if (!cgap && cdt) sourceBuckets_[source].add(t, *cdt, v[0] + v[2], v[1] + v[3]);
    }
    std::map<std::string, const AgentFacts*> byId;
    for (const auto& a : agents) byId[a.id] = &a;
    std::set<std::string> roots;
    for (const auto& a : agents) roots.insert(rootOf(a.id, byId));
    for (const auto& r : roots) {
        const auto it = agentTotals.find(r);
        agents_[r].add(t, it == agentTotals.end() ? std::array<std::uint64_t, 4>{} : it->second, cgap);
    }
}

std::optional<Rate> Engine::machineRate() const { return machine_.rate(); }

std::map<SourceKind, RatePair> Engine::sourceRates() const {
    std::map<SourceKind, RatePair> out;
    for (const auto& [s, series] : sources_) out[s] = series.rates();
    return out;
}

std::map<std::string, RatePair> Engine::agentRates() const {
    std::map<std::string, RatePair> out;
    for (const auto& [a, series] : agents_) out[a] = series.rates();
    return out;
}

std::vector<HistoryRow> Engine::history() const {
    std::vector<HistoryRow> rows;
    for (const auto& [date, d] : days_) {
        rows.push_back({date, "machine", d.machine.rx, d.machine.tx, 0, 0, d.machine.sampledMs});
        for (int k = 0; k <= static_cast<int>(SourceKind::Droid); ++k) {
            const auto source = static_cast<SourceKind>(k);
            const auto it = d.sources.find(source);
            if (it == d.sources.end()) continue;
            const auto& v = it->second;
            rows.push_back({date, std::string(sourceId(source)), v.rxSelf, v.txSelf, v.rxTools, v.txTools, d.processSampledMs});
        }
    }
    return rows;
}

}  // namespace agentspeed::traffic
