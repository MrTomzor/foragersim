using UnityEngine;

public class AbsolutePose2DSensor : MonoBehaviour
{
    public string topic;

    RoslikeTCPServer conn;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        var msg = new PoseMessage();
        CoordConversion.UnityToRos(transform.position, out float rx, out float ry, out float rz);
        msg.x = rx; msg.y = ry; msg.z = rz;
        CoordConversion.UnityRotToRosQuat(transform.rotation.eulerAngles.y, out float qx, out float qy, out float qz, out float qw);
        msg.qx = qx; msg.qy = qy; msg.qz = qz; msg.qw = qw;
        conn.Publish(topic, msg);
    }
}
