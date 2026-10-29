#pragma once

#include <QString>
#include <QStringList>
#include <QVariantMap>
#include <deque>
#include <map>
#include <optional>
#include <vector>

#include "agentspeed/board.hpp"
#include "agentspeed/report_body.hpp"
#include "agentspeed/snapshot.hpp"

class Strings;

// One board as the worker sees it: its id and sources (nullopt: every enabled source).
struct BoardSpec {
    QString id;
    std::optional<std::vector<agentspeed::SourceKind>> sources;
    bool operator==(const BoardSpec&) const = default;
};

// What a board's content depends on besides the snapshot (DESIGN 2c Content, Live metrics).
struct BoardContext {
    const Strings* strings = nullptr;
    QString currency = QStringLiteral("USD"), rate = QStringLiteral("1"), language = QStringLiteral("en");
    QStringList subscriptions;
    bool includeSubscriptions = false;
    bool totalMetric = false;
    int redline = 2000;
    qint64 steadyMs = 0;
    QString day;  // the local date, for today's peak of a subset
};

// DESIGN 2c: every board's hero, sub-gauge, stable ranking (groups of a root and its descendants) and footer, computed
// on the engine tick from the board's sources. Keeps each board's ranking, history and peak between ticks.
class BoardViews {
public:
    // `setOffset`: the index of the first board's score set in the snapshot (METRICS 6.9 Sets and groups).
    QVariantMap compute(const agentspeed::Snapshot& snap, const std::vector<BoardSpec>& boards, const BoardContext& context, std::size_t setOffset);

private:
    struct State {
        agentspeed::board::Ranking ranking;
        std::deque<std::pair<qint64, double>> seconds;  // the last 60 one-second maxima of the board's live value
        double peak = 0;
        QString peakDay;
        agentspeed::report::TitleTracker title;
    };
    std::map<QString, State> states_;
};
