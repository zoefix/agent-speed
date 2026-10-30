#pragma once

#include <QLineF>
#include <QRasterWindow>
#include <vector>

namespace orb {

// DESIGN 2b Alignment: while an orb is dragged into line with another, a 1 pt dashed guide in the accent color at 60 %
// joins the facing edges of the two discs. The window never takes the mouse.
class AlignGuide : public QRasterWindow {
public:
    AlignGuide();
    // Global segments; none hides the guide.
    void show(const std::vector<QLineF>& lines, bool dark);
    void clear();

protected:
    void paintEvent(QPaintEvent* event) override;

private:
    std::vector<QLineF> lines_;
    bool dark_ = true;
};

}  // namespace orb
