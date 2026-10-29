#include "internal.hpp"

// Results: output text, outcome, sessions and notifications (TASK-PARSERS 4.1–4.3).
namespace agentspeed::tasks::detail {

namespace {

constexpr Names kClaudeAbort = {"[Request interrupted by user", "The user doesn't want to proceed with this tool use",
                                "The user doesn't want to take this action", "Permission to use ", "Claude requested permissions to use",
                                "User rejected", "The tool use was rejected"};
constexpr const char* kClaudeExit = "Exit code (-?[0-9]+)";
constexpr const char* kClaudeBackground = "Command running in background with ID: ?([A-Za-z0-9_:-]+(?:\\.[A-Za-z0-9_:-]+)*).*";
constexpr const char* kPiExit = "\\(?Command exited with code (-?[0-9]+)\\)?";

struct CodexStatus {
    std::string text, body;
    std::optional<std::int64_t> exit;
    std::string state = "done";
    std::optional<std::string> session;
    std::optional<bool> patch;
};

CodexStatus codexStatus(const json::Value& result) {
    json::Value out = result.isObject() && result["type"].valid() && result["output"].valid() ? result["output"] : result;
    if (out.isObject() && (out["content"].isArray() || out["content"].isString())) out = out["content"];
    std::unique_ptr<OwnedJson> decoded;
    if (out.isString() && startsWith(lstrip(*out.string(), " \t\r\n"), "[")) {
        decoded = parseJson(*out.string());
        if (decoded && decoded->root().isArray() && decoded->root().size() > 0) {
            bool typed = true;
            for (const auto& x : decoded->root().elements()) typed &= x.isObject() && x["type"].isString();
            if (typed) out = decoded->root();
        }
    }
    CodexStatus status;
    status.text = lf(blocksText(out));
    status.body = status.text;
    const auto lines = split(status.text, '\n');
    const auto head = leadingObject(status.text);
    if (head && head->root()["output"].isString()) {
        const auto root = head->root();
        status.body = lf(*root["output"].string());
        const auto meta = root["metadata"].isObject() ? root["metadata"] : json::Value();
        if (const auto code = jsonInt(root["exit_code"])) {
            status.exit = code;
        } else if (const auto metaCode = meta.valid() ? jsonInt(meta["exit_code"]) : std::nullopt) {
            status.exit = metaCode;
        } else if (const auto session = jsonInt(root["session_id"])) {
            status.state = "running";
            status.session = "codex:" + std::to_string(*session);
        }
    } else {
        std::optional<std::size_t> k;
        for (std::size_t j = 0; j < std::min<std::size_t>(8, lines.size()); ++j) {
            if (lines[j] == "Output:") {
                k = j;
                break;
            }
        }
        bool headers = k.has_value();
        if (k) {
            for (std::size_t j = 0; j < *k; ++j) {
                headers &= fullMatch(lines[j], "(Chunk ID|Wall time|Exit code|Process exited with code|Process running with session ID|"
                                               "Original token count|Total output lines)[: ].*");
            }
        }
        if (headers) {
            status.body = join(slice(lines, *k + 1), "\n");
            for (std::size_t j = 0; j < *k; ++j) {
                auto r = fullGroups(lines[j], "Exit code: (-?[0-9]+)");
                if (r.empty()) r = fullGroups(lines[j], "Process exited with code (-?[0-9]+)");
                if (!r.empty()) status.exit = num(*r[1]);
                const auto running = fullGroups(lines[j], "Process running with session ID ([0-9]+)");
                if (!running.empty()) {
                    status.state = "running";
                    status.session = "codex:" + *running[1];
                }
            }
        }
    }
    const auto bodyFirst = split(status.body, '\n')[0];
    const auto first = strip(lines[0], " \t");
    if (first == "aborted" || startsWith(first, "aborted by user") || contains(first, "rejected by user")) {
        status.state = "aborted";
    } else if (prefixMatch(bodyFirst, "command timed out after [0-9]+ milliseconds")) {
        status.state = "timeout";
    }
    if (!status.exit && status.state == "done") {
        if (startsWith(bodyFirst, "Success. Updated the following files")) {
            status.patch = false;
        } else if (startsWith(bodyFirst, "apply_patch verification failed") || startsWith(bodyFirst, "Failed to apply patch")) {
            status.patch = true;
        }
    }
    return status;
}

std::string resultTextOf(const std::string& family, const json::Value& result) {
    if (family == "claude") {
        if (!result.isObject()) return {};
        auto text = lf(blocksText(result["content"]));
        const auto lines = split(text, '\n');
        if (!lines.empty() && fullMatch(lines[0], kClaudeExit)) text = join(slice(lines, 1), "\n");
        return text;
    }
    if (family == "codex") return codexStatus(result).body;
    if (family == "droid") return result.isObject() ? lf(blocksText(result["content"])) : std::string();
    if (family == "omp" || family == "pi" || family == "openclaw") {
        if (!result.isObject()) return {};
        auto lines = split(lf(blocksText(result["content"])), '\n');
        while (!lines.empty() && strip(lines.back(), " \t").empty()) lines.pop_back();
        if (!lines.empty() && fullMatch(strip(lines.back(), " \t"), kPiExit)) lines.pop_back();
        return join(lines, "\n");
    }
    return {};
}

}  // namespace

}  // namespace agentspeed::tasks::detail

