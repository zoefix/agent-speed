#pragma once

#include <QString>
#include <QWidgetAction>

class QLabel;
class QSlider;

// DESIGN 2b Context menu: the size submenu's last row, a slider over the shared range (64–240 pt) in 4 pt steps with the
// value at its trailing edge. It reports every new size while the thumb moves; ← / → step it once the row has the
// focus, and ↑ / ↓ go back to the menu.
class SizeSliderAction : public QWidgetAction {
    Q_OBJECT
public:
    static constexpr int kMin = 64, kMax = 240, kStep = 4;

    SizeSliderAction(double size, const QString& accessibleName, QObject* parent);

    // `size` on the slider's steps inside its range.
    static int snap(double size);

signals:
    void sizeChanged(double size);

private:
    void show(int size);

    QSlider* slider_ = nullptr;
    QLabel* value_ = nullptr;
    int size_ = 0;
};
