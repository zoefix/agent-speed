#pragma once

#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <vector>

#include "agentspeed/model.hpp"

namespace agentspeed {

// What METRICS 6.7 needs from a tracked agent to find its parent.
struct ParentFacts {
    std::string agentId;  // the file path
    SourceKind source = SourceKind::Codex;
    std::optional<std::string> codexThreadId;  // the first session_meta's payload.id
    std::optional<std::string> codexParentThreadId;
    std::optional<std::int64_t> createdMs;
};

// METRICS 6.7 parent links for the ported sources; self references and cycles resolve to null.
std::map<std::string, std::optional<std::string>> resolveParents(const std::vector<ParentFacts>& agents);

}  // namespace agentspeed
