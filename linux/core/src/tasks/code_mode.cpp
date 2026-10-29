#include <charconv>
#include <cmath>
#include <variant>

#include "agentspeed/text.hpp"
#include "internal.hpp"

// Codex code mode (TASK-PARSERS 4.4): the tool calls and printing statements of a JavaScript script.
namespace agentspeed::tasks::detail {

namespace {

struct JsError {};

// A JavaScript literal value as JSON: null, bool, integer (exact digits), double, string, array, object (ordered).
struct JsValue {
    enum class Kind { Null, Bool, Int, Float, String, Array, Object } kind = Kind::Null;
    bool boolean = false;
    std::string text;  // integer digits (with sign) or string value
    double number = 0;
    std::vector<JsValue> items;
    std::vector<std::pair<std::string, JsValue>> members;
};

void appendJsonString(std::string& out, const std::string& s) {
    out += '"';
    for (const unsigned char c : s) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            default:
                if (c < 0x20) {
                    char buffer[8];
                    std::snprintf(buffer, sizeof buffer, "\\u%04x", c);
                    out += buffer;
                } else {
                    out += static_cast<char>(c);
                }
        }
    }
    out += '"';
}

void serialize(std::string& out, const JsValue& v) {
    switch (v.kind) {
        case JsValue::Kind::Null: out += "null"; break;
        case JsValue::Kind::Bool: out += v.boolean ? "true" : "false"; break;
        case JsValue::Kind::Int: out += v.text; break;
        case JsValue::Kind::Float: {
            char buffer[64];
            const auto [ptr, ec] = std::to_chars(buffer, buffer + sizeof buffer, v.number);
            std::string text(buffer, ptr);
            if (text.find_first_of(".eEn") == std::string::npos) text += ".0";
            out += text;
            break;
        }
        case JsValue::Kind::String: appendJsonString(out, v.text); break;
        case JsValue::Kind::Array:
            out += '[';
            for (std::size_t i = 0; i < v.items.size(); ++i) {
                if (i) out += ',';
                serialize(out, v.items[i]);
            }
            out += ']';
            break;
        case JsValue::Kind::Object:
            out += '{';
            for (std::size_t i = 0; i < v.members.size(); ++i) {
                if (i) out += ',';
                appendJsonString(out, v.members[i].first);
                out += ':';
                serialize(out, v.members[i].second);
            }
            out += '}';
            break;
    }
}

struct Tok {
    enum class Kind { Num, Str, Regex, Id, Punct } kind;
    std::size_t start, end;
    std::optional<JsValue> value;  // str (nullopt: a template with ${…}), num (nullopt: not a plain number)
    std::string text;
};

