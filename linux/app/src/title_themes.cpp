#include "title_themes.hpp"

#include <QFont>
#include <QFontMetricsF>
#include <QGuiApplication>
#include <QJsonArray>
#include <QJsonDocument>
#include <QSettings>
#include <QTextBoundaryFinder>
#include <QTimer>
#include <QUuid>
#include <cmath>

#include "preferences.hpp"
#include "strings.hpp"
#include "task_card.hpp"

namespace {

constexpr const char* kVehicleKeys[TitleThemes::kLevels] = {"title.walk",      "title.bicycle",  "title.ebike",    "title.motorcycle", "title.car",
                                                           "title.sportsCar", "title.bulletTrain", "title.raceCar", "title.maglev",     "title.jetliner",
                                                           "title.fighterJet", "title.hypersonic", "title.rocket"};
constexpr double kFractions[TitleThemes::kLevels] = {0, 0.02, 0.03, 0.04, 0.06, 0.08, 0.12, 0.17, 0.24, 0.34, 0.5, 0.7, 1.0};
constexpr double kMaxWidthPt = 51.5;

bool emoji(char32_t c) {
    return (c >= 0x1F300 && c <= 0x1FAFF) || (c >= 0x2600 && c <= 0x27BF && c != 0x2605 && c != 0x2606 && c != 0x2726) || (c >= 0x1F000 && c <= 0x1F2FF) ||
           c == 0xFE0F;
}

}  // namespace

const QStringList& TitleThemes::presetIds() {
    static const QStringList ids = {QStringLiteral("vehicles"), QStringLiteral("animals"), QStringLiteral("wind"),
                                    QStringLiteral("space"),    QStringLiteral("cultivation"), QStringLiteral("wuxia"),
                                    QStringLiteral("mage"),     QStringLiteral("gameRank"),  QStringLiteral("engineer")};
    return ids;
}

TitleThemes::TitleThemes(const Strings& strings, Preferences& prefs, QObject* parent) : QObject(parent), strings_(strings), prefs_(prefs) {
    load();
    connect(&strings_, &Strings::changed, this, &TitleThemes::touch);
    connect(&prefs_, &Preferences::redlineChanged, this, &TitleThemes::touch);
    connect(&prefs_, &Preferences::metricChanged, this, &TitleThemes::touch);
}

void TitleThemes::touch() {
    ++revision_;
    emit changed();
}

QString TitleThemes::presetTitle(const QString& preset, int level) const {
    if (preset == QLatin1String("vehicles"))
        return strings_.t(level == 0 ? QStringLiteral("tier.idle") : QString::fromLatin1(kVehicleKeys[level - 1]));
    return strings_.t(QStringLiteral("titles.") + preset + QLatin1Char('.') + (level == 0 ? QStringLiteral("idle") : QString::number(level)));
}

QString TitleThemes::presetName(const QString& preset) const { return strings_.t(QStringLiteral("titles.") + preset + QStringLiteral(".name")); }

int TitleThemes::customIndex(const QString& id) const {
    for (std::size_t i = 0; i < custom_.size(); ++i)
        if (custom_[i].id == id) return static_cast<int>(i);
    return -1;
}

QString TitleThemes::baseOf(const QString& id) const {
    if (presetIds().contains(id)) return id;
    const int i = customIndex(id);
    return i >= 0 ? custom_[static_cast<std::size_t>(i)].base : QStringLiteral("vehicles");
}

QString TitleThemes::title(int level, const QString& theme) const {
    level = std::clamp(level, 0, kLevels);
    const auto id = theme.isEmpty() ? selected_ : theme;
    if (const int i = customIndex(id); i >= 0) {
        const auto& set = custom_[static_cast<std::size_t>(i)];
        const auto& text = level == 0 ? set.idle : set.titles.value(level - 1);
        if (!text.isEmpty()) return text;
        return presetTitle(set.base, level);
    }
    return presetTitle(presetIds().contains(id) ? id : QStringLiteral("vehicles"), level);
}

double TitleThemes::threshold(int level, double redline) const {
    if (level <= 1) return 0.5;
    return kFractions[std::min(level, kLevels) - 1] * redline;
}

bool TitleThemes::vehicleArt() const { return baseOf(selected_) == QLatin1String("vehicles"); }

QVariantMap TitleThemes::card(const QString& id, bool custom) const {
    QVariantList strip, ladder;
    for (const int level : {1, 5, 9, 13}) strip.append(QVariantMap{{QStringLiteral("title"), title(level, id)}, {QStringLiteral("level"), level}});
    QString name = custom ? custom_[static_cast<std::size_t>(customIndex(id))].name : presetName(id);
    return QVariantMap{{QStringLiteral("id"), id},
                       {QStringLiteral("name"), name},
                       {QStringLiteral("custom"), custom},
                       {QStringLiteral("strip"), strip},
                       {QStringLiteral("selected"), id == selected_}};
}

