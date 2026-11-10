#include "agentspeed/quota/sources.hpp"

#include <array>

namespace agentspeed::quota {

namespace {

constexpr std::array<SourceRoute, 13> kRoutes = {{
    {SourceKind::Codex, "quotaCodex", true, Route::CodexRollouts},
    {SourceKind::ClaudeCode, "quotaClaudeCli", false, Route::StatusLine},
    {SourceKind::ClaudeDesktop, "quotaClaudeDesktop", true, Route::DesktopCache},
    {SourceKind::Gemini, "quotaGemini", true, Route::AgentSignIn},
    {SourceKind::Antigravity, "quotaAntigravity", true, Route::Loopback},
    {SourceKind::WorkBuddy, "quotaWorkBuddy", false, Route::BrowserSession},
    {SourceKind::ZCode, "quotaZcode", false, Route::UserKey},
    {SourceKind::Kimi, "quotaKimi", true, Route::AgentSignIn},
    {SourceKind::GrokBuild, "quotaGrokBuild", true, Route::LocalFile},
    {SourceKind::Droid, "quotaDroid", true, Route::AgentSignIn},
    {SourceKind::Devin, "quotaDevin", true, Route::LocalFile},
    {SourceKind::Qoder, "quotaQoder", false, Route::BrowserSession},
    {SourceKind::QoderCli, "quotaQoder", false, Route::BrowserSession},
}};

}  // namespace

std::span<const SourceRoute> sourceRoutes() { return kRoutes; }

const SourceRoute* routeOf(SourceKind source) {
    for (const auto& r : kRoutes) {
        if (r.source == source) return &r;
    }
    return nullptr;
}

}  // namespace agentspeed::quota
