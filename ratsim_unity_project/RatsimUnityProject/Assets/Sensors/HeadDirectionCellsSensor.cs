using UnityEngine;

public class HeadDirectionCellsSensor : MonoBehaviour
{
    public string topicName = "/head_direction_cells";

    public int numCells = 12;
    public float spreadMod = 0.5f;

    /// <summary>Latest cell activations [0..1]. Read by visualizer.</summary>
    [HideInInspector] public float[] lastActivations;

    RoslikeTCPServer conn;
    float[] cellCenterAngles;

    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();

        lastActivations = new float[numCells];
        cellCenterAngles = new float[numCells];

        float step = 2f * Mathf.PI / numCells;
        for (int i = 0; i < numCells; i++)
        {
            cellCenterAngles[i] = -Mathf.PI + i * step;
        }

        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        float eulerYDeg = transform.rotation.eulerAngles.y;
        float yawRad = -eulerYDeg * Mathf.Deg2Rad;
        while (yawRad > Mathf.PI) yawRad -= 2f * Mathf.PI;
        while (yawRad < -Mathf.PI) yawRad += 2f * Mathf.PI;

        float denom = spreadMod * Mathf.PI;

        for (int i = 0; i < numCells; i++)
        {
            float diff = AngleDiff(yawRad, cellCenterAngles[i]);
            float x = diff / denom;
            lastActivations[i] = Mathf.Exp(-(x * x));
        }

        var msg = new FloatArrayMessage();
        msg.data = (float[])lastActivations.Clone();
        conn.Publish(topicName, msg);
    }

    static float AngleDiff(float a, float b)
    {
        float d = a - b;
        while (d > Mathf.PI) d -= 2f * Mathf.PI;
        while (d < -Mathf.PI) d += 2f * Mathf.PI;
        return d;
    }
}
