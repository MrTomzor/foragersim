using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SemanticLidarSensor : MonoBehaviour
{

    public static Hashtable semanticNamesToIndices = null;
    public SemanticSet activeSemanticSet;
    // Name of a SemanticSet asset in Resources/SemanticSets/. If set and the asset is found,
    // it overrides `activeSemanticSet` at Start. Allows switching semantic sets via agent config.
    public string semanticSet = "full_semantic_set";


    public int angleStartDeg = 45; // Start angle in degrees
    public int angleEndDeg = 45;
    public int angleIncrementDeg = 5;
    public float maxRange = 100f; // Maximum range of the lidar sensor


    public static uint descriptorDimension = 3;

    public string topicName = "/lidar2d";

    public bool debugDrawRays = false;

    /// <summary>Latest range readings. Read by visualizer.</summary>
    [HideInInspector] public float[] lastRanges;
    /// <summary>Latest descriptor readings (flat array, stride = descriptorDimension). Read by visualizer.</summary>
    [HideInInspector] public float[] lastDescriptors;

    public int numRays;
    RoslikeTCPServer conn;
    public bool verbose = false;
    public bool enableSmokeCorruption = true;
    public bool checkIfInCollider = true;
    public float inColliderCheckOffset = 0.02f;

    // --- Faults ---
    // Simulates an object stuck to the sensor: the selected third of the FOV
    // (by ray index, so "left" is the first third, "front" the middle, "right" the last)
    // reports a fixed distance with a zero / "default" descriptor.
    // Valid values: "none", "left", "front", "right".
    [Header("Faults")]
    public string occlusionRegion = "none";
    public float occlusionDistance = 0.1f;

    public static float[] GetNamedSemanticObjectDescriptor(string semanticName)
    {
        // One hot encoding based on the semantic name
        if (semanticNamesToIndices.ContainsKey(semanticName))
        {
            int index = (int)semanticNamesToIndices[semanticName];
            float[] descriptor = new float[semanticNamesToIndices.Count];
            descriptor[index] = 1.0f;
            return descriptor;
        }
        else
        {
            return new float[semanticNamesToIndices.Count];
        }
    }

    void InitializeSemanticSetData()
    {
        semanticNamesToIndices = new Hashtable();

        foreach(GameObject obj in activeSemanticSet.prefabs)
        {
            NamedSemanticObject namedSemanticObject = obj.GetComponentInChildren<NamedSemanticObject>();
            if (namedSemanticObject != null)
            {
                if (!semanticNamesToIndices.ContainsKey(namedSemanticObject.semanticName))
                {
                    semanticNamesToIndices[namedSemanticObject.semanticName] = semanticNamesToIndices.Count;
                }
            }
        }

        descriptorDimension = (uint)semanticNamesToIndices.Count;
        Debug.Log("Initialized Semantic Set Hashtable with " + descriptorDimension + " semantic classes.");
        Debug.Log("Semantic classes:");
        foreach (DictionaryEntry entry in semanticNamesToIndices)
        {
            Debug.Log("  " + entry.Key + " -> " + entry.Value);
        }
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // Resolve semantic set: prefer Resources/SemanticSets/{semanticSet} if set, else Inspector asset.
        if (!string.IsNullOrEmpty(semanticSet))
        {
            SemanticSet loaded = Resources.Load<SemanticSet>("SemanticSets/" + semanticSet);
            if (loaded != null)
            {
                activeSemanticSet = loaded;
                Debug.Log($"SemanticLidarSensor: loaded semantic set '{semanticSet}' from Resources/SemanticSets/");
            }
            else
            {
                Debug.LogWarning($"SemanticLidarSensor: Resources/SemanticSets/{semanticSet} not found, falling back to Inspector-assigned set");
            }
        }

        if (activeSemanticSet != null)
        {
            Debug.Log("Initializing Semantic Set Data...");
            InitializeSemanticSetData();
        }

        numRays = 1 + (angleEndDeg - angleStartDeg) / angleIncrementDeg;

        conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(SenseAndPublish, 1);
        //SenseAndPublish(null); 

    }

    public static List<Tuple<float, float[]>> GetRangesAndDescriptorsByCasting(Vector3 start, List<Vector3> worldDirections, float maxRange, bool debugDrawRays = false)
    {
        List<Tuple<float, float[]>> res = new List<Tuple<float, float[]>>();

        // Create layermask to ignore WorldGen layer
        int layerMask = 1 << LayerMask.NameToLayer("WorldGen");
        layerMask = ~layerMask;

        foreach (var dir in worldDirections)
        {
            RaycastHit hit;
            //if (Physics.Raycast(start, dir, out hit, maxRange))
            if (Physics.Raycast(start, dir, out hit, maxRange, layerMask, QueryTriggerInteraction.Ignore))
            {
                float distance = hit.distance;
                SemanticObject semanticObject = hit.collider.GetComponent<SemanticObject>();
                float[] descriptor = semanticObject != null ? semanticObject.GetDescriptor(hit.point) : new float[descriptorDimension];

                res.Add(new Tuple<float, float[]>(distance, descriptor));
                if (debugDrawRays)
                {
                    Debug.DrawLine(start, hit.point, Color.red, 0);
                }
            }
            else
            {
                res.Add(new Tuple<float, float[]>(-1, new float[descriptorDimension]));
                if (debugDrawRays)
                {
                    Debug.DrawLine(start, start + dir * maxRange, Color.red, 0);
                }
            }

            
        }

        return res;
    }

    private void ApplyOcclusionFault(List<Tuple<float, float[]>> sensed)
    {
        if (string.IsNullOrEmpty(occlusionRegion) || occlusionRegion == "none") return;

        int third = numRays / 3;
        int startIdx, endIdx;
        switch (occlusionRegion.ToLowerInvariant())
        {
            case "left":  startIdx = 0;           endIdx = third;          break;
            case "front": startIdx = third;       endIdx = 2 * third;      break;
            case "right": startIdx = 2 * third;   endIdx = numRays;        break;
            default:
                Debug.LogWarning($"SemanticLidarSensor: unknown occlusionRegion '{occlusionRegion}'");
                return;
        }

        float[] defaultDescriptor = new float[descriptorDimension];
        for (int i = startIdx; i < endIdx; i++)
            sensed[i] = new Tuple<float, float[]>(occlusionDistance, defaultDescriptor);
    }

    public void SenseAndPublish(TimerEvent ev)
    {
        var timestart = Time.realtimeSinceStartup;
        Lidar2DMessage msg = new Lidar2DMessage();

        msg.angleIncrementDeg = angleIncrementDeg;
        msg.angleStartDeg = angleStartDeg;
        msg.maxRange = maxRange;

        msg.ranges = new float[numRays];
        msg.descriptors = new float[numRays * descriptorDimension];

        // Cast rays in 2D starting from angleStartDeg to angleEndDeg
        List<Vector3> worldDirections = new List<Vector3>();
        for (int i = 0; i < numRays; i++)
        {
            float angle = angleStartDeg + i * angleIncrementDeg;
            float radians = angle * Mathf.Deg2Rad;

            // Cast a ray in the specified direction
            Vector3 dirvec = new Vector3(Mathf.Sin(radians), 0, Mathf.Cos(radians));
            dirvec = transform.TransformDirection(dirvec);
            worldDirections.Add(dirvec);
            /*
            RaycastHit hit;
            Physics.Raycast(transform.position, dirvec, out hit, maxRange);

            if (hit.collider != null)
            {
                Debug.Log(hit.collider.gameObject.name);
                msg.ranges[i] = hit.distance;
                numhit++;

                // Get the semantic object and its descriptor
                SemanticObject semanticObject = hit.collider.GetComponent<SemanticObject>();
                if (semanticObject != null)
                {
                    Debug.Log("Found semantic object: " + semanticObject.name);
                    uint dim = semanticObject.GetDescriptorDimension();
                    for (uint j = 0; j < dim; j++)
                    {
                        msg.descriptors[i * dim + j] = semanticObject.GetDescriptor(hit.point)[j];
                    }
                }
                else
                {
                    for (uint j = 0; j < descriptorDimension; j++)
                    {
                        msg.descriptors[i * descriptorDimension + j] = 0;
                    }
                }
            }
            else
            {
                msg.ranges[i] = -1;
                for (uint j = 0; j < descriptorDimension; j++)
                {
                    msg.descriptors[i * descriptorDimension + j] = 0;
                }
            }

            if (debugDrawRays)
            {
                Debug.DrawLine(transform.position, transform.position + dirvec * (msg.ranges[i] < 0 ? maxRange : msg.ranges[i]), Color.red, 0);

            }*/
        }

        // If enabled, check if sensor is inside collider. If yes, return default-material semantics and OcclusionDistance range for all rays.
        bool isInCollider = false;
        var sensed = new List<Tuple<float, float[]>>();
        if(checkIfInCollider)
        {
            // also ignore layer WorldGen
            int ignoreLayer = LayerMask.NameToLayer("WorldGen");
            float originalSphereColliderRadius = GetComponentInChildren<SphereCollider>().radius;

            // THIS CODE STILL DETECTS WORLDGEN LAYER!!!!
            //Collider[] colliders = Physics.OverlapSphere(transform.position, originalSphereColliderRadius - inColliderCheckOffset, ~ignoreLayer, QueryTriggerInteraction.Ignore);
            
            // fixed check:
            Collider[] colliders = Physics.OverlapSphere(transform.position, originalSphereColliderRadius - inColliderCheckOffset, ~ (1 << ignoreLayer), QueryTriggerInteraction.Ignore);
            
            if (colliders.Length > 0)
            {
                // check also if not overlaping SELF
                bool onlySelf = true;
                foreach(var col in colliders)
                {
                    if (col.gameObject != this.gameObject)
                    {
                        onlySelf = false;
                        break;
                    }
                }
                if(onlySelf)
                {
                    isInCollider = false;
                }
                else{
                    Debug.LogWarning($"SemanticLidarSensor: detected {colliders.Length} colliders overlapping sensor position. Assuming sensor is inside a collider and returning occlusion readings. Colliders: {string.Join(", ", (IEnumerable)colliders)}");
                    Debug.LogWarning("First collider: " + colliders[0].name + ", tag: " + colliders[0].tag);
                    // go up the hierarchy of the collider and print all names until you reach the root
                    Transform t = colliders[0].transform;
                    while (t != null)
                    {
                        Debug.LogWarning("Collider parent: " + t.name);
                        t = t.parent;
                    }
                    // log the collider layer
                    Debug.LogWarning("Collider layer: " + LayerMask.LayerToName(colliders[0].gameObject.layer));
                    
                    float[] defaultDescriptor = new float[descriptorDimension];
                    for (int i = 0; i < worldDirections.Count; i++)
                        sensed.Add(new Tuple<float, float[]>(occlusionDistance, defaultDescriptor));

                    if (debugDrawRays)
                    {
                        foreach (var dir in worldDirections)
                        {
                            Debug.DrawLine(transform.position, transform.position + dir * occlusionDistance, Color.red, 0);
                        }
                    }

                    isInCollider = true;
                }
            }
        }

        if(!isInCollider){
            sensed = GetRangesAndDescriptorsByCasting(transform.position, worldDirections, maxRange, debugDrawRays);
        }

        // ── Smoke corruption ──
        if (enableSmokeCorruption && SmokeObject2D.allActive.Count > 0)
        {
            int stepSeed = Time.frameCount * 31;
            System.Random rng = new System.Random(stepSeed);
            float[] smokeDescriptor = GetNamedSemanticObjectDescriptor("smoke");
            Vector2 origin2D = new Vector2(transform.position.x, transform.position.z);

            int smokeCount = SmokeObject2D.allActive.Count;
            float[] enters  = new float[smokeCount];
            float[] exits   = new float[smokeCount];
            float[] weights = new float[smokeCount];

            for (int i = 0; i < numRays; i++)
            {
                Vector2 dir2D = new Vector2(worldDirections[i].x, worldDirections[i].z).normalized;
                float hitDist = sensed[i].Item1 > 0 ? sensed[i].Item1 : maxRange;

                // Compute intersections with all smoke objects
                int hitCount = 0;

                for (int s = 0; s < smokeCount; s++)
                {
                    var smoke = SmokeObject2D.allActive[s];
                    if (smoke.RayIntersect2D(origin2D, dir2D, hitDist,
                                             out float tEnter, out float tExit))
                    {
                        float length = tExit - tEnter;
                        enters[hitCount]  = tEnter;
                        exits[hitCount]   = tExit;
                        weights[hitCount] = smoke.density * length;
                        hitCount++;
                    }
                }

                if (hitCount == 0) continue;

                // Read corruption mode from the first intersecting smoke object
                var firstSmoke = SmokeObject2D.allActive[0];

                if (firstSmoke.corruptionMode == SmokeCorruptionMode.RandomHits)
                {
                    // Survival probability: product of (1 - density*length) per smoke
                    double survivalProb = 1.0;
                    for (int k = 0; k < hitCount; k++)
                        survivalProb *= (1.0 - Mathf.Clamp01(weights[k]));

                    if (rng.NextDouble() > survivalProb)
                    {
                        // Pick which smoke segment corrupted the ray (weighted)
                        float totalWeight = 0f;
                        for (int k = 0; k < hitCount; k++) totalWeight += weights[k];
                        float roll = (float)rng.NextDouble() * totalWeight;
                        int chosen = 0;
                        float acc = 0f;
                        for (int k = 0; k < hitCount; k++)
                        {
                            acc += weights[k];
                            if (roll <= acc) { chosen = k; break; }
                        }

                        // Exponential sampling: denser smoke stops the ray sooner
                        float segLen = exits[chosen] - enters[chosen];
                        float chosenDensity = weights[chosen] / segLen;
                        float dIntoSmoke = Mathf.Min((float)(-System.Math.Log(1.0 - rng.NextDouble()) / chosenDensity), segLen);
                        float t = enters[chosen] + dIntoSmoke;
                        sensed[i] = new Tuple<float, float[]>(t, smokeDescriptor);
                    }
                }
                else // EffectiveRange
                {
                    // Per-ray threshold sampled from N(effectiveRange, effectiveRangeVariance)
                    double u1 = 1.0 - rng.NextDouble();
                    double u2 = rng.NextDouble();
                    double normal = System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
                    float rayThreshold = Mathf.Max(0f, firstSmoke.effectiveRange + firstSmoke.effectiveRangeVariance * (float)normal);

                    // Sort segments by entry distance (insertion sort, hitCount is small)
                    for (int a = 1; a < hitCount; a++)
                    {
                        float eA = enters[a], xA = exits[a];
                        int b = a - 1;
                        while (b >= 0 && enters[b] > eA)
                        {
                            enters[b + 1] = enters[b];
                            exits[b + 1] = exits[b];
                            b--;
                        }
                        enters[b + 1] = eA;
                        exits[b + 1] = xA;
                    }

                    // Merge overlapping segments in-place
                    int mergedCount = 1;
                    for (int k = 1; k < hitCount; k++)
                    {
                        if (enters[k] <= exits[mergedCount - 1])
                        {
                            // Overlaps — extend the current merged segment
                            exits[mergedCount - 1] = Mathf.Max(exits[mergedCount - 1], exits[k]);
                        }
                        else
                        {
                            enters[mergedCount] = enters[k];
                            exits[mergedCount] = exits[k];
                            mergedCount++;
                        }
                    }

                    // Walk merged segments, accumulate travel through smoke
                    float accumulated = 0f;
                    for (int k = 0; k < mergedCount; k++)
                    {
                        float segLen = exits[k] - enters[k];
                        if (accumulated + segLen >= rayThreshold)
                        {
                            float remaining = rayThreshold - accumulated;
                            float t = enters[k] + remaining;
                            sensed[i] = new Tuple<float, float[]>(t, smokeDescriptor);
                            break;
                        }
                        accumulated += segLen;
                    }
                }
            }
        }

        // ── Occlusion fault (object stuck to sensor) ──
        ApplyOcclusionFault(sensed);

        for (int i = 0; i < numRays; i++)
        {
            msg.ranges[i] = sensed[i].Item1;
            for (uint j = 0; j < descriptorDimension; j++)
            {
                msg.descriptors[i * descriptorDimension + j] = sensed[i].Item2[j];
            }
        }

        lastRanges = msg.ranges;
        lastDescriptors = msg.descriptors;

        var sensedonetime = Time.realtimeSinceStartup;


        // Publish the message to the specified topic
        conn.Publish(topicName, msg);

        if (verbose)
        {
            Debug.Log("Sensing time: " + (1000 * (sensedonetime - timestart)) + " ms, pushing time:" + (1000 * (Time.realtimeSinceStartup - sensedonetime)) + " ms");
        }
    }
}