QVariantList TitleThemes::presets() const {
    QVariantList out;
    for (const auto& id : presetIds()) out.append(card(id, false));
    return out;
}

QVariantList TitleThemes::customSets() const {
    QVariantList out;
    for (const auto& set : custom_) out.append(card(set.id, true));
    return out;
}

QVariantMap TitleThemes::editor() const {
    const int i = customIndex(selected_);
    if (i < 0) return {};
    const auto& set = custom_[static_cast<std::size_t>(i)];
    QVariantList rows;
    for (int level = 0; level <= kLevels; ++level) {
        rows.append(QVariantMap{{QStringLiteral("level"), level},
                                {QStringLiteral("text"), level == 0 ? set.idle : set.titles.value(level - 1)},
                                {QStringLiteral("placeholder"), presetTitle(set.base, level)},
                                {QStringLiteral("threshold"), level == 0 ? 0.0 : threshold(level, prefs_.redline())}});
    }
    return QVariantMap{{QStringLiteral("id"), set.id}, {QStringLiteral("name"), set.name}, {QStringLiteral("base"), set.base}, {QStringLiteral("rows"), rows}};
}

void TitleThemes::select(const QString& id) {
    if (!presetIds().contains(id) && customIndex(id) < 0) return;
    selected_ = id;
    save();
}

void TitleThemes::createFrom(const QString& id) {
    if (!canCreate()) return;
    Custom set;
    set.id = QUuid::createUuid().toString(QUuid::WithoutBraces).toUpper();
    const int source = customIndex(id);
    set.base = baseOf(id);
    const auto sourceName = source >= 0 ? custom_[static_cast<std::size_t>(source)].name : presetName(id);
    set.titles = source >= 0 ? custom_[static_cast<std::size_t>(source)].titles : QStringList(kLevels, QString());
    set.idle = source >= 0 ? custom_[static_cast<std::size_t>(source)].idle : QString();
    QVariantList args{sourceName};
    const auto base = task_card::fmt(strings_, QStringLiteral("titles.custom.copyName"), args);
    set.name = base;
    for (int n = 2; std::any_of(custom_.begin(), custom_.end(), [&](const Custom& c) { return c.name == set.name; }); ++n)
        set.name = base + QLatin1Char(' ') + QString::number(n);
    custom_.push_back(set);
    selected_ = set.id;
    save();
}

void TitleThemes::remove(const QString& id) {
    const int i = customIndex(id);
    if (i < 0) return;
    undo_ = std::make_pair(custom_, selected_);
    const auto base = custom_[static_cast<std::size_t>(i)].base;
    custom_.erase(custom_.begin() + i);
    if (selected_ == id) selected_ = base;
    flash(QStringLiteral("titles.custom.didDelete"));
    save();
}

void TitleThemes::resetAll() {
    const int i = customIndex(selected_);
    if (i < 0) return;
    undo_ = std::make_pair(custom_, selected_);
    auto& set = custom_[static_cast<std::size_t>(i)];
    set.idle.clear();
    set.titles = QStringList(kLevels, QString());
    flash(QStringLiteral("titles.custom.didReset"));
    save();
}

void TitleThemes::undo() {
    if (!undo_) return;
    custom_ = undo_->first;
    selected_ = undo_->second;
    undo_.reset();
    banner_.clear();
    save();
}

void TitleThemes::flash(const QString& key) {
    banner_ = strings_.t(key);
    QTimer::singleShot(8000, this, [this, text = banner_] {
        if (banner_ != text) return;
        banner_.clear();
        undo_.reset();
        touch();
    });
}

void TitleThemes::setName(const QString& name) {
    const int i = customIndex(selected_);
    if (i < 0) return;
    bool hadEmoji = false;
    auto text = cleaned(name, &hadEmoji).left(32);
    if (text.isEmpty()) return;
    custom_[static_cast<std::size_t>(i)].name = text;
    save();
}

void TitleThemes::setBase(const QString& base) {
    const int i = customIndex(selected_);
    if (i < 0 || !presetIds().contains(base)) return;
    custom_[static_cast<std::size_t>(i)].base = base;
    save();
}

void TitleThemes::setText(int level, const QString& input) {
    const int i = customIndex(selected_);
    if (i < 0 || level < 0 || level > kLevels) return;
    bool hadEmoji = false, cut = false;
    const auto text = fitted(cleaned(input, &hadEmoji), &cut);
    notice_ = hadEmoji ? strings_.t(QStringLiteral("titles.custom.noEmoji")) : cut ? strings_.t(QStringLiteral("titles.custom.tooLong")) : QString();
    if (!notice_.isEmpty()) {
        QTimer::singleShot(4000, this, [this, text = notice_] {
            if (notice_ == text) {
                notice_.clear();
                touch();
            }
        });
    }
    auto& set = custom_[static_cast<std::size_t>(i)];
    if (level == 0) set.idle = text;
    else set.titles[level - 1] = text;
    save();
}

