using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

public class SpawnerBasic : MonoBehaviour
{
    public int numObjectsToSpawn = 10;
    public GameObject objectToSpawn;
    public List<GameObject> spawnedObjects = new List<GameObject>();

    public BoxCollider spawnArea;
    public bool checkIfOccupied = false;
    public float overlapSphereRadius = 0.5f;

    public string resetTopicName = "/respawn_rat";

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        /*if (spawnArea.enabled)
        {
            spawnArea.enabled = false; // Disable the collider to prevent physics interactions
        }*/

        var conn = RoslikeTCPServer.GetInstance();
        conn.RegisterTimerDiscrete(HandleObjectSpawning, 1);
        conn.Subscribe<StringMessage>(resetTopicName, ResetObjectsCallback);

        HandleObjectSpawning(null); // Initial call to spawn objects


    }
    
    public void ResetObjectsCallback(StringMessage stringMessage)
    {
        // Destroy all spawned objects
        foreach (var obj in spawnedObjects)
        {
            if (obj != null)
            {
                Destroy(obj);
            }
        }
        spawnedObjects.Clear();

        // Re-spawn objects
        HandleObjectSpawning(null);
    }

    public void HandleObjectSpawning(TimerEvent ev)
    {
        //Remove destroyed objects from the list
        spawnedObjects.RemoveAll(obj => obj == null);

        // If we have less than the desired number of objects, spawn more
        int numtries = 0;
        while (spawnedObjects.Count < numObjectsToSpawn && numtries < 10 * numObjectsToSpawn)
        {
            numtries++;
            // Generate a random position within the spawn area
            Vector3 randomPosition = new Vector3(
                Random.Range(spawnArea.bounds.min.x, spawnArea.bounds.max.x),
                transform.position.y, // Random.Range(spawnArea.bounds.min.y, spawnArea.bounds.max.y)
                Random.Range(spawnArea.bounds.min.z, spawnArea.bounds.max.z)

            );

            // Check if the position is already occupied by another object
            if (checkIfOccupied)
            {
                // If the position is occupied, skip this iteration and try again
                var overlapped = Physics.OverlapSphere(randomPosition, overlapSphereRadius);
                bool foundNonSelf = false;
                if (overlapped.Length > 0)
                {
                    foreach (var collider in overlapped)
                    {
                        if (collider.gameObject != gameObject)
                        {
                            foundNonSelf = true;
                            break;
                        }
                    }
                }

                if (foundNonSelf)
                {
                    continue; // If the position is occupied, skip this position
                }

                // Also raycast down to check if the position is above the ground
                RaycastHit hit;
                if (!Physics.Raycast(randomPosition, Vector3.down, out hit, 100f))
                {
                    continue; // If no ground is detected, skip this position
                }
            }

            // Instantiate the object at the random position
            GameObject newObject = Instantiate(objectToSpawn, randomPosition, Quaternion.identity);

            // Add the new object to the list of spawned objects
            spawnedObjects.Add(newObject);
        }
    }
}
