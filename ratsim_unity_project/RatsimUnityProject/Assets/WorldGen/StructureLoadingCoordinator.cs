using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Bridges chunk-level load/unload events into per-WorldStructure load/unload events
/// for all registered WorldStructureProvider components.
///
/// Must be placed in the scene AFTER WorldLayoutLoader so that WorldLayoutLoader.Generate()
/// has already populated WorldData by the time this coordinator processes a chunk.
///
/// Also listens to WorldData.OnNewStructureRegistered so that structures added
/// dynamically (e.g. houses placed by CityLoader) automatically get their load events fired.
/// </summary>
public class StructureLoadingCoordinator : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.StructureEvents };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Layout };

    public bool verbose = false;

    // chunk -> LOD currently loaded
    private readonly Dictionary<Vector2Int, int> _loadedChunks = new Dictionary<Vector2Int, int>();

    // structure -> set of loaded chunk keys that overlap it
    private readonly Dictionary<WorldStructure, HashSet<Vector2Int>> _structureChunks
        = new Dictionary<WorldStructure, HashSet<Vector2Int>>();

    // ─────────────────────────────────────────────
    //  Enable / Disable
    // ─────────────────────────────────────────────

    protected override void OnEnable() {
        base.OnEnable();
        WorldData.OnNewStructureRegistered += HandleNewStructureRegistered;
    }

    protected override void OnDisable() {
        base.OnDisable();
        WorldData.OnNewStructureRegistered -= HandleNewStructureRegistered;
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void GenerateChunk(int cx, int cz, int lod) {
        var key = new Vector2Int(cx, cz);
        _loadedChunks[key] = lod;

        // WorldLayoutLoader has already run (earlier in registered list) so WorldData is populated.
        var structures = WorldData.GetStructuresInChunk(cx, cz);
        if (structures == null) return;

        // Snapshot the list — CityLoader may add houses to WorldData during event firing,
        // those arrive via HandleNewStructureRegistered instead.
        foreach (var s in structures.ToList())
            ProcessStructureLoad(s, key, lod);

        // WorldStructureProviders (e.g. CityLoader) may have instantiated new GameObjects
        // during the above loop. Sync physics so that subsequent WorldDataProviders
        // (e.g. TreeLoader) can find those colliders via OverlapBox.
        /* Physics.SyncTransforms(); */
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        var key = new Vector2Int(cx, cz);
        _loadedChunks.Remove(key);

        var structures = WorldData.GetStructuresInChunk(cx, cz);
        if (structures == null) return;

        foreach (var s in structures.ToList())
            ProcessStructureUnload(s, key);
    }

    public override void Clear() {
        // Reset LOD state on all tracked structures (WorldStructureProviders already cleared themselves).
        foreach (var s in _structureChunks.Keys)
            if (s != null) s.currentLod = -1;
        _loadedChunks.Clear();
        _structureChunks.Clear();
    }

    // ─────────────────────────────────────────────
    //  New structure registration
    // ─────────────────────────────────────────────

    private void HandleNewStructureRegistered(WorldStructure s) {
        int bestLod = GetBestLodForStructure(s);
        if (bestLod == -1) return; // not inside any currently loaded chunk

        // Build the chunk tracking set for this structure
        if (!_structureChunks.TryGetValue(s, out var chunks)) {
            chunks = new HashSet<Vector2Int>();
            _structureChunks[s] = chunks;
        }
        int cw = (int)WorldLoadingController.GetChunkWidth();
        foreach (var kvp in _loadedChunks) {
            if (ChunkOverlapsStructure(kvp.Key, cw, s))
                chunks.Add(kvp.Key);
        }

        s.currentLod = bestLod;
        NotifyLoaded(s, bestLod);
    }

    // ─────────────────────────────────────────────
    //  Core load / unload helpers
    // ─────────────────────────────────────────────

    private void ProcessStructureLoad(WorldStructure s, Vector2Int key, int lod) {
        if (!_structureChunks.TryGetValue(s, out var chunks)) {
            chunks = new HashSet<Vector2Int>();
            _structureChunks[s] = chunks;
        }
        chunks.Add(key);

        // Only fire load if this is first load or an LOD upgrade (lower = better).
        if (s.currentLod == -1 || lod < s.currentLod) {
            s.currentLod = lod;
            NotifyLoaded(s, lod);
        }
    }

    private void ProcessStructureUnload(WorldStructure s, Vector2Int key) {
        if (!_structureChunks.TryGetValue(s, out var chunks)) return;
        chunks.Remove(key);

        if (chunks.Count == 0) {
            // No loaded chunks cover this structure anymore — fully unload.
            _structureChunks.Remove(s);
            int oldLod = s.currentLod;
            s.currentLod = -1;
            if (s != null) NotifyUnloaded(s, oldLod);
        } else {
            // Still covered by other chunks; re-evaluate LOD in case it degraded.
            int bestLod = int.MaxValue;
            foreach (var c in chunks)
                if (_loadedChunks.TryGetValue(c, out int cLod) && cLod < bestLod)
                    bestLod = cLod;

            if (bestLod != int.MaxValue && bestLod > s.currentLod) {
                // LOD degraded — fire a re-load at the worse (but still valid) LOD.
                s.currentLod = bestLod;
                NotifyLoaded(s, bestLod);
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Notification
    // ─────────────────────────────────────────────

    private void NotifyLoaded(WorldStructure s, int lod) {
        if (verbose) Debug.Log($"StructureLoadingCoordinator: loading '{s.structureType}' at LOD{lod}");
        foreach (var loader in WorldStructureProvider.registered)
            loader.OnWorldStructureLoaded(s, lod);
    }

    private void NotifyUnloaded(WorldStructure s, int lod) {
        if (verbose) Debug.Log($"StructureLoadingCoordinator: unloading '{s.structureType}' (was LOD{lod})");
        foreach (var loader in WorldStructureProvider.registered)
            loader.OnWorldStructureUnloaded(s, lod);
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    /// <summary>Returns the best (lowest) LOD from all loaded chunks that overlap s, or -1 if none.</summary>
    private int GetBestLodForStructure(WorldStructure s) {
        int cw = (int)WorldLoadingController.GetChunkWidth();
        int best = int.MaxValue;
        foreach (var kvp in _loadedChunks)
            if (ChunkOverlapsStructure(kvp.Key, cw, s) && kvp.Value < best)
                best = kvp.Value;
        return best == int.MaxValue ? -1 : best;
    }

    private static bool ChunkOverlapsStructure(Vector2Int chunk, int cw, WorldStructure s) {
        var chunkBounds = new Bounds2D(
            new Vector2((chunk.x + 0.5f) * cw, (chunk.y + 0.5f) * cw),
            new Vector2(cw, cw), 0f);
        return s.GetBoundingBox2D().Overlaps(chunkBounds);
    }
}
