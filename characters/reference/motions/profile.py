"""A character's motion personality (spec/CHARACTERS.md, Motion profile): the manifest's `motion` block.

Every key is optional; the defaults are Claude's behavior, so a pack without a profile moves exactly as before.
Signature actions are hers: they join the pools of the bands they fit, weighted three times, and a face gesture among
them plays its motion even when the rig has no expression for it.
"""
import math

BANDS = ("idle", "cruise", "boost", "overdrive", "redline")

# The bands an action fits in, lowest and highest: signature actions join a band's pool only inside their range.
ACTION_BANDS = {
    "yawn": ("idle", "cruise"), "stretch": ("idle", "cruise"), "ponder": ("idle", "cruise"),
    "bow": ("idle", "boost"), "curtsy": ("idle", "boost"), "nod": ("idle", "boost"), "tilt": ("idle", "boost"),
    "lookaround": ("idle", "boost"), "shake": ("idle", "boost"), "foottap": ("idle", "boost"),
    "floatbob": ("idle", "boost"), "hum": ("cruise", "boost"), "sway": ("cruise", "boost"),
    "tiptoe": ("cruise", "boost"), "peek": ("cruise", "boost"), "giggle": ("cruise", "overdrive"),
    "doubletake": ("cruise", "overdrive"), "step": ("idle", "overdrive"), "swagger": ("cruise", "overdrive"),
    "hop": ("cruise", "redline"), "hops": ("boost", "redline"),
    "spin": ("boost", "redline"), "pounce": ("boost", "redline"), "run": ("overdrive", "redline"),
    "jump": ("overdrive", "redline"), "jumpjoy": ("overdrive", "redline"),
    "tailswish": ("idle", "redline"), "wingflap": ("idle", "redline"), "eartwitch": ("idle", "redline"),
}
# Seated characters keep their upper body only: these actions need legs or the whole body.
SEATED_SKIP = {"hop", "jump", "hops", "spin", "step", "run", "jumpjoy", "tiptoe", "foottap", "curtsy", "pounce",
               "floatbob"}
# Parameters a seated character never moves.
SEATED_FREEZE = ("PosX", "PosY", "Squash", "Lean", "Hip", "FootLift", "FootPoint", "BodySway", "SkirtFlare")
# Without a voice: no humming, no giggling.
SILENT_SKIP = {"hum", "giggle"}
HAND_SETS = ("tap", "regrip", "lift", "flex", "fan", "count", "drum", "flute", "fanflutter", "abacus", "sleeveflop",
             "ripple", "grip")
# Sets played on an instrument held in both hands: while one plays, the arms keep their pose so the hands never slide
# along the instrument, and the body carries both with it.
TWO_HANDED = {"flute", "grip"}
# Sets played on an instrument at the lips: the head keeps its pose too, so the lips never slide along it.
AT_MOUTH = {"flute"}
HEAD_PARAMS = ("AngleX", "AngleY", "AngleZ")
ARM_PARAMS = ("Arm.", "Forearm.", "Shoulder.", "Lift.", "Bend.")
# Which hands a pose's hand set plays on, back to back: both, the hand holding the object, or the free one.
POSE_HAND_SIDES = {"flute": "both", "sleeveflop": "both", "ripple": "both", "grip": "both", "fanflutter": "holder", "abacus": "free",
                   "tap": "holder", "regrip": "holder", "lift": "holder", "flex": "free", "fan": "free",
                   "count": "free", "drum": "free"}


class MotionProfile:
    def __init__(self, doc=None):
        doc = doc or {}

        def number(key, fallback=1.0):
            v = doc.get(key, fallback)
            return min(max(float(v), 0.0), 4.0) if isinstance(v, (int, float)) and math.isfinite(v) else fallback

        self.tempo = max(0.25, number("tempo"))
        self.energy = number("energy")
        self.sway = number("sway")
        self.bounce = number("bounce")
        self.gap = max(0.25, number("gap"))
        self.idle = doc.get("idle", "read")
        self.seated = bool(doc.get("seated", False))
        self.talk = bool(doc.get("talk", True))
        self.signature = [a for a in doc.get("signature", []) if a in ACTION_BANDS]
        self.exclude = set(doc.get("exclude", []))
        self.hands = [h for h in doc.get("hands", []) if h in HAND_SETS]
        self.pose_hands = {b: h for b, h in doc.get("poseHands", {}).items() if b in BANDS and h in HAND_SETS}

    def allows(self, action):
        if action in self.exclude:
            return False
        if self.seated and action in SEATED_SKIP:
            return False
        if not self.talk and action in SILENT_SKIP:
            return False
        return True

    def pool(self, base, band):
        """The band's pool: the base actions it allows, then each signature action that fits the band three times
        in all (twice more when the base already has it)."""
        out = [a for a in base if self.allows(a)]
        i = BANDS.index(band)
        for a in self.signature:
            lo, hi = ACTION_BANDS[a]
            if not self.allows(a) or not BANDS.index(lo) <= i <= BANDS.index(hi):
                continue
            out += [a] * (2 if a in base else 3)
        return out