bool isJsWs(char c) { return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v'; }
bool isIdentStart(char c) { return std::isalpha(static_cast<unsigned char>(c)) || c == '_' || c == '$'; }
bool isIdent(char c) { return isIdentStart(c) || std::isdigit(static_cast<unsigned char>(c)); }
bool isHex(char c) { return std::isxdigit(static_cast<unsigned char>(c)); }

// Decoded characters as code units / code points; surrogate pairs from \u escapes are joined, lone ones become U+FFFD.
std::string joinUnits(const std::vector<std::uint32_t>& units) {
    std::string out;
    for (std::size_t i = 0; i < units.size(); ++i) {
        const auto u = units[i];
        if (u >= 0xD800 && u <= 0xDBFF && i + 1 < units.size() && units[i + 1] >= 0xDC00 && units[i + 1] <= 0xDFFF) {
            text::appendUtf8(out, 0x10000 + ((u - 0xD800) << 10) + (units[i + 1] - 0xDC00));
            ++i;
        } else if (u >= 0xD800 && u <= 0xDFFF) {
            text::appendUtf8(out, 0xFFFD);
        } else {
            text::appendUtf8(out, u);
        }
    }
    return out;
}

std::size_t lineBreakAt(std::string_view text, std::size_t i) {
    if (text.substr(i, 2) == "\r\n") return 2;
    if (i < text.size() && (text[i] == '\n' || text[i] == '\r')) return 1;
    if (text.substr(i, 3) == "\xE2\x80\xA8" || text.substr(i, 3) == "\xE2\x80\xA9") return 3;
    return 0;
}

std::size_t jsEscape(std::string_view text, std::size_t i, std::vector<std::uint32_t>& out) {
    if (const auto brk = lineBreakAt(text, i)) return i + brk;
    const char c = text[i];
    switch (c) {
        case 'n': out.push_back('\n'); return i + 1;
        case 'r': out.push_back('\r'); return i + 1;
        case 't': out.push_back('\t'); return i + 1;
        case 'b': out.push_back('\b'); return i + 1;
        case 'f': out.push_back('\f'); return i + 1;
        case 'v': out.push_back('\v'); return i + 1;
        default: break;
    }
    if (c == '0' && !(i + 1 < text.size() && std::isdigit(static_cast<unsigned char>(text[i + 1])))) {
        out.push_back(0);
        return i + 1;
    }
    if (c == 'x' && i + 2 < text.size() && isHex(text[i + 1]) && isHex(text[i + 2])) {
        out.push_back(static_cast<std::uint32_t>(std::stoul(std::string(text.substr(i + 1, 2)), nullptr, 16)));
        return i + 3;
    }
    if (c == 'u') {
        if (i + 1 < text.size() && text[i + 1] == '{') {
            std::size_t j = i + 2;
            while (j < text.size() && isHex(text[j]) && j - (i + 2) < 6) ++j;
            if (j > i + 2 && j < text.size() && text[j] == '}') {
                const auto cp = std::stoul(std::string(text.substr(i + 2, j - i - 2)), nullptr, 16);
                if (cp <= 0x10FFFF) {
                    out.push_back(static_cast<std::uint32_t>(cp));
                    return j + 1;
                }
            }
        }
        if (i + 4 < text.size() && isHex(text[i + 1]) && isHex(text[i + 2]) && isHex(text[i + 3]) && isHex(text[i + 4])) {
            out.push_back(static_cast<std::uint32_t>(std::stoul(std::string(text.substr(i + 1, 4)), nullptr, 16)));
            return i + 5;
        }
    }
    std::size_t next = i;
    out.push_back(text::nextCodePoint(text, next));
    return next;
}

std::pair<std::string, std::size_t> jsString(std::string_view text, std::size_t i) {
    const char quote = text[i];
    std::vector<std::uint32_t> out;
    std::size_t j = i + 1;
    while (j < text.size()) {
        const char c = text[j];
        if (c == quote) return {joinUnits(out), j + 1};
        if (c == '\\' && j + 1 < text.size()) {
            j = jsEscape(text, j + 1, out);
            continue;
        }
        if (c == '\n' || c == '\r') break;
        out.push_back(text::nextCodePoint(text, j));
    }
    throw JsError{};
}

std::pair<std::vector<Tok>, std::size_t> jsLex(std::string_view text, std::size_t i, bool untilBrace);

std::pair<std::optional<std::string>, std::size_t> jsTemplate(std::string_view text, std::size_t i) {
    std::vector<std::uint32_t> out;
    std::size_t j = i + 1;
    bool opaque = false;
    while (j < text.size()) {
        const char c = text[j];
        if (c == '`') return {opaque ? std::nullopt : std::optional<std::string>(joinUnits(out)), j + 1};
        if (c == '\\' && j + 1 < text.size()) {
            j = jsEscape(text, j + 1, out);
            continue;
        }
        if (text.substr(j, 2) == "${") {
            opaque = true;
            j = jsLex(text, j + 2, true).second;
            continue;
        }
        if (c == '\r') {
            out.push_back('\n');
            j += text.substr(j, 2) == "\r\n" ? 2 : 1;
            continue;
        }
        out.push_back(text::nextCodePoint(text, j));
    }
    throw JsError{};
}

std::optional<std::size_t> jsRegexEnd(std::string_view text, std::size_t i) {
    std::size_t j = i + 1;
    bool inClass = false;
    while (j < text.size()) {
        const char c = text[j];
        if (c == '\n' || c == '\r' || text.substr(j, 3) == "\xE2\x80\xA8" || text.substr(j, 3) == "\xE2\x80\xA9") return std::nullopt;
        if (c == '\\') {
            j += 2;
            continue;
        }
        if (c == '[') {
            inClass = true;
        } else if (c == ']') {
            inClass = false;
        } else if (c == '/' && !inClass) {
            ++j;
            while (j < text.size() && isIdent(text[j])) ++j;
            return j;
        }
        ++j;
    }
    return std::nullopt;
}

bool regexAllowed(const std::vector<Tok>& toks) {
    if (toks.empty()) return true;
    const auto& last = toks.back();
    if (last.kind == Tok::Kind::Num || last.kind == Tok::Kind::Str || last.kind == Tok::Kind::Regex) return false;
    if (last.kind == Tok::Kind::Id) {
        return in(last.text, {"return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else", "yield", "await"});
    }
    return last.text != ")" && last.text != "]" && last.text != "}";
}

std::pair<std::vector<Tok>, std::size_t> jsLex(std::string_view text, std::size_t i, bool untilBrace) {
    std::vector<Tok> toks;
    int depth = 0;
    const auto n = text.size();
    while (i < n) {
        const char c = text[i];
        if (isJsWs(c)) {
            ++i;
        } else if (text.substr(i, 2) == "//") {
            const auto j = text.find('\n', i);
            i = j == std::string_view::npos ? n : j;
        } else if (text.substr(i, 2) == "/*") {
            const auto j = text.find("*/", i + 2);
            i = j == std::string_view::npos ? n : j + 2;
        } else if (c == '\'' || c == '"') {
            auto [value, j] = jsString(text, i);
            JsValue v;
            v.kind = JsValue::Kind::String;
            v.text = value;
            toks.push_back({Tok::Kind::Str, i, j, v, std::string(text.substr(i, j - i))});
            i = j;
        } else if (c == '`') {
            auto [value, j] = jsTemplate(text, i);
            std::optional<JsValue> v;
            if (value) {
                v = JsValue();
                v->kind = JsValue::Kind::String;
                v->text = *value;
            }
            toks.push_back({Tok::Kind::Str, i, j, v, std::string(text.substr(i, j - i))});
            i = j;
        } else if (c == '/' && regexAllowed(toks) && jsRegexEnd(text, i)) {
            const auto j = *jsRegexEnd(text, i);
            toks.push_back({Tok::Kind::Regex, i, j, std::nullopt, std::string(text.substr(i, j - i))});
            i = j;
        } else if (isIdentStart(c)) {
            std::size_t j = i + 1;
            while (j < n && isIdent(text[j])) ++j;
            toks.push_back({Tok::Kind::Id, i, j, std::nullopt, std::string(text.substr(i, j - i))});
            i = j;
        } else if (std::isdigit(static_cast<unsigned char>(c))) {
            std::size_t j = i + 1;
            while (j < n && (std::isalnum(static_cast<unsigned char>(text[j])) || text[j] == '_' || text[j] == '.' ||
                             ((text[j] == '+' || text[j] == '-') && (text[j - 1] == 'e' || text[j - 1] == 'E')))) {
                ++j;
            }
            const std::string raw(text.substr(i, j - i));
            std::optional<JsValue> value;
            if (fullMatch(raw, "[0-9]+(?:\\.[0-9]+)?(?:[eE][+-]?[0-9]+)?")) {
                JsValue v;
                if (raw.find_first_not_of("0123456789") == std::string::npos) {
                    v.kind = JsValue::Kind::Int;
                    v.text = lstrip(raw, "0");
                    if (v.text.empty()) v.text = "0";
                } else {
                    v.kind = JsValue::Kind::Float;
                    v.number = std::strtod(raw.c_str(), nullptr);
                }
                value = v;
            }
            toks.push_back({Tok::Kind::Num, i, j, value, raw});
            i = j;
        } else {
            if (untilBrace) {
                if (c == '{') {
                    ++depth;
                } else if (c == '}') {
                    if (depth == 0) return {toks, i + 1};
                    --depth;
                }
            }
            toks.push_back({Tok::Kind::Punct, i, i + 1, std::nullopt, std::string(1, c)});
            ++i;
        }
    }
    if (untilBrace) throw JsError{};
    return {toks, i};
}

bool isPunct(const Tok* tok, std::string_view text) { return tok && tok->kind == Tok::Kind::Punct && tok->text == text; }
bool opens(const Tok& t) { return t.kind == Tok::Kind::Punct && (t.text == "(" || t.text == "[" || t.text == "{"); }
bool closes(const Tok& t) { return t.kind == Tok::Kind::Punct && (t.text == ")" || t.text == "]" || t.text == "}"); }

std::optional<std::size_t> jsClose(const std::vector<Tok>& toks, std::size_t k) {
    int depth = 0;
    for (std::size_t j = k; j < toks.size(); ++j) {
        if (opens(toks[j])) {
            ++depth;
        } else if (closes(toks[j])) {
            if (--depth == 0) return j;
        }
    }
    return std::nullopt;
}

using Env = std::map<std::string, JsValue>;

// The literal at toks[p] before `end`, or nullopt when it is not a literal.
std::pair<std::optional<JsValue>, std::size_t> jsLiteral(const std::vector<Tok>& toks, std::size_t p, std::size_t end, const Env* env) {
    if (p >= end) return {std::nullopt, p};
    const auto& t = toks[p];
    if (t.kind == Tok::Kind::Id && env && env->contains(t.text)) return {env->at(t.text), p + 1};
    if (t.kind == Tok::Kind::Str || t.kind == Tok::Kind::Num) {
        if (!t.value) return {std::nullopt, p};
        return {*t.value, p + 1};
    }
    if (isPunct(&t, "-") && p + 1 < end && toks[p + 1].kind == Tok::Kind::Num && toks[p + 1].value) {
        auto v = *toks[p + 1].value;
        if (v.kind == JsValue::Kind::Int) {
            v.text = v.text == "0" ? "0" : "-" + v.text;
        } else {
            v.number = -v.number;
        }
        return {v, p + 2};
    }
    if (t.kind == Tok::Kind::Id && in(t.text, {"true", "false", "null"})) {
        JsValue v;
        if (t.text == "null") {
            v.kind = JsValue::Kind::Null;
        } else {
            v.kind = JsValue::Kind::Bool;
            v.boolean = t.text == "true";
        }
        return {v, p + 1};
    }
    if (isPunct(&t, "{")) {
        const auto close = jsClose(toks, p);
        if (!close || *close >= end) return {std::nullopt, p};
        JsValue obj;
        obj.kind = JsValue::Kind::Object;
        std::size_t q = p + 1;
        while (q < *close) {
            std::size_t stop = q;
            int depth = 0;
            while (stop < *close && !(depth == 0 && isPunct(&toks[stop], ","))) {
                if (opens(toks[stop])) {
                    ++depth;
                } else if (closes(toks[stop])) {
                    --depth;
                }
                ++stop;
            }
            const auto& keyTok = toks[q];
            std::optional<std::string> key;
            if (keyTok.kind == Tok::Kind::Id) {
                key = keyTok.text;
            } else if (keyTok.kind == Tok::Kind::Str && keyTok.value) {
                key = keyTok.value->text;
            } else if (keyTok.kind == Tok::Kind::Num && keyTok.value && keyTok.value->kind == JsValue::Kind::Int) {
                key = keyTok.value->text;
            }
            if (key && q + 1 < stop && isPunct(&toks[q + 1], ":")) {
                auto [value, after] = jsLiteral(toks, q + 2, stop, env);
                if (value && after == stop) {
                    bool replaced = false;
                    for (auto& [k, v] : obj.members) {
                        if (k == *key) {
                            v = *value;
                            replaced = true;
                        }
                    }
                    if (!replaced) obj.members.emplace_back(*key, *value);
                }
            }
            q = stop + 1;
        }
        return {obj, *close + 1};
    }
    if (isPunct(&t, "[")) {
        const auto close = jsClose(toks, p);
        if (!close || *close >= end) return {std::nullopt, p};
        JsValue arr;
        arr.kind = JsValue::Kind::Array;
        std::size_t q = p + 1;
        while (q < *close) {
            auto [value, after] = jsLiteral(toks, q, *close, env);
            if (!value || (after < *close && !isPunct(&toks[after], ","))) return {std::nullopt, p};
            arr.items.push_back(*value);
            q = after + 1;
        }
        return {arr, *close + 1};
    }
    return {std::nullopt, p};
}

std::optional<JsValue> callInput(const std::vector<Tok>& toks, std::size_t k, std::size_t close, const Env* env) {
    if (close == k + 4) return std::nullopt;
    auto [arg, after] = jsLiteral(toks, k + 4, close, env);
    if (!arg || after != close) return std::nullopt;
    return arg;
}

struct Plain {
    bool plain = false;
    std::vector<std::pair<int, std::string>> prints;
    std::map<std::size_t, std::optional<JsValue>> inputs;
};

Plain plainStatements(const std::vector<Tok>& toks, const std::map<std::size_t, std::pair<std::size_t, std::size_t>>& spans) {
    Env env;
    std::map<std::string, std::size_t> bound;
    Plain result;
    std::size_t p = 0;
    const auto n = toks.size();
    const auto at = [&](std::size_t i, std::string_view text, Tok::Kind kind = Tok::Kind::Punct) {
        return i < n && toks[i].kind == kind && toks[i].text == text;
    };
    const auto awaited = [&](std::size_t i) -> std::pair<std::optional<std::size_t>, std::size_t> {
        if (at(i, "await", Tok::Kind::Id) && spans.contains(i + 1)) {
            const auto [index, close] = spans.at(i + 1);
            result.inputs[index] = callInput(toks, i + 1, close, &env);
            return {index, close + 1};
        }
        return {std::nullopt, i};
    };
    const Plain fail{};
    while (p < n) {
        if (at(p, ";")) {
            ++p;
            continue;
        }
        if (toks[p].kind == Tok::Kind::Id && in(toks[p].text, {"const", "let", "var"}) && p + 2 < n && toks[p + 1].kind == Tok::Kind::Id && at(p + 2, "=")) {
            const auto name = toks[p + 1].text;
            auto [index, q] = awaited(p + 3);
            if (index) {
                bound[name] = *index;
                env.erase(name);
            } else {
                auto [literal, after] = jsLiteral(toks, p + 3, n, &env);
                if (!literal) return fail;
                env[name] = *literal;
                bound.erase(name);
                q = after;
            }
            p = q;
            continue;
        }
        if (at(p, "text", Tok::Kind::Id) && at(p + 1, "(")) {
            const auto q = p + 2;
            std::string mode = "result";
            auto [index, after] = awaited(q);
            if (!index && at(q, "(")) {
                auto [inner, innerAfter] = awaited(q + 1);
                if (inner && at(innerAfter, ")") && at(innerAfter + 1, ".") && at(innerAfter + 2, "output", Tok::Kind::Id)) {
                    index = inner;
                    mode = "output";
                    after = innerAfter + 3;
                } else {
                    index.reset();
                }
            }
            if (!index && q < n && toks[q].kind == Tok::Kind::Id && bound.contains(toks[q].text)) {
                index = bound.at(toks[q].text);
                after = q + 1;
                if (at(after, ".") && at(after + 1, "output", Tok::Kind::Id)) {
                    mode = "output";
                    after += 2;
                }
            }
            if (!index || !at(after, ")")) return fail;
            result.prints.emplace_back(static_cast<int>(*index), mode);
            p = after + 1;
            continue;
        }
        auto [index, q] = awaited(p);
        if (!index) return fail;
        p = q;
    }
    result.plain = true;
    return result;
}

}  // namespace

}  // namespace agentspeed::tasks::detail

