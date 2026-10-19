/// <summary>
/// Defines all data products in the world generation pipeline.
/// Each WorldDataProvider declares which types it Provides and DependsOn.
/// The system topologically sorts providers by these declarations.
///
/// Adding a new data type (e.g. Biomes, Rivers, Dynamics) means adding
/// a value here and creating a provider that produces it.
/// </summary>
public enum WorldDataType
{
    Height,
    Layout,
    Boundaries,
    StructureEvents,
    StructureContent,
    Rewards,
    Wells,
    WellSchedule,
    Agents,
    Vegetation,
    TerrainMesh,
    TerrainTexture,
    Lighting,
    DynamicObjects,
    Smoke,
}
