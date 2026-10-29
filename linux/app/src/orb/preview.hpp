#pragma once

#include <QElapsedTimer>
#include <QImage>
#include <QQuickPaintedItem>
#include <QTimer>
#include <memory>

#include "orb/gauge.hpp"
#include "orb/style.hpp"

namespace orb {

// A live preview of an orb style for Settings (DESIGN 5, Floating orb pane): the real renderer at `diameter`, Soft
// effects, looping cruise → redline → cruise every 8 s from `offset` seconds, at most 24 fps and only while visible.
// Frames are drawn offscreen with OpenGL and painted as images; without OpenGL it stays empty.
class PreviewItem : public QQuickPaintedItem {
    Q_OBJECT
    Q_PROPERTY(QString style READ style WRITE setStyle NOTIFY styleChanged)
    Q_PROPERTY(double diameter READ diameter WRITE setDiameter NOTIFY diameterChanged)
    Q_PROPERTY(double offset READ offset WRITE setOffset NOTIFY offsetChanged)
    Q_PROPERTY(bool dark READ dark WRITE setDark NOTIFY darkChanged)
    Q_PROPERTY(bool running READ running WRITE setRunning NOTIFY runningChanged)

public:
    explicit PreviewItem(QQuickItem* parent = nullptr);
    ~PreviewItem() override;

    // The value's unit, as the orb shows it.
    static void setUnit(QString unit);

    QString style() const { return styleKey(kind_); }
    void setStyle(const QString& style);
    double diameter() const { return diameter_; }
    void setDiameter(double diameter);
    double offset() const { return offset_; }
    void setOffset(double offset);
    bool dark() const { return dark_; }
    void setDark(bool dark);
    bool running() const { return running_; }
    void setRunning(bool running);

    void paint(QPainter* painter) override;

signals:
    void styleChanged();
    void diameterChanged();
    void offsetChanged();
    void darkChanged();
    void runningChanged();

protected:
    void itemChange(ItemChange change, const ItemChangeData& value) override;

private:
    void updateTimer();
    void tick();

    StyleKind kind_ = StyleKind::Led;
    std::unique_ptr<Style> style_;
    Gauge gauge_;
    double diameter_ = 64;
    double offset_ = 0;
    bool dark_ = true;
    bool running_ = true;
    bool fresh_ = true;
    double last_ = -1;
    double redline_ = 0, activity_ = 0;
    QTimer timer_;
    QImage frame_;
};

}  // namespace orb
