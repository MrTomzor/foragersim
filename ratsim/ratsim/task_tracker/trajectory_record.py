"""Save / load one episode's recorded trajectory as an ``.npz`` file.

The arrays come from ``TaskTracker.get_trajectory()`` (ROS frame: x forward,
y left, z up; yaw CCW radians). ``meta`` is any JSON-serialisable dict — the
writers stamp method, world seed, episode index and world bounds so a file
is self-describing for the plotting side (``ratsim.ratsim_vis.trajectory_plot``).
"""
from __future__ import annotations

import json
from pathlib import Path

import numpy as np

_ARRAY_KEYS = ("steps", "xyz", "yaw", "pickup_steps")


def save_trajectory(path: "str | Path", traj: dict, meta: "dict | None" = None) -> Path:
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    arrays = {k: np.asarray(traj[k]) for k in _ARRAY_KEYS if k in traj}
    arrays["meta_json"] = np.array(json.dumps(meta or {}, default=_json_default))
    np.savez_compressed(path, **arrays)
    return path


def load_trajectory(path: "str | Path") -> dict:
    """Return ``{"steps", "xyz", "yaw", "pickup_steps", "meta"}``."""
    with np.load(Path(path), allow_pickle=False) as z:
        out = {k: z[k] for k in _ARRAY_KEYS if k in z.files}
        out["meta"] = json.loads(str(z["meta_json"])) if "meta_json" in z.files else {}
    return out


def _json_default(o):
    if isinstance(o, (np.integer,)):
        return int(o)
    if isinstance(o, (np.floating,)):
        return float(o)
    if isinstance(o, np.ndarray):
        return o.tolist()
    if isinstance(o, Path):
        return str(o)
    return str(o)
