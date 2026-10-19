using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using System;


public static class WorldData {

    private static readonly List<WorldStructure> _structures = new List<WorldStructure>();
    private static readonly Dictionary<Vector2Int, List<WorldStructure>> _chunkToStructures
        = new Dictionary<Vector2Int, List<WorldStructure>>();
    private static readonly Dictionary<int, WorldStructure> _idToStructure
        = new Dictionary<int, WorldStructure>();

    /// <summary>
    /// Fired immediately after a WorldStructure is registered (e.g. after Instantiate).
    /// StructureLoadingCoordinator subscribes to this to handle structures placed
    /// dynamically mid-episode (e.g. houses spawned by CityLoader).
    /// </summary>
    public static event Action<WorldStructure> OnNewStructureRegistered;

    public static WorldStructure SpawnStructure(
        string    structureType,
        Vector2   position2D,
        float     rotationCCW,
        Transform parent          = null,
        Vector2?  sizeOverride    = null,
        int       lod             = -1) {

        string     prefabName = lod == -1 ? structureType : $"{structureType}_LOD{lod}";
        string     path       = $"WorldGen/WorldStructurePrefabs/{prefabName}";
        WorldStructure prefab = Resources.Load<WorldStructure>(path);

        // fall back to LOD0 prefab if no LOD-specific one exists
        if (prefab == null && lod > 0) {
            prefab = Resources.Load<WorldStructure>($"WorldGen/WorldStructurePrefabs/{structureType}");
            if (prefab != null && lod > 0)
                Debug.LogWarning($"WorldData.SpawnStructure: no LOD{lod} prefab for '{structureType}', using LOD0");
        }

        if (prefab == null) {
            Debug.LogWarning($"WorldData.SpawnStructure: prefab not found for '{prefabName}'");
            return null;
        }

        float y  = WorldServices.Get<IHeightProvider>().GetTerrainHeight(position2D.x, position2D.y);
        var   go = UnityEngine.Object.Instantiate(
            prefab,
            new Vector3(position2D.x, y, position2D.y),
            Quaternion.Euler(0f, -rotationCCW, 0f),
            parent
        );

        if (sizeOverride.HasValue)
            go.SetFootprintSize(sizeOverride.Value);

        // registration happens in WorldStructure.Awake automatically
        return go;
    }

    // convenience — despawn and optionally replace with different LOD
    public static WorldStructure SwapStructureLOD(WorldStructure existing, int newLod) {
        if (existing == null) return null;
        string    type     = existing.structureType;
        Vector2   pos      = existing.GetCenter2D();
        float     rot      = existing.GetRotationCCW();
        Transform parent   = existing.transform.parent;
        Vector2   size     = existing.GetSize();

        // unregister and destroy old
        UnregisterStructure(existing);
        UnityEngine.Object.Destroy(existing.gameObject);

        // spawn new LOD
        return SpawnStructure(type, pos, rot, parent, size, newLod);
    }

    public static void RegisterStructure(WorldStructure s) {
        if (_structures.Contains(s)) return; // idempotent
        _structures.Add(s);

        int id = s.DeterministicId;
        if (_idToStructure.TryGetValue(id, out var existing) && existing != s)
            Debug.LogError($"WorldData: deterministic ID clash ({id}): " +
                $"'{s.structureType}' at {s.transform.position} collides with " +
                $"'{existing.structureType}' at {existing.transform.position}");
        _idToStructure[id] = s;

        RegisterInChunkDict(s);
        OnNewStructureRegistered?.Invoke(s);
    }

    public static void UnregisterStructure(WorldStructure s) {
        _structures.Remove(s);
        int id = s.DeterministicId;
        if (_idToStructure.TryGetValue(id, out var existing) && existing == s)
            _idToStructure.Remove(id);
        // remove from all chunk buckets
        foreach (var list in _chunkToStructures.Values)
            list.Remove(s);
    }

    public static IReadOnlyList<WorldStructure> GetStructures() => _structures;

    public static List<WorldStructure> GetStructuresInChunk(int cx, int cz) {
        _chunkToStructures.TryGetValue(new Vector2Int(cx, cz), out var list);
        return list;
    }

    public static List<WorldStructure> GetStructuresOfTypeInChunk(string type, int cx, int cz) {
        var list = GetStructuresInChunk(cx, cz);
        return list?.Where(s => s.structureType == type).ToList();
    }

    private static void RegisterInChunkDict(WorldStructure s) {
        int cw = (int)WorldLoadingController.GetChunkWidth();
        Bounds2D bb = s.GetBoundingBox2D();

        // get AABB of OBB
        Vector2[] verts = bb.GetVertices();
        float minX = verts.Min(v => v.x), maxX = verts.Max(v => v.x);
        float minZ = verts.Min(v => v.y), maxZ = verts.Max(v => v.y);

        int cxMin = Mathf.FloorToInt(minX / cw), cxMax = Mathf.FloorToInt(maxX / cw);
        int czMin = Mathf.FloorToInt(minZ / cw), czMax = Mathf.FloorToInt(maxZ / cw);

        for (int cx = cxMin; cx <= cxMax; cx++)
        for (int cz = czMin; cz <= czMax; cz++) {
            if (!s.GetBoundingBox2D().Overlaps(GetChunkBounds(cx, cz, cw))) continue;
            var key = new Vector2Int(cx, cz);
            if (!_chunkToStructures.TryGetValue(key, out var list)) {
                list = new List<WorldStructure>();
                _chunkToStructures[key] = list;
            }
            if (!list.Contains(s)) list.Add(s);
        }
    }

    private static Bounds2D GetChunkBounds(int cx, int cz, int cw) {
        float ox = cx * cw, oz = cz * cw;
        return new Bounds2D(
            new Vector2(ox + cw * 0.5f, oz + cw * 0.5f),
            new Vector2(cw, cw), 0f
        );
    }

    public static void Clear() {
        _structures.Clear();
        _chunkToStructures.Clear();
        _idToStructure.Clear();
    }
}
