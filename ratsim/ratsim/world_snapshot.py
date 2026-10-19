"""Fetch overhead pictures of a generated world from Unity.

Unity renders the whole world from a temporary camera when the world config has
``world_snapshot/enabled: 1`` (``WorldSnapshot.cs``) and publishes two messages on the
reset step (or the one after): the PNG as an ``RGBDMessage`` on
``/sim_control/world_snapshot`` and the camera metadata (view-projection matrix, image
size, world bounds, seed) as JSON in a ``StringMessage`` on
``/sim_control/world_snapshot_meta``. This module drives that reset, decodes the reply
and saves ``<stem>.png`` + ``<stem>.json`` pairs that ``ratsim_vis.trajectory_plot``
uses as a background — or that go straight into a paper as "the environment".

Views (``VIEWS``), both straight down with image up = Unity +Z: ``ortho`` (orthographic
camera, a true 2D map) and ``persp`` (perspective camera high above the world).
``topdown`` is accepted as an alias of ``ortho``. Any ``world_snapshot/*`` key (pitch, yaw,
fov, margin, background, show_agent, ...) can be overridden through ``extra``.

CLI (needs the Unity Editor/build in play mode on port 9000, or ``--port``; the build
must render, i.e. not ``-nographics``)::

    # one preset, one seed, both views → out/<preset>_seed42_<view>.png/.json
    python -m ratsim.world_snapshot --world_preset three_malls --seed 42 \\
        --views ortho,persp --out out/

    # the exact world an experiment eval used
    python -m ratsim.world_snapshot --world_config <run>/eval_world_config.json \\
        --seed 1662057957 --out out/ --stem memory_3malls_seed1662057957

Library use::

    conn = connect_and_select_scene(agent_preset="sphereagent_2d_lidar")
    snap = fetch_world_snapshot(conn, blend_presets("world", ["three_malls"]), seed=42,
                                view="ortho", width=2048)
    save_snapshot("out/three_malls_ortho", snap)
    image, meta = load_snapshot("out/three_malls_ortho")
"""
from __future__ import annotations

import argparse
import base64
import io
import json
import sys
from pathlib import Path

import numpy as np

from ratsim.config_blender import blend_presets, to_entries_json
from ratsim.roslike_unity_connector.connector import RoslikeUnityConnector
from ratsim.roslike_unity_connector.message_definitions import BoolMessage, StringMessage
from ratsim.worldgen_dump import connect_and_select_scene  # noqa: F401  (re-exported)

IMAGE_TOPIC = "/sim_control/world_snapshot"
META_TOPIC = "/sim_control/world_snapshot_meta"
VIEWS = ("ortho", "persp")
VIEW_ALIASES = {"topdown": "ortho"}


def canonical_view(view: str) -> str:
    return VIEW_ALIASES.get(view, view)


# ─────────────────────────────────────────────
#  Fetching
# ─────────────────────────────────────────────

def snapshot_world_config(world_config: dict, seed: int | None, view: str, width: int,
                          height: int | None = None, extra: dict | None = None) -> dict:
    """World config for a snapshot reset: the given config plus the snapshot keys.

    ``maze/edge_walls_only`` is forced off — it drops interior wall blocks that no floor
    cell can see, which is invisible to the agent but leaves hollow walls in an overhead
    picture.
    """
    cfg = dict(world_config)
    if seed is not None:
        cfg["seed"] = seed
    cfg["world_snapshot/enabled"] = 1
    cfg["world_snapshot/view"] = view
    cfg["world_snapshot/width"] = int(width)
    cfg["world_snapshot/height"] = int(height or 0)
    cfg["maze/edge_walls_only"] = 0
    for k, v in (extra or {}).items():
        cfg[k if k.startswith("world_snapshot/") else f"world_snapshot/{k}"] = v
    return cfg


def decode_png(b64: str) -> np.ndarray:
    from PIL import Image
    return np.array(Image.open(io.BytesIO(base64.b64decode(b64))).convert("RGB"))


def fetch_world_snapshot(conn: RoslikeUnityConnector, world_config: dict, seed: int | None = None,
                         view: str = "ortho", width: int = 2048, height: int | None = None,
                         extra: dict | None = None, max_steps: int = 20,
                         print_status: bool = True) -> dict:
    """Reset Unity with ``world_config`` (+ snapshot keys) and return the picture.

    Returns ``{"image": (H,W,3) uint8, "meta": dict}`` where ``meta`` is what Unity
    published (``view_proj`` reshaped to 4x4, ``width``, ``height``, ``world_width``,
    ``world_height``, ``seed``, camera pose, ...). Raises RuntimeError if nothing
    arrives within ``max_steps``.
    """
    view = canonical_view(view)
    if view not in VIEWS:
        raise ValueError(f"view must be one of {VIEWS}, got {view!r}")
    cfg = snapshot_world_config(world_config, seed, view, width, height, extra)
    conn.publish(StringMessage(data=to_entries_json(cfg)), "/sim_control/world_config")
    conn.publish(BoolMessage(data=True), "/sim_control/reset_episode")
    image = meta = None
    for _ in range(max_steps):
        conn.send_messages_and_step(enable_physics_step=True)
        conn.read_messages_from_unity()
        if print_status:
            conn.process_worldgen_status()
        msgs = conn.get_received_messages(IMAGE_TOPIC)
        if msgs:
            image = decode_png(msgs[-1].rgbImageBase64)
        msgs = conn.get_received_messages(META_TOPIC)
        if msgs:
            meta = json.loads(msgs[-1].data)
        if image is not None and meta is not None:
            meta["view_proj"] = np.asarray(meta["view_proj"], dtype=np.float64).reshape(4, 4).tolist()
            meta["world_config_seed"] = cfg.get("seed")
            return {"image": image, "meta": meta}
    raise RuntimeError(
        f"no snapshot on {IMAGE_TOPIC}/{META_TOPIC} within {max_steps} steps — is the Unity "
        f"build current (WorldSnapshot.cs), rendering (not -nographics), and is "
        f"world_snapshot/enabled reaching WorldLoadingController?")


