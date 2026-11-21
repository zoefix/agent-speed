#include <gtest/gtest.h>

#include <string>

#include "agentspeed/antigravity.hpp"

using namespace agentspeed::antigravity;

namespace {

std::string varint(std::uint64_t value) {
    std::string out;
    do {
        auto byte = static_cast<unsigned char>(value & 0x7F);
        value >>= 7;
        out.push_back(static_cast<char>(value ? byte | 0x80 : byte));
    } while (value);
    return out;
}

std::string field(std::uint64_t number, const std::string& payload) {
    return varint(number << 3 | 2) + varint(payload.size()) + payload;
}

std::string intField(std::uint64_t number, std::uint64_t value) { return varint(number << 3) + varint(value); }

// A row as agy writes it: step indices first, then a chat model whose prompt comes before the two names.
std::string agyGeneration(std::size_t prompt) {
    const std::string chat = field(1, "(system prompt omitted)") + field(2, field(1, std::string(prompt, 'a'))) +
                             intField(3, 1026) + field(19, "gemini-4-hydrogen-high") +
                             field(21, "Gemini 4 Hydrogen (High)");
    return field(2, varint(5) + varint(6)) + field(4, "exec-7f21") + field(1, chat);
}

}  // namespace

TEST(AntigravitySkim, ALargeRowIsSkimmedForItsStepsAndModelWithoutReadingItsPrompt) {
    const auto blob = agyGeneration(std::size_t{3} << 20);
    std::int64_t read = 0;
    const auto g = skimGeneration(7, static_cast<std::int64_t>(blob.size()), [&](std::int64_t offset, std::int64_t count) {
        read += count;
        return blob.substr(static_cast<std::size_t>(offset), static_cast<std::size_t>(count));
    });
    EXPECT_EQ(g.stepIndices, (std::set<std::uint64_t>{5, 6}));
    EXPECT_EQ(g.model, "gemini-4-hydrogen-high");
    EXPECT_EQ(g.label, "Gemini 4 Hydrogen (High)");
    EXPECT_LT(read, 1024);
}

TEST(AntigravitySkim, ASkimStopsAfterItsReadLimit) {
    std::string chat;
    for (int i = 0; i <= kMaxSkimReads; ++i) chat += field(2, "m");
    const auto blob = field(2, varint(1)) + field(1, chat + field(19, "gemini-4-neon"));
    EXPECT_THROW(skimGeneration(0, static_cast<std::int64_t>(blob.size()),
                                [&](std::int64_t offset, std::int64_t count) {
                                    return blob.substr(static_cast<std::size_t>(offset), static_cast<std::size_t>(count));
                                }),
                 FormatError);
}
