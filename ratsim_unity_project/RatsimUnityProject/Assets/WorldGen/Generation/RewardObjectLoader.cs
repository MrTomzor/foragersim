using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// WorldStructureProvider that spawns reward objects via two parallel modes:
///
/// 1. Uniform mode: spawns reward objects randomly across the world at terrain height,
///    per-chunk (like TreeLoader). Controlled by reward_objects/uniform_density.
///
/// 2. Structure mode: spawns reward objects at rewardSpawnPositions inside structures
///    whose type is listed in reward_objects/allowed_structures. Each structure type has a
///    per-position spawn probability and a skip probability (chance of no rewards at all
///    in that structure).
///
/// Both modes can run simultaneously. Spawned objects use a single configurable prefab.
///
/// Tracks all spawned rewards and publishes a BoolMessage on /all_rewards_collected
/// when every reward in every visitable chunk and structure has been collected.
///
/// Config params (all under "reward_objects/" prefix):
///   prefab_name                          -- prefab in Resources/WorldGen/RewardObjectPrefabs/ (default "reward_obj1")
///   uniform_density                      -- objects per unit^2 for uniform mode (default 0; 0 = disabled)
///   boundary_buffer                      -- units inset from world boundary for uniform spawning (default 5)
///   uniform_constrain_to_rooms           -- 0/1; when 1 and an IRoomProvider is registered, uniform
///                                            spawns are rejected outside room footprints (default 0)
///   allowed_structures                   -- comma list of structure types for structure mode (default ""; empty = disabled)
///   {structure_type}/spawn_probability   -- 0.0-1.0 per spawn position (default 1). Ignored when min/max is set.
///   {structure_type}/skip_probability    -- 0.0-1.0 chance to skip the entire structure (default 0)
///   {structure_type}/min_per_structure   -- int. Minimum reward count per structure (default -1 = unset).
///                                            When either min or max is set the loader uses count mode:
///                                            picks an integer N in [min, max] and fills N random spawn
///                                            slots in the structure. spawn_probability is ignored.
///                                            Both min and max must be in [0, childCount]; otherwise the
///                                            loader emits an error via WorldGenStatus and skips the structure.
///   {structure_type}/max_per_structure   -- int. Maximum reward count per structure (default -1 = unset).
///   signal_source/enable_probability     -- 0.0-1.0 chance a spawned reward gets an enabled
///                                           SignalSource component (default 0 = never)
///   signal_source/channel                -- channel name for attached sources (default "reward")
///   signal_source/strength               -- peak signal strength (default 1)
///   signal_source/range                  -- signal range in world units (default 20)
///   signal_source/falloff                -- "linear" or "exponential" (default "linear")
/// </summary>
public class RewardObjectLoader : WorldStructureProvider {

    public static RewardObjectLoader Instance { get; private set; }