// The text rules: NFC; controls, separators and bidi controls removed; spaces collapsed; emoji removed; ≤ 24 graphemes.
QString TitleThemes::cleaned(const QString& input, bool* hadEmoji) const {
    QString out;
    bool space = false;
    const auto text = input.normalized(QString::NormalizationForm_C);
    for (const auto c : text.toUcs4()) {
        const auto ch = static_cast<char32_t>(c);
        const auto category = QChar::category(c);
        if ((ch >= 0x202A && ch <= 0x202E) || (ch >= 0x2066 && ch <= 0x2069)) continue;
        if (emoji(ch)) {
            *hadEmoji = true;
            continue;
        }
        if (ch == U'\t' || ch == U'\n' || ch == U'\r' || category == QChar::Separator_Space || category == QChar::Separator_Line ||
            category == QChar::Separator_Paragraph) {
            space = !out.isEmpty();
            continue;
        }
        if (category == QChar::Other_Control) continue;
        if (space) out += QLatin1Char(' ');
        space = false;
        out += QString::fromUcs4(&c, 1);
    }
    QTextBoundaryFinder graphemes(QTextBoundaryFinder::Grapheme, out);
    int count = 0;
    for (auto at = graphemes.toNextBoundary(); at >= 0; at = graphemes.toNextBoundary()) {
        if (++count == 24) return out.left(at);
    }
    return out;
}

// The maximum title budget: w7 ≤ 51.5 pt in the title style (semibold, 7 pt, uppercase in English).
QString TitleThemes::fitted(const QString& text, bool* cut) const {
    QFont font = QGuiApplication::font();
    font.setPointSizeF(7);
    font.setWeight(QFont::DemiBold);
    const QFontMetricsF metrics(font);
    const auto measure = [&](const QString& s) { return metrics.horizontalAdvance(strings_.language() == QLatin1String("en") ? s.toUpper() : s); };
    if (measure(text) <= kMaxWidthPt) return text;
    *cut = true;
    QTextBoundaryFinder graphemes(QTextBoundaryFinder::Grapheme, text);
    QString best;
    for (auto at = graphemes.toNextBoundary(); at >= 0; at = graphemes.toNextBoundary()) {
        if (measure(text.left(at)) > kMaxWidthPt) break;
        best = text.left(at);
    }
    return best.trimmed();
}

void TitleThemes::load() {
    const auto doc = QJsonDocument::fromJson(QSettings().value(QStringLiteral("titleThemes")).toByteArray());
    const auto root = doc.object();
    custom_.clear();
    for (const auto& value : root.value(QStringLiteral("custom")).toArray()) {
        const auto o = value.toObject();
        Custom set;
        set.id = o.value(QStringLiteral("id")).toString();
        if (set.id.isEmpty()) continue;
        bool ignored = false;
        set.name = cleaned(o.value(QStringLiteral("name")).toString(), &ignored).left(32);
        set.base = o.value(QStringLiteral("base")).toString();
        if (!presetIds().contains(set.base)) set.base = QStringLiteral("vehicles");
        set.idle = cleaned(o.value(QStringLiteral("idle")).toString(), &ignored);
        for (const auto& t : o.value(QStringLiteral("titles")).toArray()) set.titles.append(cleaned(t.toString(), &ignored));
        while (set.titles.size() < kLevels) set.titles.append(QString());
        while (set.titles.size() > kLevels) set.titles.removeLast();
        if (static_cast<int>(custom_.size()) < kMaxCustom) custom_.push_back(set);
    }
    selected_ = root.value(QStringLiteral("selected")).toString(QStringLiteral("vehicles"));
    if (!presetIds().contains(selected_) && customIndex(selected_) < 0) selected_ = QStringLiteral("vehicles");
}

void TitleThemes::save() {
    QJsonArray custom;
    for (const auto& set : custom_) {
        custom.append(QJsonObject{{QStringLiteral("id"), set.id},
                                  {QStringLiteral("name"), set.name},
                                  {QStringLiteral("base"), set.base},
                                  {QStringLiteral("idle"), set.idle},
                                  {QStringLiteral("titles"), QJsonArray::fromStringList(set.titles)}});
    }
    QSettings().setValue(QStringLiteral("titleThemes"),
                         QJsonDocument(QJsonObject{{QStringLiteral("version"), 1}, {QStringLiteral("selected"), selected_}, {QStringLiteral("custom"), custom}})
                             .toJson(QJsonDocument::Compact));
    touch();
}
