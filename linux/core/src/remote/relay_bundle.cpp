#include "agentspeed/remote_relay_bundle.hpp"

#include <openssl/evp.h>
#include <sys/stat.h>

#include <fstream>
#include <memory>

namespace agentspeed::remote {

namespace {

std::optional<std::string> variable(const std::vector<std::string>& environment, std::string_view name) {
    const auto prefix = std::string(name) + "=";
    for (const auto& entry : environment) {
        if (entry.starts_with(prefix)) return entry.substr(prefix.size());
    }
    return std::nullopt;
}

bool holdsRelay(const std::string& folder) {
    for (const auto arch : {Arch::X86_64, Arch::Aarch64}) {
        struct stat info {};
        if (::stat((folder + "/" + relay_name::bundled(arch)).c_str(), &info) == 0 && S_ISREG(info.st_mode)) return true;
    }
    return false;
}

std::string parentOf(const std::string& path) {
    const auto slash = path.find_last_of('/');
    if (slash == std::string::npos) return ".";
    return slash == 0 ? "/" : path.substr(0, slash);
}

}  // namespace

std::optional<std::string> fileSha256(const std::string& path) {
    std::ifstream in(path, std::ios::binary);
    if (!in) return std::nullopt;
    std::unique_ptr<EVP_MD_CTX, decltype(&EVP_MD_CTX_free)> context(EVP_MD_CTX_new(), EVP_MD_CTX_free);
    if (!context || EVP_DigestInit_ex(context.get(), EVP_sha256(), nullptr) != 1) return std::nullopt;
    std::string buffer(1 << 16, '\0');
    while (in) {
        in.read(buffer.data(), static_cast<std::streamsize>(buffer.size()));
        const auto n = in.gcount();
        if (n > 0 && EVP_DigestUpdate(context.get(), buffer.data(), static_cast<std::size_t>(n)) != 1) return std::nullopt;
    }
    if (in.bad()) return std::nullopt;
    unsigned char digest[EVP_MAX_MD_SIZE];
    unsigned int length = 0;
    if (EVP_DigestFinal_ex(context.get(), digest, &length) != 1) return std::nullopt;
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (unsigned int i = 0; i < length; ++i) {
        out += kHex[digest[i] >> 4];
        out += kHex[digest[i] & 15];
    }
    return out;
}

std::optional<std::string> RelayBundle::locate(const std::vector<std::string>& environment, const std::string& executablePath) {
    if (auto folder = variable(environment, kOverrideVariable); folder && !folder->empty()) return folder;
    const auto bin = parentOf(executablePath);
    for (const auto& folder : {parentOf(bin) + "/share/agent-speed/relay", bin + "/relay"}) {
        if (holdsRelay(folder)) return folder;
    }
    return std::nullopt;
}

std::optional<RelayExecutable> RelayBundle::executable(Arch arch) const {
    if (!directory_) return std::nullopt;
    const auto path = *directory_ + "/" + relay_name::bundled(arch);
    struct stat info {};
    if (::stat(path.c_str(), &info) != 0 || !S_ISREG(info.st_mode) || info.st_size <= 0) return std::nullopt;
    const std::int64_t modified = static_cast<std::int64_t>(info.st_mtim.tv_sec) * 1'000'000'000 + info.st_mtim.tv_nsec;
    {
        std::lock_guard lock(mutex_);
        if (const auto it = cache_.find(arch); it != cache_.end() && it->second.size == info.st_size && it->second.modifiedNs == modified) {
            return it->second.executable;
        }
    }
    const auto digest = fileSha256(path);
    if (!digest) return std::nullopt;
    RelayExecutable executable{path, arch, *digest, static_cast<std::int64_t>(info.st_size), relay_name::installed(version_, arch, *digest)};
    std::lock_guard lock(mutex_);
    cache_[arch] = {static_cast<std::int64_t>(info.st_size), modified, executable};
    return executable;
}

}  // namespace agentspeed::remote
