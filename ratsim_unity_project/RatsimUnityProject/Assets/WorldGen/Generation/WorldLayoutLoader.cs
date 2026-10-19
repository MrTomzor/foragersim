using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class WorldLayoutLoader : WorldDataProvider, ILayoutProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Layout };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height };

    public bool verbose = false;
    private bool _generated = false;
    private readonly Dictionary<WorldStructure, List<EntryPoint>> _structureEntryPoints
        = new Dictionary<WorldStructure, List<EntryPoint>>();
    
    public int maxAttempts = 50;
    public int baseSeed = 0;

    [Header("Road Generation")]
    public int   perimeterSamples = 20;
    public float extraEdgeRatio   = 0.3f;
    public float roadWidth        = 8f;

    private const string PrefabPath = "WorldGen/WorldStructurePrefabs/";
    private readonly Dictionary<string, WorldStructure> _prefabCache = new Dictionary<string, WorldStructure>();
    
    protected override void OnEnable() {
        base.OnEnable();
        // Always register at enable so CityLoader etc. find us in default mode even
        // before an episode has started. Generate() re-registers when this loader
        // is the active layout provider, overwriting any previously active loader.
        WorldServices.Register<ILayoutProvider>(this);
    }

    public List<EntryPoint> GetEntryPoints(WorldStructure s) {
        return _structureEntryPoints.TryGetValue(s, out List<EntryPoint> eps) ? eps : new List<EntryPoint>();
    }

    private void RegisterEntryPoint(EntryPoint ep) {
        if (ep.structure == null) return;
        if (!_structureEntryPoints.TryGetValue(ep.structure, out List<EntryPoint> list)) {
            list = new List<EntryPoint>();
            _structureEntryPoints[ep.structure] = list;
        }
        if (!list.Contains(ep))
            list.Add(ep);
    }

    // ─────────────────────────────────────────────
    //  Prefab loading
    // ─────────────────────────────────────────────

    private WorldStructure LoadPrefab(string structureType) {
        if (_prefabCache.TryGetValue(structureType, out WorldStructure cached))
            return cached;

        WorldStructure prefab = Resources.Load<WorldStructure>($"{PrefabPath}{structureType}");
        if (prefab == null)
            Debug.LogWarning($"WorldLayoutLoader: no prefab found at Resources/{PrefabPath}{structureType}");

        _prefabCache[structureType] = prefab; // cache even if null to avoid repeated failed loads
        return prefab;
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void Generate() {
        if (_generated) return;
        string mode = WorldLoadingController.GetParamString("layout/mode", "default");
        if (!mode.Equals("default", System.StringComparison.OrdinalIgnoreCase)) {
            // A different layout provider is active (e.g. MazeLayoutLoader). Stand down so
            // we don't double-register entry points or compete on structure placement.
            if (verbose) Debug.Log($"WorldLayoutLoader: layout/mode='{mode}', skipping default layout");
            return;
        }
        // This loader is the active one — make sure ILayoutProvider points here
        // (prior episodes may have been in maze mode and left it pointing elsewhere).
        WorldServices.Register<ILayoutProvider>(this);
        _generated = true;
        DoGenerate();
    }

    public override void Clear() {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        _generated = false;
        _structureEntryPoints.Clear();
    }

    // ─────────────────────────────────────────────
    //  Generation
    // ─────────────────────────────────────────────

    private void DoGenerate() {
        float worldW = WorldLoadingController.GetParamFloat("world_bounds/width");
        float worldH = WorldLoadingController.GetParamFloat("world_bounds/height");
        float margin = WorldLoadingController.GetParamFloat("world_bounds/structures_margin", 100f);
        string roadGenerationMode = WorldLoadingController.GetParamString("layout/roads/mode", "highway");
        maxAttempts = Mathf.Max(1, WorldLoadingController.GetParamInt("layout/max_layout_attempts", 10));
        baseSeed = WorldLoadingController.GetDerivedSeed("layout");

        // Snapshot type requirements once — used to validate each attempt and to report unmet constraints.
        var requirements = GetTypeRequirements();

        List<WorldStructure> structures = null;
        Dictionary<string, int> placedByType = null;
        int attemptsUsed = 0;
        bool satisfied = false;

        for (int attempt = 0; attempt < maxAttempts; attempt++) {
            attemptsUsed = attempt + 1;
            // Wipe anything spawned by a previous failed attempt.
            ClearSpawnedStructures();

            // Deterministic per-attempt seed: fixed master seed → identical retry sequence.
            // HashCode.Combine instead of XOR — XOR(baseSeed, attempt) has overlapping
            // cosets across adjacent base seeds (e.g. seed=43,attempt=0 produces the same
            // rng seed as seed=42,attempt=1), causing seed bumps to converge to the same
            // world after retries.
            // SeedMix, not System.HashCode: HashCode is randomised per process/domain reload.
            System.Random rng = new System.Random(SeedMix.Combine(baseSeed, attempt));
            structures = PlaceStructures(rng, worldW, worldH, margin, out placedByType);

            satisfied = true;
            foreach (var kvp in requirements) {
                int got = placedByType.TryGetValue(kvp.Key, out int g) ? g : 0;
                if (got < kvp.Value.min) { satisfied = false; break; }
            }
            if (satisfied) break;
        }

        if (!satisfied) {
            var unmet = new List<string>();
            foreach (var kvp in requirements) {
                int got = placedByType != null && placedByType.TryGetValue(kvp.Key, out int g) ? g : 0;
                if (got < kvp.Value.min)
                    unmet.Add($"{kvp.Key} (placed {got}, required min {kvp.Value.min})");
            }
            WorldGenStatus.Error("WorldLayoutLoader",
                $"Failed to satisfy structure layout constraints after {attemptsUsed} attempts " +
                $"(layout/max_layout_attempts={maxAttempts}, seed={WorldLoadingController.GetSeed()}). " +
                $"Unmet: {string.Join("; ", unmet)}. " +
                $"Consider increasing layout/max_layout_attempts, layout/per_type_placement_attempts, " +
                $"world_bounds/width|height, or relaxing min counts.");
        } else if (attemptsUsed > 1) {
            WorldGenStatus.Info("WorldLayoutLoader",
                $"Layout constraints satisfied on attempt {attemptsUsed}/{maxAttempts} (seed={WorldLoadingController.GetSeed()}).");
        }

        List<RoadEdge>       edges = roadGenerationMode == "highway" ? BuildRoadNetwork(structures) : new List<RoadEdge>();

        // assemble road nodes
        var nodeByPos = new Dictionary<Vector2, RoadNode>();
        int nextId = 0;
        RoadNode GetOrCreateNode(EntryPoint ep) {
            if (!nodeByPos.TryGetValue(ep.position, out RoadNode node)) {
                node = new RoadNode {
                    id         = nextId++,
                    position   = ep.position,
                    entryPoint = ep,
                    edges      = new List<RoadEdge>()
                };
                nodeByPos[ep.position] = node;
            }
            return node;
        }

        foreach (var edge in edges) {
            edge.fromNode = GetOrCreateNode(edge.fromEP);
            edge.toNode   = GetOrCreateNode(edge.toEP);
            edge.fromNode.edges.Add(edge);
            edge.toNode.edges.Add(edge);
        }

        foreach (var node in nodeByPos.Values)
            node.isCrossroad = node.edges.Count > 2;

        if (debugVisualizeLayoutOnGen) Debug_VisualizeLayout();
        if (verbose) Debug.Log($"WorldLayoutLoader: {structures.Count} structures, {edges.Count} edges.");

        // Clear transforms cuz we are modifying the colliders of the structs:
        Physics.SyncTransforms();

        // Process terrain modifications now that all structures are placed.
        // This scans structures for TerrainModification components, registers
        // height influence zones, and repositions structures to match terrain.
        WorldServices.Get<IHeightProvider>().ProcessTerrainModifications();
    }

    // ─────────────────────────────────────────────
    //  Structure placement
    // ─────────────────────────────────────────────

    /// <summary>
     /// Destroys all structures previously spawned by this loader (its children).
     /// Used to wipe a failed placement attempt before retrying.
     /// Manually-placed structures (not children of this loader) are preserved.
     /// </summary>
    private void ClearSpawnedStructures() {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        _structureEntryPoints.Clear();
    }

    /// <summary>
    /// Reads (min, max) counts for every configured structure type. Types with max=0 are
    /// excluded from the requirements map (they're explicitly disabled).
    /// </summary>
    private Dictionary<string, (int min, int max)> GetTypeRequirements() {
        var reqs = new Dictionary<string, (int, int)>();
        foreach (string type in GetConfiguredStructureTypes()) {
            int min = WorldLoadingController.GetParamInt($"layout/structures/{type}/min", 0);
            int max = WorldLoadingController.GetParamInt($"layout/structures/{type}/max", 0);
            if (max == 0) continue;
            reqs[type] = (min, max);
        }
        return reqs;
    }

    private List<WorldStructure> PlaceStructures(System.Random rng, float worldW, float worldH, float margin,
            out Dictionary<string, int> placedCounts) {
        placedCounts = new Dictionary<string, int>();

        // include manually pre-placed structures (not children of this loader)
        List<WorldStructure> placed = FindObjectsByType<WorldStructure>(FindObjectsSortMode.None)
            .Where(s => s.transform.parent != transform)
            .ToList();

        if (verbose && placed.Count > 0)
            Debug.Log($"WorldLayoutLoader: found {placed.Count} manually placed structures");

        // Per-type rejection-sampling budget. Default 200 matches the previous hard-coded cap.
        int perTypeBudget = Mathf.Max(1,
            WorldLoadingController.GetParamInt("layout/per_type_placement_attempts", 200));

        // discover which types are requested via config
        // we try to load a prefab for each type that has a non-zero max
        var configuredTypes = GetConfiguredStructureTypes();

        foreach (string type in configuredTypes) {
            int min = WorldLoadingController.GetParamInt($"layout/structures/{type}/min", 0);
            int max = WorldLoadingController.GetParamInt($"layout/structures/{type}/max", 0);
            if (max == 0) { placedCounts[type] = 0; continue; }

            WorldStructure prefab = LoadPrefab(type);
            if (prefab == null) { placedCounts[type] = 0; continue; }

            Vector2 size = GetSizeFromPrefab(prefab);
            if (size.sqrMagnitude < 0.01f) {
                Debug.LogWarning($"WorldLayoutLoader: prefab '{type}' has no valid footprint collider size, skipping");
                placedCounts[type] = 0;
                continue;
            }

            int count = rng.Next(min, max + 1);
            int placedCount = 0, attempts = 0;

            while (placedCount < count && attempts < perTypeBudget) {
                attempts++;

                float   rot    = (float)rng.NextDouble() * 360f;
                Vector2 center = new Vector2(
                    margin + (float)rng.NextDouble() * (worldW - margin * 2f) - worldW * 0.5f,
                    margin + (float)rng.NextDouble() * (worldH - margin * 2f) - worldH * 0.5f
                );

                Bounds2D candidate = new Bounds2D(center, size, rot);
                if (!CandidateFitsInWorld(candidate, worldW, worldH, margin)) continue;
                if (placed.Any(s => candidate.Overlaps(s.GetBoundingBox2D()))) continue;

                /*WorldStructure instance = Instantiate(
                    prefab,
                    new Vector3(center.x, 0f, center.y),
                    Quaternion.Euler(0f, -rot, 0f),
                    transform
                );
                instance.name = $"{type}_{placedCount}";
                // WorldStructure.Awake registers with WorldData automatically

                placed.Add(instance);*/
                placed.Add(WorldData.SpawnStructure(type, center, rot, transform, sizeOverride: size));

                placedCount++;
                if (verbose) Debug.Log($"Placed {type} at {center}, rot={rot:F1}°, size={size}");
            }

            placedCounts[type] = placedCount;
            if (placedCount < min && verbose)
                Debug.Log($"WorldLayoutLoader: attempt placed {placedCount}/{min} of '{type}' (will retry if attempts remain)");
        }

        return placed;
    }

    private static bool CandidateFitsInWorld(Bounds2D candidate, float worldW, float worldH, float margin) {
        float halfW = worldW * 0.5f - margin;
        float halfH = worldH * 0.5f - margin;
        foreach (Vector2 v in candidate.GetVertices()) {
            if (v.x < -halfW || v.x > halfW || v.y < -halfH || v.y > halfH)
                return false;
        }
        return true;
    }

    // reads all structure type names that have a max > 0 in config
    private List<string> GetConfiguredStructureTypes() {
        var types = new List<string>();
        // read from a flat list param or discover by scanning known prefix
        // convention: layout/structures/types = "city,village,orchard,farm"
        string typeList = WorldLoadingController.GetParamString("layout/structures/types", "");
        if (!string.IsNullOrEmpty(typeList))
            return typeList.Split(',').Select(t => t.Trim()).ToList();

        // fallback: scan for any key matching layout/structures/{type}/max
        // this requires WorldLoadingController to expose param keys — see note below
        Debug.LogWarning("WorldLayoutLoader: 'layout/structures/types' not set in config — no structures will be placed");
        return types;
    }

    private Vector2 GetSizeFromPrefab(WorldStructure prefab) {
        if (prefab.footprintCollider == null) return Vector2.zero;
        Vector3 s  = prefab.footprintCollider.size;
        Vector3 ls = prefab.footprintCollider.transform.lossyScale;
        return new Vector2(s.x * ls.x, s.z * ls.z);
    }

    // ─────────────────────────────────────────────
    //  Road network
    // ─────────────────────────────────────────────

    private List<RoadEdge> BuildRoadNetwork(List<WorldStructure> structures) {
        List<RoadEdge> edges = new List<RoadEdge>();
        if (structures.Count < 2) return edges;

        int[] parent = Enumerable.Range(0, structures.Count).ToArray();
        int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
        void Union(int a, int b) { parent[Find(a)] = Find(b); }
        bool Connected(int a, int b) => Find(a) == Find(b);
        bool AllConnected() => structures.Select((_, i) => Find(i)).Distinct().Count() == 1;

        var pairs = new List<(int iA, int iB, float dist)>();
        for (int i = 0; i < structures.Count; i++)
        for (int j = i+1; j < structures.Count; j++)
            pairs.Add((i, j, Vector2.Distance(structures[i].GetCenter2D(), structures[j].GetCenter2D())));
        pairs.Sort((a, b) => a.dist.CompareTo(b.dist));

        int  extraEdges  = Mathf.Max(1, Mathf.RoundToInt(structures.Count * extraEdgeRatio));
        int  extraAdded  = 0;
        bool mstComplete = false;

        foreach (var (iA, iB, _) in pairs) {
            if (mstComplete && extraAdded >= extraEdges) break;
            if (mstComplete && Connected(iA, iB)) continue;

            RoadEdge edge = TryConnectStructures(structures[iA], structures[iB], structures);
            if (edge == null) {
                Debug.LogWarning($"WorldLayoutLoader: no unblocked path between " +
                                 $"{structures[iA].structureType} and {structures[iB].structureType}");
                continue;
            }

            SpawnRoadSegment(edge);
            RegisterEntryPoint(edge.fromEP);
            RegisterEntryPoint(edge.toEP);
            edges.Add(edge);

            if (!Connected(iA, iB)) {
                Union(iA, iB);
                if (AllConnected()) mstComplete = true;
            } else {
                extraAdded++;
            }
        }

        foreach (var edge in edges)
            edge.waypoints = StraightPath(edge.fromEP.position, edge.toEP.position);

        return edges;
    }

    private void SpawnRoadSegment(RoadEdge edge) {
        Vector2 mid    = (edge.fromEP.position + edge.toEP.position) * 0.5f;
        float   length = Vector2.Distance(edge.fromEP.position, edge.toEP.position);
        float   angle  = Mathf.Atan2(
                            edge.toEP.position.y - edge.fromEP.position.y,
                            edge.toEP.position.x - edge.fromEP.position.x) * Mathf.Rad2Deg;
        Debug.Log($"Spawning road from {edge.fromEP.position} to {edge.toEP.position}, mid={mid}, length={length}, angle={angle}");
        edge.roadStructure = WorldData.SpawnStructure(
            "road", mid, angle, transform,
            sizeOverride: new Vector2(length, edge.width)
        );
    }

    // ─────────────────────────────────────────────
    //  Perimeter connection
    // ─────────────────────────────────────────────

    private RoadEdge TryConnectStructures(WorldStructure sA, WorldStructure sB, List<WorldStructure> all) {
        Vector2[] vertsA = sA.GetBoundingBox2D().GetVertices();
        Vector2[] vertsB = sB.GetBoundingBox2D().GetVertices();
        float[]   elA    = EdgeLengths(vertsA);
        float[]   elB    = EdgeLengths(vertsB);
        float     totA   = elA.Sum();
        float     totB   = elB.Sum();

        float      bestDist = float.MaxValue;
        EntryPoint bestEpA  = null;
        EntryPoint bestEpB  = null;

        for (int i = 0; i < perimeterSamples; i++)
        for (int j = 0; j < perimeterSamples; j++) {
            float tA = (i + 0.5f) / perimeterSamples * totA;
            float tB = (j + 0.5f) / perimeterSamples * totB;

            Vector2 pA = SamplePerimeter(vertsA, elA, totA, tA, out Vector2 nA);
            Vector2 pB = SamplePerimeter(vertsB, elB, totB, tB, out Vector2 nB);

            float d = Vector2.Distance(pA, pB);
            if (d >= bestDist) continue;
            if (LineIntersectsAnyStructure(pA, pB, all, sA, sB)) continue;

            bestDist = d;
            bestEpA  = new EntryPoint { structure = sA, position = pA, outwardNormal = nA };
            bestEpB  = new EntryPoint { structure = sB, position = pB, outwardNormal = nB };
        }

        if (bestEpA == null) return null;
        return new RoadEdge { fromEP = bestEpA, toEP = bestEpB, width = roadWidth, waypoints = new List<Vector2>() };
    }

    // ─────────────────────────────────────────────
    //  Perimeter sampling
    // ─────────────────────────────────────────────

    private float[] EdgeLengths(Vector2[] verts) {
        var el = new float[4];
        for (int i = 0; i < 4; i++)
            el[i] = Vector2.Distance(verts[i], verts[(i+1)%4]);
        return el;
    }

    private Vector2 SamplePerimeter(Vector2[] verts, float[] el, float total, float t, out Vector2 normal) {
        t = t % total;
        for (int i = 0; i < 4; i++) {
            if (t <= el[i]) {
                float   frac = t / el[i];
                Vector2 pos  = Vector2.Lerp(verts[i], verts[(i+1)%4], frac);
                Vector2 dir  = (verts[(i+1)%4] - verts[i]) / el[i];
                normal = new Vector2(dir.y, -dir.x);
                return pos;
            }
            t -= el[i];
        }
        normal = Vector2.up;
        return verts[0];
    }

    // ─────────────────────────────────────────────
    //  Visibility
    // ─────────────────────────────────────────────

    private bool LineIntersectsAnyStructure(Vector2 a, Vector2 b,
            List<WorldStructure> all, WorldStructure ownerA, WorldStructure ownerB) {
        foreach (var s in all) {
            if (s == ownerA || s == ownerB) continue;
            if (LineIntersectsBounds2D(a, b, s.GetBoundingBox2D())) return true;
        }
        return false;
    }

    private bool LineIntersectsBounds2D(Vector2 a, Vector2 b, Bounds2D bounds) {
        float   rad    = -bounds.rotation * Mathf.Deg2Rad;
        float   cosR   = Mathf.Cos(rad), sinR = Mathf.Sin(rad);
        Vector2 localA = RotateAround(a, bounds.center, cosR, sinR);
        Vector2 localB = RotateAround(b, bounds.center, cosR, sinR);
        Vector2 half   = bounds.size * 0.5f;
        return SegmentIntersectsAABB(localA, localB, -half, half);
    }

    private Vector2 RotateAround(Vector2 p, Vector2 pivot, float cosR, float sinR) {
        Vector2 d = p - pivot;
        return new Vector2(d.x * cosR - d.y * sinR, d.x * sinR + d.y * cosR);
    }

    private bool SegmentIntersectsAABB(Vector2 a, Vector2 b, Vector2 min, Vector2 max) {
        Vector2 d    = b - a;
        float   tMin = 0f, tMax = 1f;
        for (int axis = 0; axis < 2; axis++) {
            float da   = axis == 0 ? d.x   : d.y;
            float aVal = axis == 0 ? a.x   : a.y;
            float minV = axis == 0 ? min.x : min.y;
            float maxV = axis == 0 ? max.x : max.y;
            if (Mathf.Abs(da) < 1e-6f) {
                if (aVal < minV || aVal > maxV) return false;
            } else {
                float t1 = (minV - aVal) / da, t2 = (maxV - aVal) / da;
                if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
                tMin = Mathf.Max(tMin, t1);
                tMax = Mathf.Min(tMax, t2);
                if (tMin > tMax) return false;
            }
        }
        return true;
    }

    private List<Vector2> StraightPath(Vector2 from, Vector2 to) {
        var pts   = new List<Vector2>();
        int steps = Mathf.Max(2, Mathf.RoundToInt(Vector2.Distance(from, to) / 50f));
        for (int i = 0; i <= steps; i++)
            pts.Add(Vector2.Lerp(from, to, i / (float)steps));
        return pts;
    }

    // ─────────────────────────────────────────────
    //  Debug
    // ─────────────────────────────────────────────

    [Header("Debug")]
    public bool     debugVisualizeLayoutOnGen = false;
    public Material debugEdgeMaterial;
    private GameObject _debugRoot;

    [ContextMenu("Visualize Layout")]
    public void Debug_VisualizeLayout() {
        if (_debugRoot != null) Destroy(_debugRoot);
        _debugRoot = new GameObject("DEBUG_Layout");
        _debugRoot.transform.SetParent(transform);

        float debugY = 5f;
        float epSize = WorldLoadingController.GetParamFloat("world_bounds/width", 1000f) * 0.01f;

        // draw footprint outline of every registered structure
        foreach (var s in WorldData.GetStructures()) {
            Vector2[] verts = s.GetBoundingBox2D().GetVertices();
            for (int i = 0; i < 4; i++) {
                Vector2    a   = verts[i], b = verts[(i+1)%4];
                Vector2    mid = (a + b) * 0.5f;
                float      len = Vector2.Distance(a, b);
                float      ang = Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
                GameObject seg = GameObject.CreatePrimitive(PrimitiveType.Cube);
                seg.transform.SetParent(_debugRoot.transform);
                seg.transform.position   = new Vector3(mid.x, debugY, mid.y);
                seg.transform.rotation   = Quaternion.Euler(0f, -ang, 0f);
                seg.transform.localScale = new Vector3(len, 0.3f, 0.5f);
                if (debugEdgeMaterial != null)
                    seg.GetComponent<MeshRenderer>().sharedMaterial = debugEdgeMaterial;
                Destroy(seg.GetComponent<Collider>());
            }

            // label
            GameObject label = new GameObject($"label_{s.structureType}");
            label.transform.SetParent(_debugRoot.transform);
            label.transform.position = new Vector3(s.GetCenter2D().x, debugY + epSize, s.GetCenter2D().y);
        }

        Debug.Log($"Debug: {WorldData.GetStructures().Count} structures in WorldData registry");
    }

    [ContextMenu("Clear Debug Visualization")]
    private void Debug_ClearVisualization() {
        if (_debugRoot != null) Destroy(_debugRoot);
    }
}

// ─────────────────────────────────────────────
//  Data classes
// ─────────────────────────────────────────────

public class EntryPoint {
    public WorldStructure structure;
    public Vector2        position;
    public Vector2        outwardNormal;
}

public class RoadNode {
    public int            id;
    public Vector2        position;
    public EntryPoint     entryPoint;
    public bool           isCrossroad;
    public List<RoadEdge> edges;
}

public class RoadEdge {
    public EntryPoint     fromEP;
    public EntryPoint     toEP;
    public RoadNode       fromNode;
    public RoadNode       toNode;
    public WorldStructure roadStructure;
    public float          width;
    public List<Vector2>  waypoints;
}