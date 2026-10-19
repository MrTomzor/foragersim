using System.Collections.Generic;
using TMPro;
using UnityEngine;

/// <summary>
/// Manages sensor visualization UI overlays for human control mode.
/// Subscribes to the human control toggle and activates/deactivates
/// visualizers based on the first agent's active sensors.
/// Visualizers read data directly from sensor components (not via TCP topics).
///
/// Three UI styles, selected by <see cref="uiStyle"/> in the scene (baked into builds):
///   V0 TopdownRadar      — legacy egocentric top-down radar: lidar rays from screen centre,
///                          compass on top of them, head-direction cells and sector signal aside.
///   V1 DepthStrip        — depth strip across the top of the screen (one cell per ray) with a
///                          bottom bar holding the compass (left), score (centre) and sector
///                          signal (right). Head-direction cells are not drawn.
///   V2 FirstPersonCamera — the observer camera moves into the agent and aims forward, giving a
///                          plain RGB first-person view fogged down to the lidar's range. No
///                          lidar visualization at all; same V1 bottom bar as a HUD.
///
/// Keys: <see cref="viewToggleKey"/> (C) switches between the human game view — whichever of the
/// three styles is selected — and the overhead debugging view, in every style. G is not bound
/// here; it belongs to LidarRaycastVisualizer, which draws the rays in the world.
/// </summary>
public class SensorVisualizationManager : MonoBehaviour
{
    public enum HumanUIStyle
    {
        TopdownRadar = 0,
        DepthStrip = 1,
        FirstPersonCamera = 2,
    }

    [Header("UI Style")]
    [Tooltip("Which human-control UI to use. Baked into builds.")]
    public HumanUIStyle uiStyle = HumanUIStyle.DepthStrip;

    [Header("Visualizer References")]
    public Lidar2DVisualizer lidar2DVisualizer;
    public DepthStripVisualizer depthStripVisualizer;
    public FirstPersonViewController firstPersonView;
    public CompassVisualizer compassVisualizer;
    public HeadDirectionCellsVisualizer headDirectionCellsVisualizer;
    public SectorSignalVisualizer sectorSignalVisualizer;
    public GameObject cameraBlocker;
    public GameObject scoreVisualizer;
    [Tooltip("Scene observer camera. Force-enabled when switching to the overhead debug view, " +
             "since ComponentEnablerSimple leaves it disabled until human control starts.")]
    public Camera observerCamera;

    [Header("V1 / V2 Layout (bottom bar)")]
    [Tooltip("Gap between the top of the screen and the top of the depth strip, in pixels.")]
    public float v1TopMargin = 50f;
    [Tooltip("Height of the bottom bar holding compass / score / sector signal, in pixels.")]
    public float v1BottomBarHeight = 220f;
    [Tooltip("Inset of the compass and sector signal from the left/right screen edges, in pixels.")]
    public float v1BottomBarSideMargin = 110f;

    [Header("Settings")]
    public string humanControlTopic = "/enable_human_control";

    private bool visualizationEnabled = false;
    private bool humanView = true;

    private Dictionary<string, System.Action<GameObject>> sensorToVisualizer;

    [Tooltip("Switches between the human game view and the overhead debugging view, in every style.")]
    public string viewToggleKey = "c";

    void Start()
    {
        sensorToVisualizer = new Dictionary<string, System.Action<GameObject>>
        {
            { "lidar2d", SetupLidar2D },
            { "compass", SetupCompass },
            { "head_direction_cells", SetupHeadDirectionCells },
            { "sector_signal", SetupSectorSignal },
        };

        // V1 and V2 share the bottom bar; V0 keeps the authored positions.
        if (uiStyle != HumanUIStyle.TopdownRadar)
            ApplyV1Layout();

        SetAllVisualizersActive(false);

        RoslikeTCPServer.GetInstance().Subscribe<BoolMessage>(humanControlTopic, OnHumanControlToggle);
    }

    void Update()
    {
        if (!Input.GetKeyDown(viewToggleKey)) return;

        // First press brings the human UI up at all; after that it swaps between the human game
        // view and the overhead debugging view.
        if (!visualizationEnabled)
        {
            SetVisualizationEnabled(true);
            return;
        }

        humanView = !humanView;
        ApplyView();
    }

