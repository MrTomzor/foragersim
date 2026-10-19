import json
import os

import yaml

_PRESETS_DIR = os.path.dirname(os.path.abspath(__file__))

CATEGORIES = {
    "world": "world_presets",
    "agents": "agents_presets",
    "task": "task_presets",
}


def load_preset(category: str, name: str) -> dict:
    """Load a single YAML preset file by category and name, return as dict."""
    subdir = CATEGORIES.get(category)
    if subdir is None:
        raise ValueError(f"Unknown preset category '{category}'. Valid: {list(CATEGORIES.keys())}")
    path = os.path.join(_PRESETS_DIR, subdir, f"{name}.yaml")
    with open(path) as f:
        return yaml.safe_load(f) or {}


def blend_presets(category: str, names: list[str]) -> dict:
    """Load multiple presets and merge them sequentially (later overrides earlier)."""
    result = {}
    for name in names:
        preset = load_preset(category, name)
        result.update(preset)
    return result


def flatten_config(config_dict: dict) -> dict:
    """Flatten structured config (e.g. sensor lists) into the flat key/value
    format that Unity's AgentLoader expects.

    A ``sensors`` value that is a list of dicts like::

        sensors:
          - name: lidar2d
            maxRange: 50.0
          - name: odom

    becomes flat keys::

        sensors: "lidar2d, odom"
        lidar2d/maxRange: "50.0"

    If ``sensors`` is already a plain string it is passed through unchanged.
    The same applies to ``actuators``.
    """
    flat = {}
    for key, value in config_dict.items():
        if key in ("sensors", "actuators") and isinstance(value, list):
            names = []
            for item in value:
                if isinstance(item, dict):
                    name = item["name"]
                    names.append(name)
                    for param, val in item.items():
                        if param == "name":
                            continue
                        flat[f"{name}/{param}"] = val
                else:
                    names.append(str(item))
            flat[key] = ", ".join(names)
        else:
            flat[key] = value
    return flat


def to_entries_json(config_dict: dict) -> str:
    """Convert a config dict to Unity entries JSON format.

    Structured fields (sensor/actuator lists with param overrides) are
    automatically flattened before serialisation.

    Input:  {"seed": 42, "tree_generation/density": 0.03}
    Output: '{"entries": [{"key": "seed", "value": "42"}, {"key": "tree_generation/density", "value": "0.03"}]}'

    All values are stringified to match WorldLoadingController's parser.
    """
    flat = flatten_config(config_dict)
    entries = [{"key": str(k), "value": str(v)} for k, v in flat.items()]
    return json.dumps({"entries": entries})
