#include <gtest/gtest.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>

#include <fstream>
#include <map>

#include "agentspeed/droid.hpp"
#include "agentspeed/line_splitter.hpp"
#include "fixture_support.hpp"

using namespace agentspeed;
namespace fs = std::filesystem;

namespace {

struct Latest : ResponseSink {
    std::vector<std::string> order;
    std::map<std::string, ParsedResponse> byKey;
    void onResponse(const ParsedResponse& r, bool) override {
        if (!byKey.contains(r.key)) order.push_back(r.key);
        byKey[r.key] = r;
    }
};

fs::path makeTemp() {
    char pattern[] = "/tmp/agentspeed-droid-XXXXXX";
    return fs::path(mkdtemp(pattern));
}

void setMtime(const fs::path& path, std::int64_t ms) {
    struct timespec times[2];
    times[0].tv_sec = times[1].tv_sec = static_cast<time_t>(ms / 1000);
    times[0].tv_nsec = times[1].tv_nsec = static_cast<long>((ms % 1000) * 1'000'000);
    utimensat(AT_FDCWD, path.c_str(), times, 0);
}

std::string lineText(const json::Value& line) { return line.isString() ? *line.string() : fixtures::compactJson(line); }

droid::Counters countersOf(const json::Value& v) {
    droid::Counters c;
    c.f[0] = *fixtures::optInt(v["inputTokens"]);
    c.f[1] = *fixtures::optInt(v["cacheCreationTokens"]);
    c.f[2] = *fixtures::optInt(v["cacheReadTokens"]);
    c.f[3] = *fixtures::optInt(v["outputTokens"]);
    c.f[4] = *fixtures::optInt(v["thinkingTokens"]);
    return c;
}

void expectResponse(const ParsedResponse& got, const json::Value& r) {
    SCOPED_TRACE(got.key);
    EXPECT_EQ(got.key, *r["key"].string());
    EXPECT_EQ(got.startMs, fixtures::optInt(r["startMs"]));
    EXPECT_EQ(got.endMs, *fixtures::optInt(r["endMs"]));
    EXPECT_EQ(got.tokens.input, *fixtures::optInt(r["input"]));
    EXPECT_EQ(got.tokens.cachedInput, *fixtures::optInt(r["cachedInput"]));
    EXPECT_EQ(got.tokens.cacheWriteInput, *fixtures::optInt(r["cacheWriteInput"]));
    EXPECT_EQ(got.tokens.output, *fixtures::optInt(r["output"]));
    EXPECT_EQ(got.tokens.reasoning, *fixtures::optInt(r["reasoning"]));
    EXPECT_EQ(got.tokens.cacheWrite1hInput, *fixtures::optInt(r["cacheWrite1hInput"]));
    EXPECT_EQ(got.fast, r["fast"].isTrue());
    EXPECT_EQ(got.final, r["final"].isTrue());
    EXPECT_EQ(got.model, fixtures::optString(r["model"]));
    EXPECT_EQ(got.provider, fixtures::optString(r["provider"]));
}

// One followed transcript of the live replay: its parser, offset, identity and what it emitted.
struct Followed {
    std::unique_ptr<droid::Parser> parser;
    std::int64_t offset = 0;
    std::pair<std::int64_t, std::int64_t> identity;
    LineSplitter lines;
    Latest sink;
};

std::pair<std::int64_t, std::int64_t> identityOf(const fs::path& p) {
    struct stat st {};
    stat(p.c_str(), &st);
    return {static_cast<std::int64_t>(st.st_dev), static_cast<std::int64_t>(st.st_ino)};
}

}  // namespace

TEST(Droid, SidecarsAllocationsAndRoots) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    const auto d = expected.root()["droid"];
    for (const auto& c : d["sidecars"].elements()) {
        SCOPED_TRACE(*c["name"].string());
        std::string text = *c["text"].string();
        if (c["encoding"].equals("latin-1")) {
            std::string latin;
            for (std::size_t i = 0; i < text.size(); ++i) {
                const auto b = static_cast<unsigned char>(text[i]);
                if (b < 0x80) {
                    latin += static_cast<char>(b);
                } else {
                    latin += static_cast<char>(((b & 0x1F) << 6) | (static_cast<unsigned char>(text[++i]) & 0x3F));
                }
            }
            text = latin;
        }
        if (auto size = fixtures::optInt(c["size"]); size && static_cast<std::int64_t>(text.size()) < *size) {
            text.append(static_cast<std::size_t>(*size) - text.size(), ' ');
        }
        const auto read = droid::parseSidecar(text);
        EXPECT_EQ(read.status == droid::SidecarStatus::Readable, c["readable"].isTrue());
        if (!c["readable"].isTrue()) continue;
        if (const auto counters = c["counters"]; counters.isObject()) {
            ASSERT_TRUE(read.counters.has_value());
            EXPECT_EQ(*read.counters, countersOf(counters));
        } else {
            EXPECT_FALSE(read.counters.has_value());
        }
        EXPECT_EQ(read.model, fixtures::optString(c["model"]));
    }
    for (const auto& a : d["allocations"].elements()) {
        std::vector<std::int64_t> weights, want;
        for (const auto& w : a["weights"].elements()) weights.push_back(*fixtures::optInt(w));
        for (const auto& s : a["shares"].elements()) want.push_back(*fixtures::optInt(s));
        EXPECT_EQ(droid::shares(*fixtures::optInt(a["total"]), weights), want);
    }
    for (const auto& r : d["roots"].elements()) {
        const fs::path home = makeTemp();
        for (const auto& m : r["make"].elements()) fs::create_directories(home / *m.string());
        const auto setting = fixtures::optString(r["setting"]);
        const std::string root = setting ? droid::rootOf((home / *setting).string()) : droid::defaultRoot(home.string());
        EXPECT_EQ(root, (home / *r["expected"].string()).string());
        fs::remove_all(home);
    }
}

