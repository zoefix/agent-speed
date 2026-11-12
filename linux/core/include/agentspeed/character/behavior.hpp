#pragma once

#include <array>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "agentspeed/character/motion.hpp"
#include "agentspeed/character/rhythm.hpp"
#include "agentspeed/character/rng.hpp"

namespace agentspeed::character {

// What the agents of her orb are doing (spec/CHARACTERS.md, Behavior, Inputs).
enum class AgentActivity { Generating, Tools, Idle };

// One frame of what the app knows (motions/behavior.py, Inputs). Times are seconds.
struct BehaviorInputs {
    AgentActivity phase = AgentActivity::Idle;
    // Seconds the request has waited for its first output; nullopt when not generating or once output flows.
    std::optional<double> wait;
    // Seconds since the last response ended, while idle.
    double idle = 0;
    int hour = 12;
    bool newPeak = false;
    bool trouble = false;
    // The pointer relative to her face center in her heights (x right, y down); nullopt when unknown.
    std::optional<std::pair<double, double>> cursor;
    // The pointer is over her figure, and a single click on it this frame.
    bool over = false;
    bool click = false;
};

// Her mood: event moods (the first four, highest first) take over for a fixed time, state moods follow the phase.
enum class Mood { Poke, Cheer, Greet, Trouble, Think, Work, Focus, Rest, Wait };

std::string_view moodName(Mood mood);
std::optional<Mood> moodNamed(std::string_view name);
constexpr bool isEvent(Mood mood) { return mood <= Mood::Trouble; }
constexpr bool isSocial(Mood mood) { return mood == Mood::Wait || mood == Mood::Work; }

// The pose a mood asks for: one of the pack's mood poses, or (an empty `state`) whatever pose shows already.
struct MoodPose {
    std::string_view state;
    bool keep() const { return state.empty(); }
    bool operator==(const MoodPose&) const = default;
};

// Where she looks in a mood (spec/CHARACTERS.md, Behavior, Looking).
enum class LookMode { User, Up, None };

// Head and eye targets of the look.
struct LookTarget {
    double angleX = 0, angleY = 0, eyeBallX = 0, eyeBallY = 0;
};

// Anime lip flaps in phrases, not lip sync (motions/behavior.py, Talk): a group of 2–4 phrases of 3–8 syllables, a
// pause between phrases, a silence after the group. A group waits until the face is clear of expressions before its
// first syllable; `speaking` is true from then until its silence.
class Talk {
public:
    enum class Stage { Ready, Open, Gap, Pause, Silence };
    struct Shape {
        std::string_view name;
        double weight;
    };
    // The open shapes and how often each comes (`mouth-i`, the teeth, never talks); a shape set masks them.
    static const std::array<Shape, 3>& shapes();

    bool on() const { return on_; }
    // The open shape now, nullopt while closed.
    std::optional<std::string_view> shape() const { return shape_; }
    std::optional<Stage> stage() const { return stage_; }
    bool speaking() const { return stage_ != Stage::Silence; }

    // The open shape now, or nullopt for closed. `shapes` masks the open shapes the pose has.
    std::optional<std::string_view> update(double dt, bool talking, int level, int shapes, bool clear, Rng& rng);

private:
    void phrase(int level, Rng& rng);
    void syllable(int shapes, Rng& rng);
    static std::string_view pick(int shapes, Rng& rng);

    bool on_ = false;
    std::optional<std::string_view> shape_;
    std::optional<Stage> stage_;
    double left_ = 0, gap_ = 0, rate_ = 5.0;
    int syllables_ = 0, phrases_ = 0;
};

// Moods, poses, faces, looks and actions from the inputs (motions/behavior.py, Behavior). The director asks for the
// pose, the face, where she looks, whether she talks and which actions she plays. Nothing here allocates once built.
class Behavior {
public:
    // Full-body poses a pack may carry for the moods (manifest `states`).
    static const std::array<std::string, 6>& stateNames();
    static const std::array<std::string, 3>& hoverGestures();
    // She looks at a cursor within this many of her heights from her face.
    static constexpr double kLookRange = 2.0;

    Behavior(std::uint64_t seed, const std::vector<std::string>& states);

