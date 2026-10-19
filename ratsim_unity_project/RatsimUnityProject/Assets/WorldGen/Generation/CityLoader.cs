using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// WorldStructureProvider that fills the footprint of "container" structures with
/// procedurally scattered prefabs — historically cities with houses, now any structure
/// type listed in <c>city/parent_types</c> (e.g. the cells of a cell_grid maze, each
/// getting one house). This is the generic "scatter inside an area" step of the
/// generation-rules design (WORLDGEN_RULES_DESIGN.md §2.4 / §3.3).
///
/// House prefabs are any WorldStructure prefabs in Resources/WorldGen/WorldStructurePrefabs/
/// whose name starts with "house" (case-insensitive); an explicitly allowed name that is
/// not one of them is loaded from the same folder, so any WorldStructure prefab can be
/// scattered.
///
/// Children are spawned under the container WorldStructure, so they are automatically
/// destroyed when the container is destroyed at episode end. Containers that exist at
/// Generate() (top-level layout structures, maze rooms) are filled eagerly; containers
/// that appear later (chunk-lazy) are filled when they load.
///
/// Config — every key below exists globally as <c>city/&lt;key&gt;</c> and per container
/// type as <c>city/&lt;parent_type&gt;/&lt;key&gt;</c> (per-type wins):
///   city/parent_types            -- CSV of structure types to fill, exact match (default "city")
///   allowed_houses               -- CSV of prefab names ("" = every house* prefab)
///   {prefab}/probability         -- relative weight per prefab (default 1, 0 disables)
///   max_houses, max_attempts     -- count and per-house rejection budget
///   house_spacing                -- min gap between siblings / other structures
///   margin                       -- min distance from the container's footprint edge
///                                   (default = house_spacing, the legacy behaviour)
///   rotation                     -- steps90 (default; legacy: container rotation + k·90°, bounds
///                                   from the UNROTATED footprint) | steps90_fit (same rotations,
///                                   exact rotated extents kept inside the margin) | aligned
///                                   (container rotation) | free (uniform 0–360°, exact extents)
///   layout_mode                  -- random (default) | grid (city road grid; cities only)
/// </summary>
public class CityLoader : WorldStructureProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.StructureContent };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.StructureEvents };

    public bool verbose = false;

    [Header("Layout")]
    public float houseSpacing         = 5f;
    public int   maxHouses            = 40;
    public int   maxPlacementAttempts = 50;

    [Header("Grid Layout")]
    public string layoutMode      = "random";
    public float  gridRoadSpacing = 30f;
    public float  gridSpacingX    = 0f;   // 0 = use gridRoadSpacing
    public float  gridSpacingZ    = 0f;   // 0 = use gridRoadSpacing
    public float  gridMargin      = 15f;
    public float  gridRoadWidth   = 6f;

    [Header("House Selection")]
    // Comma list of house prefab names to use (must exist in Resources/WorldGen/WorldStructurePrefabs/
    // and start with "house"). Empty = use all discovered "house*" prefabs.
    public string allowedHousePrefabs = "";

    [Header("Fill targets")]
    // Comma list of structure types whose footprint gets filled (exact match). Overridden by
    // city/parent_types. Cities are the legacy default; add e.g. "cell" for cell_grid rooms.
    public string parentTypes = "city";

    private WorldStructure[]                 _housePrefabs;
    private readonly HashSet<WorldStructure> _processedCities = new HashSet<WorldStructure>();

    private struct WeightedHouse {
        public WorldStructure prefab;
        public float          weight;
    }
    // Per container type (from city/parent_types); "current" copies are swapped in per container
    // so the grid-layout path and PickHouse keep their single-list shape.
    private readonly Dictionary<string, List<WeightedHouse>> _entriesByParent = new Dictionary<string, List<WeightedHouse>>();
    private readonly Dictionary<string, float>               _weightSumByParent = new Dictionary<string, float>();
    private readonly List<string>                            _parentTypes = new List<string>();
    private List<WeightedHouse> _activeHouseEntries = new List<WeightedHouse>();
    private float _houseWeightSum;
    private bool  _paramsLoaded;

    private const string HousePrefabPath   = "WorldGen/WorldStructurePrefabs/";
    private const string HousePrefabPrefix = "house";

    private void Awake() {
        _housePrefabs = Resources.LoadAll<WorldStructure>(HousePrefabPath)
            .Where(p => p.name.StartsWith(HousePrefabPrefix, System.StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (_housePrefabs.Length == 0)
            Debug.LogWarning($"CityLoader: no house prefabs found in Resources/{HousePrefabPath}");
        else if (verbose)
            Debug.Log($"CityLoader: loaded {_housePrefabs.Length} house prefab(s): " +
                      string.Join(", ", _housePrefabs.Select(p => p.name)));
    }

    // ─────────────────────────────────────────────
    //  WorldStructureProvider
    // ─────────────────────────────────────────────

    // Eagerly populate all cities that WorldLayoutLoader.Generate() has already placed,
    // so house footprints are in WorldData before AgentLoader.Generate() needs them.
    // The _processedCities guard prevents double-processing via OnWorldStructureLoaded later.
    public override void Generate() {
        if (!_paramsLoaded) LoadParams();
        foreach (WorldStructure s in WorldData.GetStructures().ToList()) {
            if (!SelectParent(s)) continue;
            if (_processedCities.Contains(s)) continue;
            _processedCities.Add(s);
            GenerateCityHouses(s);
        }
    }

    public override void OnWorldStructureLoaded(WorldStructure s, int lod) {
        if (!_paramsLoaded) LoadParams();
        if (!SelectParent(s)) return;
        if (_processedCities.Contains(s)) return;

        _processedCities.Add(s);
        GenerateCityHouses(s);
    }

    /// <summary>
    /// True if <paramref name="s"/> is a container type with at least one active prefab;
    /// as a side effect makes that type's prefab list the current one for PickHouse.
    /// </summary>
    private bool SelectParent(WorldStructure s) {
        if (!_entriesByParent.TryGetValue(s.structureType, out List<WeightedHouse> entries)) return false;
        if (entries.Count == 0) return false;
        _activeHouseEntries = entries;
        _houseWeightSum     = _weightSumByParent[s.structureType];
        return true;
    }

    public override void OnWorldStructureUnloaded(WorldStructure s, int lod) {
        // Nothing to do: houses are children of the container, which stays in the scene
        // until episode clear, so they survive an unload. The container stays in
        // _processedCities — re-filling it on reload would add a second set of houses
        // next to the first (max_houses counts per fill). Clear() resets the set.
    }

    public override void Clear() {
        // Houses are children of the container GOs — destroyed when the layout loader destroys those.
        _processedCities.Clear();
        _entriesByParent.Clear();
        _weightSumByParent.Clear();
        _parentTypes.Clear();
        _activeHouseEntries = new List<WeightedHouse>();
        _houseWeightSum = 0f;
        _paramsLoaded   = false;
    }

    // ─────────────────────────────────────────────
    //  Per-container-type params: city/<parent>/<key> wins over city/<key>
    // ─────────────────────────────────────────────

    private const string Missing = "\u0001missing";

    private static string ParentParamString(string parent, string key, string fallback) {
        string v = WorldLoadingController.GetParamString($"city/{parent}/{key}", Missing);
        if (v != Missing) return v;
        return WorldLoadingController.GetParamString($"city/{key}", fallback);
    }

    private static float ParentParamFloat(string parent, string key, float fallback) {
        string v = ParentParamString(parent, key, Missing);
        if (v != Missing && float.TryParse(v, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f)) return f;
        return fallback;
    }

    private static int ParentParamInt(string parent, string key, int fallback) {
        string v = ParentParamString(parent, key, Missing);
        if (v != Missing && int.TryParse(v, out int i)) return i;
        return fallback;
    }

    // ─────────────────────────────────────────────
    //  Param loading (weighted house selection)
    // ─────────────────────────────────────────────

    private void LoadParams() {
        _entriesByParent.Clear();
        _weightSumByParent.Clear();
        _parentTypes.Clear();
        _activeHouseEntries = new List<WeightedHouse>();
        _houseWeightSum = 0f;
        _paramsLoaded = true;

        foreach (string raw in WorldLoadingController.GetParamString("city/parent_types", parentTypes).Split(',')) {
            string t = raw.Trim();
            if (t.Length > 0 && !_parentTypes.Contains(t)) _parentTypes.Add(t);
        }

        foreach (string parent in _parentTypes) {
            List<WeightedHouse> entries = new List<WeightedHouse>();
            float weightSum = 0f;

            string allowed = ParentParamString(parent, "allowed_houses", allowedHousePrefabs);
            IEnumerable<WorldStructure> candidates;
            if (string.IsNullOrWhiteSpace(allowed)) {
                candidates = _housePrefabs;
            } else {
                List<WorldStructure> list = new List<WorldStructure>();
                foreach (string raw in allowed.Split(',')) {
                    string name = raw.Trim();
                    if (string.IsNullOrEmpty(name)) continue;
                    WorldStructure prefab = _housePrefabs.FirstOrDefault(p => p.name == name)
                                            ?? Resources.Load<WorldStructure>(HousePrefabPath + name);
                    if (prefab == null) {
                        Debug.LogWarning($"CityLoader: prefab '{name}' (for '{parent}') not found in Resources/{HousePrefabPath}");
                        continue;
                    }
                    list.Add(prefab);
                }
                candidates = list;
            }

            foreach (WorldStructure prefab in candidates) {
                float weight = ParentParamFloat(parent, $"{prefab.name}/probability", 1f);
                if (weight <= 0f) continue;
                entries.Add(new WeightedHouse { prefab = prefab, weight = weight });
                weightSum += weight;
            }

            _entriesByParent[parent]   = entries;
            _weightSumByParent[parent] = weightSum;

            if (entries.Count == 0) {
                Debug.LogWarning($"CityLoader: no active prefabs for '{parent}' after applying allowed_houses / */probability filters — it will stay empty");
            } else if (verbose) {
                Debug.Log($"CityLoader: '{parent}' prefabs — " +
                    string.Join(", ", entries.Select(e => $"{e.prefab.name}(w={e.weight:F2})")) +
                    $" (totalWeight={weightSum:F2})");
            }
        }
    }

    private WorldStructure PickHouse(System.Random rng) {
        float r   = (float)(rng.NextDouble() * _houseWeightSum);
        float acc = 0f;
        for (int i = 0; i < _activeHouseEntries.Count; i++) {
            acc += _activeHouseEntries[i].weight;
            if (r <= acc) return _activeHouseEntries[i].prefab;
        }
        return _activeHouseEntries[_activeHouseEntries.Count - 1].prefab;
    }

    // ─────────────────────────────────────────────
    //  House generation
    // ─────────────────────────────────────────────

    private void GenerateCityHouses(WorldStructure city) {
        int seed = WorldLoadingController.GetDerivedSeed("city")
                   ^ (Mathf.RoundToInt(city.GetCenter2D().x) * 1000003)
                   ^ (Mathf.RoundToInt(city.GetCenter2D().y) * 999983);
        System.Random rng = new System.Random(seed);

        Bounds2D cityBounds = city.GetBoundingBox2D();
        float    cityRotCCW = city.GetRotationCCW();
        string   parent     = city.structureType;

        // Grid layout (road grid + house rows) is a city thing; other containers scatter.
        string layoutMode = ParentParamString(parent, "layout_mode", parent == "city" ? this.layoutMode : "random");
        if (layoutMode == "grid") {
            GenerateGridLayout(city, rng, cityBounds, cityRotCCW);
            return;
        }

        float  spacing  = ParentParamFloat(parent, "house_spacing", houseSpacing);
        float  margin   = ParentParamFloat(parent, "margin",        spacing);   // legacy: spacing doubles as edge margin
        int    maxCount = ParentParamInt  (parent, "max_houses",    maxHouses);
        int    maxTries = ParentParamInt  (parent, "max_attempts",  maxPlacementAttempts);
        string rotMode  = ParentParamString(parent, "rotation", "steps90").ToLowerInvariant();
        bool   exactExtents = rotMode != "steps90";   // legacy mode keeps its unrotated-footprint bounds

        // Snapshot global obstacles once to avoid repeated WorldData queries.
        List<Bounds2D> globalObstacles = WorldData.GetStructures()
            .Where(s => s != city)
            .Select(s => s.GetBoundingBox2D())
            .ToList();

        List<Bounds2D> placedHouseBounds = new List<Bounds2D>();
        int placed = 0;

        for (int attempt = 0; attempt < maxCount * maxTries && placed < maxCount; attempt++) {
            WorldStructure prefab = PickHouse(rng);
            Vector2        hSize  = GetPrefabSize(prefab);
            if (hSize.sqrMagnitude < 0.01f) continue;

            float houseRotCCW;
            switch (rotMode) {
                case "aligned": houseRotCCW = cityRotCCW; break;
                case "free":    houseRotCCW = cityRotCCW + (float)(rng.NextDouble() * 360.0); break;
                default:        houseRotCCW = cityRotCCW + rng.Next(4) * 90f; break;   // steps90 / steps90_fit
            }

            // Half-extents of the house footprint in the container's frame. Legacy steps90 uses
            // the unrotated footprint (a 90°-turned house may poke past the edge by half the
            // aspect difference — harmless for cities, kept for reproducibility). The other
            // modes use the exact rotated AABB so the house stays inside the margin (fences!).
            float ex = hSize.x * 0.5f, ez = hSize.y * 0.5f;
            if (exactExtents) {
                float rel = (houseRotCCW - cityRotCCW) * Mathf.Deg2Rad;
                float c = Mathf.Abs(Mathf.Cos(rel)), sn = Mathf.Abs(Mathf.Sin(rel));
                ex = hSize.x * 0.5f * c + hSize.y * 0.5f * sn;
                ez = hSize.x * 0.5f * sn + hSize.y * 0.5f * c;
            }
            float halfW = cityBounds.size.x * 0.5f - ex - margin;
            float halfH = cityBounds.size.y * 0.5f - ez - margin;
            if (halfW <= 0f || halfH <= 0f) {
                if (rotMode == "free") continue;   // another rotation may fit
                Debug.LogWarning($"CityLoader: '{city.name}' ({parent}) is too small for prefab '{prefab.name}' with margin {margin}");
                break;
            }

            float   localX   = (float)(rng.NextDouble() * 2.0 - 1.0) * halfW;
            float   localZ   = (float)(rng.NextDouble() * 2.0 - 1.0) * halfH;
            Vector2 worldPos = CityLocalToWorld(new Vector2(localX, localZ), cityBounds);

            // The sampled point is where the FOOTPRINT centre should go. Legacy steps90 puts the
            // prefab root there (the footprint child may be offset — 1.5 m on house_basic,
            // 8.9 m on house_mall — so the footprint lands off by that much); the exact modes
            // move the root so the footprint really is at worldPos and stays inside the margin.
            Vector2 rootPos = exactExtents ? worldPos - RotatedFootprintOffset(prefab, houseRotCCW) : worldPos;

            Bounds2D candidate = new Bounds2D(worldPos, hSize + Vector2.one * spacing * 2f, houseRotCCW);

            if (placedHouseBounds.Any(b => b.Overlaps(candidate))) continue;
            if (globalObstacles.Any(b => b.Overlaps(candidate))) continue;

            // Spawn parented to the container — auto-destroyed when it is cleared.
            WorldStructure house = WorldData.SpawnStructure(
                prefab.name, rootPos, houseRotCCW, city.transform
            );
            if (house == null) continue;

            placedHouseBounds.Add(new Bounds2D(worldPos, hSize, houseRotCCW));
            placed++;

            if (verbose) Debug.Log($"CityLoader: placed '{prefab.name}' at {worldPos}, rot={houseRotCCW:F1}°");
        }

        if (verbose || placed < 1)
            Debug.Log($"CityLoader: placed {placed}/{maxCount} houses in '{city.name}'");
    }

    // ─────────────────────────────────────────────
    //  Grid layout mode
    // ─────────────────────────────────────────────

    private void GenerateGridLayout(WorldStructure city, System.Random rng, Bounds2D cityBounds, float cityRotCCW) {
        float spacingDefault = WorldLoadingController.GetParamFloat("city/grid_road_spacing", gridRoadSpacing);
        float rawSpacingX    = WorldLoadingController.GetParamFloat("city/grid_road_spacing_x", gridSpacingX);
        float rawSpacingZ    = WorldLoadingController.GetParamFloat("city/grid_road_spacing_z", gridSpacingZ);
        float spacingX  = rawSpacingX > 0f ? rawSpacingX : spacingDefault;
        float spacingZ  = rawSpacingZ > 0f ? rawSpacingZ : spacingDefault;
        float margin    = WorldLoadingController.GetParamFloat("city/grid_margin",     gridMargin);
        float roadWidth = WorldLoadingController.GetParamFloat("city/grid_road_width", gridRoadWidth);

        float innerW = cityBounds.size.x - 2f * margin;
        float innerH = cityBounds.size.y - 2f * margin;
        if (innerW <= 0f || innerH <= 0f) {
            Debug.LogWarning($"CityLoader: city '{city.name}' too small for grid layout (margin={margin})");
            return;
        }

        // 3a. Compute grid lines (spacingX → N-S road separation, spacingZ → E-W road separation)
        int nX = Mathf.Max(2, Mathf.FloorToInt(innerW / spacingX) + 1);
        int nZ = Mathf.Max(2, Mathf.FloorToInt(innerH / spacingZ) + 1);
        float actualSpacingX = innerW / (nX - 1);
        float actualSpacingZ = innerH / (nZ - 1);

        float[] gridX = new float[nX];
        float[] gridZ = new float[nZ];
        for (int i = 0; i < nX; i++) gridX[i] = -innerW / 2f + i * actualSpacingX;
        for (int j = 0; j < nZ; j++) gridZ[j] = -innerH / 2f + j * actualSpacingZ;

        // 3b. Spawn grid roads
        // E-W roads (one per gridZ value)
        for (int j = 0; j < nZ; j++) {
            Vector2 localMid = new Vector2(0f, gridZ[j]);
            Vector2 worldMid = CityLocalToWorld(localMid, cityBounds);
            WorldData.SpawnStructure(
                "road", worldMid, cityRotCCW, city.transform,
                sizeOverride: new Vector2(innerW, roadWidth)
            );
        }

        // N-S roads (one per gridX value)
        for (int i = 0; i < nX; i++) {
            Vector2 localMid = new Vector2(gridX[i], 0f);
            Vector2 worldMid = CityLocalToWorld(localMid, cityBounds);
            WorldData.SpawnStructure(
                "road", worldMid, cityRotCCW + 90f, city.transform,
                sizeOverride: new Vector2(innerH, roadWidth)
            );
        }

        if (verbose)
            Debug.Log($"CityLoader grid: {nX} N-S roads, {nZ} E-W roads in '{city.name}'");

        // 3c. Connect entry point stubs
        if (WorldServices.Has<ILayoutProvider>()) {
            List<EntryPoint> entryPoints = WorldServices.Get<ILayoutProvider>().GetEntryPoints(city);
            float halfW = cityBounds.size.x * 0.5f;
            float halfH = cityBounds.size.y * 0.5f;

            foreach (EntryPoint ep in entryPoints) {
                Vector2 localEP = WorldToLocal(ep.position, cityBounds);

                // Determine which OBB edge the EP is on
                float ratioX = Mathf.Abs(localEP.x) / halfW;
                float ratioZ = Mathf.Abs(localEP.y) / halfH;

                Vector2 target;
                if (ratioX > ratioZ) {
                    // On E or W edge → connect to nearest gridZ at ±innerW/2
                    float nearestZ = FindNearest(gridZ, localEP.y);
                    float edgeX = Mathf.Sign(localEP.x) * innerW / 2f;
                    target = new Vector2(edgeX, nearestZ);
                } else {
                    // On N or S edge → connect to nearest gridX at ±innerH/2
                    float nearestX = FindNearest(gridX, localEP.x);
                    float edgeZ = Mathf.Sign(localEP.y) * innerH / 2f;
                    target = new Vector2(nearestX, edgeZ);
                }

                Vector2 targetWorld = CityLocalToWorld(target, cityBounds);
                Vector2 stubMid     = (ep.position + targetWorld) * 0.5f;
                float   stubLength  = Vector2.Distance(ep.position, targetWorld);
                float   stubAngle   = Mathf.Atan2(
                    targetWorld.y - ep.position.y,
                    targetWorld.x - ep.position.x) * Mathf.Rad2Deg;

                if (stubLength > 0.1f) {
                    WorldData.SpawnStructure(
                        "road", stubMid, stubAngle, city.transform,
                        sizeOverride: new Vector2(stubLength, roadWidth)
                    );
                }
            }

            if (verbose && entryPoints.Count > 0)
                Debug.Log($"CityLoader grid: connected {entryPoints.Count} entry point stubs in '{city.name}'");
        }

        // 3d. Place houses in blocks
        List<Bounds2D> placedHouseBounds = new List<Bounds2D>();
        int totalPlaced = 0;

        for (int i = 0; i < nX - 1; i++) {
            for (int j = 0; j < nZ - 1; j++) {
                float blockXMin = gridX[i]     + roadWidth / 2f;
                float blockXMax = gridX[i + 1] - roadWidth / 2f;
                float blockZMin = gridZ[j]     + roadWidth / 2f;
                float blockZMax = gridZ[j + 1] - roadWidth / 2f;

                float blockW = blockXMax - blockXMin;
                float blockH = blockZMax - blockZMin;
                if (blockW < 4f || blockH < 4f) continue;

                // Place houses along each edge of the block
                // North row (near blockZMax): door faces +Z → rot offset 0
                totalPlaced += PlaceHouseRow(rng, city, cityBounds, cityRotCCW,
                    blockXMin, blockXMax, blockZMax, true, 0f,
                    placedHouseBounds);

                // South row (near blockZMin): door faces -Z → rot offset 180
                totalPlaced += PlaceHouseRow(rng, city, cityBounds, cityRotCCW,
                    blockXMin, blockXMax, blockZMin, true, 180f,
                    placedHouseBounds);

                // East row (near blockXMax): door faces +X → rot offset 270
                // Inset by one house depth to avoid corner overlap
                float cornerInset = GetSmallestHouseDepth();
                float rowZMin = blockZMin + cornerInset;
                float rowZMax = blockZMax - cornerInset;
                if (rowZMax > rowZMin) {
                    totalPlaced += PlaceHouseRow(rng, city, cityBounds, cityRotCCW,
                        rowZMin, rowZMax, blockXMax, false, 270f,
                        placedHouseBounds);
                }

                // West row (near blockXMin): door faces -X → rot offset 90
                if (rowZMax > rowZMin) {
                    totalPlaced += PlaceHouseRow(rng, city, cityBounds, cityRotCCW,
                        rowZMin, rowZMax, blockXMin, false, 90f,
                        placedHouseBounds);
                }
            }
        }

        if (verbose || totalPlaced < 1)
            Debug.Log($"CityLoader grid: placed {totalPlaced} houses in '{city.name}'");
    }

    /// <summary>
    /// Place a row of houses along one edge of a block.
    /// </summary>
    /// <param name="horizontal">true = row runs along X axis (N/S edge), false = along Z axis (E/W edge)</param>
    /// <param name="rotOffset">Rotation offset from city rotation for door direction</param>
    private const float GridHouseGap = 0.5f; // tight packing gap between houses in grid mode

    private int PlaceHouseRow(System.Random rng, WorldStructure city, Bounds2D cityBounds, float cityRotCCW,
        float lineMin, float lineMax, float perpCoord, bool horizontal, float rotOffset,
        List<Bounds2D> placedHouseBounds) {

        int placed = 0;
        float cursor = lineMin;
        float houseRotCCW = cityRotCCW + rotOffset;

        while (cursor < lineMax) {
            WorldStructure prefab = PickHouse(rng);
            Vector2 hSize = GetPrefabSize(prefab);
            if (hSize.sqrMagnitude < 0.01f) continue;

            // For horizontal rows, house width runs along X; for vertical, along Z
            float houseWidth = horizontal ? hSize.x : hSize.y;
            float houseDepth = horizontal ? hSize.y : hSize.x;

            if (cursor + houseWidth > lineMax) break;

            float alongCenter = cursor + houseWidth / 2f;

            // Offset house center inward from the road edge by half its depth
            float perpCenter;
            if (rotOffset == 0f)       perpCenter = perpCoord - houseDepth / 2f;  // North: shift -Z
            else if (rotOffset == 180f) perpCenter = perpCoord + houseDepth / 2f; // South: shift +Z
            else if (rotOffset == 270f) perpCenter = perpCoord - houseDepth / 2f; // East: shift -X
            else                         perpCenter = perpCoord + houseDepth / 2f; // West: shift +X

            Vector2 localPos = horizontal
                ? new Vector2(alongCenter, perpCenter)
                : new Vector2(perpCenter, alongCenter);

            Vector2 worldPos = CityLocalToWorld(localPos, cityBounds);
            Bounds2D candidate = new Bounds2D(worldPos, hSize, houseRotCCW);

            // Block edges are already inset by roadWidth/2, so no road overlap check needed.
            if (!placedHouseBounds.Any(b => b.Overlaps(candidate))) {
                WorldStructure house = WorldData.SpawnStructure(
                    prefab.name, worldPos, houseRotCCW, city.transform
                );
                if (house != null) {
                    placedHouseBounds.Add(candidate);
                    placed++;
                }
            }

            cursor += houseWidth + GridHouseGap;
        }

        return placed;
    }

    private float GetSmallestHouseDepth() {
        float minDepth = float.MaxValue;
        IEnumerable<WorldStructure> source = _activeHouseEntries.Count > 0
            ? _activeHouseEntries.Select(e => e.prefab)
            : (IEnumerable<WorldStructure>)_housePrefabs;
        foreach (var prefab in source) {
            Vector2 size = GetPrefabSize(prefab);
            if (size.sqrMagnitude > 0.01f)
                minDepth = Mathf.Min(minDepth, Mathf.Min(size.x, size.y));
        }
        return minDepth == float.MaxValue ? 0f : minDepth;
    }

    private float FindNearest(float[] values, float target) {
        float best = values[0];
        float bestDist = Mathf.Abs(target - best);
        for (int i = 1; i < values.Length; i++) {
            float d = Mathf.Abs(target - values[i]);
            if (d < bestDist) { best = values[i]; bestDist = d; }
        }
        return best;
    }

    private Vector2 WorldToLocal(Vector2 worldPos, Bounds2D cityBounds) {
        Vector2 delta = worldPos - cityBounds.center;
        float   rad   = -cityBounds.rotation * Mathf.Deg2Rad;
        float   cosR  = Mathf.Cos(rad), sinR = Mathf.Sin(rad);
        return new Vector2(
            delta.x * cosR - delta.y * sinR,
            delta.x * sinR + delta.y * cosR
        );
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private Vector2 CityLocalToWorld(Vector2 localPos, Bounds2D cityBounds) {
        float   rad  = cityBounds.rotation * Mathf.Deg2Rad;
        float   cosR = Mathf.Cos(rad), sinR = Mathf.Sin(rad);
        Vector2 rot  = new Vector2(
            localPos.x * cosR - localPos.y * sinR,
            localPos.x * sinR + localPos.y * cosR
        );
        return cityBounds.center + rot;
    }

    /// <summary>
    /// XZ offset from the prefab root to its footprint centre, rotated into world space for a
    /// structure spawned with <paramref name="rotCCW"/> (SpawnStructure uses euler Y = -rotCCW).
    /// </summary>
    private static Vector2 RotatedFootprintOffset(WorldStructure prefab, float rotCCW) {
        if (prefab == null || prefab.footprintCollider == null) return Vector2.zero;
        Vector3 local = prefab.transform.InverseTransformPoint(
            prefab.footprintCollider.transform.TransformPoint(prefab.footprintCollider.center));
        Vector3 world = Quaternion.Euler(0f, -rotCCW, 0f) * new Vector3(local.x, 0f, local.z);
        return new Vector2(world.x, world.z);
    }

    private Vector2 GetPrefabSize(WorldStructure prefab) {
        if (prefab == null || prefab.footprintCollider == null) return Vector2.zero;
        Vector3 s  = prefab.footprintCollider.size;
        Vector3 ls = prefab.footprintCollider.transform.lossyScale;
        return new Vector2(s.x * ls.x, s.z * ls.z);
    }
}