    public override WorldDataType[] Provides => new[] { WorldDataType.Rewards };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.StructureEvents };

    public bool verbose = true;
    public bool checkSpawnCollisions = true;

    private const string PrefabFolder = "WorldGen/RewardObjectPrefabs/";
    private const string ContainerName = "_RewardLoaderContent";
    private const string AllCollectedTopic = "/all_rewards_collected";

    // ─────────────────────────────────────────────
    //  Editor-configurable defaults (overridden by episode params)
    // ─────────────────────────────────────────────

    [Header("General")]
    public string prefabName = "reward_obj1";

    [Header("Uniform Mode")]
    public float uniformDensity = 0f;
    public float boundaryBuffer = 5f;
    public bool uniformConstrainToRooms = false;

    [Header("Structure Mode")]
    public string allowedStructures = "";

    [Header("Signal Source")]
    public float signalEnableProbability = 0f;
    public string signalChannel = "reward";
    public float signalStrength = 1f;
    public float signalRange = 20f;
    public string signalFalloff = "linear";

    // ─────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────

    private struct StructureEntry {
        public string type;
        public StructureSlotDistribution.Spec spec;
    }

    private GameObject _rewardPrefab;
    private List<StructureEntry> _structureEntries = new List<StructureEntry>();
    private int _rewardSeed;
    private bool _paramsLoaded;
    private float _chunkWidth;

    // World boundary rect (for filtering spawns and determining visitable chunks)
    private bool _hasBoundary;
    private float _boundaryMinX, _boundaryMaxX;
    private float _boundaryMinZ, _boundaryMaxZ;

    // Uniform mode: chunk-based spawning (like TreeLoader)
    private Dictionary<Vector2Int, GameObject> _chunkObjects = new Dictionary<Vector2Int, GameObject>();
    private HashSet<Vector2Int> _generatedChunks = new HashSet<Vector2Int>();

    // ── Reward tracking ──
    // Structure-mode rewards are destroyed when their structure unloads and re-instantiated
    // when it loads again (same slots: the slot rng is seeded per structure). Collected slots
    // are remembered per structure so a pickup stays gone for the rest of the episode, and a
    // structure's rewards count towards _totalSpawned only on its first spawn. A slot is
    // identified by its world position (cm grid), not its index, so the key survives LOD
    // levels that list their rewardSpawnPositions differently.
    private readonly Dictionary<Pickupable, (int structureId, Vector3Int slot)> _slotOf =
        new Dictionary<Pickupable, (int, Vector3Int)>();
    private readonly Dictionary<int, HashSet<Vector3Int>> _collectedSlots = new Dictionary<int, HashSet<Vector3Int>>();
    private readonly HashSet<int> _countedStructures = new HashSet<int>();
    private int _totalSpawned;
    private int _totalCollected;
    private bool _allCollectedPublished;

    // ─────────────────────────────────────────────
    //  Singleton
    // ─────────────────────────────────────────────

    protected override void OnEnable() {
        base.OnEnable();
        Instance = this;
    }

    protected override void OnDisable() {
        base.OnDisable();
        if (Instance == this) Instance = null;
    }

    // ─────────────────────────────────────────────
    //  Param loading
    // ─────────────────────────────────────────────

    private void LoadParams() {
        _rewardSeed = WorldLoadingController.GetDerivedSeed("reward");
        _chunkWidth = WorldLoadingController.GetChunkWidth();

        prefabName = WorldLoadingController.GetParamString("reward_objects/prefab_name", prefabName);
        _rewardPrefab = Resources.Load<GameObject>(PrefabFolder + prefabName);
        if (_rewardPrefab == null)
            Debug.LogWarning($"RewardObjectLoader: prefab not found at Resources/{PrefabFolder}{prefabName}");

        uniformDensity = WorldLoadingController.GetParamFloat("reward_objects/uniform_density", uniformDensity);
        boundaryBuffer = WorldLoadingController.GetParamFloat("reward_objects/boundary_buffer", boundaryBuffer);
        uniformConstrainToRooms = WorldLoadingController.GetParamInt("reward_objects/uniform_constrain_to_rooms", uniformConstrainToRooms ? 1 : 0) != 0;

        // Read world boundary params
        string boundaryType = WorldLoadingController.GetParamString("world_bounds/boundary_type", "none");
        _hasBoundary = boundaryType.Equals("visible_wall", System.StringComparison.OrdinalIgnoreCase);
        if (_hasBoundary) {
            float worldW = WorldLoadingController.GetParamFloat("world_bounds/width", 100f);
            float worldH = WorldLoadingController.GetParamFloat("world_bounds/height", 100f);
            _boundaryMinX = -worldW * 0.5f;
            _boundaryMaxX =  worldW * 0.5f;
            _boundaryMinZ = -worldH * 0.5f;
            _boundaryMaxZ =  worldH * 0.5f;
        }

        // Signal source overrides
        signalEnableProbability = WorldLoadingController.GetParamFloat("reward_objects/signal_source/enable_probability", signalEnableProbability);
        signalChannel            = WorldLoadingController.GetParamString("reward_objects/signal_source/channel", signalChannel);
        signalStrength           = WorldLoadingController.GetParamFloat("reward_objects/signal_source/strength", signalStrength);
        signalRange              = WorldLoadingController.GetParamFloat("reward_objects/signal_source/range", signalRange);
        signalFalloff            = WorldLoadingController.GetParamString("reward_objects/signal_source/falloff", signalFalloff);

        // Structure entries
        _structureEntries.Clear();
        allowedStructures = WorldLoadingController.GetParamString("reward_objects/allowed_structures", allowedStructures);
        if (!string.IsNullOrEmpty(allowedStructures)) {
            foreach (string raw in allowedStructures.Split(',')) {
                string type = raw.Trim();
                if (string.IsNullOrEmpty(type)) continue;
                var spec = new StructureSlotDistribution.Spec {
                    spawnProbability = WorldLoadingController.GetParamFloat($"reward_objects/{type}/spawn_probability", 1f),
                    skipProbability  = WorldLoadingController.GetParamFloat($"reward_objects/{type}/skip_probability", 0f),
                    minPerStructure  = WorldLoadingController.GetParamInt($"reward_objects/{type}/min_per_structure", -1),
                    maxPerStructure  = WorldLoadingController.GetParamInt($"reward_objects/{type}/max_per_structure", -1),
                };
                // Normalise count-mode (mirror min/max, validate min <= max). Per-structure
                // validation against slot count happens later in SelectSlots.
                StructureSlotDistribution.NormalizeSpec(ref spec, "RewardObjectLoader", $"structure '{type}'");
                _structureEntries.Add(new StructureEntry { type = type, spec = spec });
            }
        }

        _paramsLoaded = true;

        Debug.Log($"RewardObjectLoader: params loaded — " +
            $"prefab={(prefabName)}, " +
            $"uniformDensity={uniformDensity:F4}, " +
            $"boundaryBuffer={boundaryBuffer:F1}, " +
            $"hasBoundary={_hasBoundary}, " +
            $"structureTypes={_structureEntries.Count}");
        if (verbose) {
            foreach (var e in _structureEntries) {
                if (e.spec.UseCountMode)
                    Debug.Log($"  structure '{e.type}': count mode min={e.spec.minPerStructure} max={e.spec.maxPerStructure}, skipProb={e.spec.skipProbability:F2}");
                else
                    Debug.Log($"  structure '{e.type}': spawnProb={e.spec.spawnProbability:F2}, skipProb={e.spec.skipProbability:F2}");
            }
        }
    }

    // ─────────────────────────────────────────────
    //  Boundary helpers
    // ─────────────────────────────────────────────

    /// <summary>Returns true if (x, z) is inside the world boundary with the buffer inset.</summary>
    private bool IsInsideBoundaryBuffer(float x, float z) {
        if (!_hasBoundary) return true; // no boundary → everything allowed
        return x >= _boundaryMinX + boundaryBuffer &&
               x <= _boundaryMaxX - boundaryBuffer &&
               z >= _boundaryMinZ + boundaryBuffer &&
               z <= _boundaryMaxZ - boundaryBuffer;
    }

    /// <summary>Returns true if the chunk overlaps the world boundary rect at all.</summary>
    private bool IsChunkVisitable(Vector2Int chunkID) {
        if (!_hasBoundary) return false; // no boundary → can't determine visitability
        float cMinX = chunkID.x * _chunkWidth;
        float cMaxX = cMinX + _chunkWidth;
        float cMinZ = chunkID.y * _chunkWidth;
        float cMaxZ = cMinZ + _chunkWidth;
        // AABB overlap test
        return cMaxX > _boundaryMinX && cMinX < _boundaryMaxX &&
               cMaxZ > _boundaryMinZ && cMinZ < _boundaryMaxZ;
    }

    // ─────────────────────────────────────────────
    //  Pickup notification (called by Pickupable)
    // ─────────────────────────────────────────────

    /// <summary>Called by Pickupable when a reward object is collected.</summary>
    public void NotifyRewardCollected(Pickupable p = null) {
        if (p != null && _slotOf.TryGetValue(p, out var key)) {
            _slotOf.Remove(p);
            if (!_collectedSlots.TryGetValue(key.structureId, out HashSet<Vector3Int> set))
                _collectedSlots[key.structureId] = set = new HashSet<Vector3Int>();
            set.Add(key.slot);
        }
        _totalCollected++;
        if (verbose)
            Debug.Log($"RewardObjectLoader: reward collected ({_totalCollected}/{_totalSpawned})");
        CheckAllCollected();
    }

    private void CheckAllCollected() {
        if (_allCollectedPublished) return;
        if (!_hasBoundary) return;
        if (_totalSpawned <= 0) return;

        // Check that all visitable chunks have been generated (loaded at LOD0)
        if (!AllVisitableChunksGenerated()) return;

        if (_totalCollected >= _totalSpawned) {
            _allCollectedPublished = true;
            RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
            conn.Publish(AllCollectedTopic, new BoolMessage { data = true });
            Debug.Log($"RewardObjectLoader: ALL REWARDS COLLECTED ({_totalCollected}/{_totalSpawned}) — published on {AllCollectedTopic}");
        }
    }

    /// <summary>
    /// Returns true if every chunk that overlaps the world boundary has been generated at LOD0.
    /// This ensures we know the total spawn count before declaring "all collected".
    /// </summary>
    private bool AllVisitableChunksGenerated() {
        // Compute the range of chunks that could overlap the boundary
        int minCX = Mathf.FloorToInt(_boundaryMinX / _chunkWidth);
        int maxCX = Mathf.FloorToInt(_boundaryMaxX / _chunkWidth);
        int minCZ = Mathf.FloorToInt(_boundaryMinZ / _chunkWidth);
        int maxCZ = Mathf.FloorToInt(_boundaryMaxZ / _chunkWidth);

        for (int cx = minCX; cx <= maxCX; cx++) {
            for (int cz = minCZ; cz <= maxCZ; cz++) {
                Vector2Int id = new Vector2Int(cx, cz);
                if (IsChunkVisitable(id) && !_generatedChunks.Contains(id))
                    return false;
            }
        }
        return true;
    }

    // ─────────────────────────────────────────────
    //  Chunk events (uniform mode)
    // ─────────────────────────────────────────────

    public override void GenerateChunk(int cx, int cz, int lod) {
        if (lod != 0) return;
        if (!_paramsLoaded) LoadParams();
        if (_rewardPrefab == null || uniformDensity <= 0f) return;

        Vector2Int chunkID = new Vector2Int(cx, cz);

        if (_generatedChunks.Contains(chunkID)) {
            if (_chunkObjects.TryGetValue(chunkID, out GameObject chunkObj))
                chunkObj.SetActive(true);
            return;
        }

        GenerateUniformChunk(chunkID);

        // A new chunk was generated — recheck all-collected (we now know more of the world)
        CheckAllCollected();
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        if (lod != 0) return;

        Vector2Int chunkID = new Vector2Int(cx, cz);
        if (_chunkObjects.TryGetValue(chunkID, out GameObject obj))
            obj.SetActive(false);
    }

    private void GenerateUniformChunk(Vector2Int chunkID) {
        float originX = chunkID.x * _chunkWidth;
        float originZ = chunkID.y * _chunkWidth;

        int chunkSeed = _rewardSeed ^ (chunkID.x * 1000003) ^ (chunkID.y * 999983);
        System.Random rng = new System.Random(chunkSeed);

        int count = Mathf.RoundToInt(uniformDensity * _chunkWidth * _chunkWidth);

        GameObject chunkObj = new GameObject($"RewardChunk_{chunkID.x}_{chunkID.y}");
        chunkObj.transform.SetParent(transform);
        _chunkObjects[chunkID] = chunkObj;
        _generatedChunks.Add(chunkID);

        // Get reward prefab size (assume it has box collider) to avoid spawning intersecting the ground
        float prefabWidth = 1f;
        float prefabHeight = 1f;
        if (_rewardPrefab.TryGetComponent<BoxCollider>(out BoxCollider col)) {
            prefabWidth = Mathf.Max(col.size.x, col.size.z);
            prefabHeight = col.size.y;
        }
        float spawnHeightOffset = prefabHeight * 0.6f;

        int placed = 0;
        for (int i = 0; i < count; i++) {
            float x = originX + (float)rng.NextDouble() * _chunkWidth;
            float z = originZ + (float)rng.NextDouble() * _chunkWidth;

            // Skip positions outside the boundary buffer zone
            if (!IsInsideBoundaryBuffer(x, z)) continue;

            // Optional: constrain uniform spawns to rooms when a room provider is available.
            // Rejection-sampled, so density inside rooms matches `uniformDensity`; outside-room
            // space just produces skips (effective density drops by room coverage fraction).
            if (uniformConstrainToRooms
                    && WorldServices.Has<IRoomProvider>()
                    && !WorldServices.Get<IRoomProvider>().IsInRoom(x, z))
                continue;

            float y = WorldServices.Get<IHeightProvider>().GetTerrainHeight(x, z);

            Quaternion rot = Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f);

            // Check if position is occupied (slightly above ground height to avoid checking against terrain colliders)
            if(checkSpawnCollisions){
                if (Physics.CheckBox(new Vector3(x, y + spawnHeightOffset, z), Vector3.one * prefabWidth * 0.5f, rot, LayerMask.GetMask("Default"), QueryTriggerInteraction.Ignore))
                    continue;
            }

            GameObject go = Instantiate(_rewardPrefab, new Vector3(x, y, z),
                rot, chunkObj.transform);
            MaybeAttachSignalSource(go, rng);
            placed++;
        }

        _totalSpawned += placed;

        if (verbose && placed > 0)
            Debug.Log($"RewardObjectLoader: uniform chunk ({chunkID.x},{chunkID.y}) placed {placed} (total spawned: {_totalSpawned})");
    }

    // ─────────────────────────────────────────────
    //  Structure events (structure mode)
    // ─────────────────────────────────────────────

    public override void OnWorldStructureLoaded(WorldStructure s, int lod) {
        if (!_paramsLoaded) LoadParams();
        if (_rewardPrefab == null || _structureEntries.Count == 0) return;

        StructureEntry? entry = FindEntry(s.structureType);
        if (!entry.HasValue) return;

        // Destroy previous container (handles LOD change / double-fire).
        DestroyContainer(s);

        Transform lodRoot = s.transform.Find($"LOD{lod}");
        if (lodRoot == null) return;

        Transform group = lodRoot.Find("rewardSpawnPositions");
        if (group == null || group.childCount == 0) {
            if (verbose) Debug.Log($"RewardObjectLoader: '{s.name}' LOD{lod} has no rewardSpawnPositions");
            return;
        }

        // Shared selection (skip + count/probability modes). The same rng continues
        // into MaybeAttachSignalSource below so the legacy draw sequence is preserved.
        System.Random rng = StructureSlotDistribution.MakeRng(s, _rewardSeed);
        List<Transform> slots = StructureSlotDistribution.SelectSlots(
            group, entry.Value.spec, rng, "RewardObjectLoader",
            $"'{s.name}' (type '{entry.Value.type}')");
        if (slots.Count == 0) return;

        GameObject container = new GameObject(ContainerName);
        container.transform.SetParent(s.transform, false);
        container.transform.SetPositionAndRotation(s.transform.position, s.transform.rotation);

        int structureId = s.DeterministicId;
        _collectedSlots.TryGetValue(structureId, out HashSet<Vector3Int> collected);

        int placed = 0;
        foreach (Transform spawnPoint in slots) {
            Vector3Int slot = Vector3Int.RoundToInt(spawnPoint.position * 100f);
            if (collected != null && collected.Contains(slot)) {
                // Already picked up this episode. Burn the draw MaybeAttachSignalSource would
                // make so the remaining rewards keep their signal sources on a reload.
                if (signalEnableProbability > 0f) rng.NextDouble();
                continue;
            }
            GameObject go = Instantiate(_rewardPrefab, spawnPoint.position, spawnPoint.rotation, container.transform);
            MaybeAttachSignalSource(go, rng);
            Pickupable p = go.GetComponent<Pickupable>();
            if (p != null) _slotOf[p] = (structureId, slot);
            placed++;
        }

        if (_countedStructures.Add(structureId)) _totalSpawned += slots.Count;

        if (verbose)
            Debug.Log($"RewardObjectLoader: '{s.name}' LOD{lod} — " +
                $"{placed}/{group.childCount} rewards placed (total spawned: {_totalSpawned})");

        // Remove empty container
        if (placed == 0)
            DestroyImmediate(container);
    }

    public override void OnWorldStructureUnloaded(WorldStructure s, int lod) {
        if (_structureEntries.Count == 0) return;
        if (!FindEntry(s.structureType).HasValue) return;
        DestroyContainer(s);
    }

    public override void Clear() {
        foreach (var kvp in _chunkObjects)
            WorldGenObjects.DestroyHidden(kvp.Value);
        _chunkObjects.Clear();
        _generatedChunks.Clear();
        // Structure containers are children of structure GOs — destroyed with them.
        _paramsLoaded = false;
        _slotOf.Clear();
        _collectedSlots.Clear();
        _countedStructures.Clear();
        _totalSpawned = 0;
        _totalCollected = 0;
        _allCollectedPublished = false;
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private StructureEntry? FindEntry(string structureType) {
        for (int i = 0; i < _structureEntries.Count; i++) {
            if (structureType.StartsWith(_structureEntries[i].type, System.StringComparison.OrdinalIgnoreCase))
                return _structureEntries[i];
        }
        return null;
    }

    // Per-structure RNG seeding moved to StructureSlotDistribution.MakeRng (shared with WellLoader).

    /// <summary>
    /// With probability <see cref="signalEnableProbability"/>, attach an enabled
    /// <see cref="SignalSource"/> to the spawned reward GameObject using the current
    /// signal_source/* params. Skipped if probability is 0.
    /// </summary>
    private void MaybeAttachSignalSource(GameObject go, System.Random rng) {
        if (signalEnableProbability <= 0f) return;
        if ((float)rng.NextDouble() >= signalEnableProbability) return;

        SignalSource src = go.GetComponent<SignalSource>();
        if (src == null) src = go.AddComponent<SignalSource>();

        src.channel  = signalChannel;
        src.strength = signalStrength;
        src.range    = signalRange;
        src.falloff  = signalFalloff.Equals("exponential", System.StringComparison.OrdinalIgnoreCase)
            ? SignalSource.FalloffMode.Exponential
            : SignalSource.FalloffMode.Linear;
        src.enabled  = true;
    }

    /// <summary>
    /// Every reward pickup this loader currently owns: uniform-mode objects under the live
    /// chunk containers plus structure-mode objects under the container of each registered
    /// structure. Authoritative for snapshots (WorldGenDump) — a scene-wide
    /// FindObjectsByType would also return last episode's objects, which Clear() releases
    /// with the deferred Destroy and which therefore still exist during the same frame.
    /// </summary>
    public List<Pickupable> GetLiveRewards() {
        var result = new List<Pickupable>();
        foreach (var kvp in _chunkObjects)
            if (kvp.Value != null)
                result.AddRange(kvp.Value.GetComponentsInChildren<Pickupable>(false));
        foreach (WorldStructure s in WorldData.GetStructures()) {
            if (s == null) continue;
            Transform c = s.transform.Find(ContainerName);
            if (c != null) result.AddRange(c.GetComponentsInChildren<Pickupable>(false));
        }
        return result;
    }

    private static void DestroyContainer(WorldStructure s) {
        Transform existing = s.transform.Find(ContainerName);
        if (existing != null)
            DestroyImmediate(existing.gameObject);
    }
}
