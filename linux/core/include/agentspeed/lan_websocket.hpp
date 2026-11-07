#pragma once

#include <cstddef>
#include <optional>
#include <string>
#include <string_view>

// The WebSocket stream's framing (LAN 4.3, 4.5, RFC 6455), on the raw socket the server shares with HTTP and the line
// protocol.
namespace agentspeed::lan::ws {

inline constexpr int kText = 0x1, kClose = 0x8, kPing = 0x9, kPong = 0xA;
inline constexpr std::size_t kClientLimit = 4096;

// Base64 of the SHA-1 of the key followed by the RFC 6455 GUID.
std::string accept(std::string_view key);
// The Base64 form of 16 bytes.
bool validKey(std::string_view key);
// A server frame: FIN, unmasked.
std::string frame(int opcode, std::string_view payload);
std::string close(std::optional<int> code);

struct ClientFrame {
    enum class Action { Incomplete, Pong, EchoClose, Ignore, Close };
    Action action = Action::Incomplete;
    std::optional<int> code;   // Close: the code to send; EchoClose: the client's code
    std::string payload;       // Pong: the ping's payload
    std::size_t consumed = 0;  // the frame's bytes (Pong, EchoClose, Ignore)
};
// How the server handles the frame at the start of `data`; Close is decided as soon as the header shows it.
ClientFrame clientFrame(std::string_view data);

}  // namespace agentspeed::lan::ws
