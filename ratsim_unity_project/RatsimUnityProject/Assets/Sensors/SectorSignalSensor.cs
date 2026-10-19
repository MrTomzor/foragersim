using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Egocentric sector-binned multi-channel signal sensor. Each tick:
///   1. For each channel the sensor listens on, iterate all active <see cref="SignalSource"/>s
///      matching that channel.
///   2. Compute distance and egocentric bearing (angle relative to agent's forward axis).
///   3. Compute the source's value at that distance (via SignalSource.ValueAt).
///   4. Spread the value across sectors with a gaussian centered on the bearing's sector,
///      σ expressed in bin widths (see <see cref="sigmaBins"/>).
///   5. Aggregate contributions from all sources on the same channel by max.
///   6. Clamp each sector to [0,1] and publish as a <see cref="FloatArrayMessage"/>
///      on "<topicPrefix>/<channel>".
///
/// Sectors are forward-centered: sector 0 straddles the agent's +forward axis, so its
/// centre is at bearing 0 and its edges are at ±π/N. Sectors wrap around.
///
/// No occlusion, no raycasts. Sources beyond their own <c>range</c> contribute 0.
///
/// Config keys (all under the sensor's component name, e.g. "sector_signal/"):
///   channels         -- comma-separated channel names, e.g. "food,predator"
///   n_sectors        -- integer, number of angular bins (default 8)
///   sigma_bins       -- gaussian σ in units of bin widths (default 1.0 — neighbours
///                       get ~0.61, bin-2 gets ~0.14)
///   topic_prefix     -- base topic; each channel publishes on "<prefix>/<channel>"
///                       (default "/sector_signal")
/// </summary>
public class SectorSignalSensor : MonoBehaviour
{
    [Header("Channels & Sectors")]
    public string channels = "default";
    public int nSectors = 8;
    public float sigmaBins = 1.0f;

    [Header("Publishing")]
    public string topicPrefix = "/sector_signal";

    /// <summary>Latest per-channel sector values, indexed [channelIdx][sectorIdx]. Read by UI visualizer.</summary>
    [HideInInspector] public float[][] lastValues;
    /// <summary>Channel names matching <see cref="lastValues"/>, in order. Read by UI visualizer.</summary>
    [HideInInspector] public string[] channelNames;

    private RoslikeTCPServer _conn;
    private string[] _perChannelTopics;

    void Start()
    {
        _conn = RoslikeTCPServer.GetInstance();

        // Parse channels
        var parsed = new List<string>();
        if (!string.IsNullOrEmpty(channels))
        {
            foreach (string raw in channels.Split(','))
            {
                string c = raw.Trim();
                if (!string.IsNullOrEmpty(c)) parsed.Add(c);
            }
        }
        channelNames = parsed.ToArray();

        if (nSectors < 1) nSectors = 1;

        lastValues = new float[channelNames.Length][];
        _perChannelTopics = new string[channelNames.Length];
        for (int i = 0; i < channelNames.Length; i++)
        {
            lastValues[i] = new float[nSectors];
            _perChannelTopics[i] = topicPrefix + "/" + channelNames[i];
        }

        _conn.RegisterTimerDiscrete(SenseAndPublish, 1);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        if (channelNames == null || channelNames.Length == 0) return;

        // Cache agent forward yaw (ROS convention: x=forward). Unity forward is +Z; we
        // compute bearing = atan2(-localRight, localForward) but it's simpler to just
        // rotate source position into the sensor's local frame and use atan2 there.
        Vector3 agentPos = transform.position;
        Quaternion invRot = Quaternion.Inverse(transform.rotation);

        float bin = 2f * Mathf.PI / nSectors;
        float denom = sigmaBins * bin;
        if (denom <= 1e-6f) denom = 1e-6f;

        // Zero out buffers
        for (int c = 0; c < channelNames.Length; c++)
            System.Array.Clear(lastValues[c], 0, nSectors);

        // Iterate all active sources
        var sources = SignalSource.Active;
        for (int si = 0; si < sources.Count; si++)
        {
            var src = sources[si];
            if (src == null || !src.enabled) continue;

            // Match channel
            int chIdx = -1;
            for (int c = 0; c < channelNames.Length; c++)
            {
                if (channelNames[c] == src.channel) { chIdx = c; break; }
            }
            if (chIdx < 0) continue;

            Vector3 delta = src.transform.position - agentPos;
            float dist = delta.magnitude;
            if (dist >= src.range) continue;

            float value = src.ValueAt(dist);
            if (value <= 0f) continue;

            // Bearing in agent-local frame (Unity: +Z forward, +X right).
            // Sensor is forward-centered, so sector 0 is around +Z.
            Vector3 local = invRot * delta;
            float bearing = Mathf.Atan2(local.x, local.z); // [-pi, pi], 0 = forward, + = right

            // Distribute across sectors with wrap-aware gaussian.
            float[] buf = lastValues[chIdx];
            for (int k = 0; k < nSectors; k++)
            {
                float centre = k * bin;
                // Forward-centered: shift so sector 0 centre is at bearing 0. We use k * bin
                // directly in [0, 2pi) and compute shortest-arc angular diff.
                float diff = AngularDiff(bearing, centre);
                float x = diff / denom;
                float w = Mathf.Exp(-(x * x));
                float contrib = value * w;
                if (contrib > buf[k]) buf[k] = contrib; // max aggregation
            }
        }

        // Clamp to [0,1] and publish per channel
        for (int c = 0; c < channelNames.Length; c++)
        {
            float[] buf = lastValues[c];
            for (int k = 0; k < nSectors; k++)
                if (buf[k] > 1f) buf[k] = 1f;

            var msg = new FloatArrayMessage { data = (float[])buf.Clone() };
            _conn.Publish(_perChannelTopics[c], msg);
        }
    }

    /// <summary>Shortest signed angular distance from a to b, in [-π, π].</summary>
    static float AngularDiff(float a, float b)
    {
        float d = a - b;
        while (d > Mathf.PI) d -= 2f * Mathf.PI;
        while (d < -Mathf.PI) d += 2f * Mathf.PI;
        return d;
    }
}
