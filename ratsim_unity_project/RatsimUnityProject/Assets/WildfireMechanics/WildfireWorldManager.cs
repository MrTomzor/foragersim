using System.Collections.Generic;
using UnityEngine;


public class WildfireWorldManager : MonoBehaviour
{
    public string goalPosTopic = "/wildfire_goal_position";

    float defaultHeight;

    // WORLD PARAMETERS SETTABLE THRU MSGS
    public string mainLayout = "forest_frogger";
    public float arenaWidth = 100f;
    public float arenaHeight = 100f;
    public float treeDensity = 0.01f; // trees per square unit
    public int seed = 42;
    public int numAgents = 1;
    public float startAndGoalClearingDistance = 10f;
    public float carRoadSpawnFrequency = 3; // number of roads to spawn

    public float houseNumerosity = 0;
    public string houseDoorDefaultType;
    public float rewardNumerosity = 0;
    public string rewardDistribution;

    System.Random rng;

    public Vector3 startPosition;
    public Vector3 goalPosition;

    // BELOW ARE INTERNAL VARIABLES (not communicated via msgs)

    // TREES
    public List<GameObject> trees;
    public List<GameObject> agents;
    public bool treeOscillationEnabled = false;
    public Vector2 treeOscillationDirection = new Vector2(1, 0);
    public List<Vector3> treeOriginalPositions;
    public List<float> treeOscillationPhases;
    public float treeOscillationMagnitude = 0.5f;
    public float treeOscillationFrequency = 1.0f;
    public float treeOscillationTime = 0;
    public float aroundAgentDynamicsBoxSize = 120f;

    // HOUSES
    public List<GameObject> houses;


    // CARS
    public float roadClearingWidth = 1.5f;
    public float roadStartGoalMargin = 20;
    public int numCarsToSpawn = 15;

    // REWARDS
    public List<GameObject> rewardObjects;

    // WALLS
    public List<GameObject> arenaWalls;


    public GameObject carSpawnerPrefab;
    public GameObject roadPrefab;
    public List<GameObject> carSpawners;
    public List<GameObject> roads;

    // PREFABS
    public bool randomizeAgentDirection = true;
    public GameObject treePrefab;
    public GameObject agentPrefab;
    public GameObject goalMarkerPrefab;
    public List<GameObject> activeMarkers;
    public GameObject houseDefaultPrefab;
    public GameObject rewardPickupPrefab;
    public GameObject arenaWallPrefab;

    RoslikeTCPServer conn;

    // FLAGS
    public bool worldgenRequested = false;
    



    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

        
        defaultHeight = this.transform.position.y;

        conn = RoslikeTCPServer.GetInstance();
        //conn.Subscribe<WildfireWorldGenMessage>(mapGenMsgTopic, GenerateMsgCallback);
        conn.RegisterTimerDiscrete(MainLoop, 1);

        conn.Subscribe<BoolMessage>("/worldgen/requested", (msg) => {
            worldgenRequested = msg.data;
            Debug.Log($"Worldgen requested: {worldgenRequested}");
        });

