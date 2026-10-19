using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Renders one picture of the whole generated world from an overhead camera and publishes
/// it to the Python client, for figures (trajectories drawn over the arena, or the arena on
/// its own). Reuses the existing message types: the PNG goes out as an
/// <see cref="RGBDMessage"/> (rgb field only) on <see cref="ImageTopic"/> and the camera
/// metadata as a <see cref="StringMessage"/> holding the JSON of <see cref="Meta"/> on
/// <see cref="MetaTopic"/>. Client side: <c>ratsim/world_snapshot.py</c>.
///
/// Opt-in per episode via world config (same mechanism as worldgen_dump):
///   world_snapshot/enabled     -- 0/1 (default 0). One frame after every provider's
///                                 Generate() every chunk overlapping the world bounds is
///                                 force-loaded at LOD0 (trees, rewards and city content are
///                                 chunk-lazy), the picture is rendered and both messages are
///                                 published. Those chunks stay loaded, so this is for
///                                 snapshot/verification resets, not training.
///   world_snapshot/view        -- camera mode, both straight down with image up = +Z
///                                 (default "ortho"; "topdown" is accepted as an alias):
///                                   ortho    orthographic camera (true 2D map)
///                                   persp    perspective camera, fov 40, placed high enough
///                                            to frame the whole world
///   world_snapshot/pitch       -- camera pitch in degrees (default 90 = straight down)
///   world_snapshot/yaw         -- camera yaw in degrees about +Y (default 0)
///   world_snapshot/fov         -- vertical field of view in degrees (persp only, default 40)
///   world_snapshot/width       -- image width in pixels (default 2048)
///   world_snapshot/height      -- image height in pixels; 0 (default) = fit the box aspect
///   world_snapshot/margin      -- extra framing around the world box, fraction (default 0.02)
///   world_snapshot/show_agent  -- 0/1 (default 0): keep the Agents layer in the render
///   world_snapshot/shadows     -- 0/1 (default 1): 0 switches every Light's shadows off for
///                                 the render (flat map look, no wall shadows on the floor)
///   world_snapshot/background  -- "solid" (default) | "skybox"
///   world_snapshot/bg_r|g|b    -- solid background colour 0-1 (default white)
///   world_snapshot/file        -- optional path; when set the PNG is also written there
///
/// Both modes share one framing routine: the eight corners of the world box (world_bounds in
/// X/Z, renderer extent in Y) are transformed into camera space and either the orthographic
/// size or the perspective distance is chosen so every corner is inside the frame. The
/// published view_proj (projectionMatrix * worldToCameraMatrix, row-major, OpenGL clip
/// convention with NDC y up) is what <c>trajectory_plot.project_to_pixels</c> expects, so
/// both modes (and any pitch/yaw override) are handled by the same client code.
/// Fog, the lidar ray visualiser, the UI and WorldGen layers (placement/footprint boxes) and
/// (by default) the agent are hidden for the one render.
/// </summary>
public static class WorldSnapshot {
    public const string ImageTopic = "/sim_control/world_snapshot";
    public const string MetaTopic  = "/sim_control/world_snapshot_meta";

    [Serializable]
    public class Meta {
        public string view;
        public string projection;       // "ortho" | "persp"
        public int width, height;
        public float pitch, yaw, fov;   // fov only meaningful for persp
        public float ortho_size;        // half the vertical extent in metres (ortho only)
        public float margin;
        public float[] view_proj;       // 16 floats, row-major, clip = VP * [x y z 1] (Unity frame)
        public float[] cam_pos;         // Unity frame
        public float[] cam_forward;     // Unity frame
        public float world_width, world_height;
        public float box_min_y, box_max_y;
        public int seed;
    }

    static readonly Dictionary<string, bool> ModeIsOrtho = new Dictionary<string, bool> {
        { "ortho",   true  },
        { "topdown", true  },   // alias
        { "persp",   false },
    };

