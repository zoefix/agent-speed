#pragma once

#include <QColor>
#include <QImage>
#include <QElapsedTimer>
#include <QPointer>
#include <QTimer>
#include <QMouseEvent>
#include <QRasterWindow>
#include <QStringList>
#include <vector>

class QVariantAnimation;

// DESIGN 2b Name plate and Source chip: a small glass capsule beside an orb with its colour dots and name, the orb's
// own glass, never wrapped and never shrunk. While the orb is hovered, dragged or just changed it widens to the chip's
// content (every source with its status). It belongs to its orb: the pointer events of the capsule are handed to it
// (hover, drag, menu, double-click); outside the capsule and with click-through it ignores the mouse. The window carries
// a margin for the shadow.
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
    // Places the capsule's top-left corner at `point` (global points). A width change springs about the point at
    // `anchor` of its width: 0.5 (above or below the disc), 0 (right of it) or 1 (left of it).
    void placeCapsule(QPointF point, double anchor = 0.5);
    void appear();
    void disappear();
    void setClickThrough(bool on);
    // The window's current picture (development aid).
    QImage picture() const;
    static double gap(double diameter);
    static constexpr int kMargin = 8;

signals:
    void hovered(bool on);
    void pressed(QPointF global, Qt::MouseButton button);
    void dragged(QPointF global);
    void released();
    void doubleClicked();

protected:
    void paintEvent(QPaintEvent* event) override;
    bool event(QEvent* event) override;
    void mousePressEvent(QMouseEvent* event) override;
    void mouseMoveEvent(QMouseEvent* event) override;
    void mouseReleaseEvent(QMouseEvent* event) override;
    void mouseDoubleClickEvent(QMouseEvent* event) override;

private:
    QFont font() const;
    QString fitted() const;
    QRectF drawn() const;
    void paintTo(QPainter& p) const;
    void step();
    void reposition();

    Look look_;
    QString text_;
    QString status_;
    QSizeF capsule_;
    // DESIGN 2b Chip and undo: the width springs (response 0.3 s, damping 0.86) while the text crossfades (0.15 s).
    double width_ = 0, velocity_ = 0, canvas_ = 0;
    QString previousText_, previousStatus_;
    double crossfade_ = 1;
    QPointF anchorPoint_;
    double anchor_ = 0.5;
    bool placed_ = false;
    QTimer spring_;
    QElapsedTimer clock_;
    bool wanted_ = false;
    bool inside_ = false;
    bool pressing_ = false;
    QPointer<QVariantAnimation> fade_;
};
