using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Scatters vegetation prefabs per chunk at a uniform density (vegetation/&lt;name&gt;/density),
/// thinned or boosted by VegetationModification on overlapping structures.
///
/// Spatial filter by structure type (see StructureTypeFilter), applied to every vegetation prefab:
///   vegetation/allowed_structures -- CSV of structure types trees may grow inside (default "" = anywhere)
///   vegetation/denied_structures  -- CSV of structure types trees must not grow inside (default "")
/// E.g. allowed_structures: park puts trees only in the "park" cells of a cell_grid maze.
/// </summary>
public class TreeLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Vegetation };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.StructureContent, WorldDataType.Agents };

    [SerializeField] private GameObject treePrefab;

    private int _worldGenLayer;
    public float _density;
    private float _chunkWidth;
    private int _treeSeed;
    private StructureTypeFilter _structureFilter;

    public bool verbose;
    bool paramsInitialized = false;

    private const string VegetationPrefabPath = "WorldGen/VegetationPrefabs/";

    private struct VegetationEntry {
        public GameObject prefab;
        public float density;
    }
    private List<VegetationEntry> _vegetationEntries = new List<VegetationEntry>();

    // chunkID → chunk parent GameObject
    private Dictionary<Vector2Int, GameObject> _chunkObjects = new Dictionary<Vector2Int, GameObject>();
    // chunkID → whether it has been generated (to distinguish "never generated" from "disabled")
    private HashSet<Vector2Int> _generatedChunks = new HashSet<Vector2Int>();

    // Registered by AgentLoader for city_outskirts spawn: no trees inside these circles.
    // Cleared each episode in Clear() before AgentLoader re-registers for the new episode.
    private struct ClearZone { public Vector2 center; public float radius; }
    private static readonly List<ClearZone> _clearZones = new List<ClearZone>();

    public static void RegisterClearZone(Vector2 center, float radius) {
        _clearZones.Add(new ClearZone { center = center, radius = radius });
    }

    private void LoadParams() {
        _worldGenLayer = LayerMask.GetMask("WorldGen");
        _chunkWidth    = WorldLoadingController.GetChunkWidth();
        _treeSeed      = WorldLoadingController.GetDerivedSeed("trees");
        _structureFilter = StructureTypeFilter.FromParams("vegetation/");
        if (_structureFilter.IsActive) Debug.Log($"TreeLoader: structure filter {_structureFilter}");

        _vegetationEntries.Clear();
        string allowedPrefabs = WorldLoadingController.GetParamString("vegetation/allowed_prefabs", "");
        Debug.Log("TreeLoader: loading vegetation prefabs, allowed_prefabs=" + allowedPrefabs);

        if (!string.IsNullOrEmpty(allowedPrefabs)) {
            // New config-driven vegetation
            string[] names = allowedPrefabs.Split(',');
            foreach (string raw in names) {
                string name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;

                GameObject prefab = Resources.Load<GameObject>(VegetationPrefabPath + name);
                if (prefab == null) {
                    Debug.LogWarning($"TreeLoader: vegetation prefab not found at Resources/{VegetationPrefabPath}{name}");
                    continue;
                }
                float density = WorldLoadingController.GetParamFloat($"vegetation/{name}/density", 0f);
                _vegetationEntries.Add(new VegetationEntry { prefab = prefab, density = density });
            }
            Debug.Log($"TreeLoader: loaded {_vegetationEntries.Count} vegetation prefabs from config");
        }
        else
        {
            Debug.LogWarning("TreeLoader: no vegetation prefabs configured");
        }

        if (_vegetationEntries.Count == 0 && treePrefab != null) {
            Debug.LogWarning("TreeLoader: no vegetation prefabs configured, falling back to treePrefab with density param");
            // Backward compat: fall back to SerializeField treePrefab + old density key
            _density = WorldLoadingController.GetParamFloat("tree_generation/density", _density);
            _vegetationEntries.Add(new VegetationEntry { prefab = treePrefab, density = _density });
        }
    }

    // --- WorldDataProvider ---

    public override void GenerateChunk(int cx, int cz, int lod) {
        if (lod != 0) return;
        if(!paramsInitialized) {
            LoadParams();
            paramsInitialized = true;
        }

        Vector2Int chunkID = new Vector2Int(cx, cz);

        if (_generatedChunks.Contains(chunkID)) {
            if (_chunkObjects.TryGetValue(chunkID, out GameObject chunkObj))
                chunkObj.SetActive(true);
            return;
        }

        GenerateChunk(chunkID);
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        if (lod != 0) return;

        Vector2Int chunkID = new Vector2Int(cx, cz);
        if (_chunkObjects.TryGetValue(chunkID, out GameObject obj))
            obj.SetActive(false);
    }

    public override void Clear() {
        foreach (var kvp in _chunkObjects)
            WorldGenObjects.DestroyHidden(kvp.Value);
        _chunkObjects.Clear();
        _generatedChunks.Clear();
        _clearZones.Clear();
        paramsInitialized = false;
    }

    // --- Generation ---

    private void GenerateChunk(Vector2Int chunkID) {
        float originX = chunkID.x * _chunkWidth;
        float originZ = chunkID.y * _chunkWidth;

        // collect WorldGen-layer colliders overlapping this chunk
        Vector3 chunkCenter = new Vector3(originX + _chunkWidth * 0.5f, 0f, originZ + _chunkWidth * 0.5f);
        Collider[] blockers = Physics.OverlapBox(
            chunkCenter,
            new Vector3(_chunkWidth * 0.5f, 1000f, _chunkWidth * 0.5f),
            Quaternion.identity,
            _worldGenLayer
        );

        // build VegetationModification lookup: only blockers whose parent WorldStructure
        // has a VegetationModification component affect tree placement.
        // Blockers without it are ignored (e.g. city footprint won't suppress trees).
        var vegMods = new Dictionary<Collider, VegetationModification>();
        foreach (var col in blockers) {
            WorldStructure ws = col.GetComponentInParent<WorldStructure>();
            if (ws == null) continue;
            VegetationModification vm = ws.GetComponent<VegetationModification>();
            if (vm != null) vegMods[col] = vm;
        }

        // create chunk parent
        GameObject chunkObj = new GameObject($"TreeChunk_{chunkID.x}_{chunkID.y}");
        chunkObj.transform.SetParent(transform);
        _chunkObjects[chunkID] = chunkObj;
        _generatedChunks.Add(chunkID);

        int baseChunkSeed = _treeSeed ^ (chunkID.x * 1000003) ^ (chunkID.y * 999983);

        for (int vi = 0; vi < _vegetationEntries.Count; vi++) {
            VegetationEntry entry = _vegetationEntries[vi];

            // each prefab type gets a slightly different seed for independent placement
            int chunkSeed = baseChunkSeed ^ (vi * 7919);
            System.Random rng = new System.Random(chunkSeed);

            int count = Mathf.RoundToInt(entry.density * _chunkWidth * _chunkWidth);
            if (verbose) Debug.Log($"TreeLoader: chunk ({chunkID.x},{chunkID.y}) prefab={entry.prefab.name} seed={chunkSeed} attempting {count} placements, {blockers.Length} blockers ({vegMods.Count} with VegMod)");

            for (int i = 0; i < count; i++) {
                float x = originX + (float)rng.NextDouble() * _chunkWidth;
                float z = originZ + (float)rng.NextDouble() * _chunkWidth;

                if (!_structureFilter.Accepts(new Vector2(x, z))) continue;
                if (ShouldSkipTree(new Vector2(x, z), blockers, vegMods, rng)) continue;

                float y = WorldServices.Get<IHeightProvider>().GetTerrainHeight(x, z);
                GameObject obj = Instantiate(entry.prefab, new Vector3(x, y, z), Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
                obj.transform.SetParent(chunkObj.transform);
            }

            // extra vegetation for IncreaseDensity zones
            PlaceIncreasedDensityVegetation(chunkID, chunkSeed, originX, originZ, vegMods, chunkObj, entry);
        }
    }

    // ─────────────────────────────────────────────
    //  Vegetation-aware tree skip
    // ─────────────────────────────────────────────

    /// <summary>
    /// Returns true if the tree at <paramref name="point"/> should be suppressed,
    /// based on VegetationModification on overlapping blockers.
    /// May consume an RNG draw for DecreaseDensity zones.
    /// </summary>
    private bool ShouldSkipTree(
        Vector2 point,
        Collider[] blockers,
        Dictionary<Collider, VegetationModification> vegMods,
        System.Random rng)
    {
        // Suppress trees inside AgentLoader-registered clear zones (e.g. city_outskirts spawn).
        foreach (ClearZone zone in _clearZones) {
            if ((point - zone.center).sqrMagnitude <= zone.radius * zone.radius)
                return true;
        }

        foreach (var col in blockers) {
            if (!IsPointInCollider(point, col)) continue;

            if (!vegMods.TryGetValue(col, out VegetationModification vm)) continue;

            switch (vm.mode) {
                case VegetationModification.Mode.Remove:
                    return true;

                case VegetationModification.Mode.DecreaseDensity:
                    // value = fraction of trees to keep (0 = none, 1 = all)
                    // consume rng regardless to keep downstream sequence consistent
                    if ((float)rng.NextDouble() > vm.value) return true;
                    break;

                // IncreaseDensity: always place; extras added in separate pass
            }
        }
        return false;
    }

    // ─────────────────────────────────────────────
    //  IncreaseDensity extra-tree pass
    // ─────────────────────────────────────────────

    private void PlaceIncreasedDensityVegetation(
        Vector2Int chunkID,
        int chunkSeed,
        float originX,
        float originZ,
        Dictionary<Collider, VegetationModification> vegMods,
        GameObject chunkObj,
        VegetationEntry entry)
    {
        // deduplicate: one extra pass per WorldStructure, not per collider
        var processed = new HashSet<WorldStructure>();

        foreach (var kvp in vegMods) {
            if (kvp.Value.mode != VegetationModification.Mode.IncreaseDensity) continue;

            WorldStructure ws = kvp.Key.GetComponentInParent<WorldStructure>();
            if (ws == null || !processed.Add(ws)) continue;

            Vector2 center = ws.GetCenter2D();
            Vector2 size   = ws.GetSize();
            float   area   = size.x * size.y;

            int extraCount = Mathf.RoundToInt(entry.density * area * kvp.Value.value);
            if (extraCount <= 0) continue;

            // zone-specific seed: independent of main chunk rng
            int zoneSeed = chunkSeed ^ (int)(center.x * 73856093) ^ (int)(center.y * 19349663);
            System.Random zoneRng = new System.Random(zoneSeed);

            // rotate local OBB coords → world
            float rotRad = ws.GetRotationCCW() * Mathf.Deg2Rad;
            float cos = Mathf.Cos(rotRad), sin = Mathf.Sin(rotRad);

            for (int i = 0; i < extraCount; i++) {
                float lx = ((float)zoneRng.NextDouble() - 0.5f) * size.x;
                float lz = ((float)zoneRng.NextDouble() - 0.5f) * size.y;

                // OBB local → world
                float wx = center.x + lx * cos - lz * sin;
                float wz = center.y + lx * sin + lz * cos;

                // only place within this chunk's bounds
                if (wx < originX || wx > originX + _chunkWidth) continue;
                if (wz < originZ || wz > originZ + _chunkWidth) continue;
                if (!_structureFilter.Accepts(new Vector2(wx, wz))) continue;

                float wy = WorldServices.Get<IHeightProvider>().GetTerrainHeight(wx, wz);
                GameObject obj = Instantiate(
                    entry.prefab,
                    new Vector3(wx, wy, wz),
                    Quaternion.Euler(0f, (float)zoneRng.NextDouble() * 360f, 0f));
                obj.transform.SetParent(chunkObj.transform);
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Point-in-collider geometry helpers
    // ─────────────────────────────────────────────

    private bool IsPointInCollider(Vector2 point, Collider col) {
        if (col is BoxCollider box)     return IsPointInBox(point, box);
        if (col is CapsuleCollider cap) return IsPointInCapsule(point, cap);
        return false;
    }

    private bool IsPointInBox(Vector2 point, BoxCollider box) {
        Vector3 center3 = box.transform.TransformPoint(box.center);
        Vector2 center  = new Vector2(center3.x, center3.z);
        Vector2 size    = new Vector2(
            box.size.x * box.transform.lossyScale.x,
            box.size.z * box.transform.lossyScale.z
        );
        float yRot = -box.transform.eulerAngles.y;

        Vector2 delta = point - center;
        float rad = -yRot * Mathf.Deg2Rad;
        Vector2 local = new Vector2(
            delta.x * Mathf.Cos(rad) - delta.y * Mathf.Sin(rad),
            delta.x * Mathf.Sin(rad) + delta.y * Mathf.Cos(rad)
        );
        return Mathf.Abs(local.x) <= size.x * 0.5f &&
               Mathf.Abs(local.y) <= size.y * 0.5f;
    }

    private bool IsPointInCapsule(Vector2 point, CapsuleCollider cap) {
        Vector3 center3 = cap.transform.TransformPoint(cap.center);
        Vector2 center  = new Vector2(center3.x, center3.z);
        float radius    = cap.radius * Mathf.Max(cap.transform.lossyScale.x, cap.transform.lossyScale.z);
        return (point - center).sqrMagnitude <= radius * radius;
    }
}
