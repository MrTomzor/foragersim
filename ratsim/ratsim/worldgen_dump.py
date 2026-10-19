"""Fetch and diff Unity world-generation dumps.

Unity publishes a JSON snapshot of everything world generation produced (structures,
rewards, wells, agents) on ``/sim_control/worldgen_dump`` when the world config has
``worldgen_dump/enabled: 1`` (see ``WorldGenDump.cs``). This module drives a reset with
that flag set, collects the dump, and diffs two dumps — the tool for checking that a
refactor of the generation pipeline (e.g. a preset rewritten in the rules language)
reproduces exactly the same world, and for regression-testing loader changes.

CLI (needs the Unity Editor/build in play mode on port 9000, or ``--port``)::

    # dump one preset to a file
    python -m ratsim.worldgen_dump dump maze_memorymaze_11x11 --seed 42 --out /tmp/a.json

    # compare two presets at the same seed
    python -m ratsim.worldgen_dump compare maze_memorymaze_11x11 rules_maze_memorymaze_11x11

    # compare every preset that has a twin with the given prefix, print a table
    python -m ratsim.worldgen_dump compare-prefix rules_ --seed 42

Library use::

    conn = connect_and_select_scene(agent_preset="sphereagent_2d_lidar")
    a = fetch_worldgen_dump(conn, blend_presets("world", ["default"]), seed=42)
    b = fetch_worldgen_dump(conn, blend_presets("world", ["rules_default"]), seed=42)
    for line in diff_dumps(a, b): print(line)
"""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
import sys
from typing import Iterable

from ratsim.config_blender import blend_presets, to_entries_json
from ratsim.config_blender.blender import _PRESETS_DIR, CATEGORIES
from ratsim.roslike_unity_connector.connector import RoslikeUnityConnector
from ratsim.roslike_unity_connector.message_definitions import BoolMessage, StringMessage

DUMP_TOPIC = "/sim_control/worldgen_dump"


# ─────────────────────────────────────────────
#  Fetching
# ─────────────────────────────────────────────

def connect_and_select_scene(agent_preset: str = "sphereagent_2d_lidar", port: int = 9000,
                             scene: str = "Wildfire") -> RoslikeUnityConnector:
    """Connect, select the scene and send the agent config (once per connection)."""
    conn = RoslikeUnityConnector(port=port, verbose=False)
    conn.connect()
    conn.publish(StringMessage(data=scene), "/sim_control/scene_select")
    conn.send_messages_and_step(enable_physics_step=False)
    conn.read_messages_from_unity()
    agent_config = blend_presets("agents", [agent_preset])
    conn.publish(StringMessage(data=to_entries_json(agent_config)), "/sim_control/agent_config")
    conn.send_messages_and_step(enable_physics_step=False)
    conn.read_messages_from_unity()
    return conn


def fetch_worldgen_dump(conn: RoslikeUnityConnector, world_config: dict, seed: int | None = None,
                        max_steps: int = 20, print_status: bool = True) -> dict:
    """Reset Unity with ``world_config`` (+ dump flag) and return the parsed dump.

    Steps the sim until the dump message arrives (it normally comes back with the reset
    step itself). Raises RuntimeError if it does not arrive within ``max_steps``.
    """
    cfg = dict(world_config)
    if seed is not None:
        cfg["seed"] = seed
    cfg["worldgen_dump/enabled"] = 1

    conn.publish(StringMessage(data=to_entries_json(cfg)), "/sim_control/world_config")
    conn.publish(BoolMessage(data=True), "/sim_control/reset_episode")
    for _ in range(max_steps):
        conn.send_messages_and_step(enable_physics_step=True)
        conn.read_messages_from_unity()
        if print_status:
            conn.process_worldgen_status()
        msgs = conn.get_received_messages(DUMP_TOPIC)
        if msgs:
            return json.loads(msgs[-1].data)
    raise RuntimeError(
        f"no message on {DUMP_TOPIC} within {max_steps} steps — is the Unity build current "
        f"(WorldGenDump.cs) and is worldgen_dump/enabled reaching WorldLoadingController?")


# ─────────────────────────────────────────────
#  Diffing
# ─────────────────────────────────────────────

_STRUCT_FIELDS = ("rot", "size_x", "size_z")


def _struct_key(e: dict) -> tuple:
    # Geometry, not DeterministicId: the id is a System.HashCode over the same fields and
    # HashCode is seeded per process, so ids only agree within one Unity session.
    return (e["type"], e["x"], e["z"], e["size_x"], e["size_z"], e["rot"])


