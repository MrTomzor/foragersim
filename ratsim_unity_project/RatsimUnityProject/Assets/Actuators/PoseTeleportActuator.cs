using System;
using UnityEngine;

public class PoseTeleportActuator : MonoBehaviour
{
    public string topic;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        RoslikeTCPServer.GetInstance().Subscribe<PoseMessage>(topic, Teleport);
    }

    // Update is called once per frame
    void Update()
    {

    }

    void Teleport(PoseMessage msg)
    {
        transform.position = CoordConversion.RosToUnity(msg.x, msg.y, msg.z);
        float eulerY = CoordConversion.RosQuatToUnityEulerY(msg.qx, msg.qy, msg.qz, msg.qw);
        transform.rotation = Quaternion.Euler(0, eulerY, 0);
    }
}
