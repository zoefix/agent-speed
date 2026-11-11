"""What she feels and shows (spec/CHARACTERS.md, Behavior): a mood from what the agents are doing, and reactions to
the user's cursor. Every update brings the inputs; the director (director.py) asks for the pose, the face, where she
looks, whether she talks and which actions she plays, and turns that into parameters.

Event moods (a click, a new peak, a greeting, trouble) take over at once for a fixed time; the other moods follow the
agents' phase and each holds at least HOLD seconds. Times are seconds.
"""
from __future__ import annotations

import math
from dataclasses import dataclass

from ..rng import MASK, Rng

# Full-body poses a pack may carry for the moods (manifest `states`).
STATES = ("think", "wait", "cheer", "sleepy", "focus", "greet")
# A mood pose that keeps whatever pose shows.
KEEP = "keep"
# Event moods, highest first, and how long each lasts.
EVENT_MOODS = (("poke", 1.6), ("cheer", 3.2), ("greet", 2.4), ("trouble", 2.5))
EVENT_NAMES = tuple(name for name, _ in EVENT_MOODS)
EVENT_INPUTS = (("newPeak", "cheer"), ("trouble", "trouble"))
STATE_MOODS = ("think", "work", "focus", "rest", "wait")
HOLD = 1.2

THINK_FACE, THINK_POSE = 3.0, 10.0
FOCUS_FACE, FOCUS_POSE = 3.0, 12.0
WAIT_POSE, WAIT_SMILE, WAIT_YAWN = 20.0, 6.0, 180.0
REST_IDLE, REST_NIGHT_IDLE, NIGHT_END = 600.0, 120.0, 6
WAKE = 60.0
POKE_SURPRISE = 0.4
ENTRY_DELAY = 0.55

LOOK_RANGE = 2.0
LOOK_IN, LOOK_OUT = 0.4, 0.8
HEAD_X, HEAD_Y, HEAD_REACH = 0.7, 0.5, 0.8
EYE_REACH = 0.3
UP = (0.15, 0.3, 0.3, -0.8)
GAZE_ON, GAZE_OFF, GAZE_DWELL, GAZE_AXIS = 0.35, 0.25, 0.5, 0.75

SMILE_AFTER, GESTURE_AFTER, GOODBYE_AFTER, ATTENTION = 0.8, 4.0, 1.0, 8.0
HOVER_GESTURES = ("wave", "peace", "thumbsup")
WINK = 1.2

MOOD_FACE = {"cheer": "excited", "greet": "smile", "trouble": "worried", "think": "thinking", "focus": "focused",
             "rest": "sleepy"}
BAND_FACE = {"idle": None, "cruise": "smile", "boost": "smile", "overdrive": "focused", "redline": "excited"}
# The expressions actions name (gestures.py, moves.py) as face patches; `talk` leaves the face as it is.
ACTION_FACE = {"smile": "smile", "happy": "excited", "surprised": "surprised", "determined": "focused",
               "sleepy": "sleepy", "talk": None}
ACTION_OWN_FACE = {"yawn": "yawn", "giggle": "laugh"}

MOOD_POOLS = {"think": (("chin", "tilt", "ponder"), (2.0, 4.0)), "focus": (("chin", "nod"), (3.0, 6.0)),
              "rest": (("yawn",), (20.0, 40.0))}
WAIT_LATE = ("yawn", "stretch")

SHAPES = ("a", "i", "u", "o")
SYLLABLES, MAX_SYLLABLES = (4.0, 0.4), 9.0
OPEN = (0.6, 0.8)
PHRASE, PAUSE = (4, 6), (0.2, 0.5)


@dataclass(frozen=True)
class Inputs:
    """One frame of what the app knows. `wait`: seconds the request has waited for its first output (None when not
    generating or once output flows). `idle`: seconds since the last response ended, while idle. `cursor`: the pointer
    relative to her face center in her heights (x right, y down), None when unknown or out of reach. `over`: the
    pointer is over her figure. `click`: a single click on her figure this frame."""
    phase: str = "idle"
    wait: float | None = None
    idle: float = 0.0
    hour: int = 12
    events: tuple = ()
    cursor: tuple | None = None
    over: bool = False
    click: bool = False


def _clamp(v, lo=-1.0, hi=1.0):
    return max(lo, min(hi, v))