def _obj_key(e: dict) -> tuple:
    return (e["kind"], e["name"], e["x"], e["z"], e["y"])


def _fmt_struct(e: dict) -> str:
    return (f"{e['type']} at ({e['x']:.2f},{e['z']:.2f}) rot={e['rot']:.1f} "
            f"size=({e['size_x']:.2f},{e['size_z']:.2f})")


def _fmt_key(k: tuple) -> str:
    kind, name, x, z, y = k
    return f"{kind} {name} at ({x:.2f},{y:.2f},{z:.2f})"


def _fmt_obj(e: dict) -> str:
    return f"{e['kind']} {e['name']} at ({e['x']:.2f},{e['y']:.2f},{e['z']:.2f})"


def _parent_desc(e: dict, by_id: dict) -> str | None:
    """Parent structure described by geometry (session-independent), None if top-level."""
    p = by_id.get(e.get("parent_id", -1))
    return _fmt_struct(p) if p is not None else None


def _host_desc(e: dict, by_id: dict) -> str | None:
    p = by_id.get(e.get("host_id", -1))
    return _fmt_struct(p) if p is not None else None


def diff_dumps(a: dict, b: dict, tol: float = 0.011) -> list[str]:
    """Return a list of human-readable differences (empty = identical).

    Structures are matched by geometry (type, position, size, rotation) — so a moved
    structure shows up as one missing + one extra — and their parent structure is compared
    by geometry too. Objects are matched by (kind, name, position) as a multiset, with
    their host structure compared by geometry. DeterministicIds are only used to resolve
    parent/host within one dump: they are System.HashCode values, seeded per process, so
    they do not agree across Unity sessions. ``tol`` gives near-miss re-pairing slack so
    the centimetre rounding in the dump never produces phantom diffs.
    """
    out: list[str] = []

    for k in ("seed", "layout_mode", "world_width", "world_height"):
        if a.get(k) != b.get(k):
            out.append(f"header {k}: {a.get(k)!r} != {b.get(k)!r}")

    ida = {e["id"]: e for e in a.get("structures", [])}
    idb = {e["id"]: e for e in b.get("structures", [])}
    sa = {_struct_key(e): e for e in a.get("structures", [])}
    sb = {_struct_key(e): e for e in b.get("structures", [])}
    if len(sa) != len(ida) or len(sb) != len(idb):
        out.append("warning: duplicate structure geometry within one dump (two structures of the "
                   "same type at the same place) — those collapse in this diff")
    for k in sorted(set(sa) - set(sb)):
        out.append(f"structure only in A: {_fmt_struct(sa[k])}")
    for k in sorted(set(sb) - set(sa)):
        out.append(f"structure only in B: {_fmt_struct(sb[k])}")
    for k in sorted(set(sa) & set(sb)):
        ea, eb = sa[k], sb[k]
        pa, pb = _parent_desc(ea, ida), _parent_desc(eb, idb)
        if pa != pb:
            out.append(f"structure {_fmt_struct(ea)}: parent {pa!r} != {pb!r}")

    # Objects: multiset keyed by (kind, name, position) — several identical pickups can
    # legitimately share a position only if something is wrong, so counts must match too.
    oa = Counter(_obj_key(e) for e in a.get("objects", []))
    ob = Counter(_obj_key(e) for e in b.get("objects", []))
    only_a = list((oa - ob).elements())
    only_b = list((ob - oa).elements())
    # Re-pair near-misses (within tol) so rounding on a .005 boundary is not reported.
    unmatched_a = []
    for ka in sorted(only_a):
        match = next((kb for kb in only_b if kb[:2] == ka[:2]
                      and all(abs(x - y) <= tol for x, y in zip(ka[2:], kb[2:]))), None)
        if match is not None:
            only_b.remove(match)
        else:
            unmatched_a.append(ka)
    for ka in unmatched_a:
        out.append(f"object only in A: {_fmt_key(ka)}")
    for kb in sorted(only_b):
        out.append(f"object only in B: {_fmt_key(kb)}")
    # Per-object extras (yaw, host) for keys present on both sides.
    ea_by = {_obj_key(e): e for e in a.get("objects", [])}
    eb_by = {_obj_key(e): e for e in b.get("objects", [])}
    for k in sorted(set(ea_by) & set(eb_by)):
        ea, eb = ea_by[k], eb_by[k]
        if abs(ea["yaw"] - eb["yaw"]) > tol:
            out.append(f"object {_fmt_obj(ea)}: yaw {ea['yaw']!r} != {eb['yaw']!r}")
        ha, hb = _host_desc(ea, ida), _host_desc(eb, idb)
        if ha != hb:
            out.append(f"object {_fmt_obj(ea)}: host {ha!r} != {hb!r}")
    return out


