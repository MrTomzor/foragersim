using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;


public class WorldLoadingController : MonoBehaviour {
    private static WorldLoadingController _instance;
    public static WorldLoadingController instance {
        get {
            if (_instance == null) {
                Debug.LogError("WorldLoadingController instance not found! Make sure a WorldLoadingController component exists in the scene.");
            }
            return _instance;
        }
    }

    public bool debugStartEpisodeOnAwake = false;
    public bool debugRandomizeSeedOnStart = false;

    int numEpisodesStarted = 0;
    public GameObject agentObject;

    private string _agentConfigJson;

    public void Awake()
    {
        _instance = this;
        _sceneChunkWidth = chunkWidth;
    }

    void Start() {
        var conn = RoslikeTCPServer.GetInstance();
        conn.Subscribe<StringMessage>("/sim_control/world_config", (msg) => {
            LoadConfig(msg.data, verbose: true);
        });
        conn.Subscribe<StringMessage>("/sim_control/agent_config", (msg) => {
            LoadAgentConfig(msg.data);
        });
        conn.Subscribe<BoolMessage>("/sim_control/reset_episode", (msg) => {
            if (msg.data) {
                StartEpisode();
                ChunkLoadingRequestor.registered.ForEach(r => r.Tick());
            }
        });
    }

    public void Update()
    {
        if (debugStartEpisodeOnAwake && numEpisodesStarted == 0 )
        {
            // Apply the config
            if (_configFile == null) { Debug.LogWarning("No config file assigned"); return; }
            LoadConfig(_configFile.text);
            

            StartEpisode();
            numEpisodesStarted++;

            // Tick
            ChunkLoadingRequestor.registered.ForEach(r => r.Tick());
            Debug.Log("Num registered requestors: " + ChunkLoadingRequestor.registered.Count);
            Debug.Log("Manually triggered StartEpisode and Tick on all requestors");
        }
    }


    // --- Param Storage ---
    private Dictionary<string, string> _params = new Dictionary<string, string>();
    public int masterSeed = 0;
    // Single source of truth for the chunk grid: every loader (terrain, structures, trees,
    // walkers, dynamic objects, requestors) derives chunk indices from GetChunkWidth().
    // Set per episode from the `chunk_width` world param in StartEpisode(); this serialized
    // value is the fallback when a config does not set it.
    public float chunkWidth = 100f;
    private float _sceneChunkWidth;

    // --- Param Access ---
    public static string GetParamString(string name) {
        if (_instance._params.TryGetValue(name, out string val)) return val;
        Debug.LogWarning($"WorldLoadingController: param '{name}' not found");
        return null;
    }

    public static float GetParamFloat(string name) {
        if (_instance._params.TryGetValue(name, out string val) && float.TryParse(val, out float result)) return result;
        Debug.LogWarning($"WorldLoadingController: param '{name}' not found or not a float");
        return 0f;
    }

    public static List<string> GetParamList(string name) {
        if (_instance._params.TryGetValue(name, out string val))
            return new List<string>(val.Split(',').Select(s => s.Trim()));
        Debug.LogWarning($"WorldLoadingController: param '{name}' not found");
        return new List<string>();
    }

    // Overloads with fallback values

    public static float GetParamFloat(string name, float fallback) {
        if (instance._params.TryGetValue(name, out string val) && float.TryParse(val, out float result)) return result;
        return fallback;
    }

    public static int GetParamInt(string name, int fallback) {
        if (instance._params.TryGetValue(name, out string val) && int.TryParse(val, out int result)) return result;
        return fallback;
    }

    public static string GetParamString(string name, string fallback) {
        if (instance._params.TryGetValue(name, out string val)) return val;
        return fallback;
    }



    public static int GetSeed() => _instance.masterSeed;

    public static float GetChunkWidth() => _instance.chunkWidth;

    public static int GetDerivedSeed(string domain) => _instance.masterSeed ^ domain.GetHashCode();

    // --- Config Loading ---
    public void LoadConfig(string json, bool verbose = true) {
        var parsed = JsonUtility.FromJson<WorldConfig>(json);
        if (verbose)
        {
            Debug.Log($"WorldLoadingController: loading config with {parsed.entries.Count} entries:");
            foreach (var entry in parsed.entries)
                Debug.Log($"  - {entry.key} = {entry.value}");
            Debug.Log("Raw string: " + json);
        }
        
        foreach (var entry in parsed.entries){
            if (verbose) Debug.Log($"WorldLoadingController: setting param '{entry.key}' = '{entry.value}'");
            _params[entry.key] = entry.value;
        }
        if (_params.TryGetValue("seed", out string seedStr) && int.TryParse(seedStr, out int seed))
            masterSeed = seed;
        else
            Debug.LogWarning("WorldLoadingController: no seed found in config, defaulting to 0");
    }

    public void LoadAgentConfig(string json) {
        _agentConfigJson = json;
        Debug.Log($"WorldLoadingController: agent config received ({json.Length} chars)");
    }

    public string GetAgentConfigJson() => _agentConfigJson;

    // --- Episode Control ---
    public void StartEpisode() {
        if(debugRandomizeSeedOnStart)
        {
            masterSeed = UnityEngine.Random.Range(int.MinValue, int.MaxValue);
            Debug.Log($"Randomized master seed for new episode: {masterSeed}");
        }

        ApplyChunkWidth();
        ClearAllWorldData();
        InitializeAllModules();
    }

