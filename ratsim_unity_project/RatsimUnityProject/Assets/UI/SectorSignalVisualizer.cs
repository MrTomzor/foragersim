using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visualizes a <see cref="SectorSignalSensor"/> as one radial ring of wedge-shaped
/// segments per channel, offset inward from the outer edge. Each segment's alpha and
/// length encode its value in [0,1]. Reads sensor.lastValues each frame.
///
/// Sector 0 is drawn at the top (agent forward = up in screen space).
/// </summary>
public class SectorSignalVisualizer : MonoBehaviour
{
    [Header("Layout")]
    public float outerRadius = 80f;
    public float innerRadius = 25f;
    public float ringSpacing = 4f;  // gap between channel rings

    [Header("Appearance")]
    public Color lowColor = new Color(0.2f, 0.4f, 0.8f, 0.1f);
    public Color[] highColors = new Color[] {
        new Color(1.0f, 0.3f, 0.3f, 1f),
        new Color(0.3f, 1.0f, 0.3f, 1f),
        new Color(0.3f, 0.6f, 1.0f, 1f),
        new Color(1.0f, 0.9f, 0.2f, 1f),
    };

    private SectorSignalSensor sensor;
    private int numChannels;
    private int numSectors;
    // [channel][sector] -> UI elements
    private RectTransform[][] cellTransforms;
    private Image[][] cellImages;
    private bool initialized = false;

    public void Initialize(SectorSignalSensor sensorRef)
    {
        sensor = sensorRef;
        numChannels = sensor.channelNames != null ? sensor.channelNames.Length : 0;
        numSectors = sensor.nSectors;

        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        if (numChannels == 0 || numSectors == 0) { initialized = false; return; }

        cellTransforms = new RectTransform[numChannels][];
        cellImages = new Image[numChannels][];

        float totalRingSpan = outerRadius - innerRadius;
        float ringThickness = Mathf.Max(2f, (totalRingSpan - (numChannels - 1) * ringSpacing) / numChannels);
        float degPerSector = 360f / numSectors;

        for (int c = 0; c < numChannels; c++)
        {
            cellTransforms[c] = new RectTransform[numSectors];
            cellImages[c] = new Image[numSectors];

            float rInner = innerRadius + c * (ringThickness + ringSpacing);
            float rCentre = rInner + ringThickness * 0.5f;
            float wedgeWidth = 2f * Mathf.PI * rCentre / numSectors * 0.9f;

            for (int k = 0; k < numSectors; k++)
            {
                var obj = new GameObject($"sig_{c}_{k}");
                obj.transform.SetParent(transform, false);
                var img = obj.AddComponent<Image>();
                img.color = lowColor;
                var rt = obj.GetComponent<RectTransform>();
                // Pivot at bottom so sizeDelta.y extends outward from pivot along local +Y.
                rt.pivot = new Vector2(0.5f, 0f);
                // Sector 0 is forward (screen up). Z rotation = -degPerSector * k (CW from up).
                float angleDeg = -k * degPerSector;
                rt.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
                // Place anchor at centre; offset pivot outward by rInner along the rotated +Y.
                float rad = k * degPerSector * Mathf.Deg2Rad;
                // In local (unrotated) frame pivot sits at (0, rInner) then rotated CW by angleDeg.
                // Equivalent world-space offset for an unparent-rotated container:
                float dx = rInner * Mathf.Sin(rad);
                float dy = rInner * Mathf.Cos(rad);
                rt.anchoredPosition = new Vector2(dx, dy);
                rt.sizeDelta = new Vector2(wedgeWidth, ringThickness);

                cellTransforms[c][k] = rt;
                cellImages[c][k] = img;
            }
        }

        initialized = true;
    }

    void Update()
    {
        if (!initialized || sensor == null || sensor.lastValues == null) return;

        for (int c = 0; c < numChannels && c < sensor.lastValues.Length; c++)
        {
            float[] buf = sensor.lastValues[c];
            Color high = highColors[c % highColors.Length];
            for (int k = 0; k < numSectors && k < buf.Length; k++)
            {
                float v = Mathf.Clamp01(buf[k]);
                cellImages[c][k].color = Color.Lerp(lowColor, high, v);
            }
        }
    }

    void OnDisable() { initialized = false; }
}
