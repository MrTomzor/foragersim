using UnityEngine;

public class SemanticObject : MonoBehaviour
{
    public virtual uint GetDescriptorDimension()
    {
        return 1;
    }

    public virtual float[] GetDescriptor(Vector3 worldPos)
    {
        return new float[] { 0 };
    }
}
