#include "net/http_wire.hpp"

#include <cctype>
#include <charconv>
#include <cstring>

namespace agentspeed::relay::net {

namespace {

using quota::http::Failure;

constexpr std::string_view kEnd = "\r\n\r\n";
constexpr std::size_t kChunkLineCap = 1024;
constexpr std::size_t kConnectHeadCap = 16 * 1024;

bool tokenCharacter(char c) {
    return std::isalnum(static_cast<unsigned char>(c)) || std::strchr("!#$%&'*+-.^_`|~", c) != nullptr;
}

bool sameName(std::string_view a, std::string_view b) {
    if (a.size() != b.size()) return false;
    for (std::size_t i = 0; i < a.size(); ++i) {
        if (std::tolower(static_cast<unsigned char>(a[i])) != std::tolower(static_cast<unsigned char>(b[i]))) return false;
    }
    return true;
}

std::string_view trimmed(std::string_view text) {
    while (!text.empty() && (text.front() == ' ' || text.front() == '\t')) text.remove_prefix(1);
    while (!text.empty() && (text.back() == ' ' || text.back() == '\t')) text.remove_suffix(1);
    return text;
}

// `HTTP/1.x NNN ...`.
std::optional<int> statusOf(std::string_view line) {
    if (line.size() < 12 || line.substr(0, 7) != "HTTP/1." || !std::isdigit(static_cast<unsigned char>(line[7])) || line[8] != ' ') {
        return std::nullopt;
    }
    int status = 0;
    const auto digits = line.substr(9, 3);
    const auto [end, ec] = std::from_chars(digits.data(), digits.data() + 3, status);
    if (ec != std::errc() || end != digits.data() + 3 || status < 100 || (line.size() > 12 && line[12] != ' ')) return std::nullopt;
    return status;
}

bool allowedHeader(const quota::http::Header& h) {
    if (h.name.empty()) return false;
    for (const char c : h.name) {
        if (!tokenCharacter(c)) return false;
    }
    for (const char c : h.value) {
        const auto u = static_cast<unsigned char>(c);
        if ((u < 0x20 && c != '\t') || u == 0x7F) return false;
    }
    // The framing headers are the client's own.
    for (const auto* own : {"Host", "Content-Length", "Transfer-Encoding", "Connection"}) {
        if (sameName(h.name, own)) return false;
    }
    return true;
}

}  // namespace

std::optional<std::string> requestBytes(const quota::http::Request& request, const Url& url, std::string_view userAgent) {
    const bool post = request.method == quota::http::Method::Post;
    std::string out = std::string(post ? "POST " : "GET ") + url.target + " HTTP/1.1\r\nHost: " + url.host;
    if (url.port != (url.tls ? 443 : 80)) out += ":" + std::to_string(url.port);
    out += "\r\n";
    bool agent = false;
    for (const auto& h : request.headers) {
        if (!allowedHeader(h)) return std::nullopt;
        agent = agent || sameName(h.name, "User-Agent");
    }
    if (!agent) out += "User-Agent: " + std::string(userAgent) + "\r\n";
    for (const auto& h : request.headers) out += h.name + ": " + h.value + "\r\n";
    if (post || !request.body.empty()) out += "Content-Length: " + std::to_string(request.body.size()) + "\r\n";
    out += "Connection: close\r\n\r\n";
    out += request.body;
    return out;
}

void ResponseReader::fail(Failure failure) {
    state_ = State::Failed;
    failure_ = failure;
    buffer_.clear();
    body_.clear();
}

void ResponseReader::append(std::string_view bytes) {
    if (body_.size() + bytes.size() > cap_) {
        fail(Failure::TooLarge);
        return;
    }
    body_.append(bytes);
}

void ResponseReader::feed(std::string_view bytes) {
    if (state_ == State::Done || state_ == State::Failed) return;
    buffer_.append(bytes);
    while (state_ == State::Head) {
        const auto end = buffer_.find(kEnd);
        if (end == std::string::npos) {
            if (buffer_.size() > kHeadCap) fail(Failure::Network);
            return;
        }
        const std::string head = buffer_.substr(0, end);
        buffer_.erase(0, end + kEnd.size());
        parseHead(head);
    }
    if (state_ == State::Body) takeBody();
    if (state_ == State::Chunks) takeChunks();
}

void ResponseReader::parseHead(std::string_view head) {
    const auto lineEnd = head.find("\r\n");
    const auto status = statusOf(head.substr(0, lineEnd));
    if (!status) {
        fail(Failure::Network);
        return;
    }
    // An interim answer (100 Continue) is followed by the real one.
    if (*status < 200) return;
    status_ = *status;
    if (status_ >= 300 && status_ < 400) {
        fail(Failure::Redirect);
        return;
    }
    bool chunked = false, encoded = false;
    std::size_t pos = lineEnd == std::string_view::npos ? head.size() : lineEnd + 2;
    while (pos < head.size()) {
        auto next = head.find("\r\n", pos);
        if (next == std::string_view::npos) next = head.size();
        const auto line = head.substr(pos, next - pos);
        pos = next + 2;
        const auto colon = line.find(':');
        if (line.empty() || line.front() == ' ' || line.front() == '\t' || colon == std::string_view::npos || colon == 0) {
            fail(Failure::Network);
            return;
        }
        const auto name = line.substr(0, colon);
        const auto value = trimmed(line.substr(colon + 1));
        if (sameName(name, "Date")) {
            date_ = std::string(value);
        } else if (sameName(name, "Content-Encoding")) {
            contentEncoding_ = std::string(value);
        } else if (sameName(name, "Transfer-Encoding")) {
            encoded = true;
            const auto last = trimmed(value.substr(value.rfind(',') == std::string_view::npos ? 0 : value.rfind(',') + 1));
            chunked = sameName(last, "chunked");
        } else if (sameName(name, "Content-Length")) {
            std::size_t length = 0;
            const auto [end, ec] = std::from_chars(value.data(), value.data() + value.size(), length);
            if (value.empty() || ec != std::errc() || end != value.data() + value.size() || (length_ && *length_ != length)) {
                fail(Failure::Network);
                return;
            }
            length_ = length;
        }
    }
    if (encoded) length_.reset();
    if (status_ == 204 || status_ == 304) {
        state_ = State::Done;
        return;
    }
    if (length_ && *length_ > cap_) {
        fail(Failure::TooLarge);
        return;
    }
    state_ = chunked ? State::Chunks : State::Body;
}

void ResponseReader::takeBody() {
    if (length_) {
        const auto take = std::min(*length_ - body_.size(), buffer_.size());
        append(std::string_view(buffer_).substr(0, take));
        buffer_.clear();
        if (state_ == State::Body && body_.size() == *length_) state_ = State::Done;
        return;
    }
    append(buffer_);
    buffer_.clear();
}

void ResponseReader::takeChunks() {
    while (state_ == State::Chunks) {
        if (chunkLeft_) {
            const auto take = std::min(*chunkLeft_, buffer_.size());
            append(std::string_view(buffer_).substr(0, take));
            if (state_ != State::Chunks) return;
            buffer_.erase(0, take);
            *chunkLeft_ -= take;
            if (*chunkLeft_ > 0) return;
            if (buffer_.size() < 2) {
                chunkLeft_ = 0;
                return;
            }
            if (buffer_.compare(0, 2, "\r\n") != 0) {
                fail(Failure::Network);
                return;
            }
            buffer_.erase(0, 2);
            chunkLeft_.reset();
            continue;
        }
        const auto end = buffer_.find("\r\n");
        if (end == std::string::npos) {
            if (buffer_.size() > kChunkLineCap) fail(Failure::Network);
            return;
        }
        const std::string line = buffer_.substr(0, end);
        buffer_.erase(0, end + 2);
        if (trailers_) {
            if (line.empty()) state_ = State::Done;
            continue;
        }
        const auto size = trimmed(std::string_view(line).substr(0, line.find(';')));
        std::size_t length = 0;
        const auto [stop, ec] = std::from_chars(size.data(), size.data() + size.size(), length, 16);
        if (size.empty() || ec != std::errc() || stop != size.data() + size.size()) {
            fail(Failure::Network);
            return;
        }
        if (length == 0) {
            trailers_ = true;
        } else if (body_.size() + length > cap_) {
            fail(Failure::TooLarge);
            return;
        } else {
            chunkLeft_ = length;
        }
    }
}

void ResponseReader::end() {
    if (state_ == State::Done || state_ == State::Failed) return;
    if (state_ == State::Body && !length_) {
        state_ = State::Done;
        return;
    }
    fail(Failure::Network);
}

std::optional<int> connectAnswer(std::string& buffer, bool& malformed) {
    malformed = false;
    const auto end = buffer.find(kEnd);
    if (end == std::string::npos) {
        malformed = buffer.size() > kConnectHeadCap;
        return std::nullopt;
    }
    const auto status = statusOf(std::string_view(buffer).substr(0, buffer.find("\r\n")));
    buffer.erase(0, end + kEnd.size());
    if (!status) {
        malformed = true;
        return std::nullopt;
    }
    return status;
}

}  // namespace agentspeed::relay::net
