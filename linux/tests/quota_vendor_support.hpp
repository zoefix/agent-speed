#pragma once

#include <gtest/gtest.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

#include <cmath>
#include <filesystem>
#include <fstream>
#include <functional>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/http.hpp"
#include "agentspeed/quota/routes/environment.hpp"
#include "agentspeed/quota/secrets.hpp"
#include "agentspeed/quota/window.hpp"

// Fakes for the phase 2 quota tests: a transport that records and answers, a secret store and a file system that
// count what is asked of them, and a private temporary home. Nothing here reaches the network.
namespace quota2_test {

namespace fs = std::filesystem;
using agentspeed::quota::SecretAttributes;
using agentspeed::quota::SecretLookup;
using agentspeed::quota::SecretResult;

struct TempHome {
    fs::path path;
    TempHome() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-quota2-XXXXXX").string();
        path = fs::canonical(::mkdtemp(pattern.data()));
    }
    ~TempHome() {
        std::error_code ignored;
        fs::remove_all(path, ignored);
    }
    TempHome(const TempHome&) = delete;
    TempHome& operator=(const TempHome&) = delete;
    std::string file(const std::string& relative) const { return (path / relative).string(); }
    std::string write(const std::string& relative, std::string_view text) const {
        fs::create_directories(fs::path(file(relative)).parent_path());
        std::ofstream(file(relative), std::ios::binary) << text;
        return file(relative);
    }
    void link(const std::string& relative, const std::string& target) const {
        fs::create_directories(fs::path(file(relative)).parent_path());
        fs::create_symlink(target, file(relative));
    }
    // Moves a file's modification time forward, so a reader sees a new version.
    void touch(const std::string& relative, int seconds) const {
        const auto p = fs::path(file(relative));
        fs::last_write_time(p, fs::last_write_time(p) + std::chrono::seconds(seconds));
    }
};

inline agentspeed::quota::http::Response answer(int status, std::string body, std::int64_t receivedMs = 0,
                                                std::optional<std::int64_t> dateMs = std::nullopt) {
    agentspeed::quota::http::Response response;
    response.status = status;
    response.body = std::move(body);
    response.receivedMs = receivedMs;
    response.dateMs = dateMs;
    return response;
}

inline agentspeed::quota::http::Response failure(agentspeed::quota::http::Failure kind) {
    agentspeed::quota::http::Response response;
    response.failure = kind;
    return response;
}

// Records every request it is handed; answers at once (or later, when `deferred`).
class FakeTransport final : public agentspeed::quota::http::Transport {
public:
    std::vector<agentspeed::quota::http::Request> sent;
    std::function<agentspeed::quota::http::Response(const agentspeed::quota::http::Request&)> handler;
    bool deferred = false;
    std::vector<std::pair<agentspeed::quota::http::Request, agentspeed::quota::http::Completion>> pending;

    void send(agentspeed::quota::http::Request request, agentspeed::quota::http::Completion done) override {
        EXPECT_TRUE(agentspeed::quota::http::permitted(request)) << request.url;
        sent.push_back(request);
        if (deferred) {
            pending.emplace_back(std::move(request), std::move(done));
            return;
        }
        done(handler ? handler(request) : answer(500, ""));
    }
    void completeAll() {
        auto waiting = std::move(pending);
        pending.clear();
        for (auto& [request, done] : waiting) done(handler ? handler(request) : answer(500, ""));
    }
    std::vector<std::string> urls() const {
        std::vector<std::string> out;
        for (const auto& r : sent) out.push_back(r.url);
        return out;
    }
};

class FakeSecrets final : public agentspeed::quota::SecretStore {
public:
    std::map<SecretAttributes, SecretLookup> items;
    std::vector<SecretAttributes> lookups;

    SecretLookup lookup(const SecretAttributes& attributes) override {
        lookups.push_back(attributes);
        const auto found = items.find(attributes);
        if (found == items.end()) return SecretLookup{SecretResult::Missing, {}};
        return found->second;
    }
    bool store(const SecretAttributes&, const std::string&, const std::string&) override { return false; }
    bool remove(const SecretAttributes&) override { return false; }
};

// The real file system, with every path asked for recorded.
class CountingFiles final : public agentspeed::quota::Files {
public:
    std::vector<std::string> touched;
    std::shared_ptr<agentspeed::quota::Files> inner = agentspeed::quota::systemFiles();

    std::optional<agentspeed::fsx::Stat> stat(const std::string& path) override {
        touched.push_back(path);
        return inner->stat(path);
    }
    std::optional<std::string> read(const std::string& path, std::size_t cap) override {
        touched.push_back(path);
        return inner->read(path, cap);
    }
    std::optional<std::string> readRange(const std::string& path, std::int64_t offset, std::size_t length) override {
        touched.push_back(path);
        return inner->readRange(path, offset, length);
    }
    std::vector<std::string> list(const std::string& folder) override {
        touched.push_back(folder);
        return inner->list(folder);
    }
    std::optional<std::string> readLink(const std::string& path) override {
        touched.push_back(path);
        return inner->readLink(path);
    }
    bool copy(const std::string& from, const std::string& to, std::size_t cap) override {
        touched.push_back(from);
        return inner->copy(from, to, cap);
    }
};

inline std::string header(const agentspeed::quota::http::Request& request, std::string_view name) {
    for (const auto& h : request.headers) {
        if (h.name == name) return h.value;
    }
    return {};
}

inline std::vector<std::string> headerNames(const agentspeed::quota::http::Request& request) {
    std::vector<std::string> out;
    for (const auto& h : request.headers) out.push_back(h.name);
    return out;
}

// One window as the fixtures write it.
inline void expectWindow(const agentspeed::quota::Window& got, const agentspeed::json::Value& want) {
    EXPECT_EQ(agentspeed::quota::kindId(got.kind), *want["kind"].string());
    if (want["name"].isNull()) {
        EXPECT_FALSE(got.name.has_value()) << *got.name;
    } else {
        EXPECT_EQ(got.name.value_or("<none>"), *want["name"].string());
    }
    if (want["minutes"].isNull()) {
        EXPECT_FALSE(got.minutes.has_value());
    } else {
        EXPECT_EQ(got.minutes, want["minutes"].truncated());
    }
    EXPECT_NEAR(got.usedPercent, *want["usedPercent"].number(), 1e-6);
    if (want["resetsAtMs"].isNull()) {
        EXPECT_FALSE(got.resetsAtMs.has_value()) << *got.resetsAtMs;
    } else {
        EXPECT_EQ(got.resetsAtMs, want["resetsAtMs"].truncated());
    }
}

inline void expectWindows(const std::vector<agentspeed::quota::Window>& got, const agentspeed::json::Value& want) {
    ASSERT_TRUE(want.isArray());
    ASSERT_EQ(got.size(), want.size());
    for (std::uint32_t i = 0; i < want.size(); ++i) {
        SCOPED_TRACE("window " + std::to_string(i));
        expectWindow(got[i], want.at(i));
    }
}

}  // namespace quota2_test
