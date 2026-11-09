#pragma once

#include <QHash>
#include <QImage>
#include <QObject>
#include <QString>
#include <filesystem>

namespace orb {

// Atlases shared by every `character` orb (DESIGN 2a.6 Techniques): decoded off the UI thread at the scale they are
// drawn at, in √2 steps (at most 1 ×, at least 1/16 ×, so a small size change reuses an atlas), kept while some orb
// asks for them and dropped a few seconds after the last request, so only the poses on screen stay resident.
class CharacterAssets : public QObject {
    Q_OBJECT
public:
    static CharacterAssets& shared();

    // Texture pixels per atlas pixel needed to draw at `pointsPerPixel` on a backing of `dpr`.
    static double needed(double pointsPerPixel, double dpr) { return pointsPerPixel * dpr * 1.15; }
    static int step(double scale);
    static double factor(int step);

    // The atlas at the step for `scale`; a null image while it decodes (`ready` fires when it is in).
    QImage atlas(const std::filesystem::path& path, double scale, double now);
    void sweep(double now);
    bool loading() const { return loading_ > 0; }

signals:
    void ready();

private:
    struct Entry {
        QImage image;
        double lastUse = 0;
        bool pending = false;
    };
    QHash<QString, Entry> entries_;
    int loading_ = 0;
};

}  // namespace orb