    public static bool IsEnabled() => WorldLoadingController.GetParamInt("world_snapshot/enabled", 0) != 0;

    /// <summary>
    /// Coroutine started by WorldLoadingController after every provider's Generate(): waits
    /// one frame (so last episode's deferred-destroyed objects are really gone) and publishes.
    /// The Python side polls a few steps for the messages, so the one-step delay is invisible.
    /// </summary>
    public static IEnumerator PublishNextFrame() {
        yield return null;
        Publish();
    }

    public static void Publish() {
        try {
            Render();
        } catch (Exception e) {
            WorldGenStatus.Error("WorldSnapshot", $"failed: {e.Message}\n{e.StackTrace}");
        }
    }

    // ─────────────────────────────────────────────

    static void Render() {
        // ---- parameters
        string viewName = WorldLoadingController.GetParamString("world_snapshot/view", "ortho");
        if (!ModeIsOrtho.TryGetValue(viewName, out bool ortho)) {
            WorldGenStatus.Warning("WorldSnapshot", $"unknown view '{viewName}' (ortho|persp), using ortho");
            viewName = "ortho";
            ortho = true;
        }
        if (viewName == "topdown") viewName = "ortho";
        float pitch  = WorldLoadingController.GetParamFloat("world_snapshot/pitch",  90f);
        float yaw    = WorldLoadingController.GetParamFloat("world_snapshot/yaw",    0f);
        float fov    = WorldLoadingController.GetParamFloat("world_snapshot/fov",    40f);
        int   width  = Mathf.Clamp(WorldLoadingController.GetParamInt("world_snapshot/width", 2048), 16, 8192);
        int   height = WorldLoadingController.GetParamInt("world_snapshot/height", 0);
        float margin = Mathf.Max(0f, WorldLoadingController.GetParamFloat("world_snapshot/margin", 0.02f));
        bool  showAgent = WorldLoadingController.GetParamInt("world_snapshot/show_agent", 0) != 0;
        bool  shadows = WorldLoadingController.GetParamInt("world_snapshot/shadows", 1) != 0;
        bool  skybox = WorldLoadingController.GetParamString("world_snapshot/background", "solid") == "skybox";
        Color bg = new Color(WorldLoadingController.GetParamFloat("world_snapshot/bg_r", 1f),
                             WorldLoadingController.GetParamFloat("world_snapshot/bg_g", 1f),
                             WorldLoadingController.GetParamFloat("world_snapshot/bg_b", 1f), 1f);
        string file = WorldLoadingController.GetParamString("world_snapshot/file", "");

        float worldW = WorldLoadingController.GetParamFloat("world_bounds/width",  100f);
        float worldH = WorldLoadingController.GetParamFloat("world_bounds/height", 100f);

        // ---- make sure the whole world exists at full detail
        ForceLoadAllChunks(worldW, worldH);
        Physics.SyncTransforms();

        // ---- world box: X/Z from the bounds, Y from what is actually rendered inside them
        Bounds box = WorldBox(worldW, worldH);

        // ---- framing: transform the 8 corners into camera space
        Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
        Quaternion inv = Quaternion.Inverse(rot);
        Vector3 fwd = rot * Vector3.forward;
        Vector3 lo = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        Vector3 hi = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        Vector3[] corners = Corners(box);
        for (int i = 0; i < 8; i++) {
            Vector3 l = inv * (corners[i] - box.center);
            lo = Vector3.Min(lo, l); hi = Vector3.Max(hi, l);
        }
        float halfW = Mathf.Max(Mathf.Abs(lo.x), Mathf.Abs(hi.x)) * (1f + margin);
        float halfH = Mathf.Max(Mathf.Abs(lo.y), Mathf.Abs(hi.y)) * (1f + margin);
        if (height <= 0)
            height = Mathf.Clamp(Mathf.RoundToInt(width * halfH / Mathf.Max(halfW, 1e-6f)), 16, 8192);
        float aspect = (float)width / height;

        float orthoSize = 0f, dist;
        if (ortho) {
            orthoSize = Mathf.Max(halfH, halfW / aspect);
            dist = -lo.z + 1f;                       // every corner in front of the near plane
        } else {
            float tanV = Mathf.Tan(0.5f * fov * Mathf.Deg2Rad);
            float tanH = tanV * aspect;
            dist = 0f;
            for (int i = 0; i < 8; i++) {
                Vector3 l = inv * (corners[i] - box.center);
                float need = Mathf.Max(Mathf.Abs(l.y) * (1f + margin) / tanV,
                                       Mathf.Abs(l.x) * (1f + margin) / tanH) - l.z;
                dist = Mathf.Max(dist, need);
            }
            dist = Mathf.Max(dist, -lo.z + 1f);
        }
        Vector3 camPos = box.center - fwd * dist;
        float near = Mathf.Max(0.05f, dist + lo.z - 1f);
        // Far plane well past the box so the terrain around the world is not cut to a
        // hard edge in tilted views (padding chunks extend beyond the bounds).
        float far  = dist + hi.z + Mathf.Max(worldW, worldH) + 1f;

        // ---- temporary camera
        var go = new GameObject("WorldSnapshotCamera");
        go.hideFlags = HideFlags.HideAndDontSave;
        Camera cam = go.AddComponent<Camera>();
        cam.enabled = false;                          // rendered only by the explicit Render() below
        cam.transform.SetPositionAndRotation(camPos, rot);
        cam.orthographic = ortho;
        if (ortho) cam.orthographicSize = orthoSize; else cam.fieldOfView = fov;
        cam.aspect = aspect;
        cam.nearClipPlane = near;
        cam.farClipPlane = far;
        cam.clearFlags = skybox ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
        cam.backgroundColor = bg;
        // Render the world only: no UI, no worldgen placement/footprint boxes (WorldGen
        // layer), and no agent unless asked for.
        int mask = ~0;
        foreach (string layer in new[] { "UI", "WorldGen" }) {
            int l = LayerMask.NameToLayer(layer);
            if (l >= 0) mask &= ~(1 << l);
        }
        int agents = LayerMask.NameToLayer("Agents");
        if (!showAgent && agents >= 0) mask &= ~(1 << agents);
        cam.cullingMask = mask;

        // ---- hide things that are not the world
        bool fogWas = RenderSettings.fog;
        RenderSettings.fog = false;
        var rayVis = UnityEngine.Object.FindObjectsByType<LidarRaycastVisualizer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var rayWas = new bool[rayVis.Length];
        for (int i = 0; i < rayVis.Length; i++) { rayWas[i] = rayVis[i].enabled; rayVis[i].enabled = false; }
        var lights = shadows ? new Light[0]
            : UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        var lightShadowsWas = new LightShadows[lights.Length];
        for (int i = 0; i < lights.Length; i++) { lightShadowsWas[i] = lights[i].shadows; lights[i].shadows = LightShadows.None; }

        Texture2D tex = null;
        RenderTexture rt = null;
        try {
            rt = new RenderTexture(width, height, 24);
            cam.targetTexture = rt;
            tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            cam.Render();
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply();
        } finally {
            cam.targetTexture = null;
            RenderTexture.active = null;
            RenderSettings.fog = fogWas;
            for (int i = 0; i < rayVis.Length; i++) if (rayVis[i] != null) rayVis[i].enabled = rayWas[i];
            for (int i = 0; i < lights.Length; i++) if (lights[i] != null) lights[i].shadows = lightShadowsWas[i];
        }

        // ---- metadata: matrix taken from the very camera that rendered
        Matrix4x4 vp = cam.projectionMatrix * cam.worldToCameraMatrix;
        var meta = new Meta {
            view = viewName, projection = ortho ? "ortho" : "persp",
            width = width, height = height, pitch = pitch, yaw = yaw, fov = ortho ? 0f : fov,
            ortho_size = orthoSize, margin = margin,
            view_proj = Flatten(vp),
            cam_pos = new[] { camPos.x, camPos.y, camPos.z },
            cam_forward = new[] { fwd.x, fwd.y, fwd.z },
            world_width = worldW, world_height = worldH,
            box_min_y = box.min.y, box_max_y = box.max.y,
            seed = WorldLoadingController.GetSeed(),
        };

        byte[] png = tex.EncodeToPNG();
        UnityEngine.Object.Destroy(tex);
        UnityEngine.Object.Destroy(rt);
        UnityEngine.Object.Destroy(go);

        if (!string.IsNullOrWhiteSpace(file)) {
            try { File.WriteAllBytes(file, png); }
            catch (Exception e) { WorldGenStatus.Warning("WorldSnapshot", $"could not write '{file}': {e.Message}"); }
        }

        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        if (conn != null) {
            conn.Publish(MetaTopic, new StringMessage { data = JsonUtility.ToJson(meta) });
            conn.Publish(ImageTopic, new RGBDMessage {
                rgbImageBase64 = Convert.ToBase64String(png), depthImageBase64 = "",
                minDepth = 0f, maxDepth = 0f });
        }

        Debug.Log($"WorldSnapshot: {viewName} {width}x{height} ({png.Length} bytes)" +
                  $"{(string.IsNullOrWhiteSpace(file) ? "" : " → " + file)}");
    }

