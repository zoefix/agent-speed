#include "agentspeed/quota/ordered_json.hpp"

#include <charconv>
#include <cmath>
#include <cstdio>

#include "agentspeed/json.hpp"
#include "agentspeed/quota/files.hpp"

namespace agentspeed::quota::ordered_json {

namespace {

Value convert(const json::Value& value) {
    Value out;
    switch (value.type()) {
        case json::Type::Null: out.type = Value::Type::Null; break;
        case json::Type::True:
        case json::Type::False:
            out.type = Value::Type::Bool;
            out.boolean = value.isTrue();
            break;
        case json::Type::Number:
            out.type = Value::Type::Number;
            out.text = std::string(value.raw());
            break;
        case json::Type::String:
            out.type = Value::Type::String;
            out.text = *value.string();
            break;
        case json::Type::Array:
            out.type = Value::Type::Array;
            for (const auto& item : value.elements()) out.items.push_back(convert(item));
            break;
        case json::Type::Object:
            out.type = Value::Type::Object;
            for (const auto& [key, member] : value.members()) out.set(key, convert(member));
            break;
    }
    return out;
}

void quote(std::string& out, std::string_view text) {
    out += '"';
    for (const char c : text) {
        const auto byte = static_cast<unsigned char>(c);
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            default:
                if (byte < 0x20) {
                    char escaped[8];
                    std::snprintf(escaped, sizeof escaped, "\\u%04x", static_cast<unsigned>(byte));
                    out += escaped;
                } else {
                    out += c;
                }
        }
    }
    out += '"';
}

void indent(std::string& out, int level) { out.append(static_cast<std::size_t>(level) * 2, ' '); }

void writeInto(std::string& out, const Value& value, int level) {
    switch (value.type) {
        case Value::Type::Null: out += "null"; break;
        case Value::Type::Bool: out += value.boolean ? "true" : "false"; break;
        case Value::Type::Number: out += value.text; break;
        case Value::Type::String: quote(out, value.text); break;
        case Value::Type::Array:
            if (value.items.empty()) {
                out += "[]";
                break;
            }
            out += "[\n";
            for (std::size_t i = 0; i < value.items.size(); ++i) {
                indent(out, level + 1);
                writeInto(out, value.items[i], level + 1);
                out += i + 1 < value.items.size() ? ",\n" : "\n";
            }
            indent(out, level);
            out += ']';
            break;
        case Value::Type::Object:
            if (value.members.empty()) {
                out += "{}";
                break;
            }
            out += "{\n";
            for (std::size_t i = 0; i < value.members.size(); ++i) {
                indent(out, level + 1);
                quote(out, value.members[i].first);
                out += ": ";
                writeInto(out, value.members[i].second, level + 1);
                out += i + 1 < value.members.size() ? ",\n" : "\n";
            }
            indent(out, level);
            out += '}';
            break;
    }
}

}  // namespace

bool Value::isFiniteNumber() const {
    if (type != Type::Number) return false;
    // from_chars ignores the locale (the app's may use a decimal comma) and reports overflow.
    double parsed = 0;
    const auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), parsed);
    return error == std::errc() && end == text.data() + text.size() && std::isfinite(parsed);
}

const Value* Value::find(std::string_view key) const {
    for (const auto& [name, value] : members) {
        if (name == key) return &value;
    }
    return nullptr;
}

void Value::set(const std::string& key, Value value) {
    for (auto& [name, current] : members) {
        if (name == key) {
            current = std::move(value);
            return;
        }
    }
    members.emplace_back(key, std::move(value));
}

void Value::remove(std::string_view key) {
    std::erase_if(members, [&](const std::pair<std::string, Value>& member) { return member.first == key; });
}

std::optional<Value> parse(std::string_view text) {
    if (text.substr(0, 3) == "\xEF\xBB\xBF" || !files::validUtf8(text)) return std::nullopt;
    json::Document doc;
    if (!doc.parse(text)) return std::nullopt;
    return convert(doc.root());
}

std::string write(const Value& value) {
    std::string out;
    writeInto(out, value, 0);
    return out;
}

}  // namespace agentspeed::quota::ordered_json
