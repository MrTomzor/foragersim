using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// WorldDataProvider that populates loaded chunks with "chaotic walkers" — capsule
/// NPCs that wander using a deterministic seeded RNG.
///
/// Lifecycle differs from DynamicObjectLoader: walkers are NOT persistent. They are
/// spawned when a chunk first enters view range and destroyed when the chunk fully
/// leaves range. Because each (chunk, walker index) seed is derived from the world
/// seed, revisiting a chunk respawns the same walker at the same position with the
/// same wander sequence — so determinism holds without tracking walker state between
/// unload/reload cycles.
///
/// Spawn density is uniform across the world, optionally restricted to (or kept out of)
/// structure footprints by type — e.g. only the "park" cells of a cell_grid maze. Per-walker properties (speed, whether
/// it avoids agents, spawn position) are sampled from the chunk RNG so each chunk is
/// reproducible independently.
///
/// ── Centralised ticking ──
/// This loader owns a SINGLE discrete timer for the whole walker population and decides
/// which walkers to step each simulation step; individual walkers do not register their
/// own timers. Two reasons:
///
///   * Cost. Stepping a walker is dominated by managed->native interop (the Rigidbody
///     velocity read/write), not by any search or distance test, so the only lever that
///     moves the needle is stepping fewer of them per step. `max_ticks_per_step` is a
///     hard cap on that count: a wrapping cursor sweeps the population round-robin, so
///     per-step cost is bounded no matter how many walkers are loaded, and each walker
///     comes round once every ceil(population / max_ticks_per_step) steps.
///   * Chunk unloads. One timer per walker meant every ClearChunk had to call
///     RoslikeTCPServer.CleanupDestroyedTimersAndSubscribers(), which sweeps the entire
///     timer list with a Unity-null check per entry. With a few hundred walkers that
///     produced a visible stall every time the agent crossed a chunk boundary.
///
/// Correctness does not depend on the schedule being exact. Each walker records the step
/// at which it last ticked and is advanced by the elapsed difference, so a walker that
/// gets skipped (or visited twice) simply sees a larger (or zero) elapsed count and stays
/// consistent. That is what makes the cursor free to use O(1) swap-removes and to ignore
/// ordering entirely.
///
/// Walkers close enough to an agent to react are ticked every step regardless of the
/// interval, so flee/chase latency is unaffected. Finding them does not scan the
/// population: reaction radii are metres and chunks are tens of metres, so only the
/// chunks around each agent are examined. That pass unions over every agent, so it is
/// already correct for multi-agent missions — `RefreshAgentPositions` is the single
/// place that needs to change when the world tracks more than one agent.
///
/// Config params (all under "chaotic_walkers/" prefix):
///   enabled                     -- 1 to enable, 0 to disable (default 0)
///   prefab_name                 -- prefab in Resources/WorldGen/WalkerPrefabs/ (default "walker_capsule")
///   density                     -- walkers per unit^2 (default 0)
///   allowed_structures          -- CSV of structure types walkers may spawn inside (default "" =
///                                  anywhere). StartsWith matching, "*" = any structure.
///   denied_structures           -- CSV of structure types walkers must not spawn inside
///                                  (default ""). Deny wins over allow. Rejected samples are
///                                  dropped, so density inside the allowed areas stays `density`.
///                                  Only the spawn point is filtered: walkers can still wander
///                                  out through openings unless `bounded` keeps them near it.
///   max_ticks_per_step          -- hard cap on how many walkers are ticked per
///                                  simulation step (default 0 = unlimited, i.e. every
///                                  walker every step). Lower values cut CPU roughly
///                                  proportionally; walkers still move continuously
///                                  because physics integrates their velocity every step,
///                                  they just re-decide direction less often. Walkers
///                                  near an agent are exempt and always tick.
///                                  Note the revisit period is population/cap, so a leg
///                                  shorter than that period can be skipped outright —
///                                  that is a real behaviour change, by design.
///   hot_radius_margin           -- extra metres added to reaction_radius when deciding
///                                  who ticks every step, covering how far a walker may
///                                  have drifted since its cached position was taken
///                                  (default 0.5)
///   sleep_distance              -- walkers further than this from EVERY agent are put to
///                                  sleep: velocity zeroed, Rigidbody.Sleep(), phase
///                                  machine frozen until an agent comes back (default 0 =
///                                  never sleep). This is what reclaims the cost of a
///                                  zero-friction material: without friction a walker
///                                  never decelerates, so PhysX never sleeps it on its
///                                  own and every walker stays awake forever. Keep this
///                                  comfortably above the agent's lidar range or walkers
///                                  will visibly freeze inside sensor view.
///   avoidance_probability       -- weight for "avoidant" mode (default 0.5)
///   aggression_probability      -- weight for "aggressive" mode (default 0)
///                                  Remaining weight (clamped so total ≤ 1) becomes "default".
///                                  If avoidance+aggression > 1, both are renormalised as weights.
///   reaction_radius             -- radius at which avoidant walkers flee and aggressive walkers
///                                  chase the agent (default 5)
///   reaction_velocity           -- m/s used while reacting (flee or chase); overrides the
///                                  walker's wander speed for avoidant/aggressive motion (default 2)
///   min_velocity, max_velocity  -- m/s range; each walker gets one value for life (default 0.5 / 1.5)
///   walk_duration_min_sec, walk_duration_max_sec   -- per-leg walk duration (default 1.0 / 3.0)
///   pause_duration_min_sec, pause_duration_max_sec -- per-leg pause duration (default 0.5 / 2.0)
///   bounded                     -- 1 to confine walkers near spawn point, 0 otherwise (default 0)
///   bound_radius                -- radius of the confinement disk (default 15)
///   inward_bias_strength        -- 0..+ weight of the inward pull when sampling directions (default 2)
/// </summary>
public class ChaoticWalkersLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.DynamicObjects };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.StructureContent };

    private const string PrefabFolder = "WorldGen/WalkerPrefabs/";
    private const float StepsPerSecond = 50f; // physics is 50Hz; see RoslikeTCPServer.SimulationMode

    public bool verbose = false;

    [Header("General")]
    public bool walkersEnabled = false;
    public string prefabName = "walker_capsule";
    public float density = 0f;

    [Header("Tick scheduling")]
    [Tooltip("Hard cap on how many walkers are ticked per simulation step. 0 = unlimited (tick every walker every step).")]
    public int maxTicksPerStep = 0;
    [Tooltip("Extra metres on reaction_radius when selecting walkers that tick every step.")]
    public float hotRadiusMargin = 0.5f;
    [Tooltip("Walkers further than this from every agent are put to sleep. 0 = never sleep. Keep it above the agent's lidar range.")]
    public float sleepDistance = 0f;

    [Header("Reaction to agent")]
    [Range(0f, 1f)] public float avoidanceProbability = 0.5f;
    [Range(0f, 1f)] public float aggressionProbability = 0f;
    public float reactionRadius = 5f;
    public float reactionVelocity = 2f;

    [Header("Velocity (per-walker, fixed at spawn)")]
    public float minVelocity = 0.5f;
    public float maxVelocity = 1.5f;

    [Header("Walk / Pause Durations (seconds)")]
    public float walkDurationMinSec = 1.0f;
    public float walkDurationMaxSec = 3.0f;
    public float pauseDurationMinSec = 0.5f;
    public float pauseDurationMaxSec = 2.0f;

    [Header("Bounding")]
    public bool bounded = false;
    public float boundRadius = 15f;
    public float inwardBiasStrength = 2f;

    // Runtime
    private GameObject _prefab;
    private int _seed;
    private float _chunkWidth;
    private bool _paramsLoaded;
    private StructureTypeFilter _spawnFilter;

    // spawnChunk → list of walkers spawned there (GC'd when chunk unloads)
    private readonly Dictionary<Vector2Int, List<ChaoticWalker>> _chunkWalkers = new Dictionary<Vector2Int, List<ChaoticWalker>>();
    // Track reference count per chunk (LOD0 and LOD1 both trigger GenerateChunk).
    private readonly HashSet<Vector2Int> _spawnedChunks = new HashSet<Vector2Int>();

    // ── Scheduler state ──
    // Flat list of every live walker, in no particular order. The cursor sweeps it
    // round-robin; swap-removes keep insertion/removal O(1) and the elapsed-step
    // accounting on each walker makes the resulting reordering harmless.
    private readonly List<ChaoticWalker> _walkers = new List<ChaoticWalker>();
    private int _cursor;
    private int _stepIndex;
    private readonly List<Vector2> _agentsXZ = new List<Vector2>();
    // Deliberately NOT reset by Clear(): the component outlives episodes, and the timer
    // it registered on the server outlives them too. Re-registering per episode would
    // stack duplicate timers and tick every walker several times per step.
    private bool _timerRegistered;

    // ─────────────────────────────────────────────

    private void LoadParams() {
        _seed = WorldLoadingController.GetDerivedSeed("chaotic_walkers");
        _chunkWidth = WorldLoadingController.GetChunkWidth();

        walkersEnabled        = WorldLoadingController.GetParamInt("chaotic_walkers/enabled", walkersEnabled ? 1 : 0) != 0;
        prefabName            = WorldLoadingController.GetParamString("chaotic_walkers/prefab_name", prefabName);
        density               = WorldLoadingController.GetParamFloat("chaotic_walkers/density", density);
        maxTicksPerStep       = WorldLoadingController.GetParamInt("chaotic_walkers/max_ticks_per_step", maxTicksPerStep);
        hotRadiusMargin       = WorldLoadingController.GetParamFloat("chaotic_walkers/hot_radius_margin", hotRadiusMargin);
        sleepDistance         = WorldLoadingController.GetParamFloat("chaotic_walkers/sleep_distance", sleepDistance);
        avoidanceProbability  = WorldLoadingController.GetParamFloat("chaotic_walkers/avoidance_probability", avoidanceProbability);
        aggressionProbability = WorldLoadingController.GetParamFloat("chaotic_walkers/aggression_probability", aggressionProbability);
        reactionRadius        = WorldLoadingController.GetParamFloat("chaotic_walkers/reaction_radius", reactionRadius);
        reactionVelocity      = WorldLoadingController.GetParamFloat("chaotic_walkers/reaction_velocity", reactionVelocity);
        minVelocity           = WorldLoadingController.GetParamFloat("chaotic_walkers/min_velocity", minVelocity);
        maxVelocity           = WorldLoadingController.GetParamFloat("chaotic_walkers/max_velocity", maxVelocity);
        walkDurationMinSec    = WorldLoadingController.GetParamFloat("chaotic_walkers/walk_duration_min_sec", walkDurationMinSec);
        walkDurationMaxSec    = WorldLoadingController.GetParamFloat("chaotic_walkers/walk_duration_max_sec", walkDurationMaxSec);
        pauseDurationMinSec   = WorldLoadingController.GetParamFloat("chaotic_walkers/pause_duration_min_sec", pauseDurationMinSec);
        pauseDurationMaxSec   = WorldLoadingController.GetParamFloat("chaotic_walkers/pause_duration_max_sec", pauseDurationMaxSec);
        bounded               = WorldLoadingController.GetParamInt("chaotic_walkers/bounded", bounded ? 1 : 0) != 0;
        boundRadius           = WorldLoadingController.GetParamFloat("chaotic_walkers/bound_radius", boundRadius);
        inwardBiasStrength    = WorldLoadingController.GetParamFloat("chaotic_walkers/inward_bias_strength", inwardBiasStrength);

        if (maxTicksPerStep < 0) maxTicksPerStep = 0;
        _spawnFilter = StructureTypeFilter.FromParams("chaotic_walkers/");

        _prefab = walkersEnabled ? Resources.Load<GameObject>(PrefabFolder + prefabName) : null;
        if (walkersEnabled && _prefab == null)
            Debug.LogWarning($"ChaoticWalkersLoader: prefab not found at Resources/{PrefabFolder}{prefabName}");

        _paramsLoaded = true;

        Debug.Log($"ChaoticWalkersLoader: params loaded — enabled={walkersEnabled}, " +
            $"prefab={prefabName}, density={density:F4}, max_ticks_per_step={maxTicksPerStep}, " +
            $"hot_radius_margin={hotRadiusMargin:F2}, sleep_distance={sleepDistance:F1}, " +
            $"avoid_p={avoidanceProbability:F2}, aggro_p={aggressionProbability:F2}, reaction_r={reactionRadius:F1}, reaction_v={reactionVelocity:F2}, " +
            $"velocity=[{minVelocity:F2},{maxVelocity:F2}], " +
            $"walk=[{walkDurationMinSec:F1},{walkDurationMaxSec:F1}]s, " +
            $"pause=[{pauseDurationMinSec:F1},{pauseDurationMaxSec:F1}]s, " +
            $"bounded={bounded} r={boundRadius:F1} bias={inwardBiasStrength:F1}, " +
            $"spawn filter: {_spawnFilter}");
    }

    /// <summary>
    /// Register the one timer that drives every walker. Called from Generate/GenerateChunk
    /// rather than Start() because the server singleton may not exist yet at Start().
    /// </summary>
    private void EnsureTimerRegistered() {
        if (_timerRegistered) return;
        var server = RoslikeTCPServer.GetInstance();
        if (server == null) return;
        server.RegisterTimerDiscrete((ev) => TickAll(), 1);
        _timerRegistered = true;
    }

    // ─────────────────────────────────────────────

    public override void Generate() {
        if (!_paramsLoaded) LoadParams();
        EnsureTimerRegistered();
    }

    public override void GenerateChunk(int cx, int cz, int lod) {
        if (!_paramsLoaded) LoadParams();
        EnsureTimerRegistered();
        if (!walkersEnabled || _prefab == null || density <= 0f) return;

        Vector2Int key = new Vector2Int(cx, cz);
        if (_spawnedChunks.Contains(key)) return;
        _spawnedChunks.Add(key);

        SpawnChunk(key);
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        Vector2Int key = new Vector2Int(cx, cz);
        if (!_spawnedChunks.Remove(key)) return;

        if (_chunkWalkers.TryGetValue(key, out var list)) {
            foreach (var w in list) {
                if (w == null) continue;
                RemoveFromScheduler(w);
                Destroy(w.gameObject);
            }
            _chunkWalkers.Remove(key);
        }
        // No CleanupDestroyedTimersAndSubscribers() here any more: walkers no longer
        // register per-instance timers, so there is nothing stale to purge. That call
        // swept the whole timer list with a Unity-null check per entry and ran once per
        // unloaded chunk, which is what made chunk-boundary crossings stall.
    }

    public override void Clear() {
        foreach (var kvp in _chunkWalkers)
            foreach (var w in kvp.Value)
                if (w != null) WorldGenObjects.DestroyHidden(w.gameObject);
        _chunkWalkers.Clear();
        _spawnedChunks.Clear();
        _walkers.Clear();
        _cursor = 0;
        _paramsLoaded = false;
        RoslikeTCPServer.GetInstance()?.CleanupDestroyedTimersAndSubscribers();
    }

    // ─────────────────────────────────────────────
    //  Scheduling
    // ─────────────────────────────────────────────

    /// <summary>
    /// Drives the whole walker population for one simulation step: everyone near an
    /// agent, plus the next slice of the round-robin sweep.
    /// </summary>
    private void TickAll() {
        _stepIndex++;
        if (!_paramsLoaded || !walkersEnabled) return;

        int count = _walkers.Count;
        if (count == 0) return;

        RefreshAgentPositions();
        TickReactionCandidates();

        // Round-robin sweep, capped at max_ticks_per_step. The cursor advances by
        // `budget` every step and wraps, so each walker comes round once every
        // ceil(count / budget) steps.
        int budget = maxTicksPerStep <= 0 ? count : Mathf.Min(count, maxTicksPerStep);
        for (int k = 0; k < budget; k++) {
            if (_walkers.Count == 0) break;
            if (_cursor >= _walkers.Count) _cursor = 0;
            var w = _walkers[_cursor++];
            if (w == null) continue;
            TickWalker(w, false, Vector2.zero);
        }
    }

    /// <summary>
    /// Tick every walker that could react to an agent this step, so flee/chase stays
    /// responsive no matter how long the tick interval is. Only the chunks around each
    /// agent are examined — a reaction radius of a few metres cannot reach out of the
    /// neighbouring chunk when chunks are tens of metres wide.
    /// </summary>
    private void TickReactionCandidates() {
        if (_agentsXZ.Count == 0) return;
        if (avoidanceProbability <= 0f && aggressionProbability <= 0f) return; // all Default mode
        if (_chunkWidth <= 0f) return;

        // A cold walker's cached position is as old as one trip round the sweep, so
        // widen the radius by how far it could have travelled in that time. Derived from
        // the live population rather than configured directly: with a fixed tick budget
        // the revisit period is count/budget, which moves as chunks load and unload.
        // Agent positions are re-read every step, so only walker drift needs covering.
        int revisitSteps = maxTicksPerStep <= 0
            ? 1
            : Mathf.Max(1, Mathf.CeilToInt(_walkers.Count / (float)maxTicksPerStep));
        float drift = Mathf.Max(maxVelocity, reactionVelocity) * revisitSteps / StepsPerSecond;
        float hotRadius = reactionRadius + hotRadiusMargin + drift;
        float hotRadiusSq = hotRadius * hotRadius;

        for (int a = 0; a < _agentsXZ.Count; a++) {
            Vector2 agentXZ = _agentsXZ[a];
            Vector2Int centre = WorldToChunk(agentXZ);

            for (int dx = -1; dx <= 1; dx++) {
                for (int dz = -1; dz <= 1; dz++) {
                    if (!_chunkWalkers.TryGetValue(new Vector2Int(centre.x + dx, centre.y + dz), out var list))
                        continue;

                    for (int i = 0; i < list.Count; i++) {
                        var w = list[i];
                        if (w == null || w.mode == ChaoticWalker.Mode.Default) continue;
                        if ((w.PosXZ - agentXZ).sqrMagnitude > hotRadiusSq) continue;
                        TickWalker(w, true, agentXZ);
                    }
                }
            }
        }
    }

    private void TickWalker(ChaoticWalker w, bool hasReactionAgent, Vector2 reactionAgentXZ) {
        int elapsed = _stepIndex - w.lastTickStep;
        if (elapsed <= 0) return; // already ticked this step (reaction pass got there first)
        // Stamped before the sleep branch on purpose: a sleeping walker keeps its stamp
        // current, so `elapsed` never accumulates while parked and waking up can't
        // trigger a huge phase-machine catch-up.
        w.lastTickStep = _stepIndex;

        // A sleeping walker cannot have moved, so its cached position is still exact and
        // there is no need to pay the transform read.
        if (!w.IsAsleep) w.RefreshPosition();

        if (sleepDistance > 0f && _agentsXZ.Count > 0) {
            if (NearestAgentDistSq(w.PosXZ) > sleepDistance * sleepDistance) {
                w.SetSleeping(true);
                return; // parked: no velocity write, no phase advance
            }
            w.SetSleeping(false);
        }

        w.Tick(elapsed, hasReactionAgent, reactionAgentXZ);
    }

    /// <summary>Squared distance from a point to the closest agent. Multi-agent by construction.</summary>
    private float NearestAgentDistSq(Vector2 posXZ) {
        float best = float.MaxValue;
        for (int i = 0; i < _agentsXZ.Count; i++) {
            float d = (posXZ - _agentsXZ[i]).sqrMagnitude;
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>
    /// Cache agent positions for this step — one interop read per agent instead of one
    /// per walker. MULTI-AGENT: this is the only place that needs to change; everything
    /// downstream already unions over the whole list.
    /// </summary>
    private void RefreshAgentPositions() {
        _agentsXZ.Clear();
        var controller = WorldLoadingController.instance;
        if (controller == null) return;
        var agent = controller.agentObject;
        if (agent == null) return;
        Vector3 p = agent.transform.position;
        _agentsXZ.Add(new Vector2(p.x, p.z));
    }

    private Vector2Int WorldToChunk(Vector2 posXZ) {
        return new Vector2Int(
            Mathf.FloorToInt(posXZ.x / _chunkWidth),
            Mathf.FloorToInt(posXZ.y / _chunkWidth));
    }

    private void AddToScheduler(ChaoticWalker w) {
        w.schedulerIndex = _walkers.Count;
        w.lastTickStep = _stepIndex; // so its first tick sees elapsed=1, not the whole run
        _walkers.Add(w);
    }

    /// <summary>
    /// O(1) removal: move the last walker into the freed slot. Order is irrelevant —
    /// each walker advances by its own elapsed-step count, so being swept out of turn
    /// costs nothing.
    /// </summary>
    private void RemoveFromScheduler(ChaoticWalker w) {
        int idx = w.schedulerIndex;
        if (idx < 0 || idx >= _walkers.Count || _walkers[idx] != w) return;

        int last = _walkers.Count - 1;
        if (idx != last) {
            var moved = _walkers[last];
            _walkers[idx] = moved;
            if (moved != null) moved.schedulerIndex = idx;
        }
        _walkers.RemoveAt(last);
        w.schedulerIndex = -1;

        if (_cursor > _walkers.Count) _cursor = 0;
    }

    // ─────────────────────────────────────────────

    private void SpawnChunk(Vector2Int chunkID) {
        int chunkSeed = _seed ^ (chunkID.x * 1000003) ^ (chunkID.y * 999983);
        System.Random rng = new System.Random(chunkSeed);

        int count = Mathf.RoundToInt(density * _chunkWidth * _chunkWidth);
        if (count <= 0) return;

        float originX = chunkID.x * _chunkWidth;
        float originZ = chunkID.y * _chunkWidth;

        int walkMinSteps  = Mathf.Max(1, Mathf.RoundToInt(walkDurationMinSec  * StepsPerSecond));
        int walkMaxSteps  = Mathf.Max(walkMinSteps, Mathf.RoundToInt(walkDurationMaxSec * StepsPerSecond));
        int pauseMinSteps = Mathf.Max(1, Mathf.RoundToInt(pauseDurationMinSec * StepsPerSecond));
        int pauseMaxSteps = Mathf.Max(pauseMinSteps, Mathf.RoundToInt(pauseDurationMaxSec * StepsPerSecond));

        var list = new List<ChaoticWalker>(count);
        _chunkWalkers[chunkID] = list;

        var heightProvider = WorldServices.Get<IHeightProvider>();

        for (int i = 0; i < count; i++) {
            float x = originX + (float)rng.NextDouble() * _chunkWidth;
            float z = originZ + (float)rng.NextDouble() * _chunkWidth;
            if (!_spawnFilter.Accepts(new Vector2(x, z))) continue;
            float y = heightProvider.GetTerrainHeight(x, z) + 1.0f; // leave a small drop margin

            float speed = Mathf.Lerp(minVelocity, maxVelocity, (float)rng.NextDouble());
            ChaoticWalker.Mode mode = SampleMode(rng);
            int walkerSeed = chunkSeed ^ (i * 265447); // per-walker seed for walk sequence and other properties

            GameObject go = Instantiate(_prefab, new Vector3(x, y, z), Quaternion.identity, transform);
            go.name = $"Walker_{chunkID.x}_{chunkID.y}_{i}";

            var walker = go.GetComponent<ChaoticWalker>();
            if (walker == null) walker = go.AddComponent<ChaoticWalker>();

            walker.speed = speed;
            walker.mode = mode;
            walker.reactionRadius = reactionRadius;
            walker.reactionVelocity = reactionVelocity;
            walker.walkMinSteps = walkMinSteps;
            walker.walkMaxSteps = walkMaxSteps;
            walker.pauseMinSteps = pauseMinSteps;
            walker.pauseMaxSteps = pauseMaxSteps;
            walker.bounded = bounded;
            walker.boundCenter = new Vector2(x, z);
            walker.boundRadius = boundRadius;
            walker.inwardBiasStrength = inwardBiasStrength;
            walker.Init(walkerSeed);

            AddToScheduler(walker);
            list.Add(walker);
        }

        if (verbose)
            Debug.Log($"ChaoticWalkersLoader: chunk ({chunkID.x},{chunkID.y}) spawned {count} walkers " +
                      $"(population now {_walkers.Count})");
    }

    /// <summary>
    /// Sample a behaviour mode for one walker. The two probabilities are treated as
    /// weights: if their sum ≤ 1, the remainder is "default"; if > 1, they are
    /// renormalised (default becomes unreachable).
    /// </summary>
    private ChaoticWalker.Mode SampleMode(System.Random rng) {
        float pAvoid = Mathf.Max(0f, avoidanceProbability);
        float pAggro = Mathf.Max(0f, aggressionProbability);
        float sum = pAvoid + pAggro;
        if (sum <= 0f) return ChaoticWalker.Mode.Default;

        float wAvoid, wAggro;
        if (sum > 1f) {
            wAvoid = pAvoid / sum;
            wAggro = pAggro / sum;
        } else {
            wAvoid = pAvoid;
            wAggro = pAggro;
        }

        double r = rng.NextDouble();
        if (r < wAvoid) return ChaoticWalker.Mode.Avoidant;
        if (r < wAvoid + wAggro) return ChaoticWalker.Mode.Aggressive;
        return ChaoticWalker.Mode.Default;
    }
}
