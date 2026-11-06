#include <algorithm>
#include <cctype>

#include "agentspeed/remote_host.hpp"
#include "agentspeed/remote_ssh.hpp"

extern char** environ;

namespace agentspeed::remote {

namespace {

std::string joined(const std::vector<std::string>& parts, std::string_view separator) {
    std::string out;
    for (std::size_t i = 0; i < parts.size(); ++i) {
        if (i) out += separator;
        out += parts[i];
    }
    return out;
}

std::optional<std::string> checkedFolder(std::string_view folder) {
    if (validation::folderIssue(folder)) return std::nullopt;
    return validation::normalizedFolder(folder);
}

std::string commandLine(const std::string& script, const std::vector<std::string>& arguments) {
    std::vector<std::string> parts{"sh", "-c", "'" + script + "'", "agent-speed"};
    parts.insert(parts.end(), arguments.begin(), arguments.end());
    return joined(parts, " ");
}

}  // namespace

namespace ssh {

const std::vector<std::string>& options() {
    static const std::vector<std::string> kOptions = {
        "-T",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3",
        "-o", "Compression=yes",
        "-o", "ControlMaster=no",
        "-o", "ClearAllForwardings=yes",
        "-o", "ForwardAgent=no",
        "-o", "ForwardX11=no",
        "-o", "PermitLocalCommand=no",
        "-o", "RequestTTY=no",
        "-o", "LogLevel=ERROR",
    };
    return kOptions;
}

std::optional<std::vector<std::string>> arguments(std::string_view destination, std::string_view remoteCommand,
                                                  const std::optional<std::string>& configFile) {
    if (validation::destinationIssue(destination)) return std::nullopt;
    auto out = options();
    if (configFile) {
        out.emplace_back("-F");
        out.push_back(*configFile);
    }
    out.emplace_back("--");
    out.emplace_back(destination);
    out.emplace_back(remoteCommand);
    return out;
}

std::optional<std::string> developmentConfigFile([[maybe_unused]] const std::vector<std::string>& environment) {
#ifndef NDEBUG
    const auto prefix = std::string(kConfigFileVariable) + "=";
    for (const auto& entry : environment) {
        if (entry.starts_with(prefix) && entry.size() > prefix.size() && entry[prefix.size()] == '/') return entry.substr(prefix.size());
    }
#endif
    return std::nullopt;
}

std::vector<std::string> environment(std::vector<std::string> base) {
    std::erase_if(base, [](const std::string& entry) { return entry.starts_with("LC_ALL="); });
    base.emplace_back("LC_ALL=C");
    return base;
}

std::vector<std::string> processEnvironment() {
    std::vector<std::string> out;
    for (char** entry = environ; entry && *entry; ++entry) out.emplace_back(*entry);
    return out;
}

std::string terminalCommand(std::string_view destination) {
    const auto plain = [](unsigned char c) {
        return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '@' || c == '%' || c == '+' ||
               c == '=' || c == ':' || c == ',' || c == '.' || c == '/' || c == '_' || c == '-';
    };
    const bool quoted = destination.empty() || !std::all_of(destination.begin(), destination.end(), plain);
    std::string out = "ssh ";
    if (!quoted) return out.append(destination);
    out += '\'';
    for (const char c : destination) {
        if (c == '\'') out += "'\\''";
        else out += c;
    }
    return out += '\'';
}

}  // namespace ssh

std::string_view archName(Arch arch) { return arch == Arch::X86_64 ? "x86_64" : "aarch64"; }

std::optional<Arch> archOfMachine(std::string_view machine) {
    std::string lower(machine);
    std::transform(lower.begin(), lower.end(), lower.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    if (lower == "x86_64" || lower == "amd64") return Arch::X86_64;
    if (lower == "aarch64" || lower == "arm64") return Arch::Aarch64;
    return std::nullopt;
}

namespace relay_name {

std::string bundled(Arch arch) { return "agent-speed-relay-linux-" + std::string(archName(arch)); }

std::string installed(std::string_view version, Arch arch, std::string_view sha256) {
    std::string clean;
    for (const char c : version.substr(0, 64)) clean += validation::isNameByte(static_cast<unsigned char>(c)) ? c : '-';
    std::string hash(sha256.substr(0, 12));
    std::transform(hash.begin(), hash.end(), hash.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return std::string(kInstalledPrefix) + (clean.empty() ? "0" : clean) + "-" + std::string(archName(arch)) + "-" + hash;
}

bool isSafe(std::string_view name) {
    return !name.empty() && name.size() <= 255 && name.front() != '-' && name != "." && name != ".." &&
           std::all_of(name.begin(), name.end(), [](char c) { return validation::isNameByte(static_cast<unsigned char>(c)); });
}

}  // namespace relay_name

namespace scripts {

const std::string kSelectHashTool = joined(
    {
        "if command -v sha256sum >/dev/null 2>&1; then h=sha256sum",
        "elif command -v shasum >/dev/null 2>&1; then h=\"shasum -a 256\"",
        "elif command -v openssl >/dev/null 2>&1; then h=\"openssl dgst -sha256 -r\"",
        "else h=; fi",
    },
    "; ");

const std::string kProbe = joined(
    {
        "echo \"system $(uname -s -m)\"",
        kSelectHashTool,
        "if [ -z \"$h\" ]; then echo \"tool none\"; echo end; exit 0; fi",
        "echo \"tool $h\"",
        "for f in \"$1\"/agent-speed-relay-*; do if [ -f \"$f\" ]; then x=$($h < \"$f\") && echo \"hash ${x%% *} ${f##*/}\"; fi; done",
        "echo end",
    },
    "; ");

const std::string kUpload = joined(
    {
        "umask 077",
        "mkdir -p \"$1\" || exit 4",
        "chmod 700 \"$1\" || exit 4",
        "t=\"$1/.upload-$$\"",
        "if cat > \"$t\" && chmod 700 \"$t\"; then :; else rm -f \"$t\"; exit 4; fi",
        kSelectHashTool,
        "if [ -z \"$h\" ]; then rm -f \"$t\"; exit 5; fi",
        "x=$($h < \"$t\")",
        "x=${x%% *}",
        "echo \"hash $x\"",
        "if [ \"$x\" = \"$3\" ]; then :; else rm -f \"$t\"; exit 3; fi",
        "if mv -f \"$t\" \"$1/$2\"; then :; else rm -f \"$t\"; exit 4; fi",
        "for f in \"$1\"/agent-speed-relay-* \"$1\"/.upload-*; do if [ \"$f\" = \"$1/$2\" ]; then :; elif [ -f \"$f\" ]; then rm -f \"$f\"; fi; done",
        "exit 0",
    },
    "; ");

const std::string kRun = "chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"";

bool quotable(std::string_view script) { return script.find_first_of("'\\!\n\r") == std::string_view::npos; }

}  // namespace scripts

namespace command {

std::optional<std::string> probe(std::string_view folder) {
    const auto checked = checkedFolder(folder);
    if (!checked) return std::nullopt;
    return commandLine(scripts::kProbe, {*checked});
}

std::optional<std::string> upload(std::string_view folder, std::string_view fileName, std::string_view sha256) {
    const auto checked = checkedFolder(folder);
    if (!checked || !relay_name::isSafe(fileName) || !isDigest(sha256)) return std::nullopt;
    std::string digest(sha256);
    std::transform(digest.begin(), digest.end(), digest.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return commandLine(scripts::kUpload, {*checked, std::string(fileName), digest});
}

std::optional<std::string> run(std::string_view folder, std::string_view fileName) {
    const auto checked = checkedFolder(folder);
    if (!checked || !relay_name::isSafe(fileName)) return std::nullopt;
    return commandLine(scripts::kRun, {*checked, std::string(fileName)});
}

bool isDigest(std::string_view text) {
    return text.size() == 64 &&
           std::all_of(text.begin(), text.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); });
}

}  // namespace command

}  // namespace agentspeed::remote
