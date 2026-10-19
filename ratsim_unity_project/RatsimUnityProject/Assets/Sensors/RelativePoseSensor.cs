using UnityEngine;

public class RelativePoseSensor : MonoBehaviour
{
    public string topic;

    public bool considerOriginRotation = false;

    RoslikeTCPServer conn;

    public Vector3 originPos;
    public Quaternion originRot;

    public void ResetOrigin()
    {
        originPos = transform.position;
        originRot = transform.rotation;
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        var msg = new PoseMessage();

        if(considerOriginRotation)
        {
            Vector3 deltaVecInOrigFrame = Quaternion.Inverse(originRot) * (transform.position - originPos);
            CoordConversion.UnityToRos(deltaVecInOrigFrame, out float rx1, out float ry1, out float rz1);
            msg.x = rx1; msg.y = ry1; msg.z = rz1;

            Quaternion relativeRot = Quaternion.Inverse(originRot) * transform.rotation;
            CoordConversion.UnityRotToRosQuat(relativeRot.eulerAngles.y, out float qx1, out float qy1, out float qz1, out float qw1);
            msg.qx = qx1; msg.qy = qy1; msg.qz = qz1; msg.qw = qw1;
        }
        else
        {
            Vector3 delta = transform.position - originPos;
            CoordConversion.UnityToRos(delta, out float rx2, out float ry2, out float rz2);
            msg.x = rx2; msg.y = ry2; msg.z = rz2;
            CoordConversion.UnityRotToRosQuat(transform.rotation.eulerAngles.y, out float qx2, out float qy2, out float qz2, out float qw2);
            msg.qx = qx2; msg.qy = qy2; msg.qz = qz2; msg.qw = qw2;
        }

        conn.Publish(topic, msg);
    }
}
