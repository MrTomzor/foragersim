using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Shared "reward-object-like distribution" over a structure's
/// <c>rewardSpawnPositions</c> slots. Both <see cref="RewardObjectLoader"/>
/// (throwaway pickups, lazy per structure-load) and <see cref="WellLoader"/>
/// (persistent well landmarks, eager at Generate) use this so the slot-selection
/// policy lives in exactly one place. Only the iteration driver (eager vs lazy)
/// and the spawned thing differ between the callers — never the selection.
///
/// Selection is deterministic per structure: the RNG is seeded from the caller's
/// base seed XOR a hash of the structure centre, so a given structure yields the
/// same slots whether it is resolved at Generate or on chunk-load.
/// </summary>
public static class StructureSlotDistribution {

    /// <summary>Per-structure-type distribution parameters.</summary>
    public struct Spec {
        public float skipProbability;   // chance to place nothing at all in the structure
        public float spawnProbability;  // per-slot Bernoulli probability (probability mode)
        public int   minPerStructure;   // -1 = unset
        public int   maxPerStructure;   // -1 = unset

        /// <summary>Count mode is active when either min or max is set (>= 0).</summary>
        public bool UseCountMode => minPerStructure >= 0 || maxPerStructure >= 0;
    }

    /// <summary>
    /// Normalise a count-mode spec: if only one of min/max is provided mirror it,
    /// and validate min &lt;= max. On invalid input publishes a WorldGenStatus error
    /// and disables count mode for this spec (falls back to probability mode).
    /// Call once at param-load time (no structure needed).
    /// </summary>
    public static void NormalizeSpec(ref Spec spec, string source, string context) {
        if (!spec.UseCountMode) return;
        if (spec.minPerStructure < 0) spec.minPerStructure = spec.maxPerStructure;
        if (spec.maxPerStructure < 0) spec.maxPerStructure = spec.minPerStructure;
        if (spec.minPerStructure > spec.maxPerStructure) {
            WorldGenStatus.Error(source,
                $"{context}: min_per_structure ({spec.minPerStructure}) > max_per_structure ({spec.maxPerStructure}) — disabling count mode for this type");
            spec.minPerStructure = -1;
            spec.maxPerStructure = -1;
        }
    }

    /// <summary>
    /// Deterministic per-structure RNG. Matches the legacy
    /// RewardObjectLoader.MakeStructureRng seeding so existing reward layouts are
    /// unchanged after the refactor.
    /// </summary>
    public static System.Random MakeRng(WorldStructure s, int baseSeed) {
        Vector2 center = s.GetCenter2D();
        int seed = baseSeed
            ^ (Mathf.RoundToInt(center.x * 100f) * 1000003)
            ^ (Mathf.RoundToInt(center.y * 100f) * 999983);
        return new System.Random(seed);
    }

    /// <summary>
    /// Select the slot Transforms to fill for one structure, drawing from the
    /// caller-supplied <paramref name="rng"/> (create it with <see cref="MakeRng"/>).
    /// The caller passing the RNG in — rather than this method making its own — lets
    /// the caller keep drawing from the same sequence afterwards (e.g. RewardObjectLoader
    /// attaching signal sources), preserving legacy determinism exactly.
    ///
    /// Returns an empty list when the structure is skipped (skip probability) or a
    /// count-mode spec is invalid for this structure's slot count (a WorldGenStatus
    /// error is published in that case). The RNG draw order is: skip → count/probability.
    /// </summary>
    public static List<Transform> SelectSlots(
        Transform group, Spec spec, System.Random rng, string source, string context) {

        if (group == null || group.childCount == 0) return new List<Transform>();
        var pool = new List<Transform>(group.childCount);
        foreach (Transform slot in group) pool.Add(slot);
        return SelectSlots(pool, spec, rng, source, context);
    }

    /// <summary>
    /// List-based overload for callers that pre-filter the slot pool (e.g. WellLoader's
    /// room-edge buffer). Selection over an unfiltered child list draws the RNG in the
    /// same order as the group overload, so legacy layouts are unchanged.
    /// </summary>
    public static List<Transform> SelectSlots(
        List<Transform> pool, Spec spec, System.Random rng, string source, string context) {

        var result = new List<Transform>();
        if (pool == null || pool.Count == 0) return result;

        // Per-structure skip chance.
        if ((float)rng.NextDouble() < spec.skipProbability) return result;

        int slotCount = pool.Count;

        if (spec.UseCountMode) {
            int min = spec.minPerStructure;
            int max = spec.maxPerStructure;
            if (min > slotCount || max > slotCount) {
                WorldGenStatus.Error(source,
                    $"{context}: min/max per structure ({min}/{max}) exceeds available " +
                    $"rewardSpawnPositions ({slotCount}). Configure min/max <= {slotCount} or add more spawn positions.");
                return result;
            }
            int n = (min == max) ? min : rng.Next(min, max + 1);
            // Fisher-Yates shuffle of slot indices; take the first n.
            int[] indices = new int[slotCount];
            for (int i = 0; i < slotCount; i++) indices[i] = i;
            for (int i = slotCount - 1; i > 0; i--) {
                int j = rng.Next(i + 1);
                int tmp = indices[i]; indices[i] = indices[j]; indices[j] = tmp;
            }
            for (int k = 0; k < n; k++)
                result.Add(pool[indices[k]]);
        } else {
            // Probability mode: independent per-slot Bernoulli.
            foreach (Transform slot in pool) {
                if ((float)rng.NextDouble() > spec.spawnProbability) continue;
                result.Add(slot);
            }
        }

        return result;
    }
}
