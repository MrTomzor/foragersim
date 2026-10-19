using UnityEngine;

public class TerrainTextureLoader : WorldDataProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.TerrainTexture };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.TerrainMesh, WorldDataType.Height };

    [Header("Texture Settings")]
    public int textureResolution = 64;
    public bool verbose = false;

    [Header("Height Thresholds (world units)")]
    public float waterLevel  = 0f;
    public float beachLevel  = 2f;
    public float grassLevel  = 30f;
    public float rockLevel   = 60f;
    public float snowLevel   = 80f;

    [Header("Terrain Colors")]
    public Color deepWaterColor  = new Color(0.10f, 0.20f, 0.50f);
    public Color shallowWater    = new Color(0.20f, 0.40f, 0.65f);
    public Color sandColor       = new Color(0.85f, 0.80f, 0.55f);
    public Color grassColor      = new Color(0.25f, 0.55f, 0.20f);
    public Color dryGrassColor   = new Color(0.55f, 0.60f, 0.25f);
    public Color rockColor       = new Color(0.45f, 0.40f, 0.35f);
    public Color snowColor       = new Color(0.95f, 0.95f, 1.00f);

    [Header("Surface Noise (for visual odometry / optical flow)")]
    [Tooltip("Fraction of mesh quads that get randomly darkened (0 = none, 1 = all).")]
    [Range(0f, 1f)]
    public float quadDarkenDensity = 0.15f;

    [Tooltip("Maximum darkening amount (0 = invisible, 1 = fully black).")]
    [Range(0f, 1f)]
    public float quadDarkenMax = 0.35f;

    private int   _chunkWidthInt;

    private void Awake() {
        _chunkWidthInt = (int)WorldLoadingController.GetChunkWidth();
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void GenerateChunk(int cx, int cz, int lod) {
        Texture2D tex = GenerateTexture(cx, cz, lod);
        WorldServices.Get<ITerrainMeshProvider>().SetChunkTexture(cx, cz, tex);
    }

    public override void Clear() {
        _chunkWidthInt = (int)WorldLoadingController.GetChunkWidth();
    }

    // ─────────────────────────────────────────────
    //  Texture generation
    // ─────────────────────────────────────────────

    private Texture2D GenerateTexture(int cx, int cz, int lod) {
        float ox = cx * _chunkWidthInt;
        float oz = cz * _chunkWidthInt;

        // Use mesh-aligned resolution: one texel per quad ensures crisp per-quad coloring.
        int quadsPerSide = WorldServices.Get<ITerrainMeshProvider>().GetQuadsPerSide(lod);
        int res = Mathf.Max(quadsPerSide, textureResolution);

        Texture2D tex = new Texture2D(res, res, TextureFormat.RGB24, false);
        tex.filterMode = FilterMode.Point; // no blending — each quad maps to sharp texels
        tex.wrapMode   = TextureWrapMode.Clamp;

        Color[] pixels = new Color[res * res];

        // Base height-based coloring
        for (int z = 0; z < res; z++)
        for (int x = 0; x < res; x++) {
            float worldX = ox + ((float)x / (res - 1)) * _chunkWidthInt;
            float worldZ = oz + ((float)z / (res - 1)) * _chunkWidthInt;
            float height = WorldServices.Get<IHeightProvider>().GetTerrainHeight(worldX, worldZ);
            pixels[z * res + x] = HeightToColor(height);
        }

        // Per-quad darkening overlay, aligned to the mesh grid
        if (quadDarkenDensity > 0f && quadsPerSide > 0) {
            int seed = WorldLoadingController.GetDerivedSeed("terrain_texture")
                       ^ (cx * 1000003) ^ (cz * 999983);
            System.Random rng = new System.Random(seed);

            // Build a per-quad darkening map
            float[] quadDarken = new float[quadsPerSide * quadsPerSide];
            for (int i = 0; i < quadDarken.Length; i++) {
                quadDarken[i] = rng.NextDouble() < quadDarkenDensity
                    ? (float)rng.NextDouble() * quadDarkenMax
                    : 0f;
            }

            // Apply: map each texel to its mesh quad and darken
            for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++) {
                int qx = Mathf.Min((int)((float)x / res * quadsPerSide), quadsPerSide - 1);
                int qz = Mathf.Min((int)((float)z / res * quadsPerSide), quadsPerSide - 1);
                float darken = quadDarken[qz * quadsPerSide + qx];
                if (darken > 0f) {
                    int idx = z * res + x;
                    Color c = pixels[idx];
                    float m = 1f - darken;
                    pixels[idx] = new Color(c.r * m, c.g * m, c.b * m);
                }
            }
        }

        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }

    private Color HeightToColor(float h) {
        if (h < waterLevel)  return Color.Lerp(deepWaterColor, shallowWater,  Mathf.InverseLerp(waterLevel  - 20f, waterLevel,  h));
        if (h < beachLevel)  return Color.Lerp(shallowWater,  sandColor,      Mathf.InverseLerp(waterLevel,        beachLevel,  h));
        if (h < grassLevel)  return Color.Lerp(sandColor,     grassColor,      Mathf.InverseLerp(beachLevel,        grassLevel,  h));
        if (h < rockLevel)   return Color.Lerp(grassColor,    dryGrassColor,  Mathf.InverseLerp(grassLevel,        rockLevel,   h));
        if (h < snowLevel)   return Color.Lerp(dryGrassColor, rockColor,      Mathf.InverseLerp(rockLevel,         snowLevel,   h));
        return Color.Lerp(rockColor, snowColor, Mathf.InverseLerp(snowLevel, snowLevel + 20f, h));
    }
}
