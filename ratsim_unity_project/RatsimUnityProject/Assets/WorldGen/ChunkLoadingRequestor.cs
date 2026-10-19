using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class ChunkLoadingRequestor : MonoBehaviour {

    public static List<ChunkLoadingRequestor> registered = new List<ChunkLoadingRequestor>();

    //protected virtual void OnAwake()  { registered.Add(this); }
    //protected virtual void OnDisable() { registered.Remove(this); }

    // Register on awake and unregister on destroy to ensure we don't have stale references in the controller
    private void Awake() { registered.Add(this); }
    private void OnDestroy() { registered.Remove(this); }

    // --- Config ---
    [SerializeField] private int radius = 3;           // in chunks
    [SerializeField] private int longRangeRadius = 8;  // LOD1 radius
    // --- State ---
    private Dictionary<Vector2Int, int> _loadedChunks = new Dictionary<Vector2Int, int>(); // chunkID → current LOD
    private Vector2Int _lastChunkPos = new Vector2Int(int.MinValue, int.MinValue);

    public bool verbose = false;

    public void Start() {
        // Register timer to tick
        RoslikeTCPServer.GetInstance().RegisterTimerDiscrete((ev) => Tick(), 1);
    }

    // --- Tick ---
    // Call this every simulation step (from your step controller)
    public void Tick() {

        Vector2Int currentChunkPos = WorldToChunk(transform.position);
        if (currentChunkPos == _lastChunkPos) return;
        _lastChunkPos = currentChunkPos;

        UpdateLoadedChunks(currentChunkPos);
    }

    private void UpdateLoadedChunks(Vector2Int center) {
        Dictionary<Vector2Int, int> desiredChunks = new Dictionary<Vector2Int, int>();

        // build desired set: inner radius = LOD0, outer radius = LOD1
        for (int x = -longRangeRadius; x <= longRangeRadius; x++) {
            for (int z = -longRangeRadius; z <= longRangeRadius; z++) {
                Vector2Int chunk = new Vector2Int(center.x + x, center.y + z);
                int desiredLOD = (Mathf.Abs(x) <= radius && Mathf.Abs(z) <= radius) ? 0 : 1;
                desiredChunks[chunk] = desiredLOD;
            }
        }

        // upgraded chunks (already loaded but at worse LOD)
        foreach (var kvp in desiredChunks) {
            if (_loadedChunks.TryGetValue(kvp.Key, out int currentLOD) && currentLOD != kvp.Value) {
                if (kvp.Value < currentLOD) { // lower int = better LOD
                    _loadedChunks[kvp.Key] = kvp.Value;
                    NotifyLoaded(kvp.Key, kvp.Value);
                }
            }
        }

        // newly loaded chunks
        foreach (var kvp in desiredChunks) {
            if (!_loadedChunks.ContainsKey(kvp.Key)) {
                _loadedChunks[kvp.Key] = kvp.Value;
                NotifyLoaded(kvp.Key, kvp.Value);
            }
        }

        // unloaded chunks
        var toUnload = _loadedChunks.Keys.Where(c => !desiredChunks.ContainsKey(c)).ToList();
        foreach (var chunk in toUnload) {
            int lod = _loadedChunks[chunk];
            _loadedChunks.Remove(chunk);
            NotifyUnloaded(chunk, lod);
        }
    }

    private void NotifyLoaded(Vector2Int chunk, int lod) {
        if(verbose)
            Debug.Log($"Requesting load of chunk {chunk} at LOD{lod}");
        foreach (var provider in WorldDataProvider.registered)
            provider.GenerateChunk(chunk.x, chunk.y, lod);
    }

    private void NotifyUnloaded(Vector2Int chunk, int lod) {
        if(verbose)
            Debug.Log($"Requesting unload of chunk {chunk} at LOD{lod}");
        foreach (var provider in WorldDataProvider.registered)
            provider.ClearChunk(chunk.x, chunk.y, lod);
    }

    public void Clear() {
        _loadedChunks.Clear();
        _lastChunkPos = new Vector2Int(int.MinValue, int.MinValue);
    }

    // --- Helpers ---
    private Vector2Int WorldToChunk(Vector3 worldPos) {
        return new Vector2Int(
            Mathf.FloorToInt(worldPos.x / WorldLoadingController.GetChunkWidth()),
            Mathf.FloorToInt(worldPos.z / WorldLoadingController.GetChunkWidth())
        );
    }

    public Vector3 ChunkToWorld(Vector2Int chunk) {
        return new Vector3(chunk.x * WorldLoadingController.GetChunkWidth(), 0, chunk.y * WorldLoadingController.GetChunkWidth());
    }

    public Vector3 ChunkCenter(Vector2Int chunk) {
        return new Vector3((chunk.x + 0.5f) * WorldLoadingController.GetChunkWidth(), 0, (chunk.y + 0.5f) * WorldLoadingController.GetChunkWidth());
    }


    // Debug tools
    [ContextMenu("Tick Manually")]
    private void Debug_Tick() {
        Tick();
    }

}
