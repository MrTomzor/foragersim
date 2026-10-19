using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

/// <summary>
/// Deterministic snapshot of everything world generation produced for the current
/// episode, published to the Python client as JSON so two generations can be diffed
/// (see <c>ratsim/worldgen_dump.py</c>). Used to verify that a refactor of the
/// generation pipeline — e.g. expressing a preset in the rules language instead of the
/// legacy keys — reproduces the exact same world, and as a regression test for any
/// loader change.
///
/// Opt-in per episode via world config:
///   worldgen_dump/enabled  -- 0/1 (default 0). When 1, one frame after every provider's
///                             Generate() the dump is published on <see cref="Topic"/> as a
///                             StringMessage whose data is the JSON below (one frame later so
///                             the previous episode's deferred-destroyed colliders are gone —
///                             they would otherwise skew collision-checked spawns).
///   worldgen_dump/file     -- optional path; when set the same JSON is also written there.
///
/// What is in the dump:
///   - every registered WorldStructure: type, DeterministicId, parent structure id (nearest
///     WorldStructure ancestor, -1 if none), centre x/z, CCW rotation, footprint size.
///   - every reward pickup (from RewardObjectLoader's registry) and well (IWellProvider),
///     with position and host structure id.
///   - every agent (one per ChunkLoadingRequestor), with position and yaw.
///
/// To include lazily-populated content (city houses spawned on chunk load, rewards
/// spawned at LOD0) every chunk overlapping the world bounds is force-loaded at LOD0
/// first — the same trick AgentLoader uses for maze walls. Those chunks stay loaded
/// (the agent's requestor would load the nearby ones anyway; the rest cost memory only),
/// so this flag is meant for verification runs, not training.
///
/// Chunk-lazy density spawns (trees, walkers) are deliberately NOT included: they are
/// not seeded per structure and would only add noise to a diff.
///
/// Entries are sorted (structures by id, objects by kind/name/position) so the JSON is
/// order-independent of spawn timing.
/// </summary>
public static class WorldGenDump {
    public const string Topic = "/sim_control/worldgen_dump";

    [Serializable]
    public class StructureEntry {
        public string type;
        public int id;
        public int parent_id;
        public float x, z;
        public float rot;
        public float size_x, size_z;
    }

    [Serializable]
    public class ObjectEntry {
        public string kind;   // "reward" | "well" | "agent"
        public string name;   // prefab / object name, well id for wells
        public float x, y, z;
        public float yaw;     // agents only (CCW degrees), 0 otherwise
        public int host_id;   // nearest WorldStructure ancestor's DeterministicId, -1 if none
    }

    [Serializable]
    public class Dump {
        public int seed;
        public string layout_mode;
        public float world_width, world_height;
        public int n_structures, n_objects;
        public List<StructureEntry> structures = new List<StructureEntry>();
        public List<ObjectEntry> objects = new List<ObjectEntry>();
    }

    public static bool IsEnabled() => WorldLoadingController.GetParamInt("worldgen_dump/enabled", 0) != 0;

    /// <summary>
    /// Coroutine started by WorldLoadingController after every provider's Generate(): waits
    /// one frame (so last episode's deferred-destroyed objects are really gone) and publishes.
    /// The Python side polls a few steps for the message, so the one-step delay is invisible.
    /// </summary>
    public static IEnumerator PublishNextFrame() {
        yield return null;
        Publish();
    }

    public static void Publish() {
        Dump dump;
        try {
            dump = Build();
        } catch (Exception e) {
            WorldGenStatus.Error("WorldGenDump", $"failed to build dump: {e.Message}");
            return;
        }

        string json = JsonUtility.ToJson(dump);

        string file = WorldLoadingController.GetParamString("worldgen_dump/file", "");
        if (!string.IsNullOrWhiteSpace(file)) {
            try {
                File.WriteAllText(file, json);
            } catch (Exception e) {
                WorldGenStatus.Warning("WorldGenDump", $"could not write '{file}': {e.Message}");
            }
        }

        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        if (conn != null)
            conn.Publish(Topic, new StringMessage { data = json });

        Debug.Log($"WorldGenDump: {dump.n_structures} structures, {dump.n_objects} objects " +
                  $"({json.Length} chars){(string.IsNullOrWhiteSpace(file) ? "" : " → " + file)}");
    }