namespace agentspeed::tasks {

using namespace detail;

std::optional<CodeScript> codeScript(SourceKind source, const std::optional<std::string>& tool, const json::Value& input) {
    if (familyOf(source) != "codex" || tool != "exec" || !input.isString()) return std::nullopt;
    std::unique_ptr<OwnedJson> holder;
    if (decodeArgs(input, holder).valid()) return std::nullopt;
    const auto text = *input.string();
    std::vector<Tok> toks;
    try {
        toks = jsLex(text, 0, false).first;
    } catch (const JsError&) {
        return std::nullopt;
    }
    CodeScript script;
    std::vector<std::optional<JsValue>> inputs;
    std::map<std::size_t, std::pair<std::size_t, std::size_t>> spans;
    for (std::size_t k = 0; k < toks.size(); ++k) {
        const auto& t = toks[k];
        if (t.kind != Tok::Kind::Id || t.text != "tools" || (k > 0 && isPunct(&toks[k - 1], "."))) continue;
        if (!(k + 3 < toks.size() && isPunct(&toks[k + 1], ".") && toks[k + 2].kind == Tok::Kind::Id && isPunct(&toks[k + 3], "("))) continue;
        const auto close = jsClose(toks, k + 3);
        if (!close) continue;
        spans[k] = {script.calls.size(), *close};
        inputs.push_back(callInput(toks, k, *close, nullptr));
        script.calls.push_back({toks[k + 2].text, {}, sha16(text.substr(t.start, toks[*close].end - t.start))});
    }
    if (script.calls.empty()) return std::nullopt;
    const auto plain = plainStatements(toks, spans);
    if (plain.plain) {
        for (const auto& [index, value] : plain.inputs) inputs[index] = value;
    }
    for (std::size_t i = 0; i < script.calls.size(); ++i) {
        std::string json;
        if (inputs[i]) {
            serialize(json, *inputs[i]);
        } else {
            json = "null";
        }
        script.calls[i].input = json;
    }
    script.plain = plain.plain;
    if (plain.plain) script.prints = plain.prints;
    return script;
}

std::optional<std::vector<std::optional<std::string>>> codeItems(SourceKind source, const json::Value& result) {
    if (familyOf(source) != "codex" || !result.isObject()) return std::nullopt;
    const auto out = result["type"].valid() && result["output"].valid() ? result["output"] : result;
    if (!out.isArray() || out.size() == 0) return std::nullopt;
    std::vector<std::optional<std::string>> texts;
    for (const auto& item : out.elements()) {
        if (item.isObject() && item["text"].isString()) {
            texts.emplace_back(lf(*item["text"].string()));
        } else {
            texts.emplace_back(std::nullopt);
        }
    }
    if (!texts[0] || split(*texts[0], '\n')[0] != "Script completed") return std::nullopt;
    texts.erase(texts.begin());
    return texts;
}

}  // namespace agentspeed::tasks