    // The mood poses the pack has, in `stateNames` order.
    const std::vector<std::string>& states() const { return states_; }
    const BehaviorInputs& inputs() const { return inputs_; }
    double t() const { return t_; }
    AgentActivity phase() const { return phase_; }
    double phaseT() const { return phaseT_; }
    Mood stateMood() const { return stateMood_; }
    Mood mood() const { return mood_; }
    double moodT() const { return moodT_; }
    double overT() const { return overT_; }
    double lookWeight() const { return lookWeight_; }
    const LookTarget& lookTarget() const { return lookTarget_; }
    // Where her eyes look as a gaze patch: `left`, `right`, `up`, `down`, or nullopt.
    std::optional<std::string_view> gaze() const { return gaze_; }
    const Talk& talk() const { return talk_; }
    LookMode lookMode() const;
    // The cursor is over her in a mood that answers it: she starts nothing from her pool meanwhile.
    bool attending() const { return inputs_.over && isSocial(mood_); }
    bool talking() const { return mood_ == Mood::Work && inputs_.phase == AgentActivity::Generating && !inputs_.wait; }

    // The hover gestures the shown pose can play (in `hoverGestures` order) and whether it has `wink`.
    void clearHoverOptions() { hoverCount_ = 0; }
    void addHoverOption(const std::string& gid);
    bool hasWink = false;

    void update(double dt, const BehaviorInputs& inputs, Band band);

    // The mood pose to show: a state the pack has, a keep, or nullopt for the band pose.
    std::optional<MoodPose> pose() const;
    // The actions she picks from between others and the band whose signature actions join them (nullopt: the mood's
    // own list, filtered by what the profile allows); an event mood picks none.
    std::pair<const std::vector<std::string>*, std::optional<Band>> pool(Band band, const std::vector<std::string>& bandPool,
                                                                         const std::vector<std::string>& idlePool,
                                                                         const std::vector<std::string>& idleEarlyPool) const;
    // The gap range between actions: the mood's own, or the band's (the idle band's while waiting).
    std::pair<double, double> gaps(Band band, std::pair<double, double> (*bandGaps)(Band)) const;
    // The queued actions to try in turn once no action plays, when their time has come (taken once).
    const std::vector<std::string>& takeQueue();
    // The expression she means to show (a patch name, unchecked against the pose), or nullopt.
    std::optional<std::string_view> face(const Motion* action, std::optional<std::string_view> eventFace, Band band) const;
    // How long a dozing blink holds the eyes shut, from the behavior's generator.
    double dozeHold(double low, double high) { return rng_.uniform(low, high); }
    // Steps the lip flaps with the behavior's own generator.
    std::optional<std::string_view> speak(double dt, bool on, int level, int shapes, bool clear) {
        return talk_.update(dt, on, level, shapes, clear, rng_);
    }

private:
    bool hasState(std::string_view name) const;
    bool resting(const BehaviorInputs& i) const;
    Mood stateWanted(const BehaviorInputs& i) const;
    void start(Mood mood, int& fresh);
    std::pair<const std::vector<std::string>*, double> entry(Mood mood, Mood cameFrom) const;
    void moods(double dt, const BehaviorInputs& i);
    void hover(double dt, const BehaviorInputs& i, Band band);
    std::string_view direction(double dx, double dy) const;
    void look(double dt, const BehaviorInputs& i);

    std::vector<std::string> states_;
    Rng rng_;
    std::array<std::string, 3> hoverOptions_;
    int hoverCount_ = 0;
    std::vector<std::string> hoverQueue_;
    std::array<double, 4> until_;
    std::array<double, 4> started_{};
    BehaviorInputs inputs_;
    double t_ = 0;
    AgentActivity phase_ = AgentActivity::Idle;
    double phaseT_ = 0;
    Mood stateMood_ = Mood::Wait;
    Mood mood_ = Mood::Wait;
    double moodT_ = 0;
    double overT_ = 0;
    double lookWeight_ = 0;
    LookTarget lookTarget_;
    std::optional<std::string_view> gaze_;
    Talk talk_;
    double stateT_;
    double woken_;
    bool hoverDone_ = false;
    double attentionUntil_;
    std::optional<std::string_view> faceOverride_;
    double faceUntil_;
    // The queued actions: a static list, or the hover gesture (`hoverQueued_`), so a move keeps them valid.
    const std::vector<std::string>* queue_;
    bool hoverQueued_ = false;
    double queueAt_ = 0;
    double gazeSince_ = 0;
};

}  // namespace agentspeed::character
