#pragma once

#include <gtest/gtest.h>

#include <string>

#include "agentspeed/lan_frame.hpp"
#include "fixture_support.hpp"

// spec/fixtures/lan-vectors.json, written by spec/tools/lan_reference.py (LAN 7).
namespace lan_vectors {

inline const fixtures::JsonFile& file() {
    static const fixtures::JsonFile vectors(fixtures::fixtureDir() / "lan-vectors.json");
    return vectors;
}

inline agentspeed::json::Value section(std::string_view name) { return file().root()[name]; }

inline std::string unhex(std::string_view hex) {
    std::string out;
    for (std::size_t i = 0; i + 1 < hex.size(); i += 2) out += static_cast<char>(std::stoi(std::string(hex.substr(i, 2)), nullptr, 16));
    return out;
}

inline std::string hex(std::string_view bytes) {
    static constexpr char kHex[] = "0123456789abcdef";
    std::string out;
    for (const char b : bytes) {
        out += kHex[static_cast<unsigned char>(b) >> 4];
        out += kHex[static_cast<unsigned char>(b) & 15];
    }
    return out;
}

inline agentspeed::lan::LiveValues liveOf(const agentspeed::json::Value& v) {
    agentspeed::lan::LiveValues live;
    live.outputTokS = v["outputTokS"].number().value_or(0);
    live.estimatedShare = v["estimatedShare"].number().value_or(0);
    live.generating = static_cast<int>(v["generating"].truncated().value_or(0));
    live.tools = static_cast<int>(v["tools"].truncated().value_or(0));
    live.active = static_cast<int>(v["active"].truncated().value_or(0));
    return live;
}

// A frame case's input as the feed hands it to the builder.
inline agentspeed::lan::FrameInput inputOf(const agentspeed::json::Value& v) {
    agentspeed::lan::FrameInput input;
    input.instance = *v["instance"].string();
    input.name = *v["name"].string();
    input.run = *v["run"].string();
    input.seq = *v["seq"].truncated();
    input.atMs = *v["atMs"].truncated();
    input.live = liveOf(v["live"]);
    for (const auto& [id, values] : v["sources"].members()) input.sources[*agentspeed::sourceFromId(id)] = liveOf(values);
    for (const auto& sample : v["history"].elements()) input.history.push_back(*sample.number());
    return input;
}

}  // namespace lan_vectors