TEST(Droid, LiveReadCycles) {
    fixtures::JsonFile expected(fixtures::fixtureDir() / "parser-expected.json");
    const auto live = expected.root()["droid"]["live"];
    const fs::path home = makeTemp();
    Deduper deduper;
    std::map<std::string, Followed> followed;
    int step = 0;
    for (const auto& s : live["steps"].elements()) {
        ++step;
        SCOPED_TRACE("step " + std::to_string(step));
        const auto target = [&](const char* key) { return home / *s[key].string(); };
        if (s["append"].isString()) {
            const fs::path path = target("append");
            fs::create_directories(path.parent_path());
            std::ofstream out(path, std::ios::binary | std::ios::app);
            for (const auto& l : s["lines"].elements()) out << lineText(l) << "\n";
        } else if (s["rewrite"].isString()) {
            const fs::path path = target("rewrite");
            const fs::path temp = path.string() + ".tmp";
            {
                std::ofstream out(temp, std::ios::binary | std::ios::trunc);
                for (const auto& l : s["lines"].elements()) out << lineText(l) << "\n";
            }
            fs::rename(temp, path);
        } else if (s["sidecar"].isString()) {
            const fs::path path = droid::sidecarOf(target("sidecar").string());
            fs::create_directories(path.parent_path());
            const std::string text = s["text"].isString() ? *s["text"].string() : fixtures::compactJson(s["doc"]);
            std::ofstream(path, std::ios::binary | std::ios::trunc) << text;
            if (auto mtime = fixtures::optInt(s["mtime"])) setMtime(path, *mtime);
        } else if (s["open"].isString()) {
            const fs::path path = target("open");
            Followed f;
            f.parser = std::make_unique<droid::Parser>(path.string(), deduper);
            f.identity = identityOf(path);
            if (s["atEnd"].isTrue()) {
                f.offset = static_cast<std::int64_t>(fs::file_size(path));
                f.parser->startAtEnd();
            }
            f.parser->beginRead(f.offset);
            followed[path.string()] = std::move(f);
        } else if (s["read"].isString()) {
            const fs::path path = target("read");
            auto& f = followed[path.string()];
            auto& sink = f.sink;
            const auto size = static_cast<std::int64_t>(fs::file_size(path));
            const auto identity = identityOf(path);
            if (identity != f.identity || size < f.offset) {
                // A new identity or a shrink resets the session (section 5); the dedupe set is kept.
                f.parser->reset();
                f.lines.reset();
                f.offset = 0;
                f.identity = identity;
                f.parser->beginRead(0);
            }
            if (size > f.offset) {
                std::ifstream in(path, std::ios::binary);
                in.seekg(f.offset);
                std::string bytes(static_cast<std::size_t>(size - f.offset), '\0');
                in.read(bytes.data(), static_cast<std::streamsize>(bytes.size()));
                f.lines.feedPositioned(bytes, f.offset, [&](std::string_view line, std::int64_t offset, std::int64_t length) {
                    f.parser->feedPositioned(line, offset, length);
                });
                f.offset = size;
            }
            f.parser->applyCycle(droid::readSidecar(droid::sidecarOf(path.string()), f.parser->knownSidecar()), sink);
            const auto responses = s["responses"].elements();
            ASSERT_EQ(sink.order.size(), responses.size());
            for (std::size_t i = 0; i < responses.size(); ++i) expectResponse(sink.byKey[sink.order[i]], responses[i]);
            if (s["pending"].isObject()) {
                EXPECT_EQ(f.parser->pending(), countersOf(s["pending"]));
            }
            if (s["phase"].isString()) {
                EXPECT_EQ(phaseId(f.parser->phase()), *s["phase"].string());
            }
        }
    }
    fs::remove_all(home);
}
