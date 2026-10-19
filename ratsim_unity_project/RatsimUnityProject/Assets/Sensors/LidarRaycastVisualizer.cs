using UnityEngine;

/// <summary>
/// Runtime visualization of lidar raycasts using GL lines.
/// Works in builds (unlike Debug.DrawLine/Gizmos). Toggle with G key.
/// Attach to the same GameObject as SemanticLidarSensor.
/// </summary>
[RequireComponent(typeof(SemanticLidarSensor))]
public class LidarRaycastVisualizer : MonoBehaviour
{
    public bool showRays = true;
    public Color hitColor = Color.red;
    public Color missColor = new Color(1f, 0.3f, 0.3f, 0.3f);
    public KeyCode toggleKey = KeyCode.G;

    SemanticLidarSensor sensor;
    Material lineMaterial;

    void Start()
    {
        sensor = GetComponent<SemanticLidarSensor>();
        CreateLineMaterial();
    }

    void Update()
    {
        if (Input.GetKeyDown(toggleKey))
            showRays = !showRays;
    }

    void CreateLineMaterial()
    {
        // Unity's built-in shader for colored lines
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        lineMaterial = new Material(shader);
        lineMaterial.hideFlags = HideFlags.HideAndDontSave;
        lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        lineMaterial.SetInt("_ZWrite", 0);
        lineMaterial.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
    }

    void OnRenderObject()
    {
        if (!showRays || sensor == null || sensor.lastRanges == null)
            return;

        lineMaterial.SetPass(0);

        GL.PushMatrix();
        GL.MultMatrix(Matrix4x4.identity);
        GL.Begin(GL.LINES);

        Vector3 origin = transform.position;
        int numRays = sensor.lastRanges.Length;

        for (int i = 0; i < numRays; i++)
        {
            float angle = sensor.angleStartDeg + i * sensor.angleIncrementDeg;
            float radians = angle * Mathf.Deg2Rad;
            Vector3 localDir = new Vector3(Mathf.Sin(radians), 0, Mathf.Cos(radians));
            Vector3 worldDir = transform.TransformDirection(localDir);

            float range = sensor.lastRanges[i];
            if (range >= 0)
            {
                GL.Color(hitColor);
                GL.Vertex(origin);
                GL.Vertex(origin + worldDir * range);
            }
            else
            {
                GL.Color(missColor);
                GL.Vertex(origin);
                GL.Vertex(origin + worldDir * sensor.maxRange);
            }
        }

        GL.End();
        GL.PopMatrix();
    }

    void OnDestroy()
    {
        if (lineMaterial != null)
            DestroyImmediate(lineMaterial);
    }
}
