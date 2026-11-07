#include "lan/lan_net.hpp"

#include <array>
#include <chrono>

#include "agentspeed/lan_address.hpp"

Q_LOGGING_CATEGORY(lanLog, "agentspeed.lan", QtInfoMsg)

namespace lan_net {

QHostAddress plain(const QHostAddress& address) {
    bool mapped = false;
    const auto v4 = address.toIPv4Address(&mapped);
    if (mapped) return QHostAddress(v4);
    QHostAddress out(address);
    out.setScopeId(QString());
    return out;
}

QString peerKey(const QHostAddress& address) { return plain(address).toString(); }

bool isLocal(const QHostAddress& address, bool udp) {
    const auto a = plain(address);
    if (a.protocol() == QAbstractSocket::IPv4Protocol) {
        const auto v = a.toIPv4Address();
        return agentspeed::lan::isLocalIPv4({static_cast<std::uint8_t>(v >> 24), static_cast<std::uint8_t>(v >> 16),
                                             static_cast<std::uint8_t>(v >> 8), static_cast<std::uint8_t>(v)},
                                            udp);
    }
    if (a.protocol() == QAbstractSocket::IPv6Protocol) {
        const auto v6 = a.toIPv6Address();
        std::array<std::uint8_t, 16> bytes{};
        for (int i = 0; i < 16; ++i) bytes[static_cast<std::size_t>(i)] = v6[i];
        return agentspeed::lan::isLocalIPv6(bytes, udp);
    }
    return false;
}

qint64 steadyMs() {
    return std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::steady_clock::now().time_since_epoch()).count();
}

}  // namespace lan_net
