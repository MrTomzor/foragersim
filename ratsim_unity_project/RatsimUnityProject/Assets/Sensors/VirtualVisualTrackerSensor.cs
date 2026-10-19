using System;
using System.Collections.Generic;
using UnityEngine;

public class VirtualVisualTrackerSensor : MonoBehaviour
{
    public bool debugDrawRays = false;
    public bool verbose = false;
    public float maxRange = 100f; // Maximum range of the sensor
                                  // Start is called once before the first execution of Update after the MonoBehaviour is created
    RoslikeTCPServer conn;

    public string topicName = "/visual_point_track_pcl";

    public int maxTrackedPoints = 100;
    public float horizontalFovDegrees = 180;
    public float verticalFovDegrees = 90;

    public float pointLosingDist = 1;

    public List<Vector3> trackedPoints = new List<Vector3>();
    public List<float[]> trackedPointDescriptors = new List<float[]>();
    public int descriptorDimension = 3; // Dimension of the descriptors

    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        VisualPointTrackerMessage msg = new VisualPointTrackerMessage();

        // Cast rays towards already tracked points and remove those for which the ray is too far from the tracked point
        List<Vector3> newTrackedPoints = new List<Vector3>();
        List<float[]> newTrackedPointDescriptors = new List<float[]>();
        Color keptPointColor = Color.cyan;
        Color newPointColor = Color.red;

        int numPtsAtStart = trackedPoints.Count;

        for (int i = 0; i < trackedPoints.Count; i++)
        {
            Vector3 point = trackedPoints[i];
            float[] descriptor = trackedPointDescriptors[i];

            // Check if the point is within the sensor's range
            RaycastHit hit;
            if (Physics.Raycast(transform.position, point - transform.position, out hit, maxRange))
            {
                if (debugDrawRays)
                {
                    Debug.DrawLine(transform.position, hit.point, keptPointColor, 0);
                }
                // Check if the hit point is close enough to the tracked point
                if (Vector3.Distance(hit.point, point) < pointLosingDist)
                {
                    // Check also that the point is within the FOV
                    Vector3 directionToPoint = (hit.point - transform.position).normalized;
                    Vector3 toPointHorizontalWorld = Vector3.ProjectOnPlane(directionToPoint, transform.up);
                    Vector3 toPointVerticalWorld = Vector3.ProjectOnPlane(directionToPoint, -transform.right);
                    float angleX = Vector3.Angle(transform.forward, toPointHorizontalWorld);
                    //float angleY = Vector3.Angle(transform.up, toPointVerticalWorld);
                    float angleY = Mathf.Atan2(directionToPoint.y, Mathf.Sqrt(directionToPoint.x * directionToPoint.x + directionToPoint.z * directionToPoint.z)) * Mathf.Rad2Deg;
                    //Debug.Log("AngleY: " + angleY);
                    //float angleX = Mathf.Abs(Vector3.Angle(transform.forward, directionToPoint));
                    //float angleY = Mathf.Abs(Vector3.Angle(transform.up, directionToPoint));
                    //if (Mathf.Abs(angleX) <= horizontalFovDegrees / 2)
                    if (Mathf.Abs(angleX) <= horizontalFovDegrees / 2 && Mathf.Abs(angleY) <= verticalFovDegrees / 2)
                    {
                        newTrackedPoints.Add(hit.point);
                        newTrackedPointDescriptors.Add(descriptor);
                    }
                }
            }
        }
        trackedPoints = newTrackedPoints;
        trackedPointDescriptors = newTrackedPointDescriptors;
        int numAfterCulling = trackedPoints.Count;

        // Add new points if we have space by sampling random points in the FOV
        if (newTrackedPoints.Count < maxTrackedPoints)
        {
            int pointsToAdd = maxTrackedPoints - newTrackedPoints.Count;
            List<Vector3> worldDirections = new List<Vector3>();
            for (int i = 0; i < pointsToAdd; i++)
            {
                float angleX = UnityEngine.Random.Range(-horizontalFovDegrees / 2, horizontalFovDegrees / 2);
                float angleY = UnityEngine.Random.Range(-verticalFovDegrees / 2, verticalFovDegrees / 2);
                Vector3 worldDirection = Quaternion.Euler(angleY, angleX, 0) * transform.forward;
                worldDirections.Add(worldDirection);
            }

            List<Tuple<float, float[]>> sensed = SemanticLidarSensor.GetRangesAndDescriptorsByCasting(transform.position, worldDirections, maxRange, false);
            for (int i = 0; i < sensed.Count; i++)
            {
                float distance = sensed[i].Item1;
                float[] descriptor = sensed[i].Item2;

                if (distance >= 0)
                {
                    Vector3 newPoint = transform.position + worldDirections[i] * distance;
                    newTrackedPoints.Add(newPoint);
                    newTrackedPointDescriptors.Add(descriptor);

                    if (debugDrawRays)
                    {
                        Debug.DrawLine(transform.position, newPoint, newPointColor, 0.2f);
                    }
                }
            }
        }

        int numAfterAddition = trackedPoints.Count;
        if (verbose)
        {
            Debug.Log($"VirtualVisualTrackerSensor: {numPtsAtStart} points at start, {numAfterCulling} after culling, {numAfterAddition} after addition.");
        }

        // Send message
        msg.trackedPointsEgocentricFLU = new float[trackedPoints.Count * 3];
        msg.trackedPointDescriptors = new float[trackedPoints.Count * descriptorDimension];
        for (int i = 0; i < trackedPoints.Count; i++)
        {
            Vector3 egocentricRUF = transform.InverseTransformPoint(trackedPoints[i]);
            msg.trackedPointsEgocentricFLU[i * 3] = egocentricRUF.z;
            msg.trackedPointsEgocentricFLU[i * 3 + 1] = -egocentricRUF.x;
            msg.trackedPointsEgocentricFLU[i * 3 + 2] = egocentricRUF.y;
            
            for (int j = 0; j < descriptorDimension; j++)
            {
                msg.trackedPointDescriptors[i * descriptorDimension + j] = trackedPointDescriptors[i][j];
            }
        }
        msg.scaleFactor = 1.0f; 

        // Publish the message to the specified topic
        conn.Publish(topicName, msg);
    }

    // Update is called once per frame
    void Update()
    {
        
    }
}
