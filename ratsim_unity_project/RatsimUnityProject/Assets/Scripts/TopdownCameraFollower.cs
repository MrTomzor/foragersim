using UnityEngine;

public class TopdownCameraFollower : MonoBehaviour
{
    public GameObject target;
    public float height = 50;
    public bool lockRotation = true;
    public float rotationAngle = 0;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }
    

    // Update is called once per frame
    void Update()
    {
        if (target != null)
        {
            Vector3 targetPosition = target.transform.position;
            transform.position = new Vector3(targetPosition.x, targetPosition.y + height, targetPosition.z);
            if (lockRotation)
            {
                // Look down and rotate around Y axis by rotationAngle
                transform.rotation = Quaternion.Euler(90, rotationAngle, 0);
            }
        }
    }
}
