using UnityEngine;
using System.Collections.Generic;

/// <summary>
/// WorldStructureProvider that configures house interiors and exteriors.
///
/// Responds to any WorldStructure whose structureType starts with "house".
/// Uses global config params + a per-house seed (world seed ^ position hash)
/// for deterministic variation.
///
/// Expected named child groups under each LOD child:
///   doorSpawnPositions/   — empty transforms where door prefabs are instantiated
///   carSpawnPositions/    — empty transforms where car prefabs are instantiated
///   roofObjects/          — meshes to enable/disable
///   breakableWalls/       — children that can be swapped for rubble prefabs
///   clutterObjects/       — objects to enable/disable partially
///   innerLayoutVariants/  — mutually exclusive layout children (one enabled, rest disabled)
///
/// Config params (all under "house/" prefix):
///   allowed_door_prefabs        — comma list of prefab names in Resources/WorldGen/HouseModulePrefabs/
///   {door_name}/probability     — relative weight for that door type (default 1)
///   enable_roofs                — 0 or 1 (default 1)
///   chance_wall_broken          — 0.0–1.0 per breakable wall (default 0)
///   rubble_prefab               — prefab name for wall rubble replacement
///   rubble_mass                 — mass of every rubble piece (default 0.2)
///   rubble_mass_multipliers     — comma list, e.g. "1,1000". Shuffled per house and drawn
///                                 without replacement, one per broken wall; every piece of
///                                 that wall gets rubble_mass * multiplier. Walls beyond the
///                                 list length get 1. Empty (default) = all walls 1.
///   clutter_density             — 0.0–1.0, fraction of clutter objects enabled (default 1)
///   allowed_car_prefabs         — comma list of car prefab names in Resources/WorldGen/HouseModulePrefabs/
///   car_spawn_chance            — 0.0–1.0 per car spawn position (default 0)
/// </summary>
public class HouseLoader : WorldStructureProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.StructureContent };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.StructureEvents };

    public bool verbose = true;

    private const string PrefabFolder = "WorldGen/HouseModulePrefabs/";

    // ─────────────────────────────────────────────
    //  Editor-configurable defaults (overridden by episode params)
    // ─────────────────────────────────────────────

    [Header("Doors")]
    public string allowedDoorPrefabs = "";
    // Per-door probability weights are set via "house/{name}/probability" params

    [Header("Cars")]
    public string allowedCarPrefabs = "";
    public float carSpawnChance = 0f;

    [Header("Roofs")]
    public bool enableRoofs = true;

    [Header("Walls")]
    [Range(0f, 1f)]
    public float chanceWallBroken = 0f;
    public string rubblePrefabName = "";
    public float rubbleMass = 0.2f;
    public string rubbleMassMultipliers = "";
    private readonly List<float> _rubbleMassMultipliers = new List<float>();

    [Header("Clutter")]
    [Range(0f, 1f)]
    public float clutterDensity = 1f;

    // ─────────────────────────────────────────────
    //  Runtime state
    // ─────────────────────────────────────────────

    private struct WeightedPrefab {
        public GameObject prefab;
        public float weight;
    }

    private List<WeightedPrefab> _doorEntries = new List<WeightedPrefab>();
    private float _doorWeightSum;

    private List<WeightedPrefab> _carEntries = new List<WeightedPrefab>();
    private float _carWeightSum;

    private GameObject _rubblePrefab;
    private int _houseSeed;
    private bool _paramsLoaded;

    private const string ContainerName = "_HouseLoaderContent";

    // ─────────────────────────────────────────────
    //  Param loading
    // ─────────────────────────────────────────────

    private void LoadParams() {
        _houseSeed = WorldLoadingController.GetDerivedSeed("house");

        // Scalar params — editor values are defaults, episode params override
        enableRoofs      = WorldLoadingController.GetParamFloat("house/enable_roofs", enableRoofs ? 1f : 0f) > 0.5f;
        chanceWallBroken = WorldLoadingController.GetParamFloat("house/chance_wall_broken", chanceWallBroken);
        clutterDensity   = WorldLoadingController.GetParamFloat("house/clutter_density", clutterDensity);
        carSpawnChance   = WorldLoadingController.GetParamFloat("house/car_spawn_chance", carSpawnChance);

        // Doors
        _doorEntries.Clear();
        _doorWeightSum = 0f;
        allowedDoorPrefabs = WorldLoadingController.GetParamString("house/allowed_door_prefabs", allowedDoorPrefabs);
        if (!string.IsNullOrEmpty(allowedDoorPrefabs)) {
            foreach (string raw in allowedDoorPrefabs.Split(',')) {
                string name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                GameObject prefab = Resources.Load<GameObject>(PrefabFolder + name);
                if (prefab == null) {
                    Debug.LogWarning($"HouseLoader: door prefab not found at Resources/{PrefabFolder}{name}");
                    continue;
                }
                float weight = WorldLoadingController.GetParamFloat($"house/{name}/probability", 1f);
                _doorEntries.Add(new WeightedPrefab { prefab = prefab, weight = weight });
                _doorWeightSum += weight;
            }
        }

        // Cars
        _carEntries.Clear();
        _carWeightSum = 0f;
        allowedCarPrefabs = WorldLoadingController.GetParamString("house/allowed_car_prefabs", allowedCarPrefabs);
        if (!string.IsNullOrEmpty(allowedCarPrefabs)) {
            foreach (string raw in allowedCarPrefabs.Split(',')) {
                string name = raw.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                GameObject prefab = Resources.Load<GameObject>(PrefabFolder + name);
                if (prefab == null) {
                    Debug.LogWarning($"HouseLoader: car prefab not found at Resources/{PrefabFolder}{name}");
                    continue;
                }
                float weight = WorldLoadingController.GetParamFloat($"house/{name}/probability", 1f);
                _carEntries.Add(new WeightedPrefab { prefab = prefab, weight = weight });
                _carWeightSum += weight;
            }
        }

        // Rubble
        rubblePrefabName = WorldLoadingController.GetParamString("house/rubble_prefab", rubblePrefabName);
        rubbleMass = WorldLoadingController.GetParamFloat("house/rubble_mass", rubbleMass);
        rubbleMassMultipliers = WorldLoadingController.GetParamString("house/rubble_mass_multipliers", rubbleMassMultipliers);
        _rubbleMassMultipliers.Clear();
        if (!string.IsNullOrEmpty(rubbleMassMultipliers)) {
            foreach (string raw in rubbleMassMultipliers.Split(',')) {
                string tok = raw.Trim();
                if (string.IsNullOrEmpty(tok)) continue;
                if (float.TryParse(tok, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float m))
                    _rubbleMassMultipliers.Add(m);
                else
                    Debug.LogWarning($"HouseLoader: bad rubble_mass_multipliers entry '{tok}' ignored");
            }
        }
        _rubblePrefab = string.IsNullOrEmpty(rubblePrefabName)
            ? null
            : Resources.Load<GameObject>(PrefabFolder + rubblePrefabName);

        _paramsLoaded = true;

        Debug.Log($"HouseLoader: params loaded — " +
            $"doors={_doorEntries.Count} (totalWeight={_doorWeightSum:F1}), " +
            $"cars={_carEntries.Count} (totalWeight={_carWeightSum:F1}, spawnChance={carSpawnChance:F2}), " +
            $"roofs={(enableRoofs ? "on" : "off")}, " +
            $"wallBroken={chanceWallBroken:F2}, " +
            $"rubblePrefab={(_rubblePrefab != null ? _rubblePrefab.name : "none")}, " +
            $"rubbleMass={rubbleMass:F2}x[{string.Join(",", _rubbleMassMultipliers)}], " +
            $"clutterDensity={clutterDensity:F2}");
    }

    // ─────────────────────────────────────────────
    //  WorldStructureProvider
    // ─────────────────────────────────────────────

    public override void OnWorldStructureLoaded(WorldStructure s, int lod) {
        if (!s.structureType.StartsWith("house", System.StringComparison.OrdinalIgnoreCase)) return;
        if (!_paramsLoaded) LoadParams();

        // Destroy any previous container immediately (not deferred) to avoid
        // duplicates if this fires twice in the same frame.
        DestroyContainer(s);

        Transform lodRoot = s.transform.Find($"LOD{lod}");
        if (lodRoot == null) {
            if (verbose) Debug.LogWarning($"HouseLoader: '{s.name}' has no LOD{lod} child");
            return;
        }

        if (verbose) {
            string[] expectedGroups = {
                "doorSpawnPositions", "carSpawnPositions", "roofObjects",
                "breakableWalls", "clutterObjects", "innerLayoutVariants"
            };
            var found = new List<string>();
            var missing = new List<string>();
            foreach (string g in expectedGroups) {
                if (lodRoot.Find(g) != null) found.Add(g);
                else missing.Add(g);
            }
            Debug.Log($"HouseLoader: configuring '{s.name}' LOD{lod} — " +
                $"found=[{string.Join(", ", found)}], missing=[{string.Join(", ", missing)}]");
        }

        // Create a container for all spawned content — easy to destroy atomically.
        GameObject container = new GameObject(ContainerName);
        container.transform.SetParent(s.transform, false);
        container.transform.SetPositionAndRotation(s.transform.position, s.transform.rotation);

        System.Random rng = MakeHouseRng(s);

        ConfigureDoors(lodRoot, container.transform, rng);
        ConfigureCars(lodRoot, container.transform, rng);
        ConfigureRoofs(lodRoot);
        ConfigureBreakableWalls(lodRoot, container.transform, rng);
        ConfigureClutter(lodRoot, rng);
        ConfigureLayoutVariant(lodRoot, rng);

        if (verbose)
            Debug.Log($"HouseLoader: '{s.name}' done — spawned {container.transform.childCount} objects");
    }

    public override void OnWorldStructureUnloaded(WorldStructure s, int lod) {
        if (!s.structureType.StartsWith("house", System.StringComparison.OrdinalIgnoreCase)) return;
        DestroyContainer(s);
    }

    public override void Clear() {
        // Containers are children of structure GOs — destroyed when structures are destroyed.
        _paramsLoaded = false;
    }

    // ─────────────────────────────────────────────
    //  Per-feature configuration
    // ─────────────────────────────────────────────

    private void ConfigureDoors(Transform lodRoot, Transform container, System.Random rng) {
        if (_doorEntries.Count == 0) return;
        Transform group = lodRoot.Find("doorSpawnPositions");
        if (group == null) return;

        int count = 0;
        foreach (Transform spawnPoint in group) {
            GameObject prefab = PickWeighted(_doorEntries, _doorWeightSum, rng);
            if (prefab == null) continue;
            Instantiate(prefab, spawnPoint.position, spawnPoint.rotation, container);
            count++;
        }
        if (verbose) Debug.Log($"  doors: {group.childCount} spawn points, placed {count}");
    }

    private void ConfigureCars(Transform lodRoot, Transform container, System.Random rng) {
        if (_carEntries.Count == 0 || carSpawnChance <= 0f) return;
        Transform group = lodRoot.Find("carSpawnPositions");
        if (group == null) return;

        int count = 0;
        foreach (Transform spawnPoint in group) {
            if ((float)rng.NextDouble() > carSpawnChance) continue;
            GameObject prefab = PickWeighted(_carEntries, _carWeightSum, rng);
            if (prefab == null) continue;
            Instantiate(prefab, spawnPoint.position, spawnPoint.rotation, container);
            count++;
        }
        if (verbose) Debug.Log($"  cars: {group.childCount} spawn points, placed {count}");
    }

    private void ConfigureRoofs(Transform lodRoot) {
        Transform group = lodRoot.Find("roofObjects");
        if (group == null) return;
        group.gameObject.SetActive(enableRoofs);
        if (verbose) Debug.Log($"  roofs: {(enableRoofs ? "enabled" : "disabled")} ({group.childCount} objects)");
    }

    private void ConfigureBreakableWalls(Transform lodRoot, Transform container, System.Random rng) {
        Transform group = lodRoot.Find("breakableWalls");
        if (group == null) return;

        // Per-house random assignment of mass multipliers: shuffle the configured list and
        // hand one out per broken wall in order (Fisher-Yates on a copy, driven by the house rng
        // so the heavy wall is deterministic per seed).
        List<float> multipliers = new List<float>(_rubbleMassMultipliers);
        for (int i = multipliers.Count - 1; i > 0; i--) {
            int j = rng.Next(i + 1);
            (multipliers[i], multipliers[j]) = (multipliers[j], multipliers[i]);
        }

        int broken = 0;
        foreach (Transform wall in group) {
            if ((float)rng.NextDouble() >= chanceWallBroken) continue;

            float wallMass = rubbleMass * (broken < multipliers.Count ? multipliers[broken] : 1f);
            wall.gameObject.SetActive(false);
            broken++;

            if (_rubblePrefab != null) {
                // Instantiate into an inactive scratch root so PersistentDynamicObject.Awake
                // doesn't fire (and reparent children out) before we can configure them.
                GameObject scratch = new GameObject("_rubbleScratch");
                scratch.SetActive(false);
                GameObject rubble = Instantiate(_rubblePrefab, scratch.transform);
                rubble.transform.SetPositionAndRotation(wall.position, wall.rotation);

                foreach (Rigidbody rb in rubble.GetComponentsInChildren<Rigidbody>(true)) {
                    rb.mass = wallMass;
                    if (rb.GetComponent<PersistentDynamicObject>() == null)
                        rb.gameObject.AddComponent<PersistentDynamicObject>();
                }

                rubble.transform.SetParent(container, worldPositionStays: true);
                Destroy(scratch);
                if (verbose) Debug.Log($"  wall '{wall.name}' -> rubble mass {wallMass:F2} per piece");
            }
        }
        if (verbose) Debug.Log($"  walls: {group.childCount} breakable, {broken} broken" +
            (_rubblePrefab == null && broken > 0 ? " (no rubble_prefab configured)" : ""));
    }

    private void ConfigureClutter(Transform lodRoot, System.Random rng) {
        Transform group = lodRoot.Find("clutterObjects");
        if (group == null) return;

        int enabled = 0;
        foreach (Transform child in group) {
            bool enable = (float)rng.NextDouble() < clutterDensity;
            child.gameObject.SetActive(enable);
            if (enable) enabled++;
        }
        if (verbose) Debug.Log($"  clutter: {enabled}/{group.childCount} enabled");
    }

    private void ConfigureLayoutVariant(Transform lodRoot, System.Random rng) {
        Transform group = lodRoot.Find("innerLayoutVariants");
        if (group == null || group.childCount == 0) return;

        int chosen = rng.Next(group.childCount);
        for (int i = 0; i < group.childCount; i++)
            group.GetChild(i).gameObject.SetActive(i == chosen);
        if (verbose) Debug.Log($"  layout: variant {chosen}/{group.childCount} ('{group.GetChild(chosen).name}')");
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private System.Random MakeHouseRng(WorldStructure s) {
        Vector2 center = s.GetCenter2D();
        int seed = _houseSeed
            ^ (Mathf.RoundToInt(center.x * 100f) * 1000003)
            ^ (Mathf.RoundToInt(center.y * 100f) * 999983);
        return new System.Random(seed);
    }

    private static GameObject PickWeighted(List<WeightedPrefab> entries, float totalWeight, System.Random rng) {
        if (entries.Count == 0 || totalWeight <= 0f) return null;

        float roll = (float)rng.NextDouble() * totalWeight;
        float cumulative = 0f;
        for (int i = 0; i < entries.Count; i++) {
            cumulative += entries[i].weight;
            if (roll <= cumulative)
                return entries[i].prefab;
        }
        return entries[entries.Count - 1].prefab;
    }

    private static void DestroyContainer(WorldStructure s) {
        Transform existing = s.transform.Find(ContainerName);
        if (existing != null)
            DestroyImmediate(existing.gameObject);
    }
}
