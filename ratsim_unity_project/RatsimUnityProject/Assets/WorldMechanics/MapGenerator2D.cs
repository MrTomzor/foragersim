using UnityEngine;
using System.Collections.Generic;

public class MapGenerator2D : MonoBehaviour
{
    public string topicName = "/mapgen";
    public float obstacleHeight = 1.0f; // Height of the obstacles in Unity units
    public GameObject obstaclePrefab; // Prefab to instantiate for obstacles

    int width, height;
    float meters_per_pixel;

    bool[,] obstacles;
    bool[,] spawnMask;
    bool[,] poiMask;
    bool[,] forbiddenMask;
    bool[,] growableMask;

    public GameObject playerObject;
    public int playerChunkX, playerChunkY;
    public float playerSensingRange = 100; // How far the player sees in meters. Affects how many chunks are loaded around the player.
    public int chunkSize = 50; // Size of each chunk in pixels

    // Track spawned chunks
    private Dictionary<(int, int), GameObject> activeChunks = new Dictionary<(int, int), GameObject>();


    Vector3 mapPixelToWorldPos(int x, int y)
    {
        //return new Vector3((x - width / 2.0f) * meters_per_pixel, 0, -(y - height / 2.0f) * meters_per_pixel);
        return new Vector3(x * meters_per_pixel, 0, (height-y) * meters_per_pixel);
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        var conn = RoslikeTCPServer.GetInstance();
        conn.Subscribe<MapGenTemplate2D>(topicName, SaveMapMsgData);
    }

    public void SaveMapMsgData(MapGenTemplate2D msg)
    {
        //GenerateMap(msg);
        Debug.Log("Received MapGenTemplate2D message");
        Debug.Log($"Map size: {msg.width}x{msg.height}, meters per pixel: {msg.meters_per_pixel}");
        width = msg.width;
        height = msg.height;
        meters_per_pixel = msg.meters_per_pixel;

        // Utility function to reshape a flattened mask into 2D bool array
        bool[,] ReshapeMask(int[] flatMask, int width, int height)
        {
            bool[,] mask2D = new bool[height, width];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    mask2D[y, x] = flatMask[y * width + x] != 0;
                }
            }
            return mask2D;
        }

        // Reshape all masks
        obstacles = ReshapeMask(msg.obstacles, msg.width, msg.height);
        if(spawnMask == null || spawnMask.Length != msg.width * msg.height){
            spawnMask = ReshapeMask(msg.spawnMask, msg.width, msg.height);
        }
        if( poiMask == null || poiMask.Length != msg.width * msg.height){
            poiMask = ReshapeMask(msg.poiMask, msg.width, msg.height);
        }
        if( forbiddenMask == null || forbiddenMask.Length != 0){
            forbiddenMask = ReshapeMask(msg.forbiddenMask, msg.width, msg.height);
        }
        if( growableMask == null || growableMask.Length != 0){
            growableMask = ReshapeMask(msg.growableMask, msg.width, msg.height);
        }

        // Example: visualize masks in console (optional)
        Debug.Log($"Map size: {msg.width}x{msg.height}, meters per pixel: {msg.meters_per_pixel}");
        Debug.Log($"Obstacles: {CountTrue(obstacles)}, Spawn: {CountTrue(spawnMask)}, POIs: {CountTrue(poiMask)}, Forbidden: {CountTrue(forbiddenMask)}, Growable: {CountTrue(growableMask)}");
    }

    public void SpawnMapObjectsFull(bool[,] obstacleMask, float metersPerPixel)
    {
        int height = obstacleMask.GetLength(0);
        int width = obstacleMask.GetLength(1);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (obstacleMask[y, x])
                {
                    // Calculate world position
                    //float worldX = x * metersPerPixel;
                    //float worldY = y * metersPerPixel;
                    Vector3 worldPos = mapPixelToWorldPos(x, y);
                    float worldX = worldPos.x;
                    float worldY = worldPos.z;

                    // Instantiate an obstacle prefab at (worldX, worldY)
                    // Scale by metersPerPixel if needed
                    GameObject obstacle = Instantiate(obstaclePrefab, new Vector3(worldX, 0, worldY), Quaternion.identity);
                    float scale = metersPerPixel; // Adjust scale as needed
                    obstacle.transform.localScale = new Vector3(scale, obstacleHeight, scale);
                    obstacle.transform.parent = this.transform; // Parent to this GameObject for organization
                }
            }
        }
    }



    void Update()
    {
        if (playerObject == null || obstacles == null) return;

        // Get player position in pixels
        Vector3 playerWorldPos = playerObject.transform.position;
        int playerPixelX = Mathf.RoundToInt(playerWorldPos.x / meters_per_pixel + width / 2.0f);
        int playerPixelY = Mathf.RoundToInt(-playerWorldPos.z / meters_per_pixel + height / 2.0f);

        // Compute which chunk player is in
        int playerChunkX = playerPixelX / chunkSize;
        int playerChunkY = playerPixelY / chunkSize;

        int chunksVisible = Mathf.CeilToInt(playerSensingRange / (chunkSize * meters_per_pixel));

        HashSet<(int, int)> neededChunks = new HashSet<(int, int)>();

        for (int dy = -chunksVisible; dy <= chunksVisible; dy++)
        {
            for (int dx = -chunksVisible; dx <= chunksVisible; dx++)
            {
                int cx = playerChunkX + dx;
                int cy = playerChunkY + dy;

                if (cx < 0 || cy < 0 || cx >= Mathf.CeilToInt((float)width / chunkSize) || cy >= Mathf.CeilToInt((float)height / chunkSize))
                    continue;

                neededChunks.Add((cx, cy));

                if (!activeChunks.ContainsKey((cx, cy)))
                {
                    GameObject chunk = SpawnChunk(cx, cy);
                    activeChunks[(cx, cy)] = chunk;
                }
            }
        }

        // Unload chunks not needed anymore
        List<(int, int)> toRemove = new List<(int, int)>();
        foreach (var kvp in activeChunks)
        {
            if (!neededChunks.Contains(kvp.Key))
            {
                Destroy(kvp.Value);
                toRemove.Add(kvp.Key);
            }
        }
        foreach (var key in toRemove) activeChunks.Remove(key);
    }

    GameObject SpawnChunk(int chunkX, int chunkY)
    {
        GameObject chunkParent = new GameObject($"Chunk_{chunkX}_{chunkY}");
        chunkParent.transform.parent = this.transform;

        int startX = chunkX * chunkSize;
        int startY = chunkY * chunkSize;

        int endX = Mathf.Min(startX + chunkSize, width);
        int endY = Mathf.Min(startY + chunkSize, height);

        for (int y = startY; y < endY; y++)
        {
            for (int x = startX; x < endX; x++)
            {
                if (obstacles[y, x])
                {
                    Vector3 worldPos = mapPixelToWorldPos(x, y);
                    GameObject obstacle = Instantiate(obstaclePrefab, new Vector3(worldPos.x, 0, worldPos.z), Quaternion.identity);
                    obstacle.transform.localScale = new Vector3(meters_per_pixel, obstacleHeight, meters_per_pixel);
                    obstacle.transform.parent = chunkParent.transform;
                }
            }
        }

        return chunkParent;
    }

    // Helper to count true values in 2D bool array
    int CountTrue(bool[,] mask)
    {
        int count = 0;
        foreach (var val in mask)
        {
            if (val) count++;
        }
        return count;
    }
}
