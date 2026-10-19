using UnityEngine;

public class Message
{

}

public class StepRequestMessage : Message
{
    public bool physicsEnabled { get; set; }
}

public class StepFinishedMessage : Message
{
    public bool success { get; set; }
}

public class StringMessage : Message
{
    public string data { get; set; }
}

public class Int32Message : Message
{
    public int data { get; set; }
}

public class Float32Message : Message
{
    public float data { get; set; }
}

public class BoolMessage : Message
{
    public bool data { get; set; }
}

public class FloatArrayMessage : Message
{
    public float[] data { get; set; }
}

public class Lidar2DMessage : Message
{
    public float[] ranges { get; set; }
    public float[] descriptors { get; set; }
    public int angleIncrementDeg { get; set; }
    public int angleStartDeg { get; set; }
    public float maxRange { get; set; }
}

public class VisualPointTrackerMessage : Message
{
    public float[] trackedPointsEgocentricFLU { get; set; }
    public float[] trackedPointDescriptors { get; set; }
    public float scaleFactor { get; set; }
}

public class PoseMessage : Message
{
    public float x { get; set; }
    public float y { get; set; }
    public float z { get; set; }
    public float qx { get; set; }
    public float qy { get; set; }
    public float qz { get; set; }
    public float qw { get; set; }
}

public class TwistMessage : Message
{
    public float linear_x { get; set; }
    public float linear_y { get; set; }
    public float linear_z { get; set; }
    public float angular_x { get; set; }
    public float angular_y { get; set; }
    public float angular_z { get; set; }
}

public class RGBDMessage : Message
{
    public string rgbImageBase64 { get; set; }
    public string depthImageBase64 { get; set; }
    public float minDepth { get; set; }
    public float maxDepth { get; set; }
}

public class CameraIntrinsicsMessage : Message
{
    public int imageWidth { get; set; }
    public int imageHeight { get; set; }
    
    public float fx { get; set; }  // focal length in pixels (x-axis)
    public float fy { get; set; }  // focal length in pixels (y-axis)
    public float cx { get; set; }  // principal point x
    public float cy { get; set; }  // principal point y

    public float nearClip { get; set; }
    public float farClip { get; set; }
    public float verticalFOV { get; set; }
}

/// <summary>
/// Status report from world generation (errors, warnings, retry events).
/// Severity is one of: "info", "warning", "error".
/// Source identifies the originating subsystem (e.g. "WorldLayoutLoader", "RewardObjectLoader").
/// </summary>
public class WorldGenStatusMessage : Message
{
    public string severity { get; set; }
    public string source { get; set; }
    public string message { get; set; }
}

public class MapGenTemplate2D : Message
{
    // Dimensions of the masks
    public int width{ get; set; }
    public int height{ get; set; }

    // Meters per pixel scale
    public float meters_per_pixel{ get; set; }  

    // Flattened binary masks: 1 = true, 0 = false
    public int[] obstacles{ get; set; }
    public int[] spawnMask{ get; set; }
    public int[] poiMask{ get; set; }
    public int[] forbiddenMask{ get; set; }
    public int[] growableMask{ get; set; }
}