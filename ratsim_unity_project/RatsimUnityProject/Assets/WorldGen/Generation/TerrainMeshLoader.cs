using UnityEngine;
using System.Collections.Generic;

public class TerrainMeshLoader : WorldDataProvider, ITerrainMeshProvider {

    public override WorldDataType[] Provides => new[] { WorldDataType.TerrainMesh };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height };

    [Header("Global Settings")]
    public int resolutionScale = 2;
    public bool verbose = false;

    [Header("Mesh Settings")]
    public Material terrainMaterial;

    [Header("Perlin (used only if WorldHeightLoader is not present)")]
    public float heightScale = 30f;
    public float noiseScale  = 0.01f;

    private int _chunkWidthInt;

    // chunkID → chunk data
    private class ChunkData {
        public GameObject go;
        public Mesh       mesh;
        public Vector3[]  vertices;
        public Vector3[]  normals;
        public int        vertsPerSide;
        public int        lod;
    }

    private readonly Dictionary<Vector2Int, ChunkData> _chunks = new Dictionary<Vector2Int, ChunkData>();

    private static readonly Vector2Int[] Neighbours = {
        new Vector2Int( 1, 0), new Vector2Int(-1, 0),
        new Vector2Int( 0, 1), new Vector2Int( 0,-1)
    };

    private int V => _chunkWidthInt + 1; // vertices per side

    // ─────────────────────────────────────────────
    //  Init
    // ─────────────────────────────────────────────

    private void Awake() {
        _chunkWidthInt = (int)WorldLoadingController.GetChunkWidth();
    }

    protected override void OnEnable() {
        base.OnEnable();
        WorldServices.Register<ITerrainMeshProvider>(this);
    }

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    public override void GenerateChunk(int cx, int cz, int lod) {
        Vector2Int key = new Vector2Int(cx, cz);

        if (_chunks.TryGetValue(key, out ChunkData existing) ) {
            // If more detailed LOD is requested and worse one loaded, remove loaded and continue
            if (lod < existing.lod) {
                Destroy(existing.go);
                _chunks.Remove(key);
            } else {
                existing.go.SetActive(true);
                return;
            }
        }

        ChunkData data = BuildChunk(cx, cz, lod);
        _chunks[key] = data;

        // stitch normals with loaded neighbours
        foreach (Vector2Int offset in Neighbours) {
            Vector2Int nKey = key + offset;
            if (_chunks.TryGetValue(nKey, out ChunkData neighbour))
                StitchNormals(key, data, nKey, neighbour);
        }
    }

    public override void ClearChunk(int cx, int cz, int lod) {
        Vector2Int key = new Vector2Int(cx, cz);
        if (!_chunks.TryGetValue(key, out ChunkData data)) return;

        if (lod == 0) {
            // disable but keep in memory for re-enable
            data.go.SetActive(false);

            // re-stitch neighbours back to self-only normals
            foreach (Vector2Int offset in Neighbours) {
                Vector2Int nKey = key + offset;
                if (_chunks.TryGetValue(nKey, out ChunkData neighbour))
                    RecomputeEdgeNormals(nKey, neighbour);
            }
        } else {
            // LOD1 unload = destroy completely
            foreach (Vector2Int offset in Neighbours) {
                Vector2Int nKey = key + offset;
                if (_chunks.TryGetValue(nKey, out ChunkData neighbour))
                    RecomputeEdgeNormals(nKey, neighbour);
            }
            Destroy(data.go);
            _chunks.Remove(key);
        }
    }

    public override void Clear() {
        foreach (var kvp in _chunks)
            WorldGenObjects.DestroyHidden(kvp.Value.go);
        _chunks.Clear();
        _chunkWidthInt = (int)WorldLoadingController.GetChunkWidth();
    }

    // ─────────────────────────────────────────────
    //  Mesh building
    // ─────────────────────────────────────────────

    private ChunkData BuildChunk(int chunkX, int chunkZ, int lod) {
        int origLOD = lod;
        lod = lod + resolutionScale; // apply global resolution scale
        int step = (int)Mathf.Pow(2, lod); // LOD0=1, LOD1=2, LOD2=4 etc.
        int vertsPerSide = (_chunkWidthInt / step) + 1;
        if(verbose)
            Debug.Log($"Building chunk ({chunkX}, {chunkZ}) at LOD{lod} with step {step} and {vertsPerSide} verts/side");


        float ox = chunkX * _chunkWidthInt;
        float oz = chunkZ * _chunkWidthInt;

        Vector3[] vertices = new Vector3[vertsPerSide * vertsPerSide];
        Vector2[] uvs      = new Vector2[vertsPerSide * vertsPerSide];

        for (int z = 0; z < vertsPerSide; z++)
        for (int x = 0; x < vertsPerSide; x++) {
            float worldX = ox + x * step;
            float worldZ = oz + z * step;
            float h      = WorldServices.Get<IHeightProvider>().GetTerrainHeight(worldX, worldZ);
            int   idx    = z * vertsPerSide + x;
            vertices[idx] = new Vector3(x * step, h, z * step);
            uvs[idx]      = new Vector2((float)x / (vertsPerSide - 1), (float)z / (vertsPerSide - 1));
        }

        int quadsPerSide = vertsPerSide - 1;
        int[] triangles  = new int[quadsPerSide * quadsPerSide * 6];
        int t = 0;
        for (int z = 0; z < quadsPerSide; z++)
        for (int x = 0; x < quadsPerSide; x++) {
            int bl = z * vertsPerSide + x;
            int br = bl + 1;
            int tl = bl + vertsPerSide;
            int tr = tl + 1;
            triangles[t++] = bl; triangles[t++] = tl; triangles[t++] = br;
            triangles[t++] = br; triangles[t++] = tl; triangles[t++] = tr;
        }

        Mesh mesh = new Mesh();
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices    = vertices;
        mesh.triangles   = triangles;
        mesh.uv          = uvs;
        mesh.RecalculateNormals();

        Vector3[] normals = mesh.normals;

        GameObject go = new GameObject($"TerrainChunk_{chunkX}_{chunkZ}");
        go.transform.SetParent(transform, false);
        go.transform.position = new Vector3(ox, 0f, oz);
        go.AddComponent<MeshFilter>().sharedMesh     = mesh;
        go.AddComponent<MeshRenderer>().sharedMaterial = terrainMaterial != null
            ? terrainMaterial : CreateDefaultMaterial();
        go.AddComponent<MeshCollider>().sharedMesh   = mesh;

        return new ChunkData { go = go, mesh = mesh, vertices = vertices, normals = normals, vertsPerSide = vertsPerSide, lod = origLOD };
    }

    // ─────────────────────────────────────────────
    //  Normal stitching
    // ─────────────────────────────────────────────

    private void StitchNormals(Vector2Int aKey, ChunkData a, Vector2Int bKey, ChunkData b) {
        Vector2Int dir = bKey - aKey;
        int vA = a.vertsPerSide;
        int vB = b.vertsPerSide;
        int v  = Mathf.Min(vA, vB); // stitch along the shorter edge

        for (int i = 0; i < v; i++) {
            // scale index into each chunk's resolution
            int iA = Mathf.RoundToInt((float)i / (v - 1) * (vA - 1));
            int iB = Mathf.RoundToInt((float)i / (v - 1) * (vB - 1));

            int idxA, idxB;
            if      (dir.x ==  1) { idxA = iA * vA + (vA-1); idxB = iB * vB + 0;      }
            else if (dir.x == -1) { idxA = iA * vA + 0;      idxB = iB * vB + (vB-1); }
            else if (dir.y ==  1) { idxA = (vA-1)*vA + iA;   idxB = 0*vB + iB;        }
            else                  { idxA = 0*vA + iA;         idxB = (vB-1)*vB + iB;   }

            Vector3 avg = (a.normals[idxA] + b.normals[idxB]).normalized;
            a.normals[idxA] = avg;
            b.normals[idxB] = avg;
        }

        ApplyNormals(a);
        ApplyNormals(b);
    }

    private void RecomputeEdgeNormals(Vector2Int key, ChunkData data) {
        data.mesh.RecalculateNormals();
        data.normals = data.mesh.normals;

        foreach (Vector2Int offset in Neighbours) {
            Vector2Int nKey = key + offset;
            if (_chunks.TryGetValue(nKey, out ChunkData neighbour))
                StitchNormals(key, data, nKey, neighbour);
        }
    }

    private void ApplyNormals(ChunkData data) {
        data.mesh.normals = data.normals;
        MeshCollider mc = data.go.GetComponent<MeshCollider>();
        if (mc != null) mc.sharedMesh = data.mesh;
    }

    // ─────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────

    /// <summary>
    /// Returns the mesh quad grid resolution for a chunk at the given LOD.
    /// TerrainTextureLoader uses this to align texture darkening to the mesh grid.
    /// </summary>
    public int GetQuadsPerSide(int lod) {
        int effectiveLod = lod + resolutionScale;
        int step = (int)Mathf.Pow(2, effectiveLod);
        return _chunkWidthInt / step;
    }

    // ─────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────

    private static Material CreateDefaultMaterial() {
        Material mat = new Material(Shader.Find("Standard"));
        mat.color = new Color(0.76f, 0.60f, 0.42f);
        return mat;
    }

    public void SetChunkTexture(int cx, int cz, Texture2D tex) {
        if (!_chunks.TryGetValue(new Vector2Int(cx, cz), out ChunkData data)) return;
        // Use MaterialPropertyBlock to override texture per-renderer without
        // creating material instances (avoids breaking shader keywords/shadow passes).
        var renderer = data.go.GetComponent<MeshRenderer>();
        var block = new MaterialPropertyBlock();
        renderer.GetPropertyBlock(block);
        block.SetTexture("_MainTex", tex);
        renderer.SetPropertyBlock(block);
    }

    public bool IsTerrainCollider(Collider col) {
        return col.transform.IsChildOf(transform);
    }
}