    void OnHumanControlToggle(BoolMessage msg)
    {
        SetVisualizationEnabled(msg.data);
    }

    void SetVisualizationEnabled(bool on)
    {
        visualizationEnabled = on;

        if (on)
        {
            humanView = true;
            ApplyView();
        }
        else
        {
            SetAllVisualizersActive(false);
        }
    }

    /// <summary>
    /// Human game view = whichever style is selected, plus its HUD.
    /// Overhead debugging view = clean world render from the topdown camera, sensor overlays
    /// hidden, score kept.
    /// </summary>
    void ApplyView()
    {
        if (!visualizationEnabled) return;

        if (humanView)
        {
            EnableVisualizersForAgent();
            // V2 renders the world through the camera, so it must not be blacked out.
            cameraBlocker.SetActive(uiStyle != HumanUIStyle.FirstPersonCamera);
            scoreVisualizer.SetActive(true);
            return;
        }

        if (lidar2DVisualizer != null) lidar2DVisualizer.gameObject.SetActive(false);
        if (depthStripVisualizer != null) depthStripVisualizer.gameObject.SetActive(false);
        if (compassVisualizer != null) compassVisualizer.gameObject.SetActive(false);
        if (headDirectionCellsVisualizer != null) headDirectionCellsVisualizer.gameObject.SetActive(false);
        if (sectorSignalVisualizer != null) sectorSignalVisualizer.gameObject.SetActive(false);
        cameraBlocker.SetActive(false);
        scoreVisualizer.SetActive(true);

        // Hand the camera back to the overhead follower and make sure it is actually rendering.
        if (firstPersonView != null) firstPersonView.SetFirstPerson(false);
        if (observerCamera != null) observerCamera.enabled = true;
    }

    /// <summary>
    /// Re-anchors the shared widgets into the V1 bottom bar: compass bottom-left,
    /// score bottom-centre, sector signal bottom-right. Run once at Start — the style
    /// is scene-baked, so there is nothing to restore.
    /// </summary>
    void ApplyV1Layout()
    {
        float barCentreY = v1BottomBarHeight * 0.5f;

        PlaceInBottomBar(compassVisualizer != null ? compassVisualizer.transform as RectTransform : null,
                         new Vector2(0f, 0f), new Vector2(v1BottomBarSideMargin, barCentreY));

        PlaceInBottomBar(scoreVisualizer != null ? scoreVisualizer.transform as RectTransform : null,
                         new Vector2(0.5f, 0f), new Vector2(0f, barCentreY));

        PlaceInBottomBar(sectorSignalVisualizer != null ? sectorSignalVisualizer.transform as RectTransform : null,
                         new Vector2(1f, 0f), new Vector2(-v1BottomBarSideMargin, barCentreY));

        // The score texts are authored left-aligned for the V0 bottom-left corner; centre
        // them on their own container so they read as one block in the middle of the bar.
        var score = scoreVisualizer != null ? scoreVisualizer.GetComponent<ScoreVisualizer>() : null;
        if (score != null)
        {
            CentreScoreText(score.totalScoreText, new Vector2(0f, -10f), 460f);
            CentreScoreText(score.positiveDeltaText, new Vector2(120f, 44f), 220f);
            CentreScoreText(score.negativeDeltaText, new Vector2(-120f, 44f), 220f);
        }

        if (depthStripVisualizer != null)
            depthStripVisualizer.topMargin = v1TopMargin;
    }

    void PlaceInBottomBar(RectTransform rt, Vector2 anchor, Vector2 offset)
    {
        if (rt == null) return;
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = offset;
    }

    void CentreScoreText(TextMeshProUGUI text, Vector2 offset, float width)
    {
        if (text == null) return;
        var rt = text.rectTransform;
        rt.anchorMin = new Vector2(0.5f, 0.5f);
        rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = offset;
        // Wide enough that the label never wraps at the sizes we use.
        rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
        text.alignment = TextAlignmentOptions.Center;
        // The V0 texts carry a non-zero right margin, which would bias the centring.
        text.margin = Vector4.zero;
    }