    public static Dump Build() {
        ForceLoadAllChunks();
        Physics.SyncTransforms();

        var dump = new Dump {
            seed         = WorldLoadingController.GetSeed(),
            layout_mode  = WorldLoadingController.GetParamString("layout/mode", "default"),
            world_width  = WorldLoadingController.GetParamFloat("world_bounds/width", 100f),
            world_height = WorldLoadingController.GetParamFloat("world_bounds/height", 100f),
        };

        foreach (WorldStructure s in WorldData.GetStructures()) {
            if (s == null) continue;
            Vector2 c = s.GetCenter2D();
            Vector2 sz = s.GetSize();
            dump.structures.Add(new StructureEntry {
                type      = s.structureType,
                id        = s.DeterministicId,
                parent_id = HostId(s.transform.parent),
                x = Round(c.x), z = Round(c.y),
                rot    = Round(NormalizeDeg(s.GetRotationCCW())),
                size_x = Round(sz.x), size_z = Round(sz.y),
            });
        }

        // Read from the owning loaders' registries, not a scene scan: the previous
        // episode's objects are released with a deferred Destroy in Clear() and would
        // still be found by FindObjectsByType during this frame.
        if (RewardObjectLoader.Instance != null)
            foreach (Pickupable p in RewardObjectLoader.Instance.GetLiveRewards())
                dump.objects.Add(MakeObject("reward", CleanName(p.gameObject.name), p.transform, HostId(p.transform)));

        if (WorldServices.Has<IWellProvider>()) {
            foreach (Well w in WorldServices.Get<IWellProvider>().GetWells()) {
                if (w == null) continue;
                WellData d = w.GetComponent<WellData>();
                string name = d != null ? $"well_{d.wellId}" : CleanName(w.gameObject.name);
                dump.objects.Add(MakeObject("well", name, w.transform, HostId(w.transform)));
            }
        }

        foreach (ChunkLoadingRequestor r in ChunkLoadingRequestor.registered) {
            if (r == null) continue;
            ObjectEntry e = MakeObject("agent", r.gameObject.name, r.transform, -1);
            e.yaw = Round(NormalizeDeg(-r.transform.eulerAngles.y));
            dump.objects.Add(e);
        }

        dump.structures = dump.structures
            .OrderBy(e => e.id).ThenBy(e => e.type).ThenBy(e => e.x).ThenBy(e => e.z).ToList();
        dump.objects = dump.objects
            .OrderBy(e => e.kind).ThenBy(e => e.name).ThenBy(e => e.x).ThenBy(e => e.z).ThenBy(e => e.y).ToList();
        dump.n_structures = dump.structures.Count;
        dump.n_objects    = dump.objects.Count;
        return dump;
    }

    // ─────────────────────────────────────────────

    private static void ForceLoadAllChunks() {
        float w  = WorldLoadingController.GetParamFloat("world_bounds/width",  100f);
        float h  = WorldLoadingController.GetParamFloat("world_bounds/height", 100f);
        float cw = WorldLoadingController.GetChunkWidth();
        if (cw <= 0f) return;
        // One chunk of padding: structures may straddle the bounds (structures_margin, roads).
        int minCX = Mathf.FloorToInt(-w * 0.5f / cw) - 1, maxCX = Mathf.FloorToInt(w * 0.5f / cw) + 1;
        int minCZ = Mathf.FloorToInt(-h * 0.5f / cw) - 1, maxCZ = Mathf.FloorToInt(h * 0.5f / cw) + 1;
        for (int cx = minCX; cx <= maxCX; cx++)
            for (int cz = minCZ; cz <= maxCZ; cz++)
                foreach (WorldDataProvider p in WorldDataProvider.registered)
                    p.GenerateChunk(cx, cz, 0);
    }

    private static ObjectEntry MakeObject(string kind, string name, Transform t, int hostId) {
        Vector3 p = t.position;
        return new ObjectEntry {
            kind = kind, name = name,
            x = Round(p.x), y = Round(p.y), z = Round(p.z),
            yaw = 0f, host_id = hostId,
        };
    }

    /// <summary>DeterministicId of the nearest WorldStructure at or above <paramref name="t"/>, -1 if none.</summary>
    private static int HostId(Transform t) {
        while (t != null) {
            WorldStructure ws = t.GetComponent<WorldStructure>();
            if (ws != null) return ws.DeterministicId;
            t = t.parent;
        }
        return -1;
    }

    private static string CleanName(string n) => n.Replace("(Clone)", "").Trim();

    private static float NormalizeDeg(float d) {
        d %= 360f;
        if (d < 0f) d += 360f;
        if (d > 359.999f) d = 0f;
        return d;
    }

    // Centimetre rounding: below the noise of float transform maths, above nothing that
    // would change what an agent perceives.
    private static float Round(float v) => Mathf.Round(v * 100f) / 100f;
}
