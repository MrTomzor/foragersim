using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// First-person style depth readout for the 2D lidar — the human control UI "V1".
///
/// The whole ray array is squashed into one horizontal strip of square cells spanning the
/// screen, one cell per ray. Cell order matches ray order, and since ray i points along
/// local (sin θ, 0, cos θ) with θ = angleStartDeg + i·angleIncrementDeg, increasing index
/// sweeps left → right in front of the agent. So cell 0 sits on the left of the screen and
/// reads as "what is to my left", with no index flip.
///
/// Brightness encodes distance: black = no hit / out of range, full brightness = touching
/// the sensor. Hue encodes the semantic class, but only for classes explicitly listed in
/// <see cref="semanticTints"/> — every other class (walls, boundaries, unknown) stays on the
/// neutral grey ramp, so structure doesn't compete visually with rewards.
///
/// The strip anchors itself across the full width of its parent, offset <see cref="topMargin"/>
/// from the top, and sizes its cells from the parent width. It rebuilds when the ray count
/// or the parent width changes.
/// </summary>
public class DepthStripVisualizer : MonoBehaviour
{
    /// <summary>Explicit hue for one semantic class, matched by the name in the active SemanticSet.</summary>
    [System.Serializable]
    public class SemanticTint
    {
        public string semanticName;
        public Color color = Color.white;
    }

    [Header("Layout")]
    [Tooltip("Distance from the top of the parent rect to the top of the strip, in pixels.")]
    public float topMargin = 50f;
    [Tooltip("Cell height as a multiple of cell width. 1 = square, 2 = twice as tall as wide, " +
             "0.5 = half. Live-editable in play mode.")]
    [Min(0.05f)] public float heightScale = 1f;
    [Tooltip("Clamp on cell height in pixels, applied after heightScale. 0 = uncapped.")]
    public float maxCellHeight = 0f;
    [Tooltip("Gap between cells, in pixels. The frame colour shows through the gaps.")]
    public float cellGap = 3f;
    [Tooltip("Frame border drawn around the whole strip, in pixels.")]
    public float framePadding = 4f;

    [Header("Appearance")]
    [Tooltip("Frame / gap colour. Deliberately lighter than pure black so out-of-range cells stay readable.")]
    public Color frameColor = new Color(0.13f, 0.13f, 0.15f, 1f);
    [Tooltip("Colour of a ray that hit nothing within maxRange.")]
    public Color noHitColor = Color.black;
    [Tooltip("Ramp colour for semantic classes with no explicit tint.")]
    public Color neutralColor = Color.white;
    [Tooltip("Brightness of a hit at exactly maxRange. Keeps far hits distinguishable from no-hit.")]
    [Range(0f, 1f)] public float minBrightness = 0.12f;
    [Tooltip("Gamma on the distance ramp. >1 emphasises near objects, <1 lifts far ones.")]
    [Range(0.2f, 3f)] public float brightnessGamma = 1f;

    [Header("Forward Marker")]
    [Tooltip("Small tick above the cell containing heading 0, so the human can find 'straight ahead'.")]
    public bool showForwardMarker = true;
    public Color forwardMarkerColor = new Color(1f, 1f, 1f, 0.55f);
    public float forwardMarkerSize = 10f;

    [Header("Semantics")]
    [Tooltip("Classes listed here get a hue; the brightness ramp still encodes distance. " +
             "Anything not listed renders on the neutral grey ramp.")]
    public SemanticTint[] semanticTints = new SemanticTint[]
    {
        new SemanticTint { semanticName = "reward_pickup", color = new Color(0.25f, 1.00f, 0.35f, 1f) },
        new SemanticTint { semanticName = "well",          color = new Color(0.25f, 0.85f, 1.00f, 1f) },
        new SemanticTint { semanticName = "smoke",         color = new Color(0.80f, 0.55f, 1.00f, 1f) },
    };

    private SemanticLidarSensor sensor;
    private RectTransform selfRect;
    private RectTransform[] cellTransforms;
    private Image[] cellImages;
    private RectTransform frameTransform;
    private RectTransform forwardMarkerTransform;

    // Per-class tint resolved from the sensor's active SemanticSet. null entry = neutral.
    private Color[] classColors;