    void EnableVisualizersForAgent()
    {
        GameObject agent = WorldLoadingController.instance?.agentObject;
        if (agent == null)
        {
            Debug.LogWarning("SensorVisualizationManager: no agent found");
            return;
        }

        // V2 takes over the camera whether or not the agent carries a lidar — the lidar only
        // supplies the visible range, and there is a fallback for agents without one.
        if (uiStyle == HumanUIStyle.FirstPersonCamera)
        {
            if (firstPersonView != null)
                firstPersonView.Activate(agent, agent.GetComponentInChildren<SemanticLidarSensor>(true));
            else
                Debug.LogWarning("SensorVisualizationManager: uiStyle is FirstPersonCamera but no FirstPersonViewController is assigned");
        }

        foreach (var kvp in sensorToVisualizer)
        {
            MonoBehaviour sensorComponent = GetSensorComponent(agent, kvp.Key);
            if (sensorComponent != null && sensorComponent.enabled)
            {
                kvp.Value(agent);
            }
        }
    }

    MonoBehaviour GetSensorComponent(GameObject agent, string sensorName)
    {
        switch (sensorName)
        {
            case "lidar2d": return agent.GetComponentInChildren<SemanticLidarSensor>(true);
            case "compass": return agent.GetComponentInChildren<CompassSensor>(true);
            case "head_direction_cells": return agent.GetComponentInChildren<HeadDirectionCellsSensor>(true);
            case "sector_signal": return agent.GetComponentInChildren<SectorSignalSensor>(true);
            default: return null;
        }
    }

    void SetupLidar2D(GameObject agent)
    {
        // V2 deliberately draws no lidar visualization at all — the camera view is the readout.
        if (uiStyle == HumanUIStyle.FirstPersonCamera) return;

        var sensor = agent.GetComponentInChildren<SemanticLidarSensor>(true);

        if (uiStyle == HumanUIStyle.DepthStrip)
        {
            if (depthStripVisualizer == null)
            {
                Debug.LogWarning("SensorVisualizationManager: uiStyle is DepthStrip but no DepthStripVisualizer is assigned");
                return;
            }
            depthStripVisualizer.gameObject.SetActive(true);
            depthStripVisualizer.Initialize(sensor);
            return;
        }

        if (lidar2DVisualizer == null) return;
        lidar2DVisualizer.gameObject.SetActive(true);
        lidar2DVisualizer.Initialize(sensor);
    }

    void SetupCompass(GameObject agent)
    {
        if (compassVisualizer == null) return;
        var sensor = agent.GetComponentInChildren<CompassSensor>(true);
        compassVisualizer.gameObject.SetActive(true);
        compassVisualizer.Initialize(sensor);
    }

    void SetupHeadDirectionCells(GameObject agent)
    {
        // V0 only — it has no slot in the V1/V2 bottom bar yet.
        if (uiStyle != HumanUIStyle.TopdownRadar) return;
        if (headDirectionCellsVisualizer == null) return;
        var sensor = agent.GetComponentInChildren<HeadDirectionCellsSensor>(true);
        headDirectionCellsVisualizer.gameObject.SetActive(true);
        headDirectionCellsVisualizer.Initialize(sensor);
    }

    void SetupSectorSignal(GameObject agent)
    {
        if (sectorSignalVisualizer == null) return;
        var sensor = agent.GetComponentInChildren<SectorSignalSensor>(true);
        sectorSignalVisualizer.gameObject.SetActive(true);
        sectorSignalVisualizer.Initialize(sensor);
    }

    void SetAllVisualizersActive(bool active)
    {
        Debug.Log($"SensorVisualizationManager: setting all visualizers active={active}, style={uiStyle}");
        bool radar = uiStyle == HumanUIStyle.TopdownRadar;
        bool strip = uiStyle == HumanUIStyle.DepthStrip;
        bool firstPerson = uiStyle == HumanUIStyle.FirstPersonCamera;

        if (lidar2DVisualizer != null) lidar2DVisualizer.gameObject.SetActive(active && radar);
        if (depthStripVisualizer != null) depthStripVisualizer.gameObject.SetActive(active && strip);
        if (compassVisualizer != null) compassVisualizer.gameObject.SetActive(active);
        if (headDirectionCellsVisualizer != null)
            headDirectionCellsVisualizer.gameObject.SetActive(active && radar);
        if (sectorSignalVisualizer != null) sectorSignalVisualizer.gameObject.SetActive(active);
        cameraBlocker.SetActive(active && !firstPerson);
        scoreVisualizer.SetActive(active);

        if (!active && firstPersonView != null) firstPersonView.Deactivate();
    }
}
