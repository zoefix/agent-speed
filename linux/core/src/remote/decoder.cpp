#include <algorithm>

#include "fields.hpp"

// REMOTE 5.2, 5.6: the dispatch of a relay line and its small messages. Relay strings are only ever returned as values:
// never used as a path, URL, command or format string.
namespace agentspeed::remote {

namespace detail {

namespace {

// The code points of valid UTF-8 text (the parser has already checked it).
template <typename Visit>
bool everyCodePoint(std::string_view text, Visit visit) {
    for (std::size_t i = 0; i < text.size();) {
        const auto c = static_cast<unsigned char>(text[i]);
        std::uint32_t cp = c;
        std::size_t length = 1;
        if (c >= 0xF0) {
            cp = c & 0x07u;
            length = 4;
        } else if (c >= 0xE0) {
            cp = c & 0x0Fu;
            length = 3;
        } else if (c >= 0xC0) {
            cp = c & 0x1Fu;
            length = 2;
        }
        for (std::size_t k = 1; k < length && i + k < text.size(); ++k) cp = (cp << 6) | (static_cast<unsigned char>(text[i + k]) & 0x3Fu);
        if (!visit(cp)) return false;
        i += length;
    }
    return true;
}

}  // namespace

bool allows(TextRule rule, std::string_view text) {
    if (rule == TextRule::Identifier) {
        return std::none_of(text.begin(), text.end(), [](char ch) {
            const auto c = static_cast<unsigned char>(ch);
            return c < 0x20 || c == 0x7F;
        });
    }
    return everyCodePoint(text, [](std::uint32_t cp) {
        const bool control = cp < 0x20 || (cp >= 0x7F && cp <= 0x9F);
        const bool separator = cp == 0x2028 || cp == 0x2029;
        const bool bidi = cp == 0x061C || cp == 0x200E || cp == 0x200F || (cp >= 0x202A && cp <= 0x202E) || (cp >= 0x2066 && cp <= 0x2069);
        return !control && !separator && !bidi;
    });
}

std::size_t codePoints(std::string_view text) {
    std::size_t n = 0;
    everyCodePoint(text, [&](std::uint32_t) {
        ++n;
        return true;
    });
    return n;
}

void fail(LineError::Kind kind, std::string field) { throw LineError{kind, std::move(field)}; }

std::string checkedString(const json::Value& value, const std::string& name, std::size_t maxBytes, TextRule rule) {
    if (!value.isString()) fail(LineError::Kind::WrongType, name);
    auto text = *value.string();
    if (text.size() > maxBytes) fail(LineError::Kind::TooLong, name);
    if (!allows(rule, text)) fail(LineError::Kind::BadCharacter, name);
    return text;
}

std::int64_t checkedInteger(const json::Value& value, const std::string& name) {
    if (!value.isNumber() || !value.isIntegerLiteral()) fail(LineError::Kind::WrongType, name);
    const auto number = value.truncated();
    if (!number || *number < -kIntegerLimit || *number > kIntegerLimit) fail(LineError::Kind::OutOfRange, name);
    return *number;
}

Fields Fields::of(const json::Value& value, const std::string& path) {
    if (!value.valid()) fail(LineError::Kind::Missing, path);
    if (!value.isObject()) fail(LineError::Kind::WrongType, path);
    return Fields(value, path + ".");
}

Fields Fields::object(std::string_view key) const { return of(required(key), name(key)); }

json::Value Fields::required(std::string_view key) const {
    const auto value = object_[key];
    if (!value.valid()) fail(LineError::Kind::Missing, name(key));
    return value;
}

std::optional<json::Value> Fields::optional(std::string_view key) const {
    const auto value = object_[key];
    if (!value.valid() || value.isNull()) return std::nullopt;
    return value;
}

std::string Fields::string(std::string_view key, std::size_t maxBytes, TextRule rule) const {
    return checkedString(required(key), name(key), maxBytes, rule);
}

std::optional<std::string> Fields::optionalString(std::string_view key, std::size_t maxBytes, TextRule rule) const {
    const auto value = optional(key);
    if (!value) return std::nullopt;
    return checkedString(*value, name(key), maxBytes, rule);
}

std::optional<std::string> Fields::modelString(std::string_view key, std::size_t maxBytes) const {
    auto text = optionalString(key, maxBytes, TextRule::Model);
    if (text && text->empty()) return std::nullopt;
    return text;
}

SourceKind Fields::source(std::string_view key) const {
    const auto value = required(key);
    if (!value.isString()) fail(LineError::Kind::WrongType, name(key));
    const auto kind = sourceFromId(*value.string());
    if (!kind) fail(LineError::Kind::OutOfRange, name(key));
    return *kind;
}

std::string Fields::day(std::string_view key) const {
    auto text = string(key, 10);
    if (!isDay(text)) fail(LineError::Kind::OutOfRange, name(key));
    return text;
}

std::string Fields::hex(std::string_view key, std::size_t count) const {
    auto text = string(key, count);
    const bool lowerHex = text.size() == count &&
                          std::all_of(text.begin(), text.end(), [](char c) { return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'); });
    if (!lowerHex) fail(LineError::Kind::OutOfRange, name(key));
    return text;
}

bool Fields::boolean(std::string_view key) const {
    const auto value = required(key);
    if (!value.isBool()) fail(LineError::Kind::WrongType, name(key));
    return value.isTrue();
}

std::optional<bool> Fields::optionalBoolean(std::string_view key) const {
    const auto value = optional(key);
    if (!value) return std::nullopt;
    if (!value->isBool()) fail(LineError::Kind::WrongType, name(key));
    return value->isTrue();
}

std::int64_t Fields::integer(std::string_view key) const { return checkedInteger(required(key), name(key)); }

std::optional<std::int64_t> Fields::optionalInteger(std::string_view key) const {
    const auto value = optional(key);
    if (!value) return std::nullopt;
    return checkedInteger(*value, name(key));
}

std::int64_t Fields::count(std::string_view key, std::int64_t limit) const {
    const auto value = integer(key);
    if (value < 0 || value > limit) fail(LineError::Kind::OutOfRange, name(key));
    return value;
}

std::int64_t Fields::time(std::string_view key) const {
    const auto value = integer(key);
    if (!isTime(value)) fail(LineError::Kind::BadTime, name(key));
    return value;
}

std::optional<std::int64_t> Fields::optionalTime(std::string_view key) const {
    const auto value = optionalInteger(key);
    if (value && !isTime(*value)) fail(LineError::Kind::BadTime, name(key));
    return value;
}

std::int64_t Fields::tokens(std::string_view key) const {
    const auto value = required(key);
    if (!value.isNumber()) fail(LineError::Kind::WrongType, name(key));
    return value.count();
}

double Fields::number(std::string_view key) const {
    const auto value = required(key);
    if (!value.isNumber()) fail(LineError::Kind::WrongType, name(key));
    const auto finite = value.finiteNumber();
    if (!finite) fail(LineError::Kind::OutOfRange, name(key));
    return *finite;
}

double Fields::fraction(std::string_view key) const { return std::clamp(number(key), 0.0, 1.0); }

json::Value Fields::members(std::string_view key) const {
    const auto value = required(key);
    if (!value.isObject()) fail(LineError::Kind::WrongType, name(key));
    return value;
}

std::vector<json::Value> Fields::array(std::string_view key, std::size_t limit) const {
    const auto value = required(key);
    if (!value.isArray()) fail(LineError::Kind::WrongType, name(key));
    if (value.size() > limit) fail(LineError::Kind::TooMany, name(key));
    return value.elements();
}

}  // namespace detail

using detail::Fields;
using detail::TextRule;
using detail::fail;

bool isTime(std::int64_t ms) { return ms >= kEarliestMs && ms <= kLatestMs; }

bool isDay(std::string_view text) {
    if (text.size() != 10) return false;
    for (std::size_t i = 0; i < 10; ++i) {
        const bool dash = i == 4 || i == 7;
        if (dash ? text[i] != '-' : (text[i] < '0' || text[i] > '9')) return false;
    }
    const auto number = [&](std::size_t from, std::size_t length) {
        int value = 0;
        for (std::size_t i = from; i < from + length; ++i) value = value * 10 + (text[i] - '0');
        return value;
    };
    const int year = number(0, 4), month = number(5, 2), day = number(8, 2);
    if (year < 2000 || year > 2100 || month < 1 || month > 12 || day < 1) return false;
    const bool leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
    constexpr int kLengths[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    return day <= kLengths[month - 1] + (month == 2 && leap ? 1 : 0);
}

namespace detail {
std::size_t codePoints(std::string_view text);
RemoteAgent agent(const Fields& f, const std::string& hostId, const std::string& prefix);
RemoteResponse response(const Fields& line, const std::string& hostId, const std::string& prefix);
RemoteTool tool(const Fields& f, const std::string& hostId, const std::string& prefix);
SourceReport status(const Fields& f);
HistoryDay historyDay(const Fields& f);
HistoryPerf historyPerf(const Fields& f);
std::string relayAgentId(const Fields& f, std::string_view key);
std::optional<RelayMessage> quotaLine(const Fields& f, const std::string& hostId);
std::optional<RelayMessage> quotaRemovedLine(const Fields& f, const std::string& hostId);
RelayMessage quotaBridgeLine(const Fields& f, const std::string& hostId);
}  // namespace detail

namespace {

// A relay of another protocol is still a `hello`: only its version is read, so the status can say why.
Hello hello(const Fields& f) {
    Hello h;
    h.protocol = f.integer("protocol");
    if (h.protocol != kProtocol) return h;
    h.version = f.string("version", 64, TextRule::Model);
    h.arch = f.string("arch", 32, TextRule::Model);
    h.nowMs = f.time("now");
    return h;
}

Parents parents(const Fields& f, const std::string& prefix) {
    const auto links = f.members("links");
    if (links.size() > limits::kAgents) fail(LineError::Kind::TooMany, "links");
    Parents out;
    for (const auto& [child, parent] : links.members()) {
        const auto parentId = detail::checkedString(parent, "links.", limits::kAgentIdBytes, TextRule::Identifier);
        if (child.empty() || parentId.empty() || child.size() > limits::kAgentIdBytes || !detail::allows(TextRule::Identifier, child)) {
            fail(LineError::Kind::BadCharacter, "links");
        }
        out.links[prefix + child] = prefix + parentId;
    }
    return out;
}

RelayError error(const Fields& f) {
    RelayError e;
    e.codeText = f.string("code", 64, TextRule::Model);
    e.detail = f.string("detail", limits::kErrorDetailCharacters * 4, TextRule::Model);
    if (detail::codePoints(e.detail) > limits::kErrorDetailCharacters) fail(LineError::Kind::TooLong, "detail");
    e.code = e.codeText == "badStart"        ? RelayError::Code::BadStart
             : e.codeText == "historyFailed" ? RelayError::Code::HistoryFailed
             : e.codeText == "internal"      ? RelayError::Code::Internal
                                             : RelayError::Code::Other;
    return e;
}

std::optional<RelayMessage> message(std::string_view type, const Fields& f, const std::string& hostId, const std::string& prefix) {
    if (type == "hello") return hello(f);
    if (type == "progress") return Progress{f.fraction("value")};
    if (type == "agent") return detail::agent(f.object("a"), hostId, prefix);
    if (type == "agentRemoved") return AgentRemoved{prefix + detail::relayAgentId(f, "id")};
    if (type == "parents") return parents(f, prefix);
    if (type == "response") return detail::response(f, hostId, prefix);
    if (type == "tool") return detail::tool(f, hostId, prefix);
    if (type == "ready") return Ready{};
    if (type == "status") return detail::status(f);
    if (type == "clock") {
        ClockReply c;
        c.id = f.integer("id");
        c.nowMs = f.time("now");
        return c;
    }
    if (type == "historyDay") return detail::historyDay(f);
    if (type == "historyPerf") return detail::historyPerf(f);
    if (type == "historyDone") {
        HistoryDone d;
        d.days = f.count("days");
        d.analyzed = f.count("analyzed");
        return d;
    }
    if (type == "historyProgress") return HistoryProgress{f.fraction("value")};
    if (type == "error") return error(f);
    if (type == "quota") return detail::quotaLine(f, hostId);
    if (type == "quotaRemoved") return detail::quotaRemovedLine(f, hostId);
    if (type == "quotaBridge") return detail::quotaBridgeLine(f, hostId);
    return std::nullopt;
}

}  // namespace

LineDecoder::LineDecoder(std::string hostId) : hostId_(std::move(hostId)), prefix_(agentPrefix(hostId_)) {}

LineDecoder::Outcome LineDecoder::decode(std::string_view line) const {
    if (line.size() > limits::kRelayLineBytes) return LineError{LineError::Kind::Oversized, {}};
    json::Document document;
    if (!document.parse(line, detail::kMaxLineDepth)) return LineError{LineError::Kind::NotJson, {}};
    if (!document.root().isObject()) return LineError{LineError::Kind::NotObject, {}};
    try {
        const Fields fields(document.root(), "");
        const auto type = fields.string("t", 64);
        auto decoded = message(type, fields, hostId_, prefix_);
        if (!decoded) return Ignored{};
        return std::move(*decoded);
    } catch (const LineError& error) {
        return error;
    }
}

}  // namespace agentspeed::remote