class Talk:
    """Lip flaps: syllables of open shapes in random order, closed between them, a pause every few syllables."""

    def __init__(self, rng):
        self.rng = rng
        self.on, self.shape, self.prev = False, None, None
        self.stage, self.left, self.gap, self.count = None, 0.0, 0.0, 0

    def _syllable(self, rate, shapes):
        options = [s for s in shapes if s != self.prev] or list(shapes)
        self.shape = self.prev = self.rng.choice(options)
        span = 1.0 / rate
        opened = span * self.rng.uniform(*OPEN)
        self.stage, self.gap = "open", span - opened
        self.left += opened

    def update(self, dt, on, level, shapes):
        """The open shape now, or None for closed."""
        if not on or not shapes:
            self.on, self.shape, self.stage, self.left = False, None, None, 0.0
            return None
        rate = min(MAX_SYLLABLES, SYLLABLES[0] + SYLLABLES[1] * level)
        if not self.on:
            self.on, self.left = True, 0.0
            self.count = PHRASE[0] + self.rng.index(PHRASE[1])
            self._syllable(rate, shapes)
        self.left -= dt
        while self.left <= 0:
            if self.stage == "open":
                self.stage, self.shape = "gap", None
                self.left += self.gap
                self.count -= 1
            elif self.stage == "gap" and self.count <= 0:
                self.stage = "pause"
                self.left += self.rng.uniform(*PAUSE)
                self.count = PHRASE[0] + self.rng.index(PHRASE[1])
            else:
                self._syllable(rate, shapes)
        return self.shape


