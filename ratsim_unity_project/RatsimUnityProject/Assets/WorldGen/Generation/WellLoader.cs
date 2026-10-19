using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Spawns the complete well roster eagerly in <see cref="Generate"/> (not
/// chunk-streamed), so the set of wells — their ids and positions — is known and
/// deterministic from reset onward. This is the deliberate difference from
/// <see cref="RewardObjectLoader"/>: wells are persistent, identity-bearing
/// landmarks, so a schedule controller (Phase 3) can address "well 123" directly.
///
/// A well is instantiated from the <c>wells/prefab_name</c> prefab (in
/// Resources/WorldGen/WellPrefabs/) and parented to this loader so it survives
/// host-structure LOD churn; WellLoader adds + configures the WellData + Well logic.
/// The prefab supplies the visual, its (non-WorldGen) collider and 'well' semantic
/// label. Anchors come
/// from <c>rewardSpawnPositions</c> in eagerly-generated structures (maze rooms,
/// the well_arena grid, top-level structures with their own slots) via the shared
/// <see cref="StructureSlotDistribution"/> — the SAME selection logic reward
/// scattering uses. Structures CityLoader places eagerly in its Generate (houses or
/// well_pads scattered into maze rooms, e.g. cell_grid cells) are covered too — hence
/// the StructureContent dependency. Chunk-lazy children (houses in cities that only
/// fill when their chunk loads) are out of scope: they don't exist at Generate, so
/// they can't be part of a complete roster.
///
/// Reward is credited by each Well publishing on <c>/reward_pickup</c> (TaskTracker
/// already sums it). In Phase 1 there is no scheduler, so wells are always-armed
/// feeders. Config is under the <c>wells/</c> prefix; <c>wells/enabled</c> defaults
/// off, so existing presets are unaffected until they opt in.
/// </summary>
public class WellLoader : WorldDataProvider, IWellProvider {

    public static WellLoader Instance { get; private set; }

    // Depends on Layout (maze rooms / top-level structures + their slots are created
    // in the layout provider's Generate); StructureContent so CityLoader's eager fill
    // (houses / well_pads scattered into maze rooms) is already registered; Height so
    // wells can snap to terrain.
    public override WorldDataType[] Provides  => new[] { WorldDataType.Wells };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.Layout, WorldDataType.StructureContent };

    [Header("General")]
    public bool verbose = true;
    public bool enabledFlag = false;                 // wells/enabled
    public string prefabName = "well_basic";         // wells/prefab_name → Resources/WorldGen/WellPrefabs/<name>
    public string allowedStructures = "";            // wells/allowed_structures (CSV)
    public string seedKey = "wells";                 // wells/seed_key ("reward" → land on the same slots reward objects would)
    public bool debugGizmo = true;                   // wells/debug_gizmo (editor state gizmo)
    public float roomEdgeBuffer = 0f;                // wells/room_edge_buffer: min distance (world units) from the
                                                     // host structure's footprint edge; keeps wells (and their reward
                                                     // annulus) off maze walls. 0 = off. If a room is too small for
                                                     // the buffer, falls back to the slot(s) nearest the room centre.

    [Header("Physical defaults (per-well; overridable in config)")]
    public float arrivalRadius = 1.0f;
    public int   dispenseDelaySteps = 25;
    public int   cooldownSteps = 50;
    public bool  depleteOnDispense = false;

    [Header("Reward dispensing")]
    public string rewardTopic = "/reward_pickup";
    public int    rewardAmount = 1;
    public string rewardPrefabName = "reward_obj1";  // "" = no reward object
    public float  rewardSpawnMinDist = 0f;           // random spawn annulus around the well
    public float  rewardSpawnMaxDist = 0f;           // both 0 = spawn on the well
    public int    rewardsPerDispense = 1;

    [Header("Cue (abstract 'LED' via SignalSource on the 'cue' channel)")]
    public string cueModeStr = "none";               // wells/cue/mode: none | timed | until_depleted
    public int    cueDurationSteps = 30;             // wells/cue/duration_steps (timed mode)
    public string cueChannel = "cue";                // wells/cue/channel (SectorSignalSensor listens here)
    public float  cueRange = 200f;                    // wells/cue/range (>= arena diagonal = sensable anywhere)
    public float  cueStrength = 1f;                   // wells/cue/strength
    public string cueFalloff = "linear";             // wells/cue/falloff: linear | exponential

    private const string WellPrefabFolder   = "WorldGen/WellPrefabs/";
    private const string RewardPrefabFolder = "WorldGen/RewardObjectPrefabs/";

    private struct StructureEntry {
        public string type;
        public StructureSlotDistribution.Spec spec;
    }

    private readonly List<StructureEntry> _entries = new List<StructureEntry>();
    private readonly List<Well> _wells = new List<Well>();
    private GameObject _wellPrefab;          // null = runtime-built
    private GameObject _rewardPrefab;        // null = no reward object dispensed
    private int _wellSeed;
    private bool _timerRegistered;
    private Transform _agent;                // cached agent transform (found via PickupableCollector)

    // ── IWellProvider ──
    public IReadOnlyList<Well> GetWells() => _wells;
    public Well GetWell(int id) => (id >= 0 && id < _wells.Count) ? _wells[id] : null;
    public int WellCount => _wells.Count;

    // ─────────────────────────────────────────────

    protected override void OnEnable() {
        base.OnEnable();
        Instance = this;
        WorldServices.Register<IWellProvider>(this);
    }

    protected override void OnDisable() {
        base.OnDisable();
        if (Instance == this) Instance = null;
    }

    private void LoadParams() {
        seedKey   = WorldLoadingController.GetParamString("wells/seed_key", seedKey);
        _wellSeed = WorldLoadingController.GetDerivedSeed(seedKey);

        enabledFlag       = WorldLoadingController.GetParamInt("wells/enabled", enabledFlag ? 1 : 0) != 0;
        debugGizmo        = WorldLoadingController.GetParamInt("wells/debug_gizmo", debugGizmo ? 1 : 0) != 0;
        prefabName        = WorldLoadingController.GetParamString("wells/prefab_name", prefabName);
        allowedStructures = WorldLoadingController.GetParamString("wells/allowed_structures", allowedStructures);
        roomEdgeBuffer    = WorldLoadingController.GetParamFloat("wells/room_edge_buffer", roomEdgeBuffer);

        arrivalRadius      = WorldLoadingController.GetParamFloat("wells/arrival_radius", arrivalRadius);
        dispenseDelaySteps = WorldLoadingController.GetParamInt("wells/dispense_delay_steps", dispenseDelaySteps);
        cooldownSteps      = WorldLoadingController.GetParamInt("wells/cooldown_steps", cooldownSteps);
        depleteOnDispense  = WorldLoadingController.GetParamInt("wells/deplete_on_dispense", depleteOnDispense ? 1 : 0) != 0;

        rewardTopic        = WorldLoadingController.GetParamString("wells/reward_topic", rewardTopic);
        rewardAmount       = WorldLoadingController.GetParamInt("wells/reward_amount", rewardAmount);
        rewardPrefabName   = WorldLoadingController.GetParamString("wells/reward_prefab", rewardPrefabName);
        rewardSpawnMinDist = WorldLoadingController.GetParamFloat("wells/reward_spawn_min_dist", rewardSpawnMinDist);
        rewardSpawnMaxDist = WorldLoadingController.GetParamFloat("wells/reward_spawn_max_dist", rewardSpawnMaxDist);
        rewardsPerDispense = WorldLoadingController.GetParamInt("wells/rewards_per_dispense", rewardsPerDispense);

        cueModeStr       = WorldLoadingController.GetParamString("wells/cue/mode", cueModeStr);
        cueDurationSteps = WorldLoadingController.GetParamInt("wells/cue/duration_steps", cueDurationSteps);
        cueChannel       = WorldLoadingController.GetParamString("wells/cue/channel", cueChannel);
        cueRange         = WorldLoadingController.GetParamFloat("wells/cue/range", cueRange);
        cueStrength      = WorldLoadingController.GetParamFloat("wells/cue/strength", cueStrength);
        cueFalloff       = WorldLoadingController.GetParamString("wells/cue/falloff", cueFalloff);

        _wellPrefab = string.IsNullOrEmpty(prefabName)
            ? null
            : Resources.Load<GameObject>(WellPrefabFolder + prefabName);

        _rewardPrefab = string.IsNullOrEmpty(rewardPrefabName)
            ? null
            : Resources.Load<GameObject>(RewardPrefabFolder + rewardPrefabName);

        // Structure entries (mirror reward_objects/* param names).
        _entries.Clear();
        if (!string.IsNullOrEmpty(allowedStructures)) {
            foreach (string raw in allowedStructures.Split(',')) {
                string type = raw.Trim();
                if (string.IsNullOrEmpty(type)) continue;
                var spec = new StructureSlotDistribution.Spec {
                    spawnProbability = WorldLoadingController.GetParamFloat($"wells/{type}/spawn_probability", 1f),
                    skipProbability  = WorldLoadingController.GetParamFloat($"wells/{type}/skip_probability", 0f),
                    minPerStructure  = WorldLoadingController.GetParamInt  ($"wells/{type}/min_per_structure", -1),
                    maxPerStructure  = WorldLoadingController.GetParamInt  ($"wells/{type}/max_per_structure", -1),
                };
                StructureSlotDistribution.NormalizeSpec(ref spec, "WellLoader", $"structure '{type}'");
                _entries.Add(new StructureEntry { type = type, spec = spec });
            }
        }
    }

    public override void Generate() {
        LoadParams();
        if (!enabledFlag) {
            if (verbose) Debug.Log("WellLoader: wells/enabled = 0, skipping");
            return;
        }
        if (_wellPrefab == null) {
            WorldGenStatus.Error("WellLoader",
                $"wells/enabled = 1 but well prefab '{prefabName}' not found in Resources/{WellPrefabFolder} — set wells/prefab_name to a prefab there");
            return;
        }
        if (_entries.Count == 0) {
            WorldGenStatus.Warning("WellLoader", "wells/enabled = 1 but wells/allowed_structures is empty — no wells spawned");
            return;
        }

        // Pass 1: collect anchors from a *snapshot* of the current structures
        // (spawning wells registers new WorldStructures, so we must not iterate the
        // live list). Only eager structures are present here — that is the roster.
        var anchors = new List<(WorldStructure host, Transform slot, int slotIndex)>();
        foreach (WorldStructure s in WorldData.GetStructures().ToList()) {
            StructureEntry? entry = FindEntry(s.structureType);
            if (!entry.HasValue) continue;

            Transform group = s.transform.Find("LOD0/rewardSpawnPositions");
            if (group == null || group.childCount == 0) continue;

            System.Random rng = StructureSlotDistribution.MakeRng(s, _wellSeed);
            List<Transform> pool = CollectSlotPool(s, group, entry.Value.spec);
            List<Transform> slots = StructureSlotDistribution.SelectSlots(
                pool, entry.Value.spec, rng, "WellLoader", s.name);
            foreach (Transform slot in slots)
                anchors.Add((s, slot, slot.GetSiblingIndex()));
        }

        // Deterministic roster order: sort by (host id, slot index) so wellId is
        // stable across runs with the same seed, independent of registration order.
        anchors.Sort((a, b) => {
            int c = a.host.DeterministicId.CompareTo(b.host.DeterministicId);
            return c != 0 ? c : a.slotIndex.CompareTo(b.slotIndex);
        });

        // Pass 2: materialise wells.
        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        for (int i = 0; i < anchors.Count; i++) {
            Vector3 pos = anchors[i].slot.position;
            Well well = SpawnWell(i, pos, ParseGridCoord(anchors[i].slot.name));
            if (well != null) _wells.Add(well);
        }

        // Register the per-step tick exactly once (this loader persists across
        // episodes; re-registering each Generate would stack duplicate timers).
        if (!_timerRegistered && conn != null) {
            conn.RegisterTimerDiscrete(TickAll, 1u);
            _timerRegistered = true;
        }

        if (verbose) Debug.Log($"WellLoader: spawned {_wells.Count} wells across {_entries.Count} structure type(s)");
    }

    public override void Clear() {
        foreach (Well w in _wells)
            if (w != null) WorldGenObjects.DestroyHidden(w.gameObject);
        _wells.Clear();
        _agent = null;
    }

    // ─────────────────────────────────────────────
    //  Per-step tick (single timer drives the whole roster)
    // ─────────────────────────────────────────────

    private void TickAll(TimerEvent evt) {
        if (_wells.Count == 0) return;

        if (_agent == null) {
            PickupableCollector collector = Object.FindFirstObjectByType<PickupableCollector>();
            if (collector != null) _agent = collector.transform;
        }
        bool agentKnown = _agent != null;
        Vector3 agentPos = agentKnown ? _agent.position : Vector3.zero;

        for (int i = 0; i < _wells.Count; i++) {
            Well w = _wells[i];
            if (w != null) w.Tick(agentPos, agentKnown);
        }
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private StructureEntry? FindEntry(string structureType) {
        for (int i = 0; i < _entries.Count; i++)
            if (structureType.StartsWith(_entries[i].type, System.StringComparison.OrdinalIgnoreCase))
                return _entries[i];
        return null;
    }

    /// <summary>
    /// The slot pool for one structure: all slots at least <c>wells/room_edge_buffer</c>
    /// from the structure's footprint edge (keeps wells + their reward-spawn annulus off
    /// maze walls). If the room is too small for the buffer to leave enough eligible
    /// slots, falls back to the slot(s) nearest the room centre (loud in verbose mode).
    /// Footprint math is world-axis-aligned — fine for RoomStructureBuilder rooms
    /// (unrotated, unit scale), which is where multi-slot structures come from.
    /// </summary>
    private List<Transform> CollectSlotPool(WorldStructure s, Transform group,
                                            StructureSlotDistribution.Spec spec) {
        var all = new List<Transform>(group.childCount);
        foreach (Transform t in group) all.Add(t);
        if (roomEdgeBuffer <= 0f) return all;

        Vector2 center = s.GetCenter2D();
        Vector2 size = s.GetSize();
        float hx = size.x * 0.5f, hz = size.y * 0.5f;

        var eligible = new List<Transform>(all.Count);
        foreach (Transform t in all) {
            float dx = Mathf.Abs(t.position.x - center.x);
            float dz = Mathf.Abs(t.position.z - center.y);
            if (hx - dx >= roomEdgeBuffer && hz - dz >= roomEdgeBuffer) eligible.Add(t);
        }

        // Enough eligible slots to satisfy the spec (count mode needs min; probability
        // mode just needs any) → use the filtered pool.
        int needed = spec.UseCountMode ? Mathf.Max(1, spec.minPerStructure) : 1;
        if (eligible.Count >= needed) return eligible;

        // Fallback: room too small for the buffer → the `needed` slots nearest the centre.
        all.Sort((a, b) => {
            float da = (a.position.x - center.x) * (a.position.x - center.x)
                     + (a.position.z - center.y) * (a.position.z - center.y);
            float db = (b.position.x - center.x) * (b.position.x - center.x)
                     + (b.position.z - center.y) * (b.position.z - center.y);
            return da.CompareTo(db);
        });
        int take = Mathf.Min(needed, all.Count);
        if (verbose)
            Debug.LogWarning($"WellLoader: '{s.name}' too small for room_edge_buffer={roomEdgeBuffer} " +
                             $"({eligible.Count}/{needed} eligible slots) — falling back to {take} centre-most slot(s)");
        return all.GetRange(0, take);
    }

    /// <summary>Parse "cell_i_j" slot names into a grid coord; (-1,-1) otherwise.</summary>
    private static Vector2Int ParseGridCoord(string slotName) {
        if (string.IsNullOrEmpty(slotName)) return new Vector2Int(-1, -1);
        string[] parts = slotName.Split('_');
        if (parts.Length == 3 && parts[0] == "cell"
            && int.TryParse(parts[1], out int i) && int.TryParse(parts[2], out int j))
            return new Vector2Int(i, j);
        return new Vector2Int(-1, -1);
    }

    private Well SpawnWell(int id, Vector3 pos, Vector2Int gridCoord) {
        // Snap to terrain height so wells sit on the ground on non-flat terrain.
        float y = WorldServices.Has<IHeightProvider>()
            ? WorldServices.Get<IHeightProvider>().GetTerrainHeight(pos.x, pos.z)
            : pos.y;
        Vector3 groundPos = new Vector3(pos.x, y, pos.z);

        // Instantiate the well from wells/prefab_name at the final pose. The prefab
        // supplies the visual, its (non-WorldGen) collider and the 'well' semantic label,
        // and may optionally include a WorldStructure/footprint (e.g. for tree-avoidance)
        // and a "cueLight" child. WellLoader adds + configures the WellData + Well logic.
        GameObject root = Instantiate(_wellPrefab, groundPos, Quaternion.identity, transform);
        root.name = $"well_{id}";
        GameObject cueLight = root.transform.Find("cueLight")?.gameObject;

        WellData data = root.GetComponent<WellData>() ?? root.AddComponent<WellData>();
        data.wellId = id;
        data.gridCoord = gridCoord;
        data.arrivalRadius = arrivalRadius;
        data.dispenseDelaySteps = dispenseDelaySteps;
        data.cooldownSteps = cooldownSteps;
        data.depleteOnDispense = depleteOnDispense;
        data.rewardSpawnMinDist = rewardSpawnMinDist;
        data.rewardSpawnMaxDist = rewardSpawnMaxDist;
        data.rewardsPerDispense = rewardsPerDispense;

        Well well = root.GetComponent<Well>() ?? root.AddComponent<Well>();
        well.data = data;

        // Abstract "LED": a SignalSource on the 'cue' channel that the Well toggles on only
        // while cued. Starts disabled so the SectorSignalSensor ignores it until activated.
        SignalSource cueSignal = root.GetComponent<SignalSource>() ?? root.AddComponent<SignalSource>();
        cueSignal.channel  = cueChannel;
        cueSignal.strength = cueStrength;
        cueSignal.range    = cueRange;
        cueSignal.falloff  = cueFalloff.Equals("exponential", System.StringComparison.OrdinalIgnoreCase)
            ? SignalSource.FalloffMode.Exponential
            : SignalSource.FalloffMode.Linear;
        cueSignal.enabled = false;

        well.Init(cueLight, _rewardPrefab, rewardTopic, rewardAmount, verbose, debugGizmo,
                  cueSignal, ParseCueMode(cueModeStr), cueDurationSteps);
        return well;
    }

    private static Well.CueMode ParseCueMode(string s) {
        if (string.IsNullOrEmpty(s)) return Well.CueMode.None;
        switch (s.Trim().ToLowerInvariant()) {
            case "timed":          return Well.CueMode.Timed;
            case "until_depleted": return Well.CueMode.UntilDepleted;
            case "none":
            default:               return Well.CueMode.None;
        }
    }
}

/// <summary>
/// Cross-provider query for the well roster (WorldServices). Lets a schedule
/// controller, sensor, or visualiser reach the wells without depending on the
/// concrete <see cref="WellLoader"/>.
/// </summary>
public interface IWellProvider {
    IReadOnlyList<Well> GetWells();
    Well GetWell(int id);
    int WellCount { get; }
}
