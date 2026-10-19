using UnityEngine;

public class ColorSemanticObject : SemanticObject
{
    public Color color;

    // Override the GetDescriptorDimension method to return the number of color components
    public override uint GetDescriptorDimension()
    {
        return 3; // RGB components
    }

    // Override the GetDescriptor method to return the color as an array of floats
    public override float[] GetDescriptor(Vector3 worldPos)
    {
        return new float[] { color.r, color.g, color.b };
    }

    void Start()
    {
        if(GetComponent<Renderer>() == null)
        {
            Debug.LogError("ColorSemanticObject requires a Renderer component to access the material color.");
            return;
        }
        color = GetComponent<Renderer>().material.color;
    }
}
