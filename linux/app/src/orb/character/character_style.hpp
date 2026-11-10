#pragma once

#include <QColor>
#include <QString>
#include <array>
#include <map>
#include <memory>
#include <optional>
#include <vector>

#include "agentspeed/character/hud.hpp"
#include "agentspeed/character/hud_models.hpp"
#include "agentspeed/character/hud_wings.hpp"
#include "agentspeed/character/pack.hpp"
#include "agentspeed/character/performer.hpp"
#include "agentspeed/character/selection.hpp"
#include "orb/character/character_cards.hpp"
#include "orb/character/character_lightning.hpp"
#include "orb/character/character_models.hpp"
#include "orb/character/character_particles.hpp"
#include "orb/character/character_quota_card.hpp"
#include "orb/style.hpp"

namespace orb {

// What a `character` orb shows besides the frame (DESIGN 2a.6, CHARACTERS 7–8), set by the window every frame.
struct CharacterContent {
    std::shared_ptr<const agentspeed::character::Pack> pack;  // null: none installed, or still loading
    bool noneInstalled = true;
    QString emptyLabel;  // character.empty
    agentspeed::character::CharacterReadouts readouts;
    QString modelName;  // the fastest model (else the last one, else her own name)
    QString title, unit, peakLabel;
    QString staticValue;  // `–` while unavailable; empty while the digits roll
    double value = 0;     // tok/s
    int level = 0;        // the speed level 0…13
    bool unavailable = false;
    std::vector<QColor> agents;  // generating agents' colors
    IntelReadout intel;          // 性能 over 思考, with the quiet rule's fade
    NetReadout net;              // ↓ over ↑, with the quiet rule's fade
    QString costGlyph, costNumber, costUnit;
    bool costAtLeast = false, costUnknown = false;
    double costOpacity = 0;  // the cost card's quiet rule
    QString todayLine, agentsLine;
    // The 性能 card's 首字 row: its label, value and unit, and its fade (empty while switched off or without a value).
    QString latencyGlyph, latencyNumber, latencyUnit;
    double latencyOpacity = 0;
    // The models card's rows, fastest first, its quiet rule's fade (0 while switched off) and its texts.
    std::vector<agentspeed::character::ModelRow> models;
    double modelsOpacity = 0;
    QString modelsMore, speedUnit;
    // The 額度 card's sources (null or empty: none), in source order; they go along while it is switched off, so it
    // fades out with them.
    std::shared_ptr<const std::vector<agentspeed::character::QuotaSource>> quota;
};

// `character` (DESIGN 2a.6): the skinned character of the chosen pack with its pose and character swaps, the ring of
// lights at her feet, agent lights, her particles, and the readout cards beside her — the speed at her right with the
// running models under it, the facts in a column at her left with 額度 last, the model above her head. Frame `s` is the
// footprint D = 2 S. Without a pack the style draws the ring and the standard readout with `character.empty`.
class CharacterStyle : public Style {
public:
    CharacterStyle();
    StyleKind kind() const override { return StyleKind::Character; }
    const ReadoutLayout& readoutLayout() const override;
    bool sharedAura() const override { return false; }
    bool sharedWave() const override { return false; }
    bool sharedRays() const override { return false; }
    double signOfLifeDuration() const override { return 0; }

    void setContent(const CharacterContent& content) { content_ = content; }
    // The window's device pixel ratio: atlases decode at the drawn size on this backing.
    void setBackingScale(double dpr) { dpr_ = dpr; }
    void pulse(double time, double strength, double heat) override;
    void celebrate(double time) override;
    void step(const Frame& frame) override;
    void build(Scene& scene, const Frame& frame) override;
    void buildReadout(Scene& scene, const Frame& frame, const ReadoutContent& content, const MeasureText& measure) override;
    bool isBusy(const Frame& frame) const override;
    int ambientFps(const Frame& frame) const override;

    // Her capsule and her outline this frame (the accessible cards read what shows).
    const agentspeed::character::HudWings& wings() const { return wings_; }
    bool drawing() const { return stage_.has_value() && drew_; }

private:
    struct Geometry {
        double feetX, feetY, height, ppp, unit, bodyDX, d;
    };
    Geometry geometry(const Frame& f, const agentspeed::character::Performer& performer) const;
    void buildRing(Scene& scene, const Frame& f);
    void addDots(Scene& scene, const Frame& f, const std::vector<agentspeed::character::HudDot>& dots);
    void drawCharacter(Scene& scene, const Frame& f, const agentspeed::character::Performer& performer, double alpha, double now);
    void drawPose(Scene& scene, const Frame& f, const agentspeed::character::Performer& performer, int pose,
                  const std::vector<agentspeed::character::Affine2>& world, const std::vector<double>& opacities, double alpha,
                  double now);
    QImage atlas(const agentspeed::character::Performer& performer, int pose, double ppp, double now);
    void drawParticles(Scene& scene, const Frame& f, bool front, int meshLayer, double now);
    void layoutCards(double dt, const Frame& f, const agentspeed::character::Performer& performer, const Geometry& g);
    std::pair<double, double> speedBox(const Frame& f);
    void drawCards(Scene& scene, const Frame& f);
    void drawSpeed(Scene& scene, const Frame& f, QPointF center, double scale, double spin, double alpha);
    void drawPeak(Scene& scene, const Frame& f);
    // The 性能 card's rows: 性能 at p, 思考 and 首字 at t with their shares while they fade, where their values start.
    struct PerfRows {
        bool score = false, thinking = false, latency = false;
        double thinkingShare = 0, latencyShare = 0;
        double thinkingColumn = 0, latencyColumn = 0;
        double w = 0, h = 0;
    };
    PerfRows perfRows(const cards::CardMetrics& m) const;
    std::array<double, 2> cardSize(agentspeed::character::HudCard card, const Frame& f) const;
    QString modelText() const;
    CharacterLightning::Strength lightningStrength(const Frame& f) const;
    static std::optional<std::array<double, 4>> petalRect(const agentspeed::character::Rig& rig);

    CharacterContent content_;
    std::optional<agentspeed::character::Stage> stage_;
    std::string shownPack_;
    std::map<std::string, std::shared_ptr<const agentspeed::character::Pack>> loaded_;
    agentspeed::character::CharacterHud hud_{5, 1};
    CharacterParticles particles_;
    std::vector<CharacterParticles::Dot> particleDots_;
    CharacterLightning lightning_;
    agentspeed::character::HudWings wings_;
    std::optional<agentspeed::character::HudSilhouette> silhouette_;
    std::vector<double> scratch_;
    std::array<agentspeed::character::CardSize, agentspeed::character::kHudCardCount> cardSizes_{};
    std::array<bool, agentspeed::character::kHudCardCount> cardShown_{};
    ModelsCard modelsCard_;
    QuotaCard quotaCard_;
    std::vector<float> vertices_;
    std::vector<std::uint32_t> indices_;
    double hudTime_ = 0;
    double speedDrop_ = 0;
    double comet_ = 0;
    double dpr_ = 2;
    QString lastModel_;
    bool drew_ = false;
};

}  // namespace orb
