using UnityEngine;

public class ComponentEnablerSimple : MonoBehaviour
{
    // public var specifying which component to enable/disable
    public Component componentToToggle;
    // var specifying which key enables the component
    public KeyCode enableKey = KeyCode.C;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    public string switchTopic = "";
    
    void Start()
    {
        if (switchTopic != "")
        {
            RoslikeTCPServer.GetInstance().Subscribe<BoolMessage>(switchTopic, OnSwitchMessage);
        }
    }

    void OnSwitchMessage(BoolMessage msg)
    {
        if (componentToToggle != null)
        {
            Behaviour behaviour = componentToToggle as Behaviour;
            if (behaviour != null)
            {
                behaviour.enabled = msg.data;
            }
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (Input.GetKeyDown(enableKey))
        {
            if (componentToToggle != null)
            {
                Behaviour behaviour = componentToToggle as Behaviour;
                if (behaviour != null)
                {
                    behaviour.enabled = !behaviour.enabled;
                }
            }
        }
    }
}
