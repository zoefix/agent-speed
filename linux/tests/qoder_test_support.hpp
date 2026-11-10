#pragma once

#include <filesystem>
#include <optional>
#include <string>

#include "agentspeed/sqlite.hpp"
#include "devin_test_support.hpp"

// Qoder IDE databases for the Qoder tests (METRICS 4.18), written with the tables of the IDE's agent core.
namespace qoder_test {

namespace fs = std::filesystem;
using devin_test::TempDir;

class IdeDatabase {
public:
    // `wal`: kept in WAL mode, as the core keeps it while the IDE runs.
    explicit IdeDatabase(std::string path, bool wal = true) : path_(std::move(path)) {
        fs::create_directories(fs::path(path_).parent_path());
        connection_.emplace(agentspeed::sqlite::Connection::open(path_, agentspeed::sqlite::OpenMode::ReadWrite));
        if (wal) connection_->execute("PRAGMA journal_mode=WAL");
        connection_->execute(
            "CREATE TABLE chat_session (session_id varchar(64) PRIMARY KEY, project_uri text, project_name text, gmt_create INTEGER, "
            "gmt_modified INTEGER, mode text, parent_session_id varchar(64), parent_tool_call_id varchar(64), session_type text);"
            "CREATE TABLE chat_message (id varchar(64) PRIMARY KEY, session_id varchar(64), request_id varchar(64), role text, "
            "content text, summary text, summary_modified INTEGER, summary_trigger text, tool_result text, token_info text, "
            "model_info text, extra text, gmt_create INTEGER);"
            "CREATE TABLE chat_record (request_id varchar(64) PRIMARY KEY, session_id varchar(64), question text, answer text, "
            "extra text, gmt_create INTEGER, gmt_modified INTEGER);");
    }

    const std::string& path() const { return path_; }
    void close() { connection_.reset(); }

    void session(const std::string& id, const std::string& projectUri = "file:///w/aurora", const std::string& parent = "") {
        auto insert = connection_->prepare("INSERT OR REPLACE INTO chat_session(session_id, project_uri, parent_session_id) VALUES (?, ?, ?)");
        insert.bind(1, std::string_view(id)).bind(2, std::string_view(projectUri)).bind(3, std::string_view(parent));
        insert.step();
    }

    // One row; `tokens` and `model` are JSON texts, empty for none.
    void row(const std::string& id, const std::string& session, const std::string& role, std::int64_t ms, const std::string& request = "",
             const std::string& tokens = "", const std::string& model = "") {
        auto insert = connection_->prepare(
            "INSERT INTO chat_message(id, session_id, request_id, role, content, token_info, model_info, gmt_create) "
            "VALUES (?, ?, ?, ?, 'ENCRYPTED', ?, ?, ?)");
        insert.bind(1, std::string_view(id)).bind(2, std::string_view(session)).bind(4, std::string_view(role)).bind(7, ms);
        request.empty() ? insert.bindNull(3) : insert.bind(3, std::string_view(request));
        tokens.empty() ? insert.bindNull(5) : insert.bind(5, std::string_view(tokens));
        model.empty() ? insert.bindNull(6) : insert.bind(6, std::string_view(model));
        insert.step();
    }

    // An assistant row with the usage Qoder's server reported, on the `auto` tier.
    void call(const std::string& id, const std::string& session, std::int64_t ms, const std::string& request, int prompt = 1000,
              int completion = 100, int cached = 400, const std::string& model = "auto") {
        row(id, session, "assistant", ms, request,
            "{\"prompt_tokens\":" + std::to_string(prompt) + ",\"completion_tokens\":" + std::to_string(completion) +
                ",\"cached_tokens\":" + std::to_string(cached) + ",\"max_input_tokens\":200000}",
            "{\"model_key\":\"" + model + "\",\"is_reasoning\":false}");
    }

    // The turn record whose `finishedAt` says the request finished.
    void finished(const std::string& request, const std::string& session, std::int64_t finishedAt) {
        auto insert = connection_->prepare("INSERT OR REPLACE INTO chat_record(request_id, session_id, extra, gmt_create) VALUES (?, ?, ?, ?)");
        const auto extra = "{\"ai-coding/turn-info\":{\"finishedAt\":" + std::to_string(finishedAt) + ",\"turn\":1}}";
        insert.bind(1, std::string_view(request)).bind(2, std::string_view(session)).bind(3, std::string_view(extra)).bind(4, finishedAt);
        insert.step();
    }

    void execute(const std::string& sql) { connection_->execute(sql); }

private:
    std::string path_;
    std::optional<agentspeed::sqlite::Connection> connection_;
};

// The files reading may never create next to a database.
inline std::vector<std::string> sideFiles(const fs::path& folder) {
    std::vector<std::string> out;
    for (const auto& entry : fs::recursive_directory_iterator(folder)) {
        const auto name = entry.path().filename().string();
        if (name.ends_with("-wal") || name.ends_with("-shm") || name.ends_with("-journal")) out.push_back(entry.path().string());
    }
    return out;
}

}  // namespace qoder_test
