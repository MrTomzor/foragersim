using System.Collections.Generic;
using UnityEngine;

public class CarSpawner : MonoBehaviour
{
    public GameObject carPrefab;
    public float minCarSpeed = 10f;
    public float maxCarSpeed = 10f;

    public float minSpawnInterval = 2f;
    public float maxSpawnInterval = 10f;

    public float roadLength = 10;

    public List<float> spawnTimes = new List<float>();
    public float realtimeClock = 0;
    public int spawnIndex = 0;

    List<GameObject> spawnedCars = new List<GameObject>();

    RoslikeTCPServer conn;

    public bool spawningEnabled = true;
    

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        conn = RoslikeTCPServer.GetInstance();
       
        conn.RegisterTimerDiscrete(MainLoop, 1);
    }

    public void SpawnAllCarsAtStart()
    {
        // Populate road with cars at relative distances based on spawn times and car speeds
        float maxTime = spawnTimes[spawnTimes.Count - 1];
        foreach (var spawnTime in spawnTimes)
        {
            float timeFraction = spawnTime / maxTime;
            Vector3 spawnPosition = this.transform.position + this.transform.forward * roadLength * timeFraction;
            Quaternion spawnRotation = this.transform.rotation;

            GameObject car = Instantiate(carPrefab, spawnPosition, spawnRotation);
            spawnedCars.Add(car);

            // Set car speed
            float carSpeed = Random.Range(minCarSpeed, maxCarSpeed);
            var rb = car.GetComponent<Rigidbody>();
            rb.linearVelocity = this.transform.forward * carSpeed;
        }

    }

    public void CheckCarsAndTeleportHome()
    {
        // teleport cars that get further than roadLength back to start
        foreach (var car in spawnedCars)
        {
            float distanceAlongRoad = Vector3.Dot(car.transform.position - this.transform.position, this.transform.forward);
            if (distanceAlongRoad > roadLength)
            {
                float overshoot = distanceAlongRoad - roadLength;
                car.transform.position = this.transform.position + this.transform.forward * overshoot;
            }
        }
    }

    public void MainLoop(TimerEvent ev)
    {
        if(!spawningEnabled)
        {
            return;
        }

        if(spawnedCars.Count == 0)
        {
            SpawnAllCarsAtStart();
        }

        CheckCarsAndTeleportHome();

        // Spawn cars at intervals defined in spawnTimes
        /*
        realtimeClock += conn.physicsStepTime;

        if (spawnIndex < spawnTimes.Count)
        {
            if (realtimeClock >= spawnTimes[spawnIndex])
            {
                // Spawn a car
                Vector3 spawnPosition = this.transform.position;
                Quaternion spawnRotation = this.transform.rotation;

                GameObject car = Instantiate(carPrefab, spawnPosition, spawnRotation);
                spawnedCars.Add(car);

                // Set car speed
                float carSpeed = Random.Range(minCarSpeed, maxCarSpeed);
                var rb = car.GetComponent<Rigidbody>();
                rb.linearVelocity = this.transform.forward * carSpeed;

                spawnIndex++;
            }
        }
        */


    }

    // Update is called once per frame
    void Update()
    {
        
    }

    // On destroy remove the cars
    void OnDestroy()
    {
        foreach (var car in spawnedCars)
        {
            Destroy(car);
        }
    }
}
