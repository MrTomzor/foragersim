using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Visualizes a compass heading as a rotating needle.
/// Reads heading directly from the CompassSensor component each frame.
/// </summary>
public class CompassVisualizer : MonoBehaviour
{
    [Header("Layout")]
    public float needleLength = 60f;
    public float needleWidth = 4f;
    public float circleRadius = 50f;

    [Header("Appearance")]
    public Color needleColor = Color.red;
    public Color circleColor = new Color(1f, 1f, 1f, 0.3f);
    public Color tickColor = new Color(1f, 1f, 1f, 0.5f);

    private RectTransform needleTransform;
    private CompassSensor sensor;
    private bool initialized = false;

    public void Initialize(CompassSensor sensorRef)
    {
        sensor = sensorRef;

        // Clean up old children if reinitializing
        for (int i = transform.childCount - 1; i >= 0; i--)
            Destroy(transform.GetChild(i).gameObject);

        // 12 small reference ticks around the circle
        for (int i = 0; i < 12; i++)
        {
            float angle = i * 30f;
            bool cardinal = (i % 3 == 0);
            var tick = CreateSprite($"Tick_{i}",
                cardinal ? tickColor : circleColor,
                new Vector2(cardinal ? 2f : 1f, cardinal ? 10f : 5f));
            tick.pivot = new Vector2(0.5f, 0.5f);
            Vector2 dir = RotateVector(Vector2.up, -angle);
            tick.anchoredPosition = dir * circleRadius;
            tick.localRotation = Quaternion.Euler(0f, 0f, -angle);
        }

        // Needle
        var needleObj = new GameObject("Needle");
        needleObj.transform.SetParent(transform, false);
        var img = needleObj.AddComponent<Image>();
        img.color = needleColor;
        needleTransform = needleObj.GetComponent<RectTransform>();
        needleTransform.pivot = new Vector2(0.5f, 0f);
        needleTransform.sizeDelta = new Vector2(needleWidth, needleLength);
        needleTransform.anchoredPosition = Vector2.zero;

        initialized = true;
    }

    void Update()
    {
        if (!initialized || sensor == null) return;

        // lastYawRad: ROS frame (0 = forward, CCW positive)
        // Unity UI Z rotation: CCW positive, but the ROS yaw is already negated
        // from Unity's eulerY, so negate again to match visual heading
        float angleDeg = -sensor.lastYawRad * Mathf.Rad2Deg;
        needleTransform.localRotation = Quaternion.Euler(0f, 0f, angleDeg);
    }

    RectTransform CreateSprite(string name, Color color, Vector2 size)
    {
        var obj = new GameObject(name);
        obj.transform.SetParent(transform, false);
        var img = obj.AddComponent<Image>();
        img.color = color;
        var rt = obj.GetComponent<RectTransform>();
        rt.sizeDelta = size;
        return rt;
    }

    Vector2 RotateVector(Vector2 v, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
    }

    void OnDisable()
    {
        initialized = false;
    }
}