    private int numRays;
    private float maxRange;
    private int descriptorDimension;
    private bool initialized = false;

    // Snapshot of the geometry the last Rebuild() used, so Inspector tweaks (and window
    // resizes) apply live instead of only on the next episode.
    private float builtWidth = -1f;
    private float builtHeightScale = float.NaN;
    private float builtMaxCellHeight = float.NaN;
    private float builtCellGap = float.NaN;
    private float builtFramePadding = float.NaN;
    private float builtTopMargin = float.NaN;
    private bool builtForwardMarker;

    public void Initialize(SemanticLidarSensor sensorRef)
    {
        sensor = sensorRef;
        maxRange = sensor.maxRange;
        numRays = sensor.numRays;
        descriptorDimension = (int)SemanticLidarSensor.descriptorDimension;

        ResolveClassColors();
        Rebuild();
    }

    /// <summary>Maps the tint table onto descriptor indices using the sensor's name→index table.</summary>
    void ResolveClassColors()
    {
        classColors = new Color[Mathf.Max(descriptorDimension, 1)];
        for (int i = 0; i < classColors.Length; i++)
            classColors[i] = neutralColor;

        var nameToIndex = SemanticLidarSensor.semanticNamesToIndices;
        if (nameToIndex == null || semanticTints == null) return;

        foreach (var tint in semanticTints)
        {
            if (tint == null || string.IsNullOrEmpty(tint.semanticName)) continue;
            if (!nameToIndex.ContainsKey(tint.semanticName)) continue;

            int idx = (int)nameToIndex[tint.semanticName];
            if (idx >= 0 && idx < classColors.Length)
                classColors[idx] = tint.color;
        }
    }

    void Rebuild()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        cellTransforms = null;
        cellImages = null;
        frameTransform = null;
        forwardMarkerTransform = null;
        initialized = false;

        selfRect = GetComponent<RectTransform>();
        if (selfRect == null || numRays <= 0) return;

        float parentWidth = GetParentWidth();
        if (parentWidth <= 0f) return;

        float cellWidth = parentWidth / numRays;
        float cellHeight = cellWidth * heightScale;
        if (maxCellHeight > 0f) cellHeight = Mathf.Min(cellHeight, maxCellHeight);

        // Full-width strip pinned below the top margin.
        selfRect.anchorMin = new Vector2(0f, 1f);
        selfRect.anchorMax = new Vector2(1f, 1f);
        selfRect.pivot = new Vector2(0.5f, 1f);
        selfRect.anchoredPosition = new Vector2(0f, -topMargin);
        selfRect.sizeDelta = new Vector2(0f, cellHeight);

        // Frame behind the cells — the gaps and the border show through it, which is what
        // keeps a pure-black (out of range) cell visible against the black camera blocker.
        frameTransform = CreateChild("Frame", frameColor);
        frameTransform.anchorMin = Vector2.zero;
        frameTransform.anchorMax = Vector2.one;
        frameTransform.pivot = new Vector2(0.5f, 0.5f);
        frameTransform.anchoredPosition = Vector2.zero;
        frameTransform.sizeDelta = new Vector2(framePadding * 2f, framePadding * 2f);

        cellTransforms = new RectTransform[numRays];
        cellImages = new Image[numRays];

        for (int i = 0; i < numRays; i++)
        {
            var rt = CreateChild($"Depth_{i}", noHitColor);
            rt.anchorMin = new Vector2(0f, 0.5f);
            rt.anchorMax = new Vector2(0f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(cellWidth * (i + 0.5f), 0f);
            rt.sizeDelta = new Vector2(Mathf.Max(1f, cellWidth - cellGap),
                                       Mathf.Max(1f, cellHeight - cellGap));

            cellTransforms[i] = rt;
            cellImages[i] = rt.GetComponent<Image>();
        }

        if (showForwardMarker)
        {
            float forwardIndex = GetForwardCellIndex();
            if (forwardIndex >= 0f)
            {
                forwardMarkerTransform = CreateChild("ForwardMarker", forwardMarkerColor);
                forwardMarkerTransform.anchorMin = new Vector2(0f, 0.5f);
                forwardMarkerTransform.anchorMax = new Vector2(0f, 0.5f);
                forwardMarkerTransform.pivot = new Vector2(0.5f, 0f);
                forwardMarkerTransform.anchoredPosition = new Vector2(
                    cellWidth * (forwardIndex + 0.5f),
                    cellHeight * 0.5f + framePadding + 3f);
                forwardMarkerTransform.sizeDelta =
                    new Vector2(forwardMarkerSize, forwardMarkerSize * 0.5f);
            }
        }

        builtWidth = parentWidth;
        builtHeightScale = heightScale;
        builtMaxCellHeight = maxCellHeight;
        builtCellGap = cellGap;
        builtFramePadding = framePadding;
        builtTopMargin = topMargin;
        builtForwardMarker = showForwardMarker;
        initialized = true;
    }

