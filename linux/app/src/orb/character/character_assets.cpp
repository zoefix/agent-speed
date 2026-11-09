#include "orb/character/character_assets.hpp"

#include <QImageReader>
#include <QPointer>
#include <QThreadPool>
#include <algorithm>
#include <cmath>

namespace orb {

CharacterAssets& CharacterAssets::shared() {
    static CharacterAssets assets;
    return assets;
}

int CharacterAssets::step(double scale) {
    const double clamped = std::min(1.0, std::max(1.0 / 16, scale));
    return static_cast<int>(std::ceil(std::log2(clamped) * 2));
}

double CharacterAssets::factor(int step) { return std::min(1.0, std::pow(2.0, step / 2.0)); }

QImage CharacterAssets::atlas(const std::filesystem::path& path, double scale, double now) {
    const int s = step(scale);
    const QString file = QString::fromStdString(path.string());
    const QString key = file + QLatin1Char('|') + QString::number(s);
    auto it = entries_.find(key);
    if (it != entries_.end()) {
        it->lastUse = now;
        return it->image;
    }
    Entry& entry = entries_[key];
    entry.lastUse = now;
    entry.pending = true;
    ++loading_;
    const double f = factor(s);
    QPointer<CharacterAssets> self(this);
    QThreadPool::globalInstance()->start([self, key, file, f] {
        QImageReader reader(file);
        reader.setAutoTransform(false);
        const QSize size = reader.size();
        if (f < 0.999 && size.isValid()) {
            reader.setScaledSize(QSize(std::max(1, static_cast<int>(std::lround(size.width() * f))),
                                       std::max(1, static_cast<int>(std::lround(size.height() * f)))));
        }
        QImage image = reader.read();
        if (!image.isNull()) image = image.convertToFormat(QImage::Format_RGBA8888);
        QMetaObject::invokeMethod(
            self.data(),
            [self, key, image] {
                if (!self) return;
                --self->loading_;
                auto found = self->entries_.find(key);
                if (found == self->entries_.end()) return;
                found->image = image;
                found->pending = false;
                emit self->ready();
            },
            Qt::QueuedConnection);
    });
    return {};
}

void CharacterAssets::sweep(double now) {
    for (auto it = entries_.begin(); it != entries_.end();) {
        if (!it->pending && now - it->lastUse > 3) it = entries_.erase(it);
        else ++it;
    }
}

}  // namespace orb
