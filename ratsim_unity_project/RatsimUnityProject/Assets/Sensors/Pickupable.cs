using UnityEngine;



public class Pickupable : MonoBehaviour
{
    public string topicName = "/pickupable";
    public int publishedNumber = 1;
    public bool depleted = false;

    // Start is called once before the first execution of Update after the MonoBehaviour is created

    void OnCollisionEnter(Collision collision)
    {
        if (depleted)
        {
            Debug.LogError("Pickupable is already depleted, but collided with " + collision.gameObject.name);
            return;
        }
        // check if collider has collector
        if(collision.gameObject.GetComponent<PickupableCollector>() == null)
        {
            return;
        }

        depleted = true;
        Debug.Log("Pickupable collided with " + collision.gameObject.name);
        RoslikeTCPServer conn = RoslikeTCPServer.GetInstance();
        conn.Publish(topicName, new Int32Message { data = publishedNumber });

        // Notify the RewardObjectLoader that a reward was collected
        if (RewardObjectLoader.Instance != null)
            RewardObjectLoader.Instance.NotifyRewardCollected(this);

        Destroy(gameObject);
    }
}
