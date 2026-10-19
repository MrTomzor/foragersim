using UnityEngine;

/// <summary>
/// Human control UI "V2": drives the scene's overhead observer camera into the agent and aims
/// it forward, so the human sees a first-person RGB view instead of a sensor abstraction.
/// Visibility is cut down to the 2D lidar's range so the human is no better sighted than the
/// policy would be.
///
/// This is a **viewer only**. It moves the scene's observer camera (the one
/// <see cref="TopdownCameraFollower"/> normally holds above the agent) and never touches
/// <see cref="RGBDSensor"/>, so RL camera observations are completely unaffected by it.
///
/// Range limiting uses fog, not the far clip plane: Unity's clip planes are planes
/// perpendicular to the camera's forward axis, so a far clip of R would cut geometry at
/// R metres of *depth*, letting the agent see further out towards the corners of the frame
/// than straight ahead. Fog is the closer approximation. (Built-in fog is also computed from
/// clip-space depth rather than true radial distance, so the cut-off is still a little
/// generous off-axis — but it fades rather than pops, which reads far better.)
///
/// The camera is positioned each LateUpdate rather than parented to the agent, because
/// AgentLoader destroys and respawns the agent every episode and a parented camera would go
/// with it.
/// </summary>
public class FirstPersonViewController : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Observer camera to commandeer. Falls back to Camera.main.")]
    public Camera viewerCamera;
    [Tooltip("The overhead follower to suspend while in first person. Falls back to the one on viewerCamera.")]
    public TopdownCameraFollower topdownFollower;

    [Header("First-person camera")]
    [Tooltip("Extra height above the lidar origin for the eye point, in world units.")]
    public float eyeHeightOffset = 0.3f;
    public float fieldOfView = 90f;
    [Tooltip("Keep the view level even if the agent body pitches or rolls.")]
    public bool lockPitchAndRoll = true;

    [Header("Range Limiting")]
    [Tooltip("Limit visibility to the lidar range using fog. Unity's far clip is depth-based, not radial, so fog is the better fit.")]
    public bool limitRangeWithFog = true;
    [Tooltip("Fraction of the range at which the fade starts. 0.35 = clear out to 35%, then fading to invisible at full range.")]
    [Range(0f, 1f)] public float fogStartFraction = 0.35f;
    public Color fogColor = Color.black;
    [Tooltip("Far clip as a multiple of the range. Everything past the fog end is already fog-coloured, so this is a pure culling win.")]
    public float farClipRangeMultiplier = 1.1f;
    [Tooltip("Visible range in world units. 0 = take it from the agent's lidar maxRange.")]
    public float rangeOverride = 0f;
    [Tooltip("Range used when the agent has no lidar and no override is set.")]
    public float fallbackRange = 20f;

    public bool IsFirstPerson => active && firstPerson;

    private GameObject agent;
    private SemanticLidarSensor lidar;
    private bool active;
    private bool firstPerson;

    // LidarRaycastVisualizer draws the rays as GL lines in the world (not an overlay, and it
    // works in builds). Switched off on entry, but left enabled so its own G key can still
    // bring the rays back if the operator wants them.
    private LidarRaycastVisualizer rayVisualizer;
    private bool savedRayVisualizerShowRays;
    // SemanticLidarSensor.debugDrawRays uses Debug.DrawLine — Editor gizmos only, invisible in a
    // build, but it clutters the first-person view while testing in the Editor.
    private SemanticLidarSensor debugRaySensor;
    private bool savedDebugDrawRays;

    // Saved state, restored when leaving first person or deactivating entirely.
    private bool stateSaved;
    private bool savedFollowerEnabled;
    private float savedFov;
    private float savedFarClip;
    private CameraClearFlags savedClearFlags;
    private Color savedBackgroundColor;
    private bool savedFog;
    private FogMode savedFogMode;
    private Color savedFogColor;
    private float savedFogStart;
    private float savedFogEnd;

    /// <summary>Take over the observer camera for this agent. Safe to call repeatedly.</summary>
    public void Activate(GameObject agentObject, SemanticLidarSensor lidarSensor)
    {
        if (viewerCamera == null) viewerCamera = Camera.main;
        if (viewerCamera == null)
        {
            Debug.LogWarning("FirstPersonViewController: no viewer camera found");
            return;
        }
        if (topdownFollower == null)
            topdownFollower = viewerCamera.GetComponent<TopdownCameraFollower>();

        agent = agentObject;
        lidar = lidarSensor;

        if (!active) SaveState();
        active = true;
        SuppressRayVisualizer();
        SetFirstPerson(true);
    }

    /// <summary>Hand the camera back to the overhead follower.</summary>
    public void Deactivate()
    {
        if (!active) return;
        RestoreState();
        RestoreRayVisualizer();
        active = false;
        firstPerson = false;
        agent = null;
        lidar = null;
    }

    /// <summary>Switch between the in-agent view and the normal overhead view.</summary>
    public void SetFirstPerson(bool on)
    {
        if (!active) return;
        firstPerson = on;

        if (on)
        {
            if (topdownFollower != null) topdownFollower.enabled = false;
            ApplyCameraSettings();
            ApplyFog();
            UpdateCameraPose();
        }
        else
        {
            RestoreState();
            if (topdownFollower != null) topdownFollower.enabled = true;
        }
    }

    void LateUpdate()
    {
        if (!active || !firstPerson) return;

        ResolveAgent();
        if (agent == null) return;

        UpdateCameraPose();
        // Re-applied every frame: LightingAndFogLoader rewrites RenderSettings on episode load.
        ApplyFog();
    }

    /// <summary>Re-acquire the agent after an episode reload destroyed the old one.</summary>
    void ResolveAgent()
    {
        if (agent != null) return;

        agent = WorldLoadingController.instance?.agentObject;
        if (agent != null)
        {
            lidar = agent.GetComponentInChildren<SemanticLidarSensor>(true);
            SuppressRayVisualizer();
        }
    }

    /// <summary>Turn off the in-world GL ray lines — V2 shows no lidar visualization.</summary>
    void SuppressRayVisualizer()
    {
        if (agent == null) return;

        var found = agent.GetComponentInChildren<LidarRaycastVisualizer>(true);
        if (found != null && found != rayVisualizer)
        {
            rayVisualizer = found;
            savedRayVisualizerShowRays = rayVisualizer.showRays;
            rayVisualizer.showRays = false;
        }

        if (lidar != null && lidar != debugRaySensor)
        {
            debugRaySensor = lidar;
            savedDebugDrawRays = debugRaySensor.debugDrawRays;
            debugRaySensor.debugDrawRays = false;
        }
    }

    void RestoreRayVisualizer()
    {
        if (rayVisualizer != null)
        {
            rayVisualizer.showRays = savedRayVisualizerShowRays;
            rayVisualizer = null;
        }

        if (debugRaySensor != null)
        {
            debugRaySensor.debugDrawRays = savedDebugDrawRays;
            debugRaySensor = null;
        }
    }

    void UpdateCameraPose()
    {
        if (viewerCamera == null || agent == null) return;

        // Sit at the lidar origin so the human's viewpoint matches where the rays are cast from.
        Transform eye = lidar != null ? lidar.transform : agent.transform;
        viewerCamera.transform.position = eye.position + Vector3.up * eyeHeightOffset;
        viewerCamera.transform.rotation = lockPitchAndRoll
            ? Quaternion.Euler(0f, eye.eulerAngles.y, 0f)
            : eye.rotation;
    }

    float GetRange()
    {
        if (rangeOverride > 0f) return rangeOverride;
        if (lidar != null && lidar.maxRange > 0f) return lidar.maxRange;
        return fallbackRange;
    }

    void ApplyCameraSettings()
    {
        float range = GetRange();
        viewerCamera.fieldOfView = fieldOfView;
        viewerCamera.farClipPlane = Mathf.Max(viewerCamera.nearClipPlane + 0.1f,
                                              range * farClipRangeMultiplier);
        // Solid colour rather than skybox, so the sky doesn't show through past the fog end.
        viewerCamera.clearFlags = CameraClearFlags.SolidColor;
        viewerCamera.backgroundColor = fogColor;
    }

    void ApplyFog()
    {
        if (!limitRangeWithFog) return;

        float range = GetRange();
        RenderSettings.fog = true;
        RenderSettings.fogMode = FogMode.Linear;
        RenderSettings.fogColor = fogColor;
        RenderSettings.fogStartDistance = range * fogStartFraction;
        RenderSettings.fogEndDistance = range;
    }

    void SaveState()
    {
        savedFollowerEnabled = topdownFollower != null && topdownFollower.enabled;
        savedFov = viewerCamera.fieldOfView;
        savedFarClip = viewerCamera.farClipPlane;
        savedClearFlags = viewerCamera.clearFlags;
        savedBackgroundColor = viewerCamera.backgroundColor;

        savedFog = RenderSettings.fog;
        savedFogMode = RenderSettings.fogMode;
        savedFogColor = RenderSettings.fogColor;
        savedFogStart = RenderSettings.fogStartDistance;
        savedFogEnd = RenderSettings.fogEndDistance;

        stateSaved = true;
    }

    void RestoreState()
    {
        if (!stateSaved || viewerCamera == null) return;

        viewerCamera.fieldOfView = savedFov;
        viewerCamera.farClipPlane = savedFarClip;
        viewerCamera.clearFlags = savedClearFlags;
        viewerCamera.backgroundColor = savedBackgroundColor;

        RenderSettings.fog = savedFog;
        RenderSettings.fogMode = savedFogMode;
        RenderSettings.fogColor = savedFogColor;
        RenderSettings.fogStartDistance = savedFogStart;
        RenderSettings.fogEndDistance = savedFogEnd;

        if (topdownFollower != null) topdownFollower.enabled = savedFollowerEnabled;
    }

    void OnDisable()
    {
        Deactivate();
    }
}
