using System;
using UnityEngine;

public class Returner : MonoBehaviour
{
    bool randomizeHeading = true;
    Vector3 startingPosition;
    public string topicName = "/respawn_rat";

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        startingPosition = transform.position;
        var conn = RoslikeTCPServer.GetInstance();
        conn.Subscribe<StringMessage>(topicName, ReturnToStartPosition);
    }

    void ReturnToStartPosition(StringMessage stringMessage)
    {
    
        transform.position = startingPosition;

        if (randomizeHeading)
        {
            // Randomize the heading
            float randomYRotation = UnityEngine.Random.Range(0f, 360f);
            transform.rotation = Quaternion.Euler(0, randomYRotation, 0);
        }
    }
}