        InitParamSubscribers();
        
    }

    void InitParamSubscribers()
    {
        conn.Subscribe<Int32Message>("/worldgen/seed", (msg) => {
            seed = msg.data;
            Debug.Log($"Worldgen seed set to: {seed}");
        });

        // layout
        conn.Subscribe<StringMessage>("/worldgen/mainLayout", (msg) => {
            mainLayout = msg.data;
            Debug.Log($"Worldgen main layout set to: {mainLayout}");
        });

        // w and h
        conn.Subscribe<Float32Message>("/worldgen/arenaWidth", (msg) => {
            arenaWidth = msg.data;
            Debug.Log($"Worldgen arena width set to: {arenaWidth}");
        });
        conn.Subscribe<Float32Message>("/worldgen/arenaHeight", (msg) => {
            arenaHeight = msg.data;
            Debug.Log($"Worldgen arena height set to: {arenaHeight}");
        });

        // trees
        conn.Subscribe<Float32Message>("/worldgen/treeDensity", (msg) => {
            treeDensity = msg.data;
            Debug.Log($"Worldgen tree density set to: {treeDensity}");
        });

        conn.Subscribe<BoolMessage>("/worldgen/treeOscillationEnabled", (msg) => {
            treeOscillationEnabled = msg.data;
            Debug.Log($"Tree oscillation enabled set to: {treeOscillationEnabled}");
        });


        // houses
        conn.Subscribe<Float32Message>("/worldgen/houseNumerosity", (msg) => {
            houseNumerosity = msg.data;
            Debug.Log($"Worldgen house numerosity set to: {houseNumerosity}");
        });
        conn.Subscribe<StringMessage>("/worldgen/houseDoorDefaultType", (msg) => {
            houseDoorDefaultType = msg.data;
            Debug.Log($"Worldgen house door default type set to: {houseDoorDefaultType}");
        });
        conn.Subscribe<StringMessage>("/worldgen/rewardDistribution", (msg) => {
            rewardDistribution = msg.data;
            Debug.Log($"Worldgen reward distribution set to: {rewardDistribution}");
        });


        // rewards
        conn.Subscribe<Float32Message>("/worldgen/rewardNumerosity", (msg) => {
            rewardNumerosity = msg.data;
            Debug.Log($"Worldgen reward numerosity set to: {rewardNumerosity}");
        });
        conn.Subscribe<StringMessage>("/worldgen/rewardDistribution", (msg) => {
            rewardDistribution = msg.data;
            Debug.Log($"Worldgen reward distribution set to: {rewardDistribution}");
        });
    }

    public void MainLoop(TimerEvent ev)
    {
        if(worldgenRequested)
        {
            worldgenRequested = false;
            GenerateWorld();
        }

        PublishGoalPosition();
        if(treeOscillationEnabled){
            HandleTreeOscillation();
        }
    }

    // WORLD GENERATION CORE

    void RespawnAgents()
    {
        for (int i = 0; i < numAgents; i++)
        {
            GameObject agent;

            Vector3 spawnpos = startPosition;
            if(i < agents.Count && agents[i] != null)
            {
                spawnpos.y = agents[i].transform.position.y; // keep current height if agent already exists
            }
            Quaternion spawnrot = Quaternion.identity;
            // make agent point towards goal at start
            Vector3 directionToGoal = (goalPosition - startPosition).normalized;
            if (directionToGoal != Vector3.zero)
            {
                spawnrot = Quaternion.LookRotation(directionToGoal);
            }

            if (i < agents.Count)
            {
                agent = agents[i];
                agent.transform.position = spawnpos;
                agent.transform.rotation = spawnrot;
            }
            else
            {
                agent = Instantiate(agentPrefab, spawnpos, spawnrot);
                agents.Add(agent);
                // TODO later -- set agent's topic prefix according to its index
            }

            if(randomizeAgentDirection)
            {
                // Randomize initial rotation a bit
                float randomYaw = (float)(rng.NextDouble() * 360.0);
                agent.transform.Rotate(0, randomYaw, 0);
            }

            // Reset agents' relative pose trackers if they have them
            if(agent.GetComponent<RelativePoseSensor>() != null)
            {
                agent.GetComponent<RelativePoseSensor>().ResetOrigin();
            }

            // Reset velocity if they have a Rigidbody
            if(agent.GetComponent<Rigidbody>() != null)
            {
                agent.GetComponent<Rigidbody>().linearVelocity = Vector3.zero;
                agent.GetComponent<Rigidbody>().angularVelocity = Vector3.zero;
            }
        }
    }

    void GenerateFroggerWorld()
    {
        // Generate start and goal positions
        float clearingDist = startAndGoalClearingDistance;
        startPosition = new Vector3(0, defaultHeight, arenaHeight * 0.2f - arenaHeight/2);
        goalPosition = new Vector3(0, defaultHeight, arenaHeight * 0.8f - arenaHeight/2);

        // Generate trees
        GenerateTreesUniform();

        // Generate roads
        GenerateRoadsSimple();

        // Generate markers
        GenerateMarkers();

        // Spawn or move agents to start position
        RespawnAgents();

        if(treeOscillationEnabled){
            InitializeTreeDynamics();
        }


        // Send data to python
        PublishGoalPosition();
        // TODO implement later
    }

    void GenerateSuburbWorld()
    {

        // tp agent to start
        RespawnAgents();

        GenerateArenaWalls();

        GenerateTreesUniform();

        // Generate houses 
        GenerateHousesUniform();

        GenerateRewards();
    }

    void GenerateWorld()
    {
        Debug.Log("*** Generating world with seed: " + seed + ", layout: " + mainLayout + ", arena size: " + arenaWidth + "x" + arenaHeight + ", tree density: " + treeDensity);
        // apply seed
        rng = new System.Random(seed);

        // Clear all obstacles and rewards
        foreach (var tree in trees)
        {
            DestroyImmediate(tree);
        }
        trees.Clear();
            foreach (var house in houses)
            {
                DestroyImmediate(house);
            }
            houses.Clear();
                foreach (var reward in rewardObjects)
            {
                DestroyImmediate(reward);
            }
            rewardObjects.Clear();
        
        // 3. CRITICAL: Sync transforms to physics engine
        Physics.SyncTransforms();
    
        // 4. CRITICAL: Simulate one tiny step to clear collision state
        Physics.Simulate(0.001f);  // Tiny simulation to flush state



        if(mainLayout == "forest_frogger")
        {
            GenerateFroggerWorld();
            return;
        }
        if(mainLayout == "suburb")
        {
            GenerateSuburbWorld();
            return;
        }

        Debug.LogError($"Unknown mainLayout for worldgen: {mainLayout}");
        
    }
        
    // WORLD GENERATION HELPERS
    void GenerateArenaWalls()
    {
        // Remove old
        foreach(var wall in arenaWalls)
        {
            DestroyImmediate(wall);
        }
        arenaWalls.Clear();


        // spawn 4 walls around the arena perimeter to prevent agents from leaving the area
        // Assume the wall is a thin stretchable object in the X direction, and its pivot is in the center.
        // The walls will have different lengths depending on whether they are vertical or horizontal, but the same thickness.
        float wallThickness = 1f;
        Vector3 wallScaleHorizontal = new Vector3(arenaWidth + wallThickness * 2, 5f, wallThickness);
        Vector3 wallScaleVertical = new Vector3(wallThickness, 5f, arenaHeight + wallThickness * 2);

        // bottom wall
        GameObject bottomWall = Instantiate(arenaWallPrefab, new Vector3(0, defaultHeight, -arenaHeight / 2 - wallThickness / 2), Quaternion.identity);
        bottomWall.transform.localScale = wallScaleHorizontal;
        arenaWalls.Add(bottomWall);
        // top wall
        GameObject topWall = Instantiate(arenaWallPrefab, new Vector3(0, defaultHeight, arenaHeight / 2 + wallThickness / 2), Quaternion.identity);
        topWall.transform.localScale = wallScaleHorizontal;
        arenaWalls.Add(topWall);
        // left wall
        GameObject leftWall = Instantiate(arenaWallPrefab, new Vector3(-arenaWidth / 2 - wallThickness / 2, defaultHeight, 0), Quaternion.identity);
        leftWall.transform.localScale = wallScaleVertical;
        arenaWalls.Add(leftWall);
        // right wall
        GameObject rightWall = Instantiate(arenaWallPrefab, new Vector3(arenaWidth / 2 + wallThickness / 2, defaultHeight, 0), Quaternion.identity);
        rightWall.transform.localScale = wallScaleVertical;
        arenaWalls.Add(rightWall);

    }

    void GenerateRewards()
    {
        // remove all reward objects
        foreach(var reward in rewardObjects)
        {
            DestroyImmediate(reward);
        }
        rewardObjects.Clear();

        if(rewardDistribution == "none")
        {
            return;
        }
        else if (rewardDistribution == "everywhere")
        {
            // spawn rewards uniformly across the map
            float mapArea = arenaWidth * arenaHeight;
            int numRewards = (int)(rewardNumerosity * mapArea);
            Debug.Log($"Generating {numRewards} rewards uniformly across the map.");
            for (int i = 0; i < numRewards; i++)
            {
                float x = (float)(rng.NextDouble() * arenaWidth) - arenaWidth / 2;
                float z = (float)(rng.NextDouble() * arenaHeight) - arenaHeight / 2;
                Vector3 position = new Vector3(x, defaultHeight, z);

                if(Vector3.Distance(position, startPosition) < startAndGoalClearingDistance ||
                   Vector3.Distance(position, goalPosition) < startAndGoalClearingDistance)
                {
                    // Skip reward placement if too close to start or goal
                    continue;
                }

                GameObject reward = Instantiate(rewardPickupPrefab, position, Quaternion.identity);
                rewardObjects.Add(reward);
            }

        }
        else if (rewardDistribution == "houses")
        {
            // spawn rewards in front of house doors
            foreach(GameObject house in houses)
            {
                

                House houseComponent = house.GetComponent<House>();
                foreach(var rewPos in houseComponent.rewardSpawnPoints){
                    // here numerosity means chance of spawning a reward in front of a given house, rather than total number of rewards, to avoid overcrowding in front of houses when there are many houses
                    if(rng.NextDouble() > rewardNumerosity)
                    {
                        continue;
                    }
                    Vector3 rewardPosition = rewPos.transform.position;

                    
                    GameObject reward = Instantiate(rewardPickupPrefab, rewardPosition, Quaternion.identity);
                    rewardObjects.Add(reward);
                }
            }
        }
        
    }

    void GenerateHouse(GameObject housePrefab, Vector3 position, Quaternion rotation, bool removeTreesInHouseAreas = true)
    {
        // Find trees that lie within the house's clearing area and remove them
        if (removeTreesInHouseAreas)
        {
            House houseComponent = housePrefab.GetComponent<House>();
            BoxCollider clearingBox = houseComponent.clearingAreaCollider;
            Vector3 worldCenter = position + rotation * clearingBox.center;
            Vector3 halfExtents = clearingBox.size * 0.5f;
            List<GameObject> treesToRemove = new List<GameObject>();

            // The trees have colliders, so we can use Physics.OverlapBox to find them
            Collider[] colliders = Physics.OverlapBox(worldCenter, halfExtents, rotation, LayerMask.GetMask("DynaTrees"));
            foreach (var collider in colliders)
            {
                treesToRemove.Add(collider.gameObject);
                
            }

            foreach (var tree in treesToRemove)
            {
                DestroyImmediate(tree);
            }
        }

        // Spawn the house prefab at the specified position and rotation
        GameObject house = Instantiate(housePrefab, position, rotation);
        houses.Add(house);
    }

    void GenerateHousesUniform()
    {
        // clear previous houses
        foreach (var house in houses)
        {
            DestroyImmediate(house);
        }
        houses.Clear();

        // get clearing box info from prefab
        BoxCollider houseClearingBox = houseDefaultPrefab
            .GetComponent<House>()
            .clearingAreaCollider;

        Vector3 clearingBoxSize = houseClearingBox.size;
        Vector3 clearingBoxCenter = houseClearingBox.center;
        var houseLayerMask = LayerMask.GetMask("House"); // make sure the house prefab is on the "House" layer and that layer is included in this mask

        Debug.Log("trying to uniformly spawn houses with numerosity: " + houseNumerosity);

        for (int i = 0; i < houseNumerosity; i++)
        {
            int maxAttempts = 10;
            bool validPositionFound = false;
            Vector3 position = Vector3.zero;
            Quaternion rotation = Quaternion.identity;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                // random position, BUT take into account the size of the house's clearing box
                float houseMaxMargin = Mathf.Max(clearingBoxSize.x, clearingBoxSize.z) / 2f;
                float x = (float)(rng.NextDouble() * (arenaWidth - 2 * houseMaxMargin)) - (arenaWidth / 2f - houseMaxMargin);
                float z = (float)(rng.NextDouble() * (arenaHeight - 2 * houseMaxMargin)) - (arenaHeight / 2f - houseMaxMargin);
                position = new Vector3(x, defaultHeight, z);

                // random rotation
                rotation = Quaternion.Euler(0f, (float)(rng.NextDouble() * 360.0), 0f);

                // compute world-space clearing box
                Vector3 worldCenter = position + rotation * clearingBoxCenter;
                Vector3 halfExtents = clearingBoxSize * 0.5f;

                // ---------- check distance from start & goal ----------
                Vector2 houseXZ = new Vector2(worldCenter.x, worldCenter.z);
                Vector2 startXZ = new Vector2(startPosition.x, startPosition.z);
                //Vector2 goalXZ  = new Vector2(goalPosition.x, goalPosition.z);

                float safeRadius = Mathf.Max(halfExtents.x, halfExtents.z) + startAndGoalClearingDistance;

                if (Vector2.Distance(houseXZ, startXZ) < safeRadius)
                    continue;

                // if (Vector2.Distance(houseXZ, goalXZ) < safeRadius)
                //     continue;

                // ---------- check overlap with existing houses ----------
                // This checks against existing houses' TRIGGER clearing colliders
                bool overlapsExistingHouse = Physics.CheckBox(
                    worldCenter,
                    halfExtents,
                    rotation,
                    houseLayerMask,
                    QueryTriggerInteraction.Collide // IMPORTANT: detect triggers
                );

                if (overlapsExistingHouse)
                    continue;

                validPositionFound = true;
                break;
            }

            if (validPositionFound)
            {
                Debug.Log($"Spawning house {i} at position {position} with rotation {rotation.eulerAngles} after finding valid position in {maxAttempts} attempts.");
                GenerateHouse(
                    houseDefaultPrefab,
                    position,
                    rotation,
                    removeTreesInHouseAreas: true
                );
            }
            else
            {
                Debug.LogWarning($"Could not find valid position for house {i} after {maxAttempts} attempts.");
            }
        }
    }

    void GenerateMarkers()
    {
        // Clear existing markers
        foreach (var marker in activeMarkers)
        {
            DestroyImmediate(marker);
        }
        activeMarkers.Clear();

        // Create new goal marker
        GameObject goalMarker = Instantiate(goalMarkerPrefab, goalPosition, Quaternion.identity);
        activeMarkers.Add(goalMarker);
    }
   
    void GenerateRoadsSimple()
    {
        // delete old
        foreach (var road in roads)
        {
            DestroyImmediate(road);
        }
        roads.Clear();
        foreach (var spawner in carSpawners)
        {
            spawner.GetComponent<CarSpawner>().spawningEnabled = false;
            DestroyImmediate(spawner);
        }
        carSpawners.Clear();

        // First determine number of roads
        int numRoads = (int)carRoadSpawnFrequency; // assume its just number of roads for now
        Debug.Log($"Generating {numRoads} roads.");

        // Then for each road, determine position along the axis from start to goal (assume in Z).
        
        for(int i = 0; i < numRoads; i++){
            // Select along random pos between start and goal, with some margin from both positions
            float roadZ = (float)(rng.NextDouble() * ((goalPosition.z - roadStartGoalMargin) - (startPosition.z + roadStartGoalMargin)) + (startPosition.z + roadStartGoalMargin));

            // check if not too close to other roads, otherwise skip
            bool tooClose = false;
            foreach(var existingRoad in roads)
            {
                if(Mathf.Abs(existingRoad.transform.position.z - roadZ) < roadClearingWidth / 2)
                {
                    tooClose = true;
                    break;
                }
            }
            if(tooClose)
            {
                Debug.Log($"Skipping road at Z={roadZ} due to proximity to existing road.");
                continue;
            }

            // CLEAR ALL TREES IN THE ROAD AREA
            List<GameObject> treesToRemove = new List<GameObject>();
            foreach (var tree in trees)
            {
                if (Mathf.Abs(tree.transform.position.z - roadZ) < roadClearingWidth / 2)
                {
                    treesToRemove.Add(tree);
                }
            }
            foreach (var tree in treesToRemove)
            {
                DestroyImmediate(tree);
                trees.Remove(tree);
            }
            Debug.Log($"Cleared {treesToRemove.Count} trees for road at Z={roadZ}.");

            // Spawn the road prefab (for visualization)
            Vector3 roadPosition = new Vector3(0, 0.1f, roadZ);

            // Spawn road
            GameObject road = Instantiate(roadPrefab, roadPosition, Quaternion.identity);
            roads.Add(road);

            // Determine car direction (left or right), determines spawn
            bool directionLeftToRight = (rng.NextDouble() > 0.5);
            
            // Spawn car spawner at one end of the road 
            float spawnerX = directionLeftToRight ? -arenaWidth / 2 : arenaWidth / 2;
            float spawnerZ = roadZ;
            Vector3 spawnerPosition = new Vector3(spawnerX, defaultHeight, spawnerZ);
            Quaternion spawnerRotation = directionLeftToRight ? Quaternion.Euler(0, 90, 0) : Quaternion.Euler(0, -90, 0);
            GameObject carSpawner = Instantiate(carSpawnerPrefab, spawnerPosition, spawnerRotation);
            carSpawners.Add(carSpawner);   

            // Generate sequence of spawn times for the car spawner (using the given seed for the world to be deterministic)
            CarSpawner spawnerScript = carSpawner.GetComponent<CarSpawner>();
            spawnerScript.spawnTimes.Clear();
            
            float minSpawnInterval = spawnerScript.minSpawnInterval;
            float maxSpawnInterval = spawnerScript.maxSpawnInterval;
            spawnerScript.roadLength = arenaWidth;
            float currentTime = 0f;
            for (int j = 0; j < numCarsToSpawn; j++)
            {
                float interval = (float)(rng.NextDouble() * (maxSpawnInterval - minSpawnInterval) + minSpawnInterval);
                currentTime += interval;
                spawnerScript.spawnTimes.Add(currentTime);
            }
                     
        }

        
    }

    void InitializeTreeDynamics()
    {

        treeOriginalPositions = new List<Vector3>();
        treeOscillationPhases = new List<float>();
        treeOscillationTime = 0;
    

        for(int i = 0; i < trees.Count; i++)
        {
            Vector3 position = trees[i].transform.position;
                
            treeOriginalPositions.Add(position);
            treeOscillationPhases.Add((float)(rng.NextDouble() * 2 * Mathf.PI));
        }

            
    }

    void GenerateTreesUniform()
    {
        // Clear existing trees
        foreach (var tree in trees)
        {
            DestroyImmediate(tree);
        }
        trees.Clear();
        


        // Instantiate new trees based on wildfireWorldGenMessage and its seed
        float mapArea = arenaWidth * arenaHeight;
        int numTrees = (int)(treeDensity * mapArea); 
        Debug.Log($"Generating {numTrees} trees.");

        for (int i = 0; i < numTrees; i++)
        {
            float x = (float)(rng.NextDouble() * arenaWidth) - arenaWidth / 2;
            float z = (float)(rng.NextDouble() * arenaHeight) - arenaHeight / 2;
            Vector3 position = new Vector3(x, defaultHeight, z);

            if(Vector3.Distance(position, startPosition) < startAndGoalClearingDistance ||
               Vector3.Distance(position, goalPosition) < startAndGoalClearingDistance)
            {
                // Skip tree placement if too close to start or goal
                continue;
            }

            GameObject tree = Instantiate(treePrefab, position, Quaternion.identity);
            trees.Add(tree);
            
        }

        Debug.Log($"Total trees generated: {trees.Count}");
    }

    // RUNTIME FUNCTIONALITY
    void HandleTreeOscillation()
    {
        treeOscillationTime += conn.physicsStepTime * treeOscillationFrequency;

        float agentX = agents[0].transform.position.x;
        float agentZ = agents[0].transform.position.z;

        float time = treeOscillationTime;
        for (int i = 0; i < trees.Count; i++)
        {
            // do not oscillate if outside of aroundAgentDynamicsBoxSize
            Vector3 treePos = trees[i].transform.position;
            if (Mathf.Abs(treePos.x - agentX) > aroundAgentDynamicsBoxSize / 2 ||
                Mathf.Abs(treePos.z - agentZ) > aroundAgentDynamicsBoxSize / 2)
            {
                continue;
            }
            
            Vector3 originalPos = treeOriginalPositions[i];
            GameObject tree = trees[i];
            float phase = treeOscillationPhases[i];

            float oscillation = Mathf.Sin(time + phase) * treeOscillationMagnitude;
            Vector3 offset = new Vector3(treeOscillationDirection.x, 0, treeOscillationDirection.y).normalized * oscillation;

            tree.transform.position = originalPos + offset;
        }
    }
    void PublishGoalPosition()
    {
        // Publish goal position
        PoseMessage goalMsg = new PoseMessage();
        CoordConversion.UnityToRos(goalPosition, out float gx, out float gy, out float gz);
        goalMsg.x = gx; goalMsg.y = gy; goalMsg.z = gz;
        goalMsg.qx = 0f;
        goalMsg.qy = 0f;
        goalMsg.qz = 0f;
        goalMsg.qw = 1f;
        conn.Publish(goalPosTopic, goalMsg);
    }


}
