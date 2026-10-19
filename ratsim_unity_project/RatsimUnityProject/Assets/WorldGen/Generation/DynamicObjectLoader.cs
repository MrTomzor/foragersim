using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldDataProvider that manages persistent dynamic objects (rubble, physics props, etc.).
///
/// Responsibilities:
///   - Maintains a registry of all live PersistentDynamicObject instances.
///   - Tracks which structures have already spawned their dynamic objects (by step index),
///     so that structure reload does not duplicate them.
///   - Listens to chunk load/unload events and enables/disables dynamic objects based on
///     whether the chunk they are CURRENTLY in (by world position) is loaded at sufficient LOD.
///
/// Add this component to the scene alongside other WorldDataProviders. It depends on
/// StructureContent so it runs after structure loaders have spawned their content.
/// </summary>
public class DynamicObjectLoader : WorldDataProvider {

    public static DynamicObjectLoader Instance { get; private set; }

    public override WorldDataType[] Provides => new[] { WorldDataType.DynamicObjects };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.StructureContent };

    public bool verbose = false;

    // All registered persistent dynamic objects.
    private readonly List<PersistentDynamicObject> _objects = new List<PersistentDynamicObject>();

    // structureId → step index when its dynamic objects were first spawned.
    private readonly Dictionary<int, uint> _structureSpawnSteps = new Dictionary<int, uint>();

    // Currently loaded chunks and their LOD (mirrors ChunkLoadingRequestor's view).
    private readonly Dictionary<Vector2Int, int> _loadedChunks = new Dictionary<Vector2Int, int>();

    private float _chunkWidth;

    protected override void OnEnable() {
        base.OnEnable();
        Instance = this;
    }

    protected override void OnDisable() {
        base.OnDisable();
        if (Instance == this) Instance = null;
    }

    // ─────────────────────────────────────────────
    //  Structure spawn tracking
    // ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given structure already has living persistent objects
    /// from a PREVIOUS step (i.e. this is a reload, not the original spawn).
    /// </summary>
    public bool IsStructureRespawn(int structureId, uint currentStep) {
        return _structureSpawnSteps.TryGetValue(structureId, out uint spawnStep)
            && spawnStep != currentStep;
    }

    /// <summary>
    /// Records that the given structure spawned dynamic objects at the given step.
    /// Called by PersistentDynamicObject.Awake().
    /// </summary>
    public void RecordStructureSpawn(int structureId, uint currentStep) {
        _structureSpawnSteps[structureId] = currentStep;
    }

    // ─────────────────────────────────────────────
    //  Object registry
    // ─────────────────────────────────────────────

    public void Register(PersistentDynamicObject obj) {
        if (!_objects.Contains(obj))
            _objects.Add(obj);
    }

    /// <summary>
    /// Apply the chunk-LOD rule to one object now. Called by PersistentDynamicObject.Start():
    /// an object spawned over a chunk that is not loaded (e.g. rubble of a house that loaded
    /// because a neighbouring chunk did) would otherwise stay active with no terrain under it
    /// until that chunk gets an event — and fall through the world.
    /// </summary>
    public void RefreshVisibility(PersistentDynamicObject obj) {
        if (obj != null && _objects.Contains(obj)) UpdateObjectVisibility(obj);
    }

    public void Unregister(PersistentDynamicObject obj) {
        _objects.Remove(obj);
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider — chunk events
    // ─────────────────────────────────────────────

    public override void Generate() {
        _chunkWidth = WorldLoadingController.GetChunkWidth();
    }

    public override void GenerateChunk(int cx, int cz, int lod) {
        var key = new Vector2Int(cx, cz);
        _loadedChunks[key] = lod;
        UpdateObjectsInChunk(cx, cz);
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        var key = new Vector2Int(cx, cz);
        _loadedChunks.Remove(key);
        UpdateObjectsInChunk(cx, cz);
    }

    public override void Clear() {
        // Destroy all tracked dynamic objects.
        for (int i = _objects.Count - 1; i >= 0; i--) {
            if (_objects[i] != null)
                WorldGenObjects.DestroyHidden(_objects[i].gameObject);
        }
        _objects.Clear();
        _structureSpawnSteps.Clear();
        _loadedChunks.Clear();
    }

    // ─────────────────────────────────────────────
    //  Enable/disable logic
    // ─────────────────────────────────────────────

    /// <summary>
    /// Re-evaluate visibility of all objects whose current position falls in the given chunk.
    /// Called on chunk load and unload events.
    /// </summary>
    private void UpdateObjectsInChunk(int cx, int cz) {
        for (int i = _objects.Count - 1; i >= 0; i--) {
            var obj = _objects[i];
            if (obj == null) { _objects.RemoveAt(i); continue; }

            Vector2Int objChunk = WorldToChunk(obj.transform.position);
            if (objChunk.x == cx && objChunk.y == cz)
                UpdateObjectVisibility(obj);
        }
    }

    private void UpdateObjectVisibility(PersistentDynamicObject obj) {
        Vector2Int chunk = WorldToChunk(obj.transform.position);
        bool shouldBeActive = false;

        if (_loadedChunks.TryGetValue(chunk, out int chunkLod))
            shouldBeActive = chunkLod <= obj.requiredLod;

        if (obj.gameObject.activeSelf != shouldBeActive) {
            obj.gameObject.SetActive(shouldBeActive);
            if (verbose)
                Debug.Log($"DynamicObjectLoader: {obj.name} " +
                    $"{(shouldBeActive ? "enabled" : "disabled")} " +
                    $"(chunk {chunk}, lod={(!shouldBeActive ? "unloaded" : chunkLod.ToString())}, " +
                    $"required={obj.requiredLod})");
        }
    }

    private Vector2Int WorldToChunk(Vector3 worldPos) {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / _chunkWidth),
            Mathf.FloorToInt(worldPos.z / _chunkWidth)
        );
    }
}