    // ─────────────────────────────────────────────

    static float[] Flatten(Matrix4x4 m) {
        var f = new float[16];
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 4; c++)
                f[r * 4 + c] = m[r, c];
        return f;
    }

    static Vector3[] Corners(Bounds b) {
        Vector3 mn = b.min, mx = b.max;
        return new[] {
            new Vector3(mn.x, mn.y, mn.z), new Vector3(mx.x, mn.y, mn.z),
            new Vector3(mn.x, mx.y, mn.z), new Vector3(mx.x, mx.y, mn.z),
            new Vector3(mn.x, mn.y, mx.z), new Vector3(mx.x, mn.y, mx.z),
            new Vector3(mn.x, mx.y, mx.z), new Vector3(mx.x, mx.y, mx.z),
        };
    }

    /// <summary>X/Z = world bounds (centred on the origin); Y = extent of every renderer
    /// whose bounds overlap that rectangle. Falls back to 0..10 when nothing renders.</summary>
    static Bounds WorldBox(float w, float h) {
        float minY = float.MaxValue, maxY = float.MinValue;
        var rect = new Rect(-w * 0.5f, -h * 0.5f, w, h);
        foreach (Renderer r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) {
            if (!r.enabled) continue;
            Bounds b = r.bounds;
            if (b.max.x < rect.xMin || b.min.x > rect.xMax || b.max.z < rect.yMin || b.min.z > rect.yMax) continue;
            minY = Mathf.Min(minY, b.min.y);
            maxY = Mathf.Max(maxY, b.max.y);
        }
        if (minY > maxY) { minY = 0f; maxY = 10f; }
        var box = new Bounds();
        box.SetMinMax(new Vector3(-w * 0.5f, minY, -h * 0.5f), new Vector3(w * 0.5f, maxY, h * 0.5f));
        return box;
    }

    static void ForceLoadAllChunks(float w, float h) {
        float cw = WorldLoadingController.GetChunkWidth();
        if (cw <= 0f) return;
        int minCX = Mathf.FloorToInt(-w * 0.5f / cw) - 1, maxCX = Mathf.FloorToInt(w * 0.5f / cw) + 1;
        int minCZ = Mathf.FloorToInt(-h * 0.5f / cw) - 1, maxCZ = Mathf.FloorToInt(h * 0.5f / cw) + 1;
        for (int cx = minCX; cx <= maxCX; cx++)
            for (int cz = minCZ; cz <= maxCZ; cz++)
                foreach (WorldDataProvider p in WorldDataProvider.registered)
                    p.GenerateChunk(cx, cz, 0);
    }
}
