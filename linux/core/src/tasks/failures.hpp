#pragma once

#include <cstdint>
#include <map>
#include <memory>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

// Failure fingerprints of test and build output (spec/TASK-PARSERS.md 5).
namespace agentspeed::tasks::detail {

using Lines = std::vector<std::string>;
using Counts = std::map<std::string, std::int64_t>;

constexpr std::string_view kWs = " \t";

// The first `count` code points of UTF-8 text.
std::string headChars(std::string_view text, std::size_t count);
std::vector<std::string> splitOn(std::string_view text, std::string_view sep);
std::int64_t countOf(const Counts& counts, const std::string& key);
// "<n> <label>" parts of a summary line, summed per label.
Counts parseCounts(std::string_view text, std::string_view sep = ",");

std::string mask(std::string_view text, const std::optional<std::string>& cwd);
std::string maskName(std::string_view text, const std::optional<std::string>& cwd);
std::string normFile(std::string_view path, const std::optional<std::string>& cwd);

struct Summary {
    std::string framework, family;
    std::int64_t failed = 0;
    std::optional<std::int64_t> passed;
    bool sum = false;  // repeated summaries add up instead of the last one winning
};

class Found {
public:
    explicit Found(std::optional<std::string> cwd) : cwd_(std::move(cwd)) {}

    void test(const std::string& framework, std::string_view name, std::string_view file = {});
    void error(const std::string& framework, std::string_view file, std::string_view code, std::string_view message);
    void summary(const std::string& framework, const std::string& family, std::int64_t failed, std::optional<std::int64_t> passed,
                 bool sum = false);

    std::vector<std::string> fps;  // canonical strings, in output order
    std::vector<Summary> summaries;

private:
    void fp(const std::string& framework, std::string_view kind, std::string_view file, std::string_view code, std::string_view message);
    std::optional<std::string> cwd_;
};

class Recognizer {
public:
    explicit Recognizer(Found& found) : f(found) {}
    virtual ~Recognizer() = default;
    // Lines claimed from lines[i] on; 0 when the line is not this recognizer's.
    virtual std::size_t step(const Lines& lines, std::size_t i) = 0;
    virtual void finish() {}

protected:
    Found& f;
};

std::vector<std::unique_ptr<Recognizer>> makeRecognizers(Found& found);

}  // namespace agentspeed::tasks::detail
