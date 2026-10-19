using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visualizes 2D lidar data. Two modes:
///   Lines — rays from center, length = distance, color = semantic class.
///   Pointcloud — dots at hit positions + grey arc showing max range.
/// </summary>
public class Lidar2DVisualizer : MonoBehaviour
{
    public enum DisplayMode { Lines, Pointcloud }

    [Header("Mode")]
    public DisplayMode displayMode = DisplayMode.Lines;

    [Header("Layout")]
    public float displayRadius = 100f;

    [Header("Appearance")]
    public Color noHitColor = new Color(0.3f, 0.3f, 0.3f, 0.5f);
    public Color arcColor = new Color(0.3f, 0.3f, 0.3f, 0.25f);
    public float rayWidth = 2f;
    public float pointSize = 5f;
    public float arcDotSize = 2f;

    private static readonly Color[] SemanticColors = new Color[]
    {
        new Color(1.0f, 0.2f, 0.2f, 1f), // 0 red
        new Color(0.2f, 0.8f, 0.2f, 1f), // 1 green
        new Color(0.2f, 0.4f, 1.0f, 1f), // 2 blue
        new Color(1.0f, 0.8f, 0.0f, 1f), // 3 yellow
        new Color(0.8f, 0.2f, 0.8f, 1f), // 4 magenta
        new Color(0.0f, 0.8f, 0.8f, 1f), // 5 cyan
        new Color(1.0f, 0.5f, 0.0f, 1f), // 6 orange
        new Color(0.6f, 0.3f, 0.1f, 1f), // 7 brown
        new Color(0.5f, 1.0f, 0.5f, 1f), // 8 light green
        new Color(0.7f, 0.7f, 1.0f, 1f), // 9 light blue
    };

    private RectTransform[] elementTransforms;
    private Image[] elementImages;
    private RectTransform[] arcTransforms;
    private SemanticLidarSensor sensor;
    private int numRays;
    private float maxRange;
    private int descriptorDimension;
    private DisplayMode builtMode;
    private bool initialized = false;

    public void Initialize(SemanticLidarSensor sensorRef)
    {
        sensor = sensorRef;
        maxRange = sensor.maxRange;
        numRays = sensor.numRays;
        descriptorDimension = (int)SemanticLidarSensor.descriptorDimension;

        Rebuild();
    }

    void Rebuild()
    {
        // Destroy all children
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        builtMode = displayMode;

        if (displayMode == DisplayMode.Lines)
            BuildLines();
        else
            BuildPointcloud();

        initialized = true;
    }

    void BuildLines()
    {
        arcTransforms = null;
        elementTransforms = new RectTransform[numRays];
        elementImages = new Image[numRays];

        for (int i = 0; i < numRays; i++)
        {
            var rt = CreateElement($"LidarRay_{i}", noHitColor, new Vector2(rayWidth, 0f));
            rt.anchoredPosition = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);

            float angleDeg = sensor.angleStartDeg + i * sensor.angleIncrementDeg;
            rt.localRotation = Quaternion.Euler(0f, 0f, -angleDeg);

            elementTransforms[i] = rt;
            elementImages[i] = rt.GetComponent<Image>();
        }
    }

    void BuildPointcloud()
    {
        elementTransforms = new RectTransform[numRays];
        elementImages = new Image[numRays];

        // Arc dots showing max range boundary
        arcTransforms = new RectTransform[numRays];
        for (int i = 0; i < numRays; i++)
        {
            float angleDeg = sensor.angleStartDeg + i * sensor.angleIncrementDeg;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            // UI coords: up = 0 deg, CW positive in lidar convention
            Vector2 pos = new Vector2(Mathf.Sin(angleRad), Mathf.Cos(angleRad)) * displayRadius;

            var arcRt = CreateElement($"Arc_{i}", arcColor, new Vector2(arcDotSize, arcDotSize));
            arcRt.anchoredPosition = pos;
            arcTransforms[i] = arcRt;
        }

        // Hit point dots
        for (int i = 0; i < numRays; i++)
        {
            var rt = CreateElement($"Point_{i}", noHitColor, new Vector2(pointSize, pointSize));
            rt.anchoredPosition = Vector2.zero;

            elementTransforms[i] = rt;
            elementImages[i] = rt.GetComponent<Image>();
        }
    }

    RectTransform CreateElement(string name, Color color, Vector2 size)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        var img = obj.AddComponent<Image>();
        img.color = color;
        var rt = obj.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        rt.pivot = new Vector2(0.5f, 0.5f);
        return rt;
    }

    void Update()
    {
        if (!initialized || sensor == null || sensor.lastRanges == null) return;

        // Rebuild if mode changed in editor
        if (displayMode != builtMode)
            Rebuild();

        if (displayMode == DisplayMode.Lines)
            UpdateLines();
        else
            UpdatePointcloud();
    }

    void UpdateLines()
    {
        for (int i = 0; i < numRays && i < sensor.lastRanges.Length; i++)
        {
            float range = sensor.lastRanges[i];

            if (range < 0f)
            {
                elementTransforms[i].sizeDelta = new Vector2(rayWidth, displayRadius);
                elementImages[i].color = noHitColor;
            }
            else
            {
                float length = (range / maxRange) * displayRadius;
                elementTransforms[i].sizeDelta = new Vector2(rayWidth, length);
                elementImages[i].color = GetSemanticColor(i);
            }
        }
    }

    void UpdatePointcloud()
    {
        for (int i = 0; i < numRays && i < sensor.lastRanges.Length; i++)
        {
            float range = sensor.lastRanges[i];
            float angleDeg = sensor.angleStartDeg + i * sensor.angleIncrementDeg;
            float angleRad = angleDeg * Mathf.Deg2Rad;
            Vector2 dir = new Vector2(Mathf.Sin(angleRad), Mathf.Cos(angleRad));

            if (range < 0f)
            {
                // No hit — hide point
                elementImages[i].color = Color.clear;
            }
            else
            {
                float dist = (range / maxRange) * displayRadius;
                elementTransforms[i].anchoredPosition = dir * dist;
                elementImages[i].color = GetSemanticColor(i);
            }
        }
    }

    Color GetSemanticColor(int rayIndex)
    {
        int dominantClass = GetDominantClass(sensor.lastDescriptors, rayIndex);
        return dominantClass >= 0
            ? SemanticColors[dominantClass % SemanticColors.Length]
            : Color.white;
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
