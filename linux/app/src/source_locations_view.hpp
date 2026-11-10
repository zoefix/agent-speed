#pragma once

#include <QObject>
#include <QVariantList>

#include "agentspeed/locations.hpp"

class Preferences;

// DESIGN 5 資料來源: the location lists as Settings shows and edits them (METRICS 2.1). Lists are named as stored
// (`codex`, `claude`, … `qoderCli`).
class SourceLocationsView : public QObject {
    Q_OBJECT
    Q_PROPERTY(int revision READ revision NOTIFY changed)
    Q_PROPERTY(int maxEntries READ maxEntries CONSTANT)

public:
    explicit SourceLocationsView(Preferences& prefs, QObject* parent = nullptr);

    int revision() const { return revision_; }
    static int maxEntries() { return static_cast<int>(agentspeed::LocationLists::kMaxEntries); }

    // Every group in settings order: {list, sources (ids), hint (string key), acceptsFile}.
    Q_INVOKABLE QVariantList groups() const;
    // One row per entry: {key, automatic, lines [{text, full, readable}], exists, canReveal, isFile, path, accessible}.
    Q_INVOKABLE QVariantList entries(const QString& list) const;
    Q_INVOKABLE bool canAdd(const QString& list) const;
    Q_INVOKABLE bool canRemove(const QString& list) const;
    Q_INVOKABLE bool canReset(const QString& list) const;

    // Opens the folder picker (OpenClaw's also takes `openclaw-agent.sqlite`) and adds the pick.
    Q_INVOKABLE void choose(const QString& list, const QString& title);
    // Adds a location as a pick would; a refusal is announced with `refused`.
    Q_INVOKABLE void add(const QString& list, const QString& path);
    Q_INVOKABLE void remove(const QString& list, int index);
    Q_INVOKABLE void reset(const QString& list);
    // Show in Files: the entry's folder (a database file's folder) in the file manager.
    Q_INVOKABLE void reveal(const QString& list, int index) const;
    Q_INVOKABLE QString displayPath(const QString& path) const;

signals:
    void changed();
    // `reason`: alreadyListed, alreadyDefault or limit; `path` as picked.
    void refused(const QString& list, const QString& reason, const QString& path);

private:
    std::optional<agentspeed::RootFamily> listOf(const QString& list) const;
    QString pickerStart(agentspeed::RootFamily list) const;

    Preferences& prefs_;
    agentspeed::Environment environment_;
    std::string home_;
    int revision_ = 0;
};