# ─────────────────────────────────────────────
#  Files
# ─────────────────────────────────────────────

def save_snapshot(stem: str | Path, snap: dict) -> Path:
    """Write ``<stem>.png`` and ``<stem>.json``; returns the PNG path."""
    from PIL import Image
    stem = Path(stem)
    stem.parent.mkdir(parents=True, exist_ok=True)
    png = stem.with_suffix(".png")
    Image.fromarray(np.asarray(snap["image"], dtype=np.uint8)).save(png)
    with open(stem.with_suffix(".json"), "w") as f:
        json.dump(snap["meta"], f, indent=2)
    return png


def load_snapshot(path: str | Path) -> tuple[np.ndarray, dict]:
    """Load a saved pair by stem, ``.png`` or ``.json`` path → (image, meta)."""
    from PIL import Image
    p = Path(path)
    stem = p.with_suffix("") if p.suffix in (".png", ".json") else p
    image = np.array(Image.open(stem.with_suffix(".png")).convert("RGB"))
    with open(stem.with_suffix(".json")) as f:
        meta = json.load(f)
    meta["view_proj"] = np.asarray(meta["view_proj"], dtype=np.float64).reshape(4, 4)
    return image, meta


def load_world_config_file(path: str | Path) -> dict:
    """World config dict from a JSON file: either a flat config or an
    ``eval_world_config.json`` (``{"world_config": {...}, ...}``)."""
    with open(path) as f:
        d = json.load(f)
    return d["world_config"] if "world_config" in d else d


# ─────────────────────────────────────────────
#  CLI
# ─────────────────────────────────────────────

def main(argv=None) -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--world_preset", nargs="+", help="world preset name(s) to blend")
    src.add_argument("--world_config", help="JSON file: flat config or eval_world_config.json")
    ap.add_argument("--seed", type=int, nargs="+", default=[None], help="world seed(s)")
    ap.add_argument("--views", default="ortho", help=f"comma-separated subset of {VIEWS}")
    ap.add_argument("--width", type=int, default=2048)
    ap.add_argument("--height", type=int, default=0, help="0 = fit the world box aspect")
    ap.add_argument("--set", action="append", default=[], metavar="KEY=VAL",
                    help="extra world_snapshot/<KEY> override, e.g. margin=0.05, show_agent=1")
    ap.add_argument("--out", default=".", help="output directory")
    ap.add_argument("--stem", default=None, help="file stem (default <preset>_seed<seed>); "
                                                  "the view is always appended")
    ap.add_argument("--agent_preset", default="sphereagent_2d_lidar")
    ap.add_argument("--port", type=int, default=9000)
    args = ap.parse_args(argv)

    views = [canonical_view(v.strip()) for v in args.views.split(",") if v.strip()]
    bad = [v for v in views if v not in VIEWS]
    if bad:
        ap.error(f"unknown views {bad}; choose from {VIEWS}")
    extra = {}
    for item in args.set:
        k, _, v = item.partition("=")
        if not _:
            ap.error(f"--set expects KEY=VAL, got {item!r}")
        extra[k] = v

    if args.world_preset:
        world_config = blend_presets("world", args.world_preset)
        base = "+".join(args.world_preset)
    else:
        world_config = load_world_config_file(args.world_config)
        base = Path(args.world_config).stem

    conn = connect_and_select_scene(agent_preset=args.agent_preset, port=args.port)
    out = Path(args.out)
    for seed in args.seed:
        eff_seed = seed if seed is not None else world_config.get("seed")
        stem = args.stem or f"{base}_seed{eff_seed}"
        for view in views:
            snap = fetch_world_snapshot(conn, world_config, seed=seed, view=view,
                                        width=args.width, height=args.height, extra=extra)
            png = save_snapshot(out / f"{stem}_{view}", snap)
            m = snap["meta"]
            print(f"{png}  {m['width']}x{m['height']}  seed={m['seed']}  "
                  f"world={m['world_width']:g}x{m['world_height']:g}")
    conn.disconnect() if hasattr(conn, "disconnect") else None


if __name__ == "__main__":
    sys.exit(main())
