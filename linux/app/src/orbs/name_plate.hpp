#pragma once

#include <QColor>
#include <QPointer>
#include <QRasterWindow>
#include <QStringList>
#include <vector>

class QVariantAnimation;

// DESIGN 2b Name plate and Source chip: a small glass capsule beside an orb with its colour dots and name, the orb's
// own glass, never wrapped and never shrunk. While the orb is hovered, dragged or just changed it widens to the chip's
// content (every source with its status). Click-through; the window carries a margin for the shadow.
class NamePlate : public QRasterWindow {
    Q_OBJECT
public:
    struct Dot {
        QColor color;
        bool available = true;
        bool operator==(const Dot&) const = default;
    };
    struct Look {
        std::vector<Dot> dots;         // at most three are drawn
        std::vector<QColor> spectrum;  // all sources: one dot through every source color
        // The name and its shorter forms, longest first; the last one keeps `tail` and ellipsizes the rest.
        QStringList forms;
        QString tail;
        QString status;  // secondary text after the name (the chip's statuses)
        bool dark = true;
        double diameter = 148;
        double budget = 222;  // the widest the capsule may be
        bool operator==(const Look&) const = default;
    };

    NamePlate();
    void setLook(const Look& look);
    // The capsule's size (without the shadow margin) for the current look.
    QSizeF capsule() const { return capsule_; }
    // Places the capsule's top-left corner at `point` (global points).
    void placeCapsule(QPointF point);
    void appear();
    void disappear();
    static double gap(double diameter);
    static constexpr int kMargin = 8;

protected:
    void paintEvent(QPaintEvent* event) override;

private:
    QFont font() const;
    QString fitted() const;

    Look look_;
    QString text_;
    QSizeF capsule_;
    bool wanted_ = false;
    QPointer<QVariantAnimation> fade_;
};
