using UnityEngine;

public class Odom2DSensor : MonoBehaviour
{
    public string topic;

    RoslikeTCPServer conn;
    Vector3 lastPos;
    Quaternion lastRot;
    public bool verbose = false;

    public float calculatedDT =0;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);

        lastPos = transform.position;
        lastRot = transform.rotation;
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        Quaternion deltaRot = Quaternion.Inverse(lastRot) * transform.rotation;

        Vector3 deltaVecAbsolute = transform.position - lastPos;
        Vector3 deltaVecInPrevFrame = Quaternion.Inverse(lastRot) * (transform.position - lastPos);

        lastPos = transform.position;
        lastRot = transform.rotation;

        var msg = new PoseMessage();
        CoordConversion.UnityToRos(deltaVecInPrevFrame, out float rx, out float ry, out float rz);
        msg.x = rx; msg.y = ry; msg.z = rz;
        CoordConversion.UnityRotToRosQuat(deltaRot.eulerAngles.y, out float qx, out float qy, out float qz, out float qw);
        msg.qx = qx; msg.qy = qy; msg.qz = qz; msg.qw = qw;
        if (verbose)
        {
            float distTraveled = deltaVecInPrevFrame.magnitude;
            Debug.Log("odom data: x=" + msg.x + ", y=" + msg.y + ", qz=" + msg.qz + ", qw=" + msg.qw);
            Rigidbody rb = GetComponent<Rigidbody>();
            if (rb != null)            {
                Debug.Log("velocity: " + rb.linearVelocity + ", angularVelocity: " + rb.angularVelocity);
                float velocityMagnitude = rb.linearVelocity.magnitude;
                // output (debugging) calculated DT based on velocity and distance:
                if (velocityMagnitude > 0.001f)
                {
                    // debug traveled dist and velocity magnitudes
                    Debug.Log("distTraveled: " + distTraveled + ", velocityMagnitude: " + velocityMagnitude);
                    calculatedDT = distTraveled / velocityMagnitude;
                    float physicsDT = Time.fixedDeltaTime;
                    float unityDT = Time.deltaTime;
                    float serverDT = conn.physicsStepTime;
                    Debug.Log("calculatedDT: " + calculatedDT + " (physicsDT: " + physicsDT + ", unityDT: " + unityDT + ", serverDT: " + serverDT + ")");
                }
            }
        }
        conn.Publish(topic, msg);
    }
}
