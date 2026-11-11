#pragma once

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

#include "agentspeed/remote_messages.hpp"

// REMOTE 11.1: the relay's quota lines, bounded as the app validates them (5.6), so a line the app would reject is never
// sent: a window that cannot be shown is left out, a value the app clamps anyway is clamped here.
namespace agentspeed::relay {

// Display text (a window's name, the settings path): valid UTF-8 without C0 or C1 controls, DEL, U+2028, U+2029 or
// bidirectional controls, cut to `maxCodePoints`; nullopt when nothing is left.
std::optional<std::string> displayText(std::string_view text, std::size_t maxCodePoints);

// At most 16 windows (`quota::ordered` decides which stay when there are more); `other` needs its minutes, minutes
// outside 1 … 527 040 become null, a non-finite percent drops the window, `usedPercent` is clamped to 0 … 1000 and a
// reset outside 2000–2100 becomes null.
std::vector<quota::Window> sendableWindows(const std::vector<quota::Window>& windows);

// `{"t":"quota","q":{source, observedMs, windows, running, signedIn, status}}`; empty when `observedMs` is not a time
// the app accepts or the source is not one a host reads.
std::string quotaLine(const remote::HostQuota& report);
std::string quotaRemovedLine(SourceKind source);
// `{"t":"quotaBridge","state","settingsPath"}`; a path the app could not show goes as "" (the app then names the default).
std::string quotaBridgeLine(remote::HostQuotaBridge::State state, std::string_view settingsPath);

}  // namespace agentspeed::relay