namespace agentspeed::tasks {

using namespace detail;

std::string resultText(SourceKind source, const json::Value& result) { return resultTextOf(familyOf(source), result); }

Outcome outcome(SourceKind source, const json::Value& result, std::optional<std::int64_t> t, const std::optional<std::string>& tool,
                const json::Value& input, const std::optional<std::string>& cwd) {
    const auto family = familyOf(source);
    std::optional<std::int64_t> exitCode;
    std::optional<bool> failed;
    std::string state = "done";
    if (family == "claude" && result.isObject()) {
        const auto text = lf(blocksText(result["content"]));
        const auto first = split(text, '\n')[0];
        if (const auto m = fullGroups(first, kClaudeExit); !m.empty()) exitCode = num(*m[1]);
        bool aborted = false;
        for (const auto prefix : kClaudeAbort) aborted |= startsWith(text, prefix);
        if (aborted) {
            state = "aborted";
        } else if (fullMatch(first, "Command timed out after .*")) {
            state = "timeout";
        } else if (fullMatch(first, kClaudeBackground)) {
            state = "running";
        } else if (tool && (*tool == "BashOutput" || *tool == "TaskOutput")) {
            const auto status = searchGroups(text, "<status>([a-z_]+)</status>");
            if (!status.empty() && *status[1] == "running") state = "running";
            const auto exit = searchGroups(text, "<exit_code>(-?[0-9]+)</exit_code>");
            if (!exit.empty() && !exitCode && !status.empty()) exitCode = num(*exit[1]);
        }
        failed = exitCode ? std::optional<bool>(*exitCode != 0) : std::optional<bool>(result["is_error"].isTrue());
    } else if (family == "codex") {
        const auto status = codexStatus(result);
        exitCode = status.exit;
        state = status.state;
        failed = exitCode ? std::optional<bool>(*exitCode != 0) : status.patch;
    } else if ((family == "omp" || family == "pi" || family == "openclaw") && result.isObject()) {
        Words nonempty;
        for (const auto& x : split(lf(blocksText(result["content"])), '\n')) {
            const auto t2 = strip(x, " \t");
            if (!t2.empty()) nonempty.push_back(t2);
        }
        const auto details = result["details"].isObject() ? result["details"] : json::Value();
        if (const auto code = details.valid() ? jsonInt(details["exitCode"]) : std::nullopt) {
            exitCode = code;
        } else if (const auto code2 = details.valid() ? jsonInt(details["exit_code"]) : std::nullopt) {
            exitCode = code2;
        } else if (!nonempty.empty()) {
            if (const auto m = fullGroups(nonempty.back(), kPiExit); !m.empty()) exitCode = num(*m[1]);
        }
        bool timedOut = details.valid() && details["timedOut"].isTrue();
        for (std::size_t k = nonempty.size() >= 2 ? nonempty.size() - 2 : 0; k < nonempty.size(); ++k) {
            timedOut |= fullMatch(nonempty[k], "\\[?Command timed out.*");
        }
        if ((details.valid() && details["status"].equals("running")) || (!nonempty.empty() && prefixMatch(nonempty[0], "Command still running \\(session .*"))) {
            state = "running";
        } else if (timedOut) {
            state = "timeout";
        } else if (!nonempty.empty() && (fullMatch(nonempty.back(), "(Command aborted.*|Command was terminated.*|Operation aborted.*|Command cancelled.*)") ||
                                         fullMatch(nonempty[0], "(Command aborted.*|Command was terminated.*|Operation aborted.*|Command cancelled.*)"))) {
            state = "aborted";
        }
        failed = exitCode ? std::optional<bool>(*exitCode != 0) : std::optional<bool>(result["isError"].isTrue());
    } else if (family == "droid" && result.isObject()) {
        failed = result["is_error"].isTrue();
    } else {
        return {std::nullopt, std::nullopt, t};
    }
    if (tool && state == "done") {
        const auto text = commandText(source, tool, input);
        if (text) {
            std::unique_ptr<OwnedJson> holder;
            const auto args = decodeArgs(input, holder);
            const bool flagged = args.valid() && ((family == "claude" && args["run_in_background"].isTrue()) ||
                                                  (family == "openclaw" && args["background"].isTrue()));
            const auto info = commandInfo(*text, cwd);
            if (flagged || info.background) {
                state = "running";
            } else if (info.masked) {
                const auto shown = maskedStatus(*text, resultTextOf(family, result), cwd);
                if (!shown) {
                    failed = std::nullopt;
                } else {
                    failed = shown->failed;
                    if (shown->exitCode || failed.value_or(false)) exitCode = shown->exitCode;
                }
            }
        }
    }
    if (state != "done") {
        failed = std::nullopt;
        if (state == "running" || state == "aborted") exitCode = std::nullopt;
    }
    return {exitCode, failed, t};
}

std::optional<std::string> continuation(SourceKind source, const std::optional<std::string>& tool, const json::Value& input) {
    const auto family = familyOf(source);
    std::unique_ptr<OwnedJson> holder;
    const auto args = decodeArgs(input, holder);
    if (!args.valid()) return std::nullopt;
    if (family == "codex" && tool == "write_stdin") {
        if (const auto id = jsonInt(args["session_id"])) return "codex:" + std::to_string(*id);
    }
    if (family == "claude" && tool && (*tool == "BashOutput" || *tool == "TaskOutput")) {
        for (const char* field : {"bash_id", "shell_id", "task_id"}) {
            if (const auto id = args[field].nonEmptyString()) return "claude:" + *id;
        }
    }
    return std::nullopt;
}

std::optional<std::string> runningSession(SourceKind source, const json::Value& result) {
    const auto family = familyOf(source);
    if (family == "codex") return codexStatus(result).session;
    if (family == "claude" && result.isObject()) {
        const auto first = split(lf(blocksText(result["content"])), '\n')[0];
        const auto m = fullGroups(first, kClaudeBackground);
        if (!m.empty()) return "claude:" + *m[1];
    }
    return std::nullopt;
}

std::optional<Notification> notification(SourceKind source, const json::Value& line) {
    if (familyOf(source) != "claude" || !line.isObject() || !line["type"].equals("attachment")) return std::nullopt;
    const auto att = line["attachment"];
    if (!att.isObject() || !att["type"].equals("queued_command") || !att["commandMode"].equals("task-notification")) return std::nullopt;
    if (!att["prompt"].isString()) return std::nullopt;
    const auto text = lf(*att["prompt"].string());
    const auto task = searchGroups(text, "<task-id>([^<]+)</task-id>");
    const auto summary = searchGroups(text, "<summary>([^\\n]*?)</summary>");
    if (task.empty() || summary.empty()) return std::nullopt;
    const auto m = fullGroups(strip(*summary[1], " \t"), "Background command [^\\n]*(?:\\(exit code (-?[0-9]+)\\)|with exit code (-?[0-9]+))");
    if (m.empty()) return std::nullopt;
    return Notification{"claude:" + *task[1], num(m[1] ? *m[1] : *m[2])};
}

std::pair<bool, bool> shellCallState(SourceKind source, const std::optional<std::string>& tool, const json::Value& input,
                                     const std::optional<std::string>& cwd) {
    const auto text = commandText(source, tool, input);
    if (!text) return {false, false};
    std::unique_ptr<OwnedJson> holder;
    const auto args = decodeArgs(input, holder);
    const auto family = familyOf(source);
    const bool flagged = args.valid() && ((family == "claude" && args["run_in_background"].isTrue()) ||
                                          (family == "openclaw" && args["background"].isTrue()));
    const auto info = commandInfo(*text, cwd);
    return {flagged || info.background, info.masked};
}

}  // namespace agentspeed::tasks
