using UnityEngine;

public class TerrainSemanticObject : SemanticObject
{
    public Terrain terrain;
    public int gridResolution = 512; // e.g., 512x512 grid

    private bool[,] treeGrid;
    private TerrainData terrainData;
    private Vector3 terrainSize;
    private Vector3 terrainPosition;
    public float treeRadius = 2f; // Radius around the tree position to consider as occupied

    public Color treeColor = Color.green; // Color to visualize trees in the grid

    void Start()
    {
        terrainData = terrain.terrainData;
        terrainSize = terrainData.size;
        terrainPosition = terrain.GetPosition();
        treeGrid = new bool[gridResolution, gridResolution];

        BuildTreeGrid();
    }

    void BuildTreeGrid()
    {
        int count = 0;
        float cellSizeX = terrainSize.x / gridResolution;
        float cellSizeZ = terrainSize.z / gridResolution;
        int[,] visited = new int[gridResolution, gridResolution];

        foreach (TreeInstance tree in terrainData.treeInstances)
        {
            /*Vector3 normalizedPos = tree.position; // values in [0, 1]
            int x = Mathf.FloorToInt(normalizedPos.x * gridResolution);
            int y = Mathf.FloorToInt(normalizedPos.z * gridResolution);

            // Clamp in case of rounding
            x = Mathf.Clamp(x, 0, gridResolution - 1);
            y = Mathf.Clamp(y, 0, gridResolution - 1);

            if (!treeGrid[x, y])
            {
                treeGrid[x, y] = true;
                count++;
            }*/

            Vector3 normalizedPos = tree.position;
            Vector3 worldPos = new Vector3(
                normalizedPos.x * terrainSize.x + terrainPosition.x,
                0f,
                normalizedPos.z * terrainSize.z + terrainPosition.z
            );

            int centerX = Mathf.FloorToInt((worldPos.x - terrainPosition.x) / cellSizeX);
            int centerZ = Mathf.FloorToInt((worldPos.z - terrainPosition.z) / cellSizeZ);

            int radiusX = Mathf.CeilToInt(treeRadius / cellSizeX);
            int radiusZ = Mathf.CeilToInt(treeRadius / cellSizeZ);

            for (int dx = -radiusX; dx <= radiusX; dx++)
            {
                for (int dz = -radiusZ; dz <= radiusZ; dz++)
                {
                    int gx = centerX + dx;
                    int gz = centerZ + dz;

                    if (gx < 0 || gx >= gridResolution || gz < 0 || gz >= gridResolution)
                        continue;

                    // Check actual distance to keep circular shape
                    float distX = dx * cellSizeX;
                    float distZ = dz * cellSizeZ;
                    float distanceSquared = distX * distX + distZ * distZ;

                    if (distanceSquared <= treeRadius * treeRadius)
                    {
                        if (!treeGrid[gx, gz])
                        {
                            treeGrid[gx, gz] = true;
                            count++;
                        }
                    }
                }
            }

        }

        Debug.Log($"TreeGrid: {count} cells contain trees out of {gridResolution * gridResolution}");
    }

    public bool HasTreeAt(Vector3 worldPos)
    {
        Vector3 localPos = worldPos - terrainPosition;
        float normX = localPos.x / terrainSize.x;
        float normZ = localPos.z / terrainSize.z;

        int gridX = Mathf.FloorToInt(normX * gridResolution);
        int gridZ = Mathf.FloorToInt(normZ * gridResolution);

        // Handle out-of-bounds
        if (gridX < 0 || gridX >= gridResolution || gridZ < 0 || gridZ >= gridResolution)
            return false;

        return treeGrid[gridX, gridZ];
    }

     // Override the GetDescriptorDimension method to return the number of color components
    public override uint GetDescriptorDimension()
    {
        return 3; // RGB components
    }

    // Override the GetDescriptor method to return the color as an array of floats
    public override float[] GetDescriptor(Vector3 worldPos)
    {
        if (HasTreeAt(worldPos))
            return new float[] { treeColor.r, treeColor.g, treeColor.b };
        else
        {
            // return black color
            return new float[] { 0, 0, 0 };
        }
    }
}
