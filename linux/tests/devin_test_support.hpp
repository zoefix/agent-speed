#pragma once

#include <unistd.h>

#include <filesystem>
#include <fstream>
#include <optional>
#include <string>

#include "agentspeed/sqlite.hpp"

// Devin CLI databases and scratch folders for the Devin tests (METRICS 4.17).
namespace devin_test {

namespace fs = std::filesystem;

struct TempDir {
    fs::path path;
    TempDir() {
        std::string pattern = (fs::temp_directory_path() / "agentspeed-devin-XXXXXX").string();
        path = fs::canonical(::mkdtemp(pattern.data()));
    }
    ~TempDir() {
        std::error_code ignored;
        fs::remove_all(path, ignored);
    }
    TempDir(const TempDir&) = delete;
    TempDir& operator=(const TempDir&) = delete;
    std::string file(const std::string& name) const { return (path / name).string(); }
    std::string write(const std::string& name, std::string_view text) const {
        fs::create_directories(fs::path(file(name)).parent_path());
        std::ofstream(file(name), std::ios::binary) << text;
        return file(name);
    }
};

// A database written the way the CLI does (the public readers' tables, rows appended by `row_id`).
class CliDatabase {
public:
    explicit CliDatabase(std::string path) : path_(std::move(path)) {
        fs::create_directories(fs::path(path_).parent_path());
        connection_.emplace(agentspeed::sqlite::Connection::open(path_, agentspeed::sqlite::OpenMode::ReadWrite));
        // WAL: every commit grows the `-wal`, so a write within the file clock's resolution still changes the stamp.
        connection_->execute("PRAGMA journal_mode=WAL");
        connection_->execute(
            "CREATE TABLE sessions (id TEXT PRIMARY KEY, working_directory TEXT NOT NULL, model TEXT NOT NULL, title TEXT, "
            "hidden INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL DEFAULT 0, last_activity_at INTEGER NOT NULL DEFAULT 0);"
            "CREATE TABLE message_nodes (row_id INTEGER PRIMARY KEY AUTOINCREMENT, session_id TEXT NOT NULL, "
            "node_id INTEGER NOT NULL DEFAULT 0, parent_node_id INTEGER, chat_message TEXT NOT NULL, created_at NOT NULL, metadata TEXT);");
    }

    const std::string& path() const { return path_; }
    void close() { connection_.reset(); }

    void session(const std::string& id, const std::string& model = "swe-1-6", const std::string& directory = "/w/aurora") {
        auto insert = connection_->prepare("INSERT OR REPLACE INTO sessions(id, working_directory, model) VALUES (?, ?, ?)");
        insert.bind(1, std::string_view(id)).bind(2, std::string_view(directory)).bind(3, std::string_view(model));
        insert.step();
    }

    void row(const std::string& session, const std::string& message, std::int64_t seconds) {
        auto insert = connection_->prepare("INSERT INTO message_nodes(session_id, chat_message, created_at) VALUES (?, ?, ?)");
        insert.bind(1, std::string_view(session)).bind(2, std::string_view(message)).bind(3, seconds);
        insert.step();
    }

    // A row whose `created_at` is no number.
    void untimedRow(const std::string& session, const std::string& message) {
        auto insert = connection_->prepare("INSERT INTO message_nodes(session_id, chat_message, created_at) VALUES (?, ?, 'soon')");
        insert.bind(1, std::string_view(session)).bind(2, std::string_view(message));
        insert.step();
    }

    // One assistant row with metrics: 900 cached tokens, 2 s total, 0.5 s to the first token.
    void call(const std::string& session, const std::optional<std::string>& request, std::int64_t seconds, int input = 100, int output = 40,
              bool tools = false) {
        const std::string id = request ? ",\"request_id\":\"" + *request + "\"" : std::string();
        const std::string toolCalls = tools ? ",\"tool_calls\":[{\"id\":\"c1\"}]" : std::string();
        row(session,
            "{\"role\":\"assistant\"" + toolCalls + ",\"metadata\":{\"generation_model\":\"swe-1-6\"" + id +
                ",\"metrics\":{\"input_tokens\":" + std::to_string(input) + ",\"output_tokens\":" + std::to_string(output) +
                ",\"cache_read_tokens\":900,\"total_time_ms\":2000,\"ttft_ms\":500}}}",
            seconds);
    }

private:
    std::string path_;
    std::optional<agentspeed::sqlite::Connection> connection_;
};

}  // namespace devin_test