    // Before ClearAllWorldData so every provider's Clear()/Generate() already sees the new grid.
    private void ApplyChunkWidth() {
        float w = GetParamFloat("chunk_width", _sceneChunkWidth);
        if (w < 1f) {
            Debug.LogWarning($"WorldLoadingController: chunk_width={w} is invalid, using {_sceneChunkWidth}");
            w = _sceneChunkWidth;
        }
        if (!Mathf.Approximately(w, chunkWidth))
            Debug.Log($"WorldLoadingController: chunk width {chunkWidth} -> {w}");
        chunkWidth = w;
    }
    

    public void ClearAllWorldData()
    {
        // 1. Clear the static structure registry (must happen before providers,
        //    so providers don't query stale destroyed structures during their Clear)
        WorldData.Clear();
        Debug.Log("WorldData structure registry cleared");

        // 2. Clear all providers in reverse dependency order
        var sorted = TopologicalSort(WorldDataProvider.registered);
        for (int i = sorted.Count - 1; i >= 0; i--)
            sorted[i].Clear();
        Debug.Log("All world data providers cleared");

        // Clear the cache of all chunk loading requestors as well
        foreach (var requestor in ChunkLoadingRequestor.registered)
            requestor.Clear();
        Debug.Log("All chunk loading requestors cleared");
    }

    public void InitializeAllModules()
    {
        var sorted = TopologicalSort(WorldDataProvider.registered);
        foreach (var provider in sorted)
            provider.Generate();
        Debug.Log("All world data providers initialized (Generate)");

        // Optional verification snapshot (worldgen_dump/enabled). Taken one frame later:
        // Clear() releases last episode's objects with a deferred Destroy, so during this
        // frame their colliders still exist and would skew collision-checked spawns in the
        // chunks the dump force-loads. Next frame the scene is exactly what the agent's
        // requestor would stream in.
        if (WorldGenDump.IsEnabled())
            StartCoroutine(WorldGenDump.PublishNextFrame());
        // Optional overhead picture of the whole world (world_snapshot/enabled), same timing.
        if (WorldSnapshot.IsEnabled())
            StartCoroutine(WorldSnapshot.PublishNextFrame());
    }

    // ─────────────────────────────────────────────
    //  Topological sort by DependsOn → Provides
    // ─────────────────────────────────────────────

    private static List<WorldDataProvider> TopologicalSort(List<WorldDataProvider> providers)
    {
        // Build lookup: WorldDataType → provider(s) that produce it
        var typeToProviders = new Dictionary<WorldDataType, List<WorldDataProvider>>();
        foreach (var p in providers)
            foreach (var t in p.Provides) {
                if (!typeToProviders.TryGetValue(t, out var list)) {
                    list = new List<WorldDataProvider>();
                    typeToProviders[t] = list;
                }
                list.Add(p);
            }

        // Kahn's algorithm
        var inDegree = new Dictionary<WorldDataProvider, int>();
        var depEdges = new Dictionary<WorldDataProvider, List<WorldDataProvider>>(); // dep → dependents
        foreach (var p in providers) {
            if (!inDegree.ContainsKey(p)) inDegree[p] = 0;
            if (!depEdges.ContainsKey(p)) depEdges[p] = new List<WorldDataProvider>();
        }

        foreach (var p in providers)
            foreach (var dep in p.DependsOn) {
                if (!typeToProviders.TryGetValue(dep, out var depProviders)) continue;
                foreach (var dp in depProviders) {
                    if (dp == p) continue;
                    depEdges[dp].Add(p);
                    inDegree[p]++;
                }
            }

        var queue = new Queue<WorldDataProvider>();
        foreach (var kvp in inDegree)
            if (kvp.Value == 0) queue.Enqueue(kvp.Key);

        var sorted = new List<WorldDataProvider>();
        while (queue.Count > 0) {
            var current = queue.Dequeue();
            sorted.Add(current);
            foreach (var dependent in depEdges[current]) {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count != providers.Count) {
            var missing = providers.Where(p => !sorted.Contains(p)).Select(p => p.GetType().Name);
            Debug.LogError($"WorldLoadingController: dependency cycle detected involving: {string.Join(", ", missing)}");
            // Fall back to registration order for unsorted providers
            foreach (var p in providers)
                if (!sorted.Contains(p)) sorted.Add(p);
        }

        return sorted;
    }

    public void ResetEpisode(string json) {
        LoadConfig(json);
        StartEpisode();
    }


    // Debug control to manually trigger chunk load/unload (for testing), assume some chunkrequestor exists and is registered as the first requestor
    [ContextMenu("Start Episode (current config)")]
    private void Debug_StartEpisode() {
        StartEpisode();
        // Also tick all registered requstors
        ChunkLoadingRequestor.registered.ForEach(r => r.Tick());
        Debug.Log("Num registered requestors: " + ChunkLoadingRequestor.registered.Count);  
        Debug.Log("Manually triggered StartEpisode and Tick on all requestors");
    }

    // Debug print of currenct config:
    [ContextMenu("Print Current Config")]
    private void Debug_PrintCurrentConfig() {
        Debug.Log("Current World Config:");
        foreach (var kvp in _params)
        {
            Debug.Log($"  - {kvp.Key} = {kvp.Value}");
        }
    }

    [ContextMenu("Clear All Modules")]
    private void Debug_ClearAll() {
        ClearAllWorldData();
    }

    [SerializeField] private TextAsset _configFile;

    [ContextMenu("Load Config From File")]
    private void Debug_LoadConfigFromFile() {
        if (_configFile == null) { Debug.LogWarning("No config file assigned"); return; }
        LoadConfig(_configFile.text);
        Debug.Log($"WorldLoadingController: config loaded from {_configFile.name}");
    }
}

// --- JSON Helpers ---
[Serializable]
public class WorldConfig {
    public List<WorldConfigEntry> entries;
}

[Serializable]
public class WorldConfigEntry {
    public string key;
    public string value;
}