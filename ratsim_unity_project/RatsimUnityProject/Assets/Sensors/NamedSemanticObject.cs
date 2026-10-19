using UnityEngine;

public class NamedSemanticObject : SemanticObject
{

    public string semanticName;

    // Override the GetDescriptorDimension method to return the number of color components
    public override uint GetDescriptorDimension()
    {
        return SemanticLidarSensor.descriptorDimension;
    }

    // Override the GetDescriptor method to return the color as an array of floats
    public override float[] GetDescriptor(Vector3 worldPos)
    {
        return SemanticLidarSensor.GetNamedSemanticObjectDescriptor(semanticName);
    }

    void Start()
    {

    }   
}
