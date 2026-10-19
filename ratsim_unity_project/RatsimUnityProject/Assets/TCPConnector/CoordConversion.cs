using UnityEngine;

public static class CoordConversion
{
    // Unity (x=right, y=up, z=forward) <-> ROS (x=forward, y=left, z=up)
    // ros.x = unity.z, ros.y = -unity.x, ros.z = unity.y

    public static void UnityToRos(Vector3 unityPos, out float rosX, out float rosY, out float rosZ)
    {
        rosX = unityPos.z;
        rosY = -unityPos.x;
        rosZ = unityPos.y;
    }

    public static Vector3 RosToUnity(float rosX, float rosY, float rosZ)
    {
        return new Vector3(-rosY, rosZ, rosX);
    }

    public static void UnityRotToRosQuat(float eulerYDeg, out float qx, out float qy, out float qz, out float qw)
    {
        float yaw = -eulerYDeg * Mathf.Deg2Rad;
        qx = 0f;
        qy = 0f;
        qz = Mathf.Sin(yaw / 2f);
        qw = Mathf.Cos(yaw / 2f);
    }

    public static float RosQuatToUnityEulerY(float qx, float qy, float qz, float qw)
    {
        float yaw = 2f * Mathf.Atan2(qz, qw);
        return -yaw * Mathf.Rad2Deg;
    }
}
