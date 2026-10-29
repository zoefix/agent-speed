#pragma once

#include <QColor>
#include <QHash>
#include <QString>

// DESIGN 1 Source colors, by METRICS 1 key.
inline QColor sourceColor(const QString& id) {
    static const QHash<QString, QRgb> colors = {
        {QStringLiteral("codex"), 0x34D399},     {QStringLiteral("claudeCode"), 0xFF8A65},
        {QStringLiteral("claudeDesktop"), 0xB39DFF}, {QStringLiteral("deepseek"), 0x4D6BFE},
        {QStringLiteral("opencode"), 0xF4B13E},  {QStringLiteral("gemini"), 0x34B8F0},
        {QStringLiteral("omp"), 0x84CC16},       {QStringLiteral("antigravity"), 0xEC4899},
        {QStringLiteral("workbuddy"), 0x11A68B}, {QStringLiteral("workbuddyAi"), 0x007AB4},
        {QStringLiteral("zcode"), 0x466BFF},     {QStringLiteral("kimi"), 0xE16F8A},
        {QStringLiteral("grokBuild"), 0x8290A6}, {QStringLiteral("pi"), 0xA855F7},
        {QStringLiteral("openclaw"), 0xDE2E1F},  {QStringLiteral("hermes"), 0xB8860B},
        {QStringLiteral("droid"), 0x9E5F46}};
    return QColor(colors.value(id, 0x7C8798));
}
