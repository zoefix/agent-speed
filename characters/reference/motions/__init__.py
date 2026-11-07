"""Motions and the director that turns speed into motion (see director.py)."""
from .curves import Curve, Motion, merge
from .director import BANDS, POOLS, Director, band_of
from .gestures import COMBOS, FACE_GESTURES, GESTURES, HEAD_GESTURES
from .moves import MOVES
from .springs import SecondOrder, Springs

__all__ = ["BANDS", "COMBOS", "Curve", "Director", "FACE_GESTURES", "GESTURES", "HEAD_GESTURES", "MOVES", "Motion",
           "POOLS", "SecondOrder", "Springs", "band_of", "merge"]