class Behavior:
    """Moods, poses, faces, looks and actions from the inputs. `can(action)` tells whether the shown pose can play
    an arm gesture and `has_face(name)` whether it has that expression; `states` are the mood poses of the pack."""

    def __init__(self, seed, states=(), can=None, has_face=None):
        self.rng = Rng((seed + 1) & MASK)
        self.states = tuple(s for s in STATES if s in states)
        self.can = can or (lambda gid: False)
        self.has_face = has_face or (lambda name: False)
        self.inputs = Inputs()
        self.t = 0.0
        self.phase, self.phase_t = "idle", 0.0
        self.state_mood, self.state_t = "wait", HOLD
        self.until, self.started = {}, {}
        self.mood, self.mood_t = "wait", 0.0
        self.woken = -math.inf
        self.over_t, self.hover_done, self.attention_until = 0.0, False, -math.inf
        self.face_override, self.face_until = None, -math.inf
        self.queue, self.queue_at = [], 0.0
        self.look_mode, self.look_w, self.look_target = None, 0.0, (0.0, 0.0, 0.0, 0.0)
        self.gaze, self.gaze_since = None, 0.0
        self.talk = Talk(self.rng)

    # Moods

    def _resting(self, i):
        night = 0 <= i.hour < NIGHT_END
        long_idle = i.idle >= REST_IDLE or (night and i.idle >= REST_NIGHT_IDLE)
        return i.phase == "idle" and long_idle and self.t - self.woken >= WAKE

    def _state_wanted(self, i):
        if i.phase == "generating":
            return "think" if i.wait is not None and i.wait >= THINK_FACE else "work"
        if i.phase == "tools":
            return "focus" if self.phase_t >= FOCUS_FACE else "work"
        return "rest" if self._resting(i) else "wait"

    def _start(self, mood, fresh):
        self.until[mood] = self.t + dict(EVENT_MOODS)[mood]
        self.started[mood] = self.t
        fresh.append(mood)

    def _entry(self, mood, came_from):
        """The actions a mood starts with: (candidates, earliest start)."""
        if mood == "poke":
            return ["doubletake"], self.t
        if mood == "trouble":
            return ["chin", "shake"], self.t
        if mood == "cheer":
            return (["hop"] if "cheer" in self.states else ["jumpjoy", "hop"]), self.t + ENTRY_DELAY
        if mood == "greet":
            return ([] if "greet" in self.states else ["wave", "bow"]), self.t + ENTRY_DELAY
        if mood == "wait" and came_from in ("work", "think", "focus"):
            return ["nod"], self.t
        if mood == "rest":
            return ["yawn"], self.t + ENTRY_DELAY
        return [], self.t

    def _moods(self, dt, i):
        fresh = []
        if i.phase != self.phase:
            if self.phase == "idle" and self.state_mood == "rest":
                self._start("greet", fresh)
            self.phase, self.phase_t = i.phase, 0.0
        else:
            self.phase_t += dt
        for name, mood in EVENT_INPUTS:
            if name in i.events:
                self._start(mood, fresh)
        if i.click:
            if self.state_mood == "rest":
                self.woken = self.t
            self._start("poke", fresh)

        previous_state = self.state_mood
        self.state_t += dt
        wanted = self._state_wanted(i)
        if wanted != self.state_mood and self.state_t >= HOLD:
            self.state_mood, self.state_t = wanted, 0.0

        before = self.mood
        self.mood = next((m for m in EVENT_NAMES if self.until.get(m, -math.inf) > self.t), self.state_mood)
        if self.mood != before:
            self.mood_t, self.queue = 0.0, []
        else:
            self.mood_t += dt
        if self.mood in fresh or (self.mood == self.state_mood and self.state_mood != previous_state):
            self.queue, self.queue_at = self._entry(self.mood, previous_state)

    # The cursor

    def _hover(self, dt, i, band):
        social = self.mood in ("wait", "work")
        if i.over:
            self.over_t += dt
            if self.over_t >= SMILE_AFTER:
                self.attention_until = self.t + ATTENTION
            if self.over_t >= GESTURE_AFTER and not self.hover_done and social:
                self.hover_done = True
                options = [g for g in HOVER_GESTURES if self.can(g)]
                if options:
                    self.queue, self.queue_at = [self.rng.choice(options)], self.t
                elif self.has_face("wink"):
                    self.face_override, self.face_until = "wink", self.t + WINK
            return
        if self.over_t >= GOODBYE_AFTER and social and band in ("idle", "cruise"):
            self.queue, self.queue_at = ["wave", "nod"], self.t
        self.over_t, self.hover_done = 0.0, False

    def _direction(self, dx, dy):
        horizontal = abs(dx) >= abs(dy)
        if self.gaze in ("left", "right"):
            horizontal = abs(dx) >= GAZE_AXIS * abs(dy)
        elif self.gaze in ("up", "down"):
            horizontal = GAZE_AXIS * abs(dx) > abs(dy)
        if horizontal:
            return "right" if dx > 0 else "left"
        return "down" if dy > 0 else "up"

    def _look(self, dt, i):
        mode = "up" if self.mood == "think" else None if self.mood in ("focus", "rest") else "user"
        self.look_mode = mode
        cursor = i.cursor if i.cursor is not None and math.hypot(*i.cursor) <= LOOK_RANGE else None
        on, gaze = True, None
        if mode == "up":
            self.look_target, gaze = UP, "up"
        elif mode == "user" and cursor is not None:
            dx, dy = cursor
            self.look_target = (HEAD_X * _clamp(dx / HEAD_REACH), -HEAD_Y * _clamp(dy / HEAD_REACH),
                                _clamp(dx / EYE_REACH), _clamp(dy / EYE_REACH))
            far = max(abs(dx), abs(dy))
            if far >= (GAZE_OFF if self.gaze is not None else GAZE_ON):
                gaze = self._direction(dx, dy)
        elif mode == "user" and self.mood in EVENT_NAMES:
            self.look_target = (0.0, 0.0, 0.0, 0.0)
        else:
            on = False
        if on:
            self.look_w = min(1.0, self.look_w + dt / LOOK_IN)
        else:
            self.look_w = max(0.0, self.look_w - dt / LOOK_OUT)
        if gaze != self.gaze and self.t - self.gaze_since >= GAZE_DWELL:
            self.gaze, self.gaze_since = gaze, self.t

    def update(self, dt, inputs, band):
        self.t += dt
        self.inputs = inputs
        self._moods(dt, inputs)
        self._hover(dt, inputs, band)
        self._look(dt, inputs)

    # What the director asks for

    def pose(self):
        """The mood pose to show: a state name the pack has, KEEP, or None for the band pose."""
        m, i = self.mood, self.inputs
        if m in ("poke", "trouble"):
            return KEEP
        name = {"cheer": "cheer", "greet": "greet", "rest": "sleepy"}.get(m)
        if m == "think" and i.wait is not None and i.wait >= THINK_POSE:
            name = "think"
        elif m == "focus" and self.phase_t >= FOCUS_POSE:
            name = "focus"
        elif m == "wait" and i.idle >= WAIT_POSE and self.t >= self.attention_until:
            name = "wait"
        return name if name in self.states else None

    def pool(self, band, pools, gaps):
        """The actions she picks from between others, the gap range, and the band whose signature actions join them
        (None: the mood's own list, no signature actions); an event mood picks none."""
        m = self.mood
        if m in EVENT_NAMES:
            return [], gaps[band], None
        if m in MOOD_POOLS:
            pool, gap = MOOD_POOLS[m]
            return list(pool), gap, None
        if m == "wait":
            late = self.inputs.idle >= WAIT_YAWN
            return [g for g in pools["idle"] if late or g not in WAIT_LATE], gaps["idle"], "idle"
        return list(pools[band]), gaps[band], band

    def take_queue(self):
        """The queued actions to try in turn once no action plays, when their time has come."""
        if not self.queue or self.t < self.queue_at:
            return []
        out, self.queue = self.queue, []
        return out

    def face(self, action, event_face, band):
        """The expression she means to show (a patch name, unchecked against the pose), or None."""
        m, t = self.mood, self.t
        if m == "poke":
            return "surprised" if t - self.started["poke"] < POKE_SURPRISE else "laugh"
        if action is not None and action.name == "yawn":
            return "yawn"
        if m not in ("work", "wait"):
            return MOOD_FACE[m]
        if t < self.face_until:
            return self.face_override
        if action is not None and action.expression:
            return ACTION_OWN_FACE.get(action.name, ACTION_FACE.get(action.expression, action.expression))
        if m == "work" and event_face:
            return ACTION_FACE.get(event_face, event_face)
        face = BAND_FACE[band] if m == "work" else ("smile" if self.inputs.idle < WAIT_SMILE else None)
        if face is None and self.over_t >= SMILE_AFTER:
            face = "smile"
        return face

    def attending(self):
        """The cursor is over her in a mood that answers it: she starts nothing from her pool meanwhile."""
        return self.inputs.over and self.mood in ("wait", "work")

    def talking(self):
        i = self.inputs
        return self.mood == "work" and i.phase == "generating" and i.wait is None
