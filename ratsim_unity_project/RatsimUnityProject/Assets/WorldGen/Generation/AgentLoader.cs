using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

public class AgentLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Agents };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height, WorldDataType.StructureContent };

    public TopdownCameraFollower agentCameraFollower;

    private bool _spawned = false;
    private readonly List<GameObject> _spawnedAgents = new List<GameObject>();

    [Header("Spawn Settings")]
    public float spawnSafetyRadius = 2f;
    public int maxPlacementAttempts = 50;

    private static readonly Dictionary<string, Type> SensorNameToType = new Dictionary<string, Type> {
        { "lidar2d",        typeof(SemanticLidarSensor) },
        { "rgbd",           typeof(RGBDSensor) },
        { "odom",           typeof(Odom2DSensor) },
        { "collision",      typeof(CollisionSensor) },
        { "relative_pose",  typeof(RelativePoseSensor) },
        { "absolute_pose",  typeof(AbsolutePose2DSensor) },
        { "compass",         typeof(CompassSensor) },
        { "head_direction_cells", typeof(HeadDirectionCellsSensor) },
        { "sector_signal",  typeof(SectorSignalSensor) },
    };

    private static readonly Dictionary<string, Type> ActuatorNameToType = new Dictionary<string, Type> {
        { "velocity",       typeof(Twist2DActuator) },
        { "twist2d",        typeof(Twist2DActuator) },
        { "teleport",       typeof(PoseTeleportActuator) },
    };


    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void Generate() {
        if (_spawned) return;
        _spawned = true;
        SpawnAgents();
    }

    public override void Clear() {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        _spawnedAgents.Clear();
        _spawned = false;

        // Sensors register timer callbacks with RoslikeTCPServer in Start().
        // After destroying the agent, purge stale references so the server
        // doesn't invoke callbacks on destroyed components.
        RoslikeTCPServer.GetInstance()?.CleanupDestroyedTimersAndSubscribers();
    }

    // ─────────────────────────────────────────────
    //  Spawning
    // ─────────────────────────────────────────────

    private void SpawnAgents() {
        string json = WorldLoadingController.instance.GetAgentConfigJson();
        if (string.IsNullOrEmpty(json)) {
            Debug.LogWarning("AgentLoader: no agent config received, skipping spawn");
            return;
        }

        // Parse agent config (same key/value entries format as world config)
        var parsed = JsonUtility.FromJson<WorldConfig>(json);
        var config = new Dictionary<string, string>();
        foreach (var entry in parsed.entries)
            config[entry.key] = entry.value;

        // Load prefab
        string prefabName;
        if (!config.TryGetValue("prefab_name", out prefabName)) {
            Debug.LogError("AgentLoader: agent config missing 'prefab_name'");
            return;
        }

        GameObject prefab = Resources.Load<GameObject>($"AgentPrefabs/{prefabName}");
        if (prefab == null) {
            Debug.LogError($"AgentLoader: prefab not found at Resources/AgentPrefabs/{prefabName}");
            return;
        }

        // Find safe spawn position (mode driven by world config param "agents_spawn_pos")
        Vector3 spawnPos = FindSafeSpawnPosition(prefab);

        // Spawn rotation: identity by default, or random yaw if requested. We use a
        // separate derived seed so randomized rotation doesn't shift the spawn-position
        // RNG stream (changing this flag mid-experiment doesn't move spawn coords).
        Quaternion spawnRot = Quaternion.identity;
        bool randomizeRotation = WorldLoadingController.GetParamInt("agents_spawn_pos/randomize_rotation", 0) != 0;
        if (randomizeRotation) {
            System.Random yawRng = new System.Random(WorldLoadingController.GetDerivedSeed("agent_rotation"));
            float yaw = (float)(yawRng.NextDouble() * 360.0);
            spawnRot = Quaternion.Euler(0f, yaw, 0f);
        }

        // Instantiate
        GameObject agent = Instantiate(prefab, spawnPos, spawnRot, transform);
        string namePrefix;
        if (config.TryGetValue("name_prefix", out namePrefix))
            agent.name = namePrefix;

        if (agentCameraFollower != null)
            agentCameraFollower.target = agent; // Clear camera target before destroying old agent

        // Disable all known sensor types
        foreach (var kvp in SensorNameToType) {
            var comp = agent.GetComponentInChildren(kvp.Value, true);
            if (comp != null)
                ((MonoBehaviour)comp).enabled = false;
        }

        // Enable requested sensors
        string sensorsStr;
        var enabledSensorNames = new HashSet<string>();
        if (config.TryGetValue("sensors", out sensorsStr)) {
            string[] sensorNames = sensorsStr.Split(',');
            foreach (string raw in sensorNames) {
                string sensorName = raw.Trim();
                if (string.IsNullOrEmpty(sensorName)) continue;

                Type sensorType;
                if (!SensorNameToType.TryGetValue(sensorName, out sensorType)) {
                    Debug.LogWarning($"AgentLoader: unknown sensor '{sensorName}'");
                    continue;
                }

                var comp = agent.GetComponentInChildren(sensorType, true);
                if (comp != null) {
                    ((MonoBehaviour)comp).enabled = true;
                    enabledSensorNames.Add(sensorName);
                    Debug.Log($"AgentLoader: enabled sensor '{sensorName}'");
                } else {
                    Debug.LogWarning($"AgentLoader: sensor component {sensorType.Name} not found on prefab");
                }
            }
        }

        // Override sensor/actuator params via reflection (keys like "lidar2d/maxRange" or "velocity/maxLinearVelocity")
        foreach (var kvp in config) {
            int slashIdx = kvp.Key.IndexOf('/');
            if (slashIdx < 0) continue;

            string componentName = kvp.Key.Substring(0, slashIdx);
            string fieldName = kvp.Key.Substring(slashIdx + 1);

            // Try sensors first, then actuators
            Type componentType = null;
            if (SensorNameToType.TryGetValue(componentName, out componentType)) {
                // sensor
            } else if (ActuatorNameToType.TryGetValue(componentName, out componentType)) {
                // actuator
            } else {
                continue;
            }

            var comp = agent.GetComponentInChildren(componentType, true);
            if (comp == null) continue;

            FieldInfo field = componentType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field == null) {
                Debug.LogWarning($"AgentLoader: field '{fieldName}' not found on {componentType.Name}");
                continue;
            }

            try {
                object value = Convert.ChangeType(kvp.Value, field.FieldType);
                field.SetValue(comp, value);
                Debug.Log($"AgentLoader: set {componentType.Name}.{fieldName} = {kvp.Value}");
            } catch (Exception e) {
                Debug.LogWarning($"AgentLoader: failed to set {componentType.Name}.{fieldName}: {e.Message}");
            }
        }

        // Set agent reference on controller
        WorldLoadingController.instance.agentObject = agent;

        // Ground-truth absolute pose sensor is ALWAYS enabled, regardless of
        // the user's `sensors` config. It is intended for debugging and reward
        // computation (e.g. volumetric exploration tracking) — NOT as an RL
        // observation input. Topic follows the per-agent convention so that
        // multi-agent setups stay disambiguated.
        string gtNamePrefix = string.IsNullOrEmpty(namePrefix) ? "agent" : namePrefix;
        var gtPoseSensor = agent.GetComponentInChildren<AbsolutePose2DSensor>(true);
        if (gtPoseSensor != null) {
            gtPoseSensor.enabled = true;
            gtPoseSensor.topic = $"/{gtNamePrefix}/gt_pose";
            Debug.Log($"AgentLoader: ground-truth pose sensor always-on, topic='{gtPoseSensor.topic}'");
        } else {
            Debug.LogWarning(
                "AgentLoader: no AbsolutePose2DSensor on prefab — GT-pose-dependent " +
                "systems (volumetric exploration, etc.) will be unavailable");
        }

        // Reset relative pose origin if enabled
        if (enabledSensorNames.Contains("relative_pose")) {
            var relPose = agent.GetComponentInChildren<RelativePoseSensor>(true);
            if (relPose != null)
                relPose.ResetOrigin();
        }

        _spawnedAgents.Add(agent);
        Debug.Log($"AgentLoader: spawned '{agent.name}' at {spawnPos}");
    }

    // ─────────────────────────────────────────────
    //  Spawn position routing
    //
    //  Two ways to configure spawning:
    //
    //  1. NEW (preferred): orthogonal params under "agents_spawn_pos/":
    //       agents_spawn_pos/mode                 -- "uniform" | "in_structures" | "city_outskirts"
    //       agents_spawn_pos/allowed_structures   -- CSV of structureType prefixes ("*" = any).
    //                                                In "in_structures" mode this is the pool to
    //                                                pick from (required, non-empty). In "uniform"
    //                                                mode it's an extra constraint: the candidate
    //                                                must land inside one of these.
    //       agents_spawn_pos/denied_structures    -- CSV of structureType prefixes ("*" = any).
    //                                                Reject any candidate inside a denied structure.
    //                                                Deny wins over allow if a type is in both.
    //       agents_spawn_pos/skip_collider_checks -- 0/1; when 1, skip the physics OverlapSphere
    //                                                check and the surrounding chunk pre-load.
    //                                                Faster and works when colliders aren't loaded
    //                                                yet, but doesn't catch overlaps with rewards
    //                                                / props / chunk-streamed walls.
    //       agents_spawn_pos/randomize_rotation   -- 0/1 (default 0); when 1, sample yaw uniformly
    //                                                in [0°, 360°) on spawn (rotation around Y).
    //                                                Useful when you don't want the agent to always
    //                                                face the same direction (e.g. mazes where the
    //                                                spawn cell has an obvious "natural" facing).
    //                                                Uses derived seed "agent_rotation" — independent
    //                                                from the spawn-position RNG so toggling it
    //                                                doesn't shift spawn coordinates.
    //
    //  2. LEGACY (still supported): single-string "agents_spawn_pos":
    //       "origin"/"random"     → mode=uniform
    //       "outside_structures"  → mode=uniform, denied="*"
    //       "in_room"             → mode=in_structures, allowed="maze_room,reward_room,empty_room"
    //       "city"                → FindSpawnInCity (separate code path)
    //       "city_outskirts"      → FindSpawnAtCityOutskirts (separate code path)
    //
    //  The new generic path (uniform / in_structures) does:
    //    sample candidate → cheap structure-filter rejection → force-load chunks within the
    //    safety sphere → Physics.SyncTransforms → physics check → accept/reject → retry.
    //  Pre-loaded chunks stay loaded (the agent's ChunkLoadingRequestor would request them
    //  on its first tick anyway).
    //
    //  Requires scene registration order:
    //    WorldLayoutLoader → StructureLoadingCoordinator → CityLoader → AgentLoader → TreeLoader
    //  WorldLayoutLoader and CityLoader now generate eagerly in Initialize() so
    //  city + house footprints are available in WorldData when this runs.
    // ─────────────────────────────────────────────

    private struct SpawnConfig {
        public string mode;                 // "uniform" | "in_structures"
        public List<string> allowed;        // structureType prefixes; empty = no constraint
        public List<string> denied;         // structureType prefixes; empty = no exclusion
        public bool skipColliderChecks;
    }

    private Vector3 FindSafeSpawnPosition(GameObject prefab) {
        float colliderRadius = GetPrefabColliderRadius(prefab);
        System.Random rng = new System.Random(WorldLoadingController.GetDerivedSeed("agents"));

        SpawnConfig cfg;
        string newMode = WorldLoadingController.GetParamString("agents_spawn_pos/mode", "");
        if (!string.IsNullOrWhiteSpace(newMode)) {
            // New API path.
            cfg = new SpawnConfig {
                mode               = newMode.ToLowerInvariant(),
                allowed            = ParseTypeList(WorldLoadingController.GetParamString("agents_spawn_pos/allowed_structures", "")),
                denied             = ParseTypeList(WorldLoadingController.GetParamString("agents_spawn_pos/denied_structures",  "")),
                skipColliderChecks = WorldLoadingController.GetParamInt("agents_spawn_pos/skip_collider_checks", 0) != 0,
            };
        } else {
            // Translate legacy single-string mode.
            string legacy = WorldLoadingController.GetParamString("agents_spawn_pos", "origin").ToLowerInvariant();
            cfg = new SpawnConfig {
                allowed            = new List<string>(),
                denied             = new List<string>(),
                skipColliderChecks = WorldLoadingController.GetParamInt("agents_spawn_pos/skip_collider_checks", 0) != 0,
            };
            switch (legacy) {
                case "city":              return FindSpawnInCity(colliderRadius, rng);
                case "city_outskirts":    cfg.mode = "city_outskirts"; break;
                case "outside_structures": cfg.mode = "uniform"; cfg.denied.Add("*"); break;
                case "in_room":
                    cfg.mode = "in_structures";
                    cfg.allowed.AddRange(new[] { "maze_room", "reward_room", "empty_room" });
                    break;
                case "origin":
                case "random":
                default:                  cfg.mode = "uniform"; break;
            }
        }

        if (cfg.mode == "city_outskirts") return FindSpawnAtCityOutskirts(colliderRadius, rng);
        return FindSpawnGeneric(cfg, colliderRadius, rng);
    }

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

    private float GetPrefabColliderRadius(GameObject prefab) {
        float radius = 0.5f;
        var sphereCol = prefab.GetComponentInChildren<SphereCollider>();
        if (sphereCol != null)
            radius = sphereCol.radius * Mathf.Max(
                prefab.transform.lossyScale.x,
                prefab.transform.lossyScale.y,
                prefab.transform.lossyScale.z);
        return radius;
    }

    // ─────────────────────────────────────────────
    //  Spawn strategies
    // ─────────────────────────────────────────────

    /// <summary>
    /// Thin legacy wrapper for unconstrained random spawning. Used as a fallback in
    /// FindSpawnInCity / FindSpawnAtCityOutskirts. New callers should go through
    /// FindSpawnGeneric directly.
    /// </summary>
    private Vector3 FindSpawnRandom(float colliderRadius, System.Random rng) {
        SpawnConfig cfg = new SpawnConfig {
            mode               = "uniform",
            allowed            = new List<string>(),
            denied             = new List<string>(),
            skipColliderChecks = WorldLoadingController.GetParamInt("agents_spawn_pos/skip_collider_checks", 0) != 0,
        };
        return FindSpawnGeneric(cfg, colliderRadius, rng);
    }

    private Vector3 FindSpawnGeneric(SpawnConfig cfg, float colliderRadius, System.Random rng) {
        float margin = Mathf.Max(spawnSafetyRadius, colliderRadius);
        int worldGenLayer = LayerMask.NameToLayer("WorldGen");

        float worldW = WorldLoadingController.GetParamFloat("world_bounds/width",  100f);
        float worldH = WorldLoadingController.GetParamFloat("world_bounds/height", 100f);
        float worldMinX = -worldW * 0.5f, worldMaxX = worldW * 0.5f;
        float worldMinZ = -worldH * 0.5f, worldMaxZ = worldH * 0.5f;
        float xRange = (worldMaxX - worldMinX) - 2f * margin;
        float zRange = (worldMaxZ - worldMinZ) - 2f * margin;

        // For "in_structures": build the candidate pool once.
        List<WorldStructure> pool = null;
        if (cfg.mode == "in_structures") {
            if (cfg.allowed.Count == 0) {
                Debug.LogError("AgentLoader: 'in_structures' mode requires a non-empty allowed_structures list; using world center fallback");
                return WorldCenterFallback(colliderRadius);
            }
            pool = WorldData.GetStructures()
                .Where(s => MatchesAny(s.structureType, cfg.allowed))
                .ToList();
            if (pool.Count == 0) {
                Debug.LogError($"AgentLoader: 'in_structures' mode but no structures match allowed=[{string.Join(",", cfg.allowed)}]; using world center fallback");
                return WorldCenterFallback(colliderRadius);
            }
        } else if (cfg.mode != "uniform") {
            Debug.LogError($"AgentLoader: unknown mode '{cfg.mode}'; falling back to uniform");
            cfg.mode = "uniform";
        }

        int rejectedFilter   = 0;
        int rejectedPhysics  = 0;
        int rejectedTooSmall = 0;
        Vector2 lastCandidate = Vector2.zero;

        for (int attempt = 0; attempt < maxPlacementAttempts; attempt++) {
            float x, z;

            if (cfg.mode == "uniform") {
                if (xRange <= 0f || zRange <= 0f) {
                    Debug.LogError("AgentLoader: world is smaller than the safety margin; using world center fallback");
                    return WorldCenterFallback(colliderRadius);
                }
                x = worldMinX + margin + (float)rng.NextDouble() * xRange;
                z = worldMinZ + margin + (float)rng.NextDouble() * zRange;
            } else {
                // in_structures: sample uniformly inside one of the allowed structures' OBBs.
                WorldStructure s = pool[rng.Next(pool.Count)];
                Bounds2D bb = s.GetBoundingBox2D();
                float halfW = bb.size.x * 0.5f - margin;
                float halfH = bb.size.y * 0.5f - margin;
                if (halfW < 0f || halfH < 0f) { rejectedTooSmall++; continue; }

                float lx = (float)(rng.NextDouble() * 2.0 - 1.0) * halfW;
                float lz = (float)(rng.NextDouble() * 2.0 - 1.0) * halfH;
                float rotRad = bb.rotation * Mathf.Deg2Rad;
                float c = Mathf.Cos(rotRad), sn = Mathf.Sin(rotRad);
                x = bb.center.x + lx * c - lz * sn;
                z = bb.center.y + lx * sn + lz * c;
            }
            lastCandidate = new Vector2(x, z);

            // Cheap structure-based rejection BEFORE the chunk pre-load / physics check.
            if (!PassesStructureFilter(new Vector2(x, z), cfg)) { rejectedFilter++; continue; }

            float y = WorldServices.Get<IHeightProvider>().GetTerrainHeight(x, z) + colliderRadius + 0.1f;
            Vector3 pos3D = new Vector3(x, y, z);

            if (cfg.skipColliderChecks) {
                Debug.Log($"AgentLoader: spawned at ({x:F1},{z:F1}) after {attempt + 1} attempt(s) " +
                          $"[mode={cfg.mode}, skip_collider_checks=true, " +
                          $"filter_rejects={rejectedFilter}, too_small={rejectedTooSmall}]");
                return pos3D;
            }

            // Force-load chunks overlapping the safety sphere so chunk-streamed colliders
            // (e.g. maze walls) are present for the OverlapSphere check.
            ForceLoadChunksAround(x, z, margin);
            Physics.SyncTransforms();

            if (!IsPhysicsBlocked(pos3D, worldGenLayer)) {
                Debug.Log($"AgentLoader: spawned at ({x:F1},{z:F1}) after {attempt + 1} attempt(s) " +
                          $"[mode={cfg.mode}, filter_rejects={rejectedFilter}, " +
                          $"physics_rejects={rejectedPhysics}, too_small={rejectedTooSmall}]");
                return pos3D;
            }
            rejectedPhysics++;
        }

        Debug.LogError(
            $"AgentLoader: no valid spawn in {maxPlacementAttempts} attempts. " +
            $"mode={cfg.mode}, allowed=[{string.Join(",", cfg.allowed)}], denied=[{string.Join(",", cfg.denied)}], " +
            $"filter_rejects={rejectedFilter}, physics_rejects={rejectedPhysics}, too_small={rejectedTooSmall}, " +
            $"last_candidate=({lastCandidate.x:F1},{lastCandidate.y:F1}). " +
            "Likely causes: allow/deny constraints leave no free space; all allowed structures too small for the agent; world densely occupied. " +
            "Using world center fallback so the episode can continue.");
        return WorldCenterFallback(colliderRadius);
    }

    private bool PassesStructureFilter(Vector2 pos2D, SpawnConfig cfg) {
        if (cfg.denied.Count == 0 && (cfg.mode != "uniform" || cfg.allowed.Count == 0)) return true;

        bool sawAllowed = false;
        foreach (WorldStructure s in WorldData.GetStructures()) {
            // Deny wins over allow if the same type matches both lists.
            if (cfg.denied.Count > 0 && MatchesAny(s.structureType, cfg.denied)
                && s.GetBoundingBox2D().Contains(pos2D)) return false;

            if (cfg.mode == "uniform" && cfg.allowed.Count > 0 && !sawAllowed
                && MatchesAny(s.structureType, cfg.allowed) && s.GetBoundingBox2D().Contains(pos2D)) {
                sawAllowed = true;
            }
        }
        if (cfg.mode == "uniform" && cfg.allowed.Count > 0 && !sawAllowed) return false;
        return true;
    }

    /// <summary>
    /// Synchronously asks every WorldDataProvider to load the LOD0 chunks overlapping a
    /// `radius`-square around (x, z). Idempotent — providers that have already generated
    /// a given chunk no-op. The chunks stay loaded; the agent's ChunkLoadingRequestor
    /// would request them on its first tick anyway, so we're paying that cost a few ms
    /// earlier rather than extra.
    /// </summary>
    private static void ForceLoadChunksAround(float x, float z, float radius) {
        float chunkWidth = WorldLoadingController.GetChunkWidth();
        int minCX = Mathf.FloorToInt((x - radius) / chunkWidth);
        int maxCX = Mathf.FloorToInt((x + radius) / chunkWidth);
        int minCZ = Mathf.FloorToInt((z - radius) / chunkWidth);
        int maxCZ = Mathf.FloorToInt((z + radius) / chunkWidth);
        for (int cx = minCX; cx <= maxCX; cx++) {
            for (int cz = minCZ; cz <= maxCZ; cz++) {
                foreach (WorldDataProvider p in WorldDataProvider.registered)
                    p.GenerateChunk(cx, cz, 0);
            }
        }
    }

    private Vector3 WorldCenterFallback(float colliderRadius) {
        float y = WorldServices.Get<IHeightProvider>().GetTerrainHeight(0f, 0f) + colliderRadius + 0.1f;
        return new Vector3(0f, y, 0f);
    }

    /// <summary>
    /// Tries to find an open spawn point inside the city OBB that does not overlap
    /// any house footprint. Falls back to FindSpawnAtCityOutskirts if all attempts fail.
    /// </summary>
    private Vector3 FindSpawnInCity(float colliderRadius, System.Random rng) {
        List<WorldStructure> cities = WorldData.GetStructures()
            .Where(s => s.structureType == "city").ToList();

        if (cities.Count == 0) {
            Debug.LogWarning("AgentLoader: no cities in WorldData for 'city' spawn; falling back to random");
            return FindSpawnRandom(colliderRadius, rng);
        }

        WorldStructure city = cities[rng.Next(cities.Count)];
        Bounds2D cityBounds = city.GetBoundingBox2D();

        // Collect house footprints to avoid spawning inside buildings.
        // CityLoader.Generate() places houses before AgentLoader runs (dependency graph guarantees this).
        List<Bounds2D> houseBounds = WorldData.GetStructures()
            .Where(s => s.structureType.StartsWith("house", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.GetBoundingBox2D())
            .ToList();

        int maxAttempts  = WorldLoadingController.GetParamInt("agents_city_spawn_attempts", 200);
        int worldGenLayer = LayerMask.NameToLayer("WorldGen");

        float rotRad = cityBounds.rotation * Mathf.Deg2Rad;
        float cosR = Mathf.Cos(rotRad), sinR = Mathf.Sin(rotRad);
        float halfW = cityBounds.size.x * 0.5f, halfH = cityBounds.size.y * 0.5f;

        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            // Random point in city OBB local space, then rotate to world space
            float lx = (float)(rng.NextDouble() * 2.0 - 1.0) * halfW;
            float lz = (float)(rng.NextDouble() * 2.0 - 1.0) * halfH;
            Vector2 pos2D = cityBounds.center + new Vector2(lx * cosR - lz * sinR,
                                                             lx * sinR + lz * cosR);

            // Reject if inside any house footprint
            bool inHouse = false;
            foreach (Bounds2D hb in houseBounds) {
                if (hb.Contains(pos2D)) { inHouse = true; break; }
            }
            if (inHouse) continue;

            float y = WorldServices.Get<IHeightProvider>().GetTerrainHeight(pos2D.x, pos2D.y) + colliderRadius + 0.1f;
            Vector3 pos = new Vector3(pos2D.x, y, pos2D.y);

            if (!IsPhysicsBlocked(pos, worldGenLayer)) {
                Debug.Log($"AgentLoader: city spawn at {pos} after {attempt + 1} attempt(s)");
                return pos;
            }
        }

        Debug.LogWarning($"AgentLoader: no open spot inside city after {maxAttempts} attempts; " +
                         "falling back to city_outskirts");
        return FindSpawnAtCityOutskirts(colliderRadius, rng);
    }

    /// <summary>
    /// Spawns radially outside the city OBB. Registers a tree-free zone at the
    /// chosen position via TreeLoader so that trees do not generate on top of the agent.
    /// Has a guaranteed fallback so this never fails.
    /// </summary>
    private Vector3 FindSpawnAtCityOutskirts(float colliderRadius, System.Random rng) {
        List<WorldStructure> cities = WorldData.GetStructures()
            .Where(s => s.structureType == "city").ToList();

        if (cities.Count == 0) {
            Debug.LogWarning("AgentLoader: no cities for 'city_outskirts' spawn; falling back to random");
            return FindSpawnRandom(colliderRadius, rng);
        }

        WorldStructure city = cities[rng.Next(cities.Count)];
        Bounds2D cityBounds = city.GetBoundingBox2D();

        // World config params
        float outskirtsDist = WorldLoadingController.GetParamFloat("agents_outskirts_margin", 15f);
        float clearRadius   = WorldLoadingController.GetParamFloat("agents_outskirts_clear_radius", 5f);
        int   worldGenLayer = LayerMask.NameToLayer("WorldGen");

        // Half-diagonal: max distance from city center to any corner of the OBB.
        // Spawn positions are placed between cityHalfDiag and cityHalfDiag+outskirtsDist from center.
        float cityHalfDiag = new Vector2(cityBounds.size.x, cityBounds.size.y).magnitude * 0.5f;

        for (int attempt = 0; attempt < maxPlacementAttempts; attempt++) {
            float   angle      = (float)(rng.NextDouble() * 2.0 * Math.PI);
            float   radialDist = cityHalfDiag + (float)(rng.NextDouble() * outskirtsDist);
            Vector2 dir        = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 pos2D      = cityBounds.center + dir * radialDist;

            float   y   = WorldServices.Get<IHeightProvider>().GetTerrainHeight(pos2D.x, pos2D.y) + colliderRadius + 0.1f;
            Vector3 pos = new Vector3(pos2D.x, y, pos2D.y);

            if (!IsPhysicsBlocked(pos, worldGenLayer)) {
                TreeLoader.RegisterClearZone(pos2D, clearRadius);
                Debug.Log($"AgentLoader: city_outskirts spawn at {pos} after {attempt + 1} attempt(s); " +
                          $"tree clear zone r={clearRadius}");
                return pos;
            }
        }

        // Guaranteed fallback: fixed bearing (+X from city center), always clears trees
        Vector2 fallback2D = cityBounds.center + new Vector2(cityHalfDiag + 5f, 0f);
        float fallbackY = WorldServices.Get<IHeightProvider>().GetTerrainHeight(fallback2D.x, fallback2D.y) + colliderRadius + 0.1f;
        TreeLoader.RegisterClearZone(fallback2D, clearRadius);
        Debug.LogWarning("AgentLoader: outskirts fallback position used; tree clearing applied");
        return new Vector3(fallback2D.x, fallbackY, fallback2D.y);
    }

    // ─────────────────────────────────────────────
    //  Physics helpers
    // ─────────────────────────────────────────────

    private bool IsPhysicsBlocked(Vector3 pos, int worldGenLayer) {
        Collider[] overlaps = Physics.OverlapSphere(pos, spawnSafetyRadius);
        var terrainMesh = WorldServices.Has<ITerrainMeshProvider>()
            ? WorldServices.Get<ITerrainMeshProvider>() : null;
        foreach (var col in overlaps) {
            if (terrainMesh != null && terrainMesh.IsTerrainCollider(col))
                continue;
            if (col.gameObject.layer == worldGenLayer)
                continue;
            return true;
        }
        return false;
    }
}
