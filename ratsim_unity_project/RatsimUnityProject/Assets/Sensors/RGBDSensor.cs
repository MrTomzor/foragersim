using UnityEngine;
using System.Collections;
using System.Text;
using System;

public class RGBDSensor : MonoBehaviour
{

    public Camera cam;
    public string rgbdTopic = "/rgbd";
    public string intrinsicsTopic = "/camera_intrinsics";

    public int imageWidth = 640;
    public int imageHeight = 480;
    public float depthImageMaxRange = 100.0f; // Maximum range for depth sensor
    public bool captureDepth = true; // If false, skip depth capture/encode and publish RGB only
    RoslikeTCPServer conn;


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
        //cam = GetComponent<Camera>();
    }

    float[] GetFloatValuesFromTexture(Texture2D tex)
{
    Color[] pixels = tex.GetPixels();
    float[] depthValues = new float[pixels.Length];

    for (int i = 0; i < pixels.Length; i++)
    {
        depthValues[i] = pixels[i].r; // use red channel for depth
    }

    return depthValues;
}


    public void SenseAndPublish(TimerEvent ev)
    {
        // Capture RGB
        Texture2D rgbTex = CaptureCamera(cam);
        string rgbBase64 = Convert.ToBase64String(rgbTex.EncodeToPNG());
        UnityEngine.Object.Destroy(rgbTex);

        string depthBase64 = "";
        float minDepth = 0f;
        float maxDepth = 0f;

        if (captureDepth)
        {
            // Capture Depth
            Texture2D depthTex = CaptureDepthLinear(cam);
            float[] depthValues = GetFloatValuesFromTexture(depthTex);

            // Compute min/max
            minDepth = float.MaxValue;
            maxDepth = float.MinValue;
            if(maxDepth > depthImageMaxRange){
                maxDepth = depthImageMaxRange; // Clamp to max range
            }

            foreach (float d in depthValues)
            {
                if (d > 0.0001f) // avoid zero or garbage values
                {
                    if (d < minDepth) minDepth = d;
                    if (d > maxDepth) maxDepth = d;
                }
            }

            Debug.Log("Min depth: " + minDepth + ", Max depth: " + maxDepth);
            // Create a normalized depth texture
            Texture2D depthTexNormalized = CreateNormalizedDepthPNG(depthValues, minDepth, maxDepth);
            depthBase64 = Convert.ToBase64String(depthTexNormalized.EncodeToPNG());

            UnityEngine.Object.Destroy(depthTex);
            UnityEngine.Object.Destroy(depthTexNormalized);
        }

        // Create and return message. When captureDepth is false, depthImageBase64
        // is empty and min/maxDepth are 0 — consumers should treat this as "no depth".
        var msg = new RGBDMessage
        {
            rgbImageBase64 = rgbBase64,
            minDepth = minDepth,
            maxDepth = maxDepth,
            depthImageBase64 = depthBase64
        };

        conn.Publish(rgbdTopic, msg);
    }

    Texture2D CreateNormalizedDepthPNG(float[] depthValues, float minDepth, float maxDepth)
    {
        //Texture2D depthPNG = new Texture2D(imageWidth, imageHeight, TextureFormat.Alpha8, false);
        Texture2D depthPNG = new Texture2D(imageWidth, imageHeight, TextureFormat.RGBA32, false);
        Color[] pixels = new Color[depthValues.Length];

        float range = maxDepth - minDepth;

        for (int i = 0; i < depthValues.Length; i++)
        {
            float norm = 0f;
            if (depthValues[i] > 0.0001f)
            {
                norm = Mathf.Clamp01((depthValues[i] - minDepth) / range);

            }
            byte val = (byte)(norm * 255f);
            pixels[i] = new Color32(0, 0, 0, val); // Store in alpha channel
        }

        depthPNG.SetPixels(pixels);
        depthPNG.Apply();
        return depthPNG;
    }

    
    Texture2D CaptureCamera(Camera cam)
    {
        RenderTexture rt = new RenderTexture(imageWidth, imageHeight, 24);
        cam.targetTexture = rt;
        Texture2D tex = new Texture2D(imageWidth, imageHeight, TextureFormat.RGB24, false);
        cam.Render();
        RenderTexture.active = rt;
        tex.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
        tex.Apply();

        cam.targetTexture = null;
        RenderTexture.active = null;
        Destroy(rt);

        return tex;
    }

    Texture2D CaptureDepth(Camera cam)
    {
        RenderTexture rt = new RenderTexture(imageWidth, imageHeight, 24, RenderTextureFormat.Depth);
        cam.targetTexture = rt;
        cam.Render();

        RenderTexture.active = rt;
        Texture2D depthTex = new Texture2D(imageWidth, imageHeight, TextureFormat.R16, false);
        depthTex.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
        depthTex.Apply();

        cam.targetTexture = null;
        RenderTexture.active = null;
        Destroy(rt);

        return depthTex;
    }

    public Shader linearDepthShader; // Assign in Inspector

    Texture2D CaptureDepthLinear(Camera cam)
{
    RenderTexture rt = new RenderTexture(imageWidth, imageHeight, 24, RenderTextureFormat.ARGBFloat);
    rt.Create();

    cam.targetTexture = rt;
    cam.RenderWithShader(linearDepthShader, "");
    RenderTexture.active = rt;

    // Use RGBAFloat for compatibility with ReadPixels
    Texture2D tex = new Texture2D(imageWidth, imageHeight, TextureFormat.RGBAFloat, false);
    tex.ReadPixels(new Rect(0, 0, imageWidth, imageHeight), 0, 0);
    tex.Apply();

    cam.targetTexture = null;
    RenderTexture.active = null;
    rt.Release();
    Destroy(rt);

    return tex;
}
    
    public static CameraIntrinsicsMessage CreateIntrinsicsMessage(Camera cam, int imageWidth, int imageHeight)
    {
        float verticalFOVDeg = cam.fieldOfView;
        float verticalFOVRad = verticalFOVDeg * Mathf.Deg2Rad;

        // Compute focal length in pixels
        float fy = imageHeight / (2.0f * Mathf.Tan(verticalFOVRad / 2.0f));
        float fx = fy * ((float)imageWidth / imageHeight);  // Assuming square pixels

        // Principal point assumed at image center
        float cx = imageWidth / 2.0f;
        float cy = imageHeight / 2.0f;

        return new CameraIntrinsicsMessage
        {
            imageWidth = imageWidth,
            imageHeight = imageHeight,
            fx = fx,
            fy = fy,
            cx = cx,
            cy = cy,
            nearClip = cam.nearClipPlane,
            farClip = cam.farClipPlane,
            verticalFOV = verticalFOVDeg
        };
    }
}
