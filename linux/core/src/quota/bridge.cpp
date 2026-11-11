#include "agentspeed/quota/bridge.hpp"

#include <sys/stat.h>

#include "agentspeed/quota/files.hpp"

// QUOTAS 5.1–5.4 on disk. Claude Code's settings are only read, and written back through a temporary file and a rename
// that keep their permissions; nothing else of Claude Code's is touched.
namespace agentspeed::quota::bridge {

namespace {

constexpr std::size_t kSettingsLimit = 16u << 20;
constexpr std::size_t kSidecarLimit = 1u << 20;
constexpr std::size_t kHelperLimit = 64u << 20;
// A settings file Agent Speed creates: it may later hold the user's environment (keys), so it starts private.
constexpr mode_t kNewSettingsMode = 0600;

std::string inFolder(const std::string& folder, std::string_view name) { return folder + "/" + std::string(name); }

}  // namespace

std::string Paths::quotaFile() const { return inFolder(quotaFolder, kQuotaFileName); }
std::string Paths::sidecar() const { return inFolder(quotaFolder, kSidecarFileName); }
std::string Paths::backup() const { return inFolder(quotaFolder, kBackupFileName); }

Paths Paths::forComputer(const Environment& environment) { return forData(claudeSettingsFile(environment), dataFolder(environment)); }

Paths Paths::forData(std::string settingsFile, const std::string& data) {
    return Paths{std::move(settingsFile), quotaFolderOf(data), helperCopyOf(data)};
}

Bridge::Bridge(Paths paths, std::optional<std::string> command)
    : paths_(std::move(paths)), command_(command ? std::move(*command) : posixCommand(paths_.helperCopy)) {}

bool Bridge::installHelper(const std::string& helperSource, mode_t mode) const {
    const auto source = files::readFile(helperSource, kHelperLimit, true);
    if (source.status != files::ReadStatus::Ok || source.bytes.empty()) return false;
    const auto current = files::readFile(paths_.helperCopy, kHelperLimit, false);
    if (current.status == files::ReadStatus::Ok && current.bytes == source.bytes) {
        const auto currentMode = files::modeOf(paths_.helperCopy);
        return (currentMode && (*currentMode & S_IXUSR) != 0) || ::chmod(paths_.helperCopy.c_str(), mode) == 0;
    }
    return files::makeFolders(files::parentOf(paths_.helperCopy), 0700) && files::replaceFile(paths_.helperCopy, source.bytes, mode);
}

TurnOnResult Bridge::turnOn(const std::string& helperSource, mode_t helperMode) const {
    std::string text;
    const auto read = readSettings(text);
    if (read == Read::Failed) return TurnOnResult::Failed;
    if (read == Read::Unreadable) return TurnOnResult::Unreadable;
    const auto current = read == Read::Text ? std::optional<std::string>(text) : std::nullopt;
    const auto edit = turnOnEdit(current, command_, readSidecar());
    if (!edit.readable || !edit.settings) return TurnOnResult::Unreadable;
    if (!installHelper(helperSource, helperMode)) return TurnOnResult::Failed;
    if (!edit.wasOurs) {
        if (!edit.previous || !files::makeFolders(paths_.quotaFolder, 0700) ||
            !files::replaceFile(paths_.sidecar(), ordered_json::write(*edit.previous) + "\n", 0600)) {
            return TurnOnResult::Failed;
        }
        if (current && !files::replaceFile(paths_.backup(), *current, 0600)) {
            files::removeFile(paths_.sidecar());
            return TurnOnResult::Failed;
        }
    }
    if (!writeSettings(*edit.settings)) {
        if (!edit.wasOurs) {
            files::removeFile(paths_.sidecar());
            files::removeFile(paths_.backup());
        }
        return TurnOnResult::Failed;
    }
    return TurnOnResult::On;
}

bool Bridge::turnOff() const {
    std::string text;
    if (readSettings(text) == Read::Text) {
        if (const auto restored = turnOffEdit(text, readSidecar())) {
            if (!writeSettings(*restored)) {
                // The sidecar and the backup stay, so turning it off again can still restore the user's line.
                files::removeFile(paths_.quotaFile());
                return false;
            }
        }
    }
    files::removeFile(paths_.sidecar());
    files::removeFile(paths_.backup());
    files::removeFile(paths_.quotaFile());
    return true;
}

Status Bridge::status() const {
    std::string text;
    const auto read = readSettings(text);
    if (read == Read::Failed || read == Read::Unreadable) return Status::Unreadable;
    const bool hasQuotaFile = files::stampOf(paths_.quotaFile(), false).has_value();
    return statusOf(read == Read::Text ? std::optional<std::string>(text) : std::nullopt, hasQuotaFile);
}

Bridge::Read Bridge::readSettings(std::string& text) const {
    auto read = files::readFile(paths_.settingsFile, kSettingsLimit, true);
    switch (read.status) {
        case files::ReadStatus::Ok:
            text = std::move(read.bytes);
            return Read::Text;
        case files::ReadStatus::Missing: return Read::Missing;
        case files::ReadStatus::TooLarge: return Read::Unreadable;
        case files::ReadStatus::Failed: break;
    }
    return Read::Failed;
}

std::optional<ordered_json::Value> Bridge::readSidecar() const {
    const auto read = files::readFile(paths_.sidecar(), kSidecarLimit, false);
    if (read.status != files::ReadStatus::Ok) return std::nullopt;
    auto value = ordered_json::parse(read.bytes);
    if (!value || !value->isObject()) return std::nullopt;
    return value;
}

// A symbolic link (a dotfiles repository) is written where it points, so the link stays.
bool Bridge::writeSettings(const std::string& text) const {
    const auto target = files::resolvedPath(paths_.settingsFile).value_or(paths_.settingsFile);
    const auto mode = files::modeOf(target).value_or(kNewSettingsMode);
    const auto parent = files::parentOf(target);
    if (!parent.empty() && !files::makeFolders(parent, 0700)) return false;
    return files::replaceFile(target, text, mode);
}

}  // namespace agentspeed::quota::bridge