    /// <summary>True when a geometry field changed since the last Rebuild().</summary>
    bool LayoutChanged()
    {
        return !Mathf.Approximately(GetParentWidth(), builtWidth)
            || !Mathf.Approximately(heightScale, builtHeightScale)
            || !Mathf.Approximately(maxCellHeight, builtMaxCellHeight)
            || !Mathf.Approximately(cellGap, builtCellGap)
            || !Mathf.Approximately(framePadding, builtFramePadding)
            || !Mathf.Approximately(topMargin, builtTopMargin)
            || showForwardMarker != builtForwardMarker;
    }

    /// <summary>Fractional cell index of heading 0, or -1 when forward is outside the FOV.</summary>
    float GetForwardCellIndex()
    {
        if (sensor == null || sensor.angleIncrementDeg == 0) return -1f;
        float idx = -(float)sensor.angleStartDeg / sensor.angleIncrementDeg;
        return (idx < 0f || idx > numRays - 1) ? -1f : idx;
    }

    float GetParentWidth()
    {
        var parentRect = transform.parent as RectTransform;
        return parentRect != null ? parentRect.rect.width : 0f;
    }

    RectTransform CreateChild(string name, Color color)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        var img = obj.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return obj.GetComponent<RectTransform>();
    }

    void Update()
    {
        if (sensor == null) return;

        // Ray count changes on episode reload (new agent config); geometry changes on window
        // resize or when a layout field is edited in the Inspector.
        if (sensor.numRays != numRays || LayoutChanged())
        {
            numRays = sensor.numRays;
            maxRange = sensor.maxRange;
            descriptorDimension = (int)SemanticLidarSensor.descriptorDimension;
            ResolveClassColors();
            Rebuild();
        }

        if (!initialized || sensor.lastRanges == null) return;

        for (int i = 0; i < numRays && i < sensor.lastRanges.Length; i++)
        {
            float range = sensor.lastRanges[i];

            if (range < 0f)
            {
                cellImages[i].color = noHitColor;
                continue;
            }

            float t = maxRange > 0f ? Mathf.Clamp01(1f - range / maxRange) : 1f;
            if (!Mathf.Approximately(brightnessGamma, 1f))
                t = Mathf.Pow(t, brightnessGamma);

            float brightness = Mathf.Lerp(minBrightness, 1f, t);
            Color tint = GetClassColor(i);
            cellImages[i].color = new Color(tint.r * brightness,
                                            tint.g * brightness,
                                            tint.b * brightness, 1f);
        }
    }

    Color GetClassColor(int rayIndex)
    {
        int dominant = GetDominantClass(sensor.lastDescriptors, rayIndex);
        if (classColors == null || dominant < 0 || dominant >= classColors.Length)
            return neutralColor;
        return classColors[dominant];
    }

    int GetDominantClass(float[] descriptors, int rayIndex)
    {
        if (descriptors == null || descriptorDimension <= 0) return -1;

        int offset = rayIndex * descriptorDimension;
        if (offset + descriptorDimension > descriptors.Length) return -1;

        int bestIdx = -1;
        float bestVal = 0f;
        for (int j = 0; j < descriptorDimension; j++)
        {
            float v = descriptors[offset + j];
            if (v > bestVal)
            {
                bestVal = v;
                bestIdx = j;
            }
        }
        return bestIdx;
    }

    void OnDisable()
    {
        initialized = false;
    }
}
