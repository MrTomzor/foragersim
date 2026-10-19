using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Label-based spatial filter for density spawners (trees, walkers): a sampled XZ point
/// passes when it lies inside the footprint of at least one allowed structure type
/// (empty list = anywhere) and inside no denied one. Deny wins over allow.
///
/// Types are matched by case-insensitive StartsWith, "*" matches everything — the same
/// convention as agents_spawn_pos/allowed_structures and reward_objects/allowed_structures.
///
/// Config keys, read under the loader's prefix (e.g. "chaotic_walkers/"):
///   allowed_structures  -- CSV of structure types the point must be inside (default "")
///   denied_structures   -- CSV of structure types the point must not be inside (default "")
///
/// Lookup uses WorldData's chunk index, so only structures overlapping the point's chunk are
/// tested. Structures must already be registered when the point is tested — true for layout
/// structures, maze rooms and anything CityLoader places eagerly into them.
/// </summary>
public sealed class StructureTypeFilter {
    private readonly List<string> _allowed;
    private readonly List<string> _denied;

    public bool IsActive => _allowed.Count > 0 || _denied.Count > 0;

    private StructureTypeFilter(List<string> allowed, List<string> denied) {
        _allowed = allowed;
        _denied = denied;
    }

    public static StructureTypeFilter FromParams(string prefix) {
        return new StructureTypeFilter(
            ParseTypeList(WorldLoadingController.GetParamString(prefix + "allowed_structures", "")),
            ParseTypeList(WorldLoadingController.GetParamString(prefix + "denied_structures", "")));
    }

    public bool Accepts(Vector2 xz) {
        if (!IsActive) return true;

        int cw = (int)WorldLoadingController.GetChunkWidth();
        List<WorldStructure> candidates = WorldData.GetStructuresInChunk(
            Mathf.FloorToInt(xz.x / cw), Mathf.FloorToInt(xz.y / cw));

        bool insideAllowed = _allowed.Count == 0;
        if (candidates != null) {
            foreach (WorldStructure s in candidates) {
                if (s == null) continue;
                bool isDenied = _denied.Count > 0 && MatchesAny(s.structureType, _denied);
                bool isAllowed = !insideAllowed && MatchesAny(s.structureType, _allowed);
                if (!isDenied && !isAllowed) continue;
                if (!s.GetBoundingBox2D().Contains(xz)) continue;
                if (isDenied) return false;
                insideAllowed = true;
            }
        }
        return insideAllowed;
    }

    public override string ToString() =>
        $"allowed=[{string.Join(",", _allowed)}], denied=[{string.Join(",", _denied)}]";

    private static List<string> ParseTypeList(string csv) {
        List<string> list = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (string raw in csv.Split(',')) {
            string t = raw.Trim();
            if (!string.IsNullOrEmpty(t)) list.Add(t);
        }
        return list;
    }

    private static bool MatchesAny(string structureType, List<string> patterns) {
        for (int i = 0; i < patterns.Count; i++) {
            string p = patterns[i];
            if (p == "*") return true;
            if (structureType.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
