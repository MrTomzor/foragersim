using UnityEngine;
using System.Collections.Generic;

public class WorldHeightLoader : WorldDataProvider, IHeightProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.Height };
    // No DependsOn — Height is a root provider.
    // Layout calls ProcessTerrainModifications() explicitly after structure placement.

    [Header("General")]
    public string mode = "superflat";

    [Header("Superflat")]
    public float superflatHeight = 0f;

    [Header("Perlin")]
    public float perlinScale     = 200f;
    public float perlinAmplitude = 50f;

    [Header("Meta Height")]
    public string metaHeightMode   = "disabled";  // "disabled" | "valley"
    public float valleyEdgeHeight  = 50f;
    public float valleyExponent    = 2f;

    private float _perlinOffsetX;
    private float _perlinOffsetZ;
    private float _worldHalfW;
    private float _worldHalfH;

    // ─────────────────────────────────────────────
    //  Height influence zones
    // ─────────────────────────────────────────────

    private struct HeightInfluenceZone {
        public Bounds2D innerBounds;     // structure footprint (unmodified height inside)
        public float    blendMargin;     // expansion distance for smooth transition
        public TerrainModification.Mode mode;
        public float    targetHeight;    // Flatten / SetHeight
        public float    heightDelta;     // AddHeight
    }

    private readonly List<HeightInfluenceZone> _zones = new List<HeightInfluenceZone>();

    protected override void OnEnable() {
        base.OnEnable();
        WorldServices.Register<IHeightProvider>(this);
        LoadParams();
    }

    private void LoadParams() {
        mode             = WorldLoadingController.GetParamString("height_generation/mode",              mode);
        superflatHeight  = WorldLoadingController.GetParamFloat ("height_generation/superflat_height",  superflatHeight);
        perlinScale      = WorldLoadingController.GetParamFloat ("height_generation/perlin_scale",      perlinScale);
        perlinAmplitude  = WorldLoadingController.GetParamFloat ("height_generation/perlin_amplitude",  perlinAmplitude);

        metaHeightMode  = WorldLoadingController.GetParamString("meta_height_generation/mode",               metaHeightMode);
        valleyEdgeHeight = WorldLoadingController.GetParamFloat("meta_height_generation/valley_edge_height", valleyEdgeHeight);
        valleyExponent   = WorldLoadingController.GetParamFloat("meta_height_generation/valley_exponent",    valleyExponent);

        float worldW = WorldLoadingController.GetParamFloat("world_bounds/width",  200f);
        float worldH = WorldLoadingController.GetParamFloat("world_bounds/height", 200f);
        _worldHalfW = worldW * 0.5f;
        _worldHalfH = worldH * 0.5f;

        int seed = WorldLoadingController.GetDerivedSeed("height");
        System.Random rng = new System.Random(seed);
        _perlinOffsetX = (float)rng.NextDouble() * 10000f;
        _perlinOffsetZ = (float)rng.NextDouble() * 10000f;
    }

    // ─────────────────────────────────────────────
    //  Terrain modifications (called by WorldLayoutLoader after structure placement)
    // ─────────────────────────────────────────────

    /// <summary>
    /// Scans all registered WorldStructures for TerrainModification components,
    /// computes target heights, registers influence zones, and repositions structures.
    /// Must be called after all structures are placed (i.e. at the end of layout generation).
    /// </summary>
    public void ProcessTerrainModifications() {
        _zones.Clear();

        foreach (WorldStructure s in WorldData.GetStructures()) {
            var mod = s.GetComponent<TerrainModification>();
            if (mod == null) continue;

            Bounds2D footprint = s.GetBoundingBox2D();

            var zone = new HeightInfluenceZone {
                innerBounds = footprint,
                blendMargin = mod.blendMargin,
                mode        = mod.mode
            };

            switch (mod.mode) {
                case TerrainModification.Mode.Flatten:
                    zone.targetHeight = SampleFootprintEdgeAverage(footprint);
                    RepositionStructure(s, zone.targetHeight);
                    break;

                case TerrainModification.Mode.SetHeight:
                    zone.targetHeight = mod.targetHeight;
                    RepositionStructure(s, zone.targetHeight);
                    break;

                case TerrainModification.Mode.AddHeight:
                    zone.heightDelta = mod.heightDelta;
                    break;
            }

            _zones.Add(zone);
        }
    }

    /// <summary>
    /// Samples unmodified base terrain height at the midpoints of each footprint edge
    /// and returns their average. This gives a representative "ground level" for flattening.
    /// </summary>
    private float SampleFootprintEdgeAverage(Bounds2D footprint) {
        Vector2[] verts = footprint.GetVertices();
        float sum = 0f;
        for (int i = 0; i < 4; i++) {
            Vector2 mid = (verts[i] + verts[(i + 1) % 4]) * 0.5f;
            sum += GetBaseHeight(mid.x, mid.y);
        }
        return sum / 4f;
    }

    private static void RepositionStructure(WorldStructure s, float targetY) {
        Vector3 pos = s.transform.position;
        s.transform.position = new Vector3(pos.x, targetY, pos.z);
        Debug.Log($"Repositioned structure '{s.name}' to Y={targetY} for terrain modification.");
    }

    // ─────────────────────────────────────────────
    //  Height queries
    // ─────────────────────────────────────────────

    // ─────────────────────────────────────────────
    //  IHeightProvider implementation
    // ─────────────────────────────────────────────

    /// <summary>
    /// Returns the final terrain height at (x, z), including any terrain modifications.
    /// </summary>
    public float GetTerrainHeight(float x, float z) {
        float baseH = GetBaseHeight(x, z);

        if (_zones.Count == 0) return baseH;

        return ApplyInfluenceZones(x, z, baseH);
    }

    /// <summary>
    /// Returns the unmodified base terrain height (perlin/superflat) without influence zones.
    /// </summary>
    public float GetBaseTerrainHeight(float x, float z) {
        return GetBaseHeight(x, z);
    }

    private float GetBaseHeight(float x, float z) {
        float baseH = mode switch {
            "superflat" => superflatHeight,
            "perlin"    => SamplePerlin(x, z),
            _           => superflatHeight
        };
        return baseH + SampleMetaHeight(x, z);
    }

    private float SampleMetaHeight(float x, float z) {
        if (metaHeightMode != "valley" || mode == "superflat") return 0f;

        float dx = Mathf.Clamp01(Mathf.Abs(x) / _worldHalfW);
        float dz = Mathf.Clamp01(Mathf.Abs(z) / _worldHalfH);
        float edgeFactor = Mathf.Max(dx, dz);
        return valleyEdgeHeight * Mathf.Pow(edgeFactor, valleyExponent);
    }

    private float ApplyInfluenceZones(float x, float z, float baseH) {
        Vector2 point = new Vector2(x, z);

        // Track the strongest Flatten/SetHeight override and accumulated AddHeight deltas
        float bestOverrideBlend  = 0f;
        float bestOverrideHeight = baseH;
        float addHeightAccum     = 0f;

        for (int i = 0; i < _zones.Count; i++) {
            HeightInfluenceZone zone = _zones[i];
            float blend = ComputeBlend(point, zone);
            if (blend <= 0f) continue;

            switch (zone.mode) {
                case TerrainModification.Mode.Flatten:
                case TerrainModification.Mode.SetHeight:
                    if (blend > bestOverrideBlend) {
                        bestOverrideBlend  = blend;
                        bestOverrideHeight = zone.targetHeight;
                    }
                    break;

                case TerrainModification.Mode.AddHeight:
                    addHeightAccum += Smoothstep(blend) * zone.heightDelta;
                    break;
            }
        }

        float result = baseH;

        // Apply the strongest Flatten/SetHeight override
        if (bestOverrideBlend > 0f)
            result = Mathf.Lerp(baseH, bestOverrideHeight, Smoothstep(bestOverrideBlend));

        // Apply additive height changes on top
        result += addHeightAccum;

        return result;
    }

    /// <summary>
    /// Computes a raw blend factor [0, 1] for a point relative to an influence zone.
    /// 1 = fully inside inner bounds, 0 = at or beyond the outer (blend margin) boundary.
    /// Uses Chebyshev distance in the OBB's local space for a box-shaped blend region.
    /// </summary>
    private static float ComputeBlend(Vector2 point, HeightInfluenceZone zone) {
        Bounds2D inner = zone.innerBounds;

        // Transform point into the OBB's local coordinate space
        Vector2 local = InverseRotate(point - inner.center, inner.rotation);
        float halfW = inner.size.x * 0.5f;
        float halfH = inner.size.y * 0.5f;

        // Signed distance from inner box edge (Chebyshev: max of per-axis distances)
        float dx = Mathf.Max(0f, Mathf.Abs(local.x) - halfW);
        float dy = Mathf.Max(0f, Mathf.Abs(local.y) - halfH);
        float dist = Mathf.Max(dx, dy);

        if (dist <= 0f) return 1f; // inside inner box
        if (zone.blendMargin <= 0f) return 0f; // no blend margin, hard edge
        if (dist >= zone.blendMargin) return 0f; // outside outer boundary

        return 1f - dist / zone.blendMargin;
    }

    /// <summary>
    /// Hermite smoothstep for smoother blend transitions (no harsh linear falloff).
    /// </summary>
    private static float Smoothstep(float t) {
        t = Mathf.Clamp01(t);
        return t * t * (3f - 2f * t);
    }

    private static Vector2 InverseRotate(Vector2 v, float deg) {
        float rad = -deg * Mathf.Deg2Rad;
        return new Vector2(
            v.x * Mathf.Cos(rad) - v.y * Mathf.Sin(rad),
            v.x * Mathf.Sin(rad) + v.y * Mathf.Cos(rad));
    }

    private float SamplePerlin(float x, float z) {
        float sampleX = (x + _perlinOffsetX) / perlinScale;
        float sampleZ = (z + _perlinOffsetZ) / perlinScale;
        return Mathf.PerlinNoise(sampleX, sampleZ) * perlinAmplitude;
    }

    public override void Clear() {
        _zones.Clear();
        LoadParams();
    }
}