def summarize(d: dict) -> str:
    by_type: dict[str, int] = {}
    for e in d.get("structures", []):
        by_type[e["type"]] = by_type.get(e["type"], 0) + 1
    by_kind: dict[str, int] = {}
    for e in d.get("objects", []):
        by_kind[e["kind"]] = by_kind.get(e["kind"], 0) + 1
    st = ", ".join(f"{t}={n}" for t, n in sorted(by_type.items()))
    ob = ", ".join(f"{t}={n}" for t, n in sorted(by_kind.items()))
    return f"seed={d.get('seed')} layout={d.get('layout_mode')} structures[{st}] objects[{ob}]"


# ─────────────────────────────────────────────
#  CLI
# ─────────────────────────────────────────────

def _world_preset_names() -> list[str]:
    d = os.path.join(_PRESETS_DIR, CATEGORIES["world"])
    return sorted(f[:-5] for f in os.listdir(d) if f.endswith(".yaml"))


def _compare_pair(conn, name_a: str, name_b: str, seed: int | None, verbose: bool) -> list[str]:
    a = fetch_worldgen_dump(conn, blend_presets("world", [name_a]), seed=seed, print_status=verbose)
    b = fetch_worldgen_dump(conn, blend_presets("world", [name_b]), seed=seed, print_status=verbose)
    if verbose:
        print(f"  A: {summarize(a)}")
        print(f"  B: {summarize(b)}")
    return diff_dumps(a, b)


def main(argv: Iterable[str] | None = None) -> int:
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--port", type=int, default=9000)
    common.add_argument("--agent", default="sphereagent_2d_lidar", help="agent preset (AgentLoader needs one)")
    common.add_argument("--seed", type=int, default=42)
    common.add_argument("-v", "--verbose", action="store_true")

    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)

    d = sub.add_parser("dump", parents=[common], help="dump one world preset")
    d.add_argument("preset")
    d.add_argument("--out", help="write JSON here (default: stdout summary only)")

    c = sub.add_parser("compare", parents=[common], help="compare two world presets at the same seed")
    c.add_argument("preset_a")
    c.add_argument("preset_b")

    cp = sub.add_parser("compare-prefix", parents=[common], help="compare every preset X against <prefix>X")
    cp.add_argument("prefix")

    args = p.parse_args(list(argv) if argv is not None else None)
    conn = connect_and_select_scene(agent_preset=args.agent, port=args.port)

    if args.cmd == "dump":
        dump = fetch_worldgen_dump(conn, blend_presets("world", [args.preset]), seed=args.seed)
        print(summarize(dump))
        if args.out:
            with open(args.out, "w") as f:
                json.dump(dump, f, indent=1)
            print(f"wrote {args.out}")
        return 0

    if args.cmd == "compare":
        diffs = _compare_pair(conn, args.preset_a, args.preset_b, args.seed, args.verbose)
        if not diffs:
            print(f"IDENTICAL: {args.preset_a} == {args.preset_b} (seed={args.seed})")
            return 0
        print(f"DIFFERENT: {args.preset_a} vs {args.preset_b} (seed={args.seed}), {len(diffs)} differences:")
        for line in diffs[:50]:
            print("  " + line)
        if len(diffs) > 50:
            print(f"  ... {len(diffs) - 50} more")
        return 1

    # compare-prefix
    names = _world_preset_names()
    pairs = [(n, args.prefix + n) for n in names if (args.prefix + n) in names]
    if not pairs:
        print(f"no preset has a '{args.prefix}' twin")
        return 2
    rc = 0
    rows = []
    for a_name, b_name in pairs:
        try:
            diffs = _compare_pair(conn, a_name, b_name, args.seed, args.verbose)
            status = "identical" if not diffs else f"{len(diffs)} diffs: {diffs[0]}"
            rc |= 1 if diffs else 0
        except Exception as e:  # keep going, report per row
            status = f"ERROR: {e}"
            rc |= 4
        rows.append((a_name, status))
        print(f"{a_name:40s} {status}", flush=True)
    print(f"\n{sum(1 for _, s in rows if s == 'identical')}/{len(rows)} identical (seed={args.seed})")
    return rc


if __name__ == "__main__":
    sys.exit(main())
