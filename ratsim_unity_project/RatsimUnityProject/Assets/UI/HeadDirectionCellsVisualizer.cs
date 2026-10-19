using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visualizes head direction cell activations as radial sprite lines.
/// Reads data directly from the HeadDirectionCellsSensor component each frame.
/// </summary>
public class HeadDirectionCellsVisualizer : MonoBehaviour
{
    [Header("Layout")]
    public float maxLineLength = 60f;
    public float minLineLength = 5f;
    public float lineWidth = 4f;

    [Header("Appearance")]
    public Color lowActivationColor = new Color(0.2f, 0.2f, 0.5f, 0.5f);
    public Color highActivationColor = new Color(1.0f, 0.2f, 0.2f, 1.0f);

    private RectTransform[] cellTransforms;
    private Image[] cellImages;
    private HeadDirectionCellsSensor sensor;
    private int numCells;
    private bool initialized = false;

    public void Initialize(HeadDirectionCellsSensor sensorRef)
    {
        sensor = sensorRef;
        numCells = sensor.numCells;

        // Clean up old children if reinitializing
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        cellTransforms = new RectTransform[numCells];
        cellImages = new Image[numCells];

        float step = 360f / numCells;

        for (int i = 0; i < numCells; i++)
        {
            var obj = new GameObject($"HDC_{i}");
            obj.transform.SetParent(transform, false);

            var img = obj.AddComponent<Image>();
            img.color = lowActivationColor;

            var rt = obj.GetComponent<RectTransform>();
            rt.anchoredPosition = Vector2.zero;
            rt.pivot = new Vector2(0.5f, 0f);
            rt.sizeDelta = new Vector2(lineWidth, minLineLength);

            // Cell center angles go from -pi to pi, step = 2pi/N
            // Map to UI: -180 + i * step degrees, negate for Z rotation
            float angleDeg = -180f + i * step;
            rt.localRotation = Quaternion.Euler(0f, 0f, -angleDeg);

            cellTransforms[i] = rt;
            cellImages[i] = img;
        }

        initialized = true;
    }

    void Update()
    {
        if (!initialized || sensor == null || sensor.lastActivations == null) return;

        for (int i = 0; i < numCells && i < sensor.lastActivations.Length; i++)
        {
            float activation = Mathf.Clamp01(sensor.lastActivations[i]);

            float length = Mathf.Lerp(minLineLength, maxLineLength, activation);
            cellTransforms[i].sizeDelta = new Vector2(lineWidth, length);

            cellImages[i].color = Color.Lerp(lowActivationColor, highActivationColor, activation);
        }
    }

    void OnDisable()
    {
        initialized = false;
    }
}
